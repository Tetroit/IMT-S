using System;
using System.Threading.Tasks;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// The atmosphere's table for the shader (the atmosphere's build, step 3; decided 2026-10-29, measured with
    /// IMTNS/atmosphere/path_options.py): per sub-band, the transmission τ and the path radiance L_path from the camera
    /// along <see cref="Directions"/> directions out to <see cref="Ranges"/> ranges, one ThermalMath.MarchRay per
    /// direction. Two RGBAFloat textures, a range per column and a direction per row, the sub-bands in RGBA
    /// (ThermalMath.SubBandEdges' order).
    /// <list type="bullet">
    /// <item>Directions even in sign(v) v² of sin(elevation), v from −1 to 1: dense at the horizon, where the paths
    /// change fastest.</item>
    /// <item>Ranges even in log(1 + s / <see cref="RangeScale"/>), from 0 to the camera's far plane.</item>
    /// </list>
    /// Against each target's own walk: 0.41 NETD for a 900 m camera out to 40 km, 0.23 for a 2 m camera out to 5 km.
    /// Built off the main thread, the rays in parallel, and swapped in whole when done, so a build never stalls a
    /// frame. A new build starts when the camera height or the air has moved enough since the last one began to move
    /// the image by ~0.05 NETD. Until MarchRay is written the table stays neutral: τ 1, L_path 0.
    /// </summary>
    public sealed class ThermalAtmosphereTable : IDisposable
    {
        public const int Directions = 128, Ranges = 256;
        public const double RangeScale = 50;   // m

        // Each alone moves the image by ~0.05 NETD (path_options.py refresh: 0.28 NETD per 0.01 K of air, 0.50 per
        // 0.01 K of dew point, 0.14 per 0.5 m of camera height).
        const double k_HeightStep = 0.15, k_AirStep = 0.002, k_DewStep = 0.001, k_OzoneStep = 0.1e-9;

        // In a class of their own, so they are looked up on the first Push and not by the layout and Build, which the
        // Atmosphere Check's harness runs outside Unity.
        static class Ids
        {
            public static readonly int Tau = Shader.PropertyToID("_ThermalAtmosphereTau");
            public static readonly int Path = Shader.PropertyToID("_ThermalAtmospherePath");
            public static readonly int Params = Shader.PropertyToID("_ThermalAtmosphereParams");
        }

        /// <summary>What one table is built for.</summary>
        public struct Inputs
        {
            public AtmosphereAir air;
            public double cameraHeightM;   // above the ground (world y 0)
            public double farM;            // the last range
        }

        /// <summary>sin(elevation) of direction row <paramref name="r"/>.</summary>
        public static double DirectionSine(int r)
        {
            double v = -1 + 2.0 * r / (Directions - 1);
            return Math.Sign(v) * v * v;
        }

        /// <summary>The ranges, m, for a far plane of <paramref name="farM"/>: <see cref="Ranges"/> of them, from 0.</summary>
        public static void RangeNodes(double farM, double[] nodes)
        {
            double top = Math.Log(1 + farM / RangeScale);
            for (int m = 0; m < Ranges; m++)
                nodes[m] = RangeScale * (Math.Exp(top * m / (Ranges - 1)) - 1);
        }

        /// <summary>
        /// Fills <paramref name="tau"/> and <paramref name="path"/>, [direction, range, sub-band], one MarchRay per
        /// direction, the directions in parallel when <paramref name="parallel"/>. <paramref name="scratch"/>, from
        /// <see cref="NewScratch"/>, saves the rays' arrays from being allocated per build. Throws what MarchRay throws.
        /// </summary>
        public static void Build(ThermalAtmosphere atmosphere, Inputs inputs, Action<double, double[]> radiance,
                                 float[] tau, float[] path, bool parallel, double[][] scratch = null)
        {
            var nodes = new double[Ranges];
            RangeNodes(inputs.farM, nodes);
            void Ray(int r)
            {
                double[] t = scratch != null ? scratch[2 * r] : new double[Ranges * 4];
                double[] p = scratch != null ? scratch[2 * r + 1] : new double[Ranges * 4];
                Array.Clear(t, 0, t.Length);
                Array.Clear(p, 0, p.Length);
                ThermalMath.MarchRay(atmosphere, inputs.air, inputs.cameraHeightM, DirectionSine(r), nodes, radiance, t, p);
                int o = r * Ranges * 4;
                for (int i = 0; i < Ranges * 4; i++)
                {
                    tau[o + i] = (float)t[i];
                    path[o + i] = (float)p[i];
                }
            }
            if (!parallel)
            {
                for (int r = 0; r < Directions; r++)
                    Ray(r);
                return;
            }
            try
            {
                Parallel.For(0, Directions, Ray);
            }
            catch (AggregateException e)
            {
                throw e.Flatten().InnerExceptions[0];
            }
        }

        /// <summary>Two arrays per direction for <see cref="Build"/>, kept from build to build.</summary>
        public static double[][] NewScratch()
        {
            var scratch = new double[2 * Directions][];
            for (int i = 0; i < scratch.Length; i++)
                scratch[i] = new double[Ranges * 4];
            return scratch;
        }

        /// <summary>Why a built table is not taken, or null: every value finite, τ in [0, 1], L_path not negative.</summary>
        public static string Problem(float[] tau, float[] path)
        {
            for (int i = 0; i < tau.Length; i++)
            {
                if (!(tau[i] >= 0 && tau[i] <= 1.000001f))
                    return $"τ {tau[i]} at direction {i / (Ranges * 4)}, range {i / 4 % Ranges}, sub-band {i % 4}";
                if (!(path[i] >= -1e-6f) || float.IsInfinity(path[i]))
                    return $"L_path {path[i]} at direction {i / (Ranges * 4)}, range {i / 4 % Ranges}, sub-band {i % 4}";
            }
            return null;
        }

        const string k_NotWritten = "not written";

        Texture2D m_Tau, m_Path;
        // The worker fills these; the main thread uploads them once it is done, before the next build starts.
        readonly float[] m_TauData = new float[Directions * Ranges * 4];
        readonly float[] m_PathData = new float[Directions * Ranges * 4];
        // Kept, not allocated per build: while the camera climbs a build runs every ~20 ms, and 2 MB each would keep
        // Mono's collector, which stops every thread, busy.
        readonly double[][] m_Scratch = NewScratch();
        Task<string> m_Build;          // null when none is running; its result null, or why the table was not taken
        Inputs m_Building, m_Started, m_Built;
        bool m_HasStarted, m_HasBuilt, m_NotWritten, m_Warned;

        /// <summary>True once a built table has been taken; until then the textures are neutral.</summary>
        public bool HasTable => m_HasBuilt;

        /// <summary>What the table in the textures was built for (valid when <see cref="HasTable"/>).</summary>
        public Inputs BuiltInputs => m_Built;

        /// <summary>True while ThermalMath.MarchRay is not written and the table stays neutral.</summary>
        public bool MarchIsPlaceholder => m_NotWritten;

        public Texture2D Tau => m_Tau;
        public Texture2D Path => m_Path;

        /// <summary>
        /// Once per frame, on the main thread: takes a finished build into the textures, then starts the next if the
        /// inputs have moved since the last one began. <paramref name="radiance"/> is called from worker threads.
        /// </summary>
        public void Update(ThermalAtmosphere atmosphere, Inputs inputs, Action<double, double[]> radiance)
        {
            EnsureTextures();
            if (m_Build != null)
            {
                if (!m_Build.IsCompleted)
                {
#if UNITY_EDITOR
                    // In edit mode Update runs only when something changes; this one has to come back for the result.
                    if (!Application.isPlaying)
                        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                    return;
                }
                Finish();
            }
            if (atmosphere == null || !atmosphere.IsValid || m_NotWritten)
                return;
            if (m_HasStarted && !Moved(m_Started, inputs))
                return;

            m_Started = m_Building = inputs;
            m_HasStarted = true;
            float[] tau = m_TauData, path = m_PathData;
            double[][] scratch = m_Scratch;
            m_Build = Task.Run(() =>
            {
                try
                {
                    Build(atmosphere, inputs, radiance, tau, path, true, scratch);
                    return Problem(tau, path);
                }
                catch (NotImplementedException)
                {
                    return k_NotWritten;
                }
                catch (Exception e)
                {
                    return $"MarchRay threw {e.GetType().Name} ({e.Message})";
                }
            });
        }

        void Finish()
        {
            string problem = m_Build.Result;
            m_Build = null;
            if (problem == null)
            {
                m_Tau.SetPixelData(m_TauData, 0);
                m_Tau.Apply(false, false);
                m_Path.SetPixelData(m_PathData, 0);
                m_Path.Apply(false, false);
                m_Built = m_Building;
                m_HasBuilt = true;
                return;
            }
            if (problem == k_NotWritten)
            {
                // Tried once per domain reload: writing it recompiles, which clears this.
                m_NotWritten = true;
                Debug.LogWarning("[Thermal] ThermalMath.MarchRay is not written yet, so the atmosphere's table stays " +
                                 "neutral: τ 1, no path radiance");
                return;
            }
            if (!m_Warned)
            {
                Debug.LogWarning($"[Thermal] the atmosphere's table was not taken: {problem} - keeping the previous " +
                                 "one until the inputs move again");
                m_Warned = true;
            }
        }

        static bool Moved(Inputs a, Inputs b)
        {
            return Math.Abs(a.cameraHeightM - b.cameraHeightM) >= k_HeightStep ||
                   Math.Abs(a.air.airTemperatureK - b.air.airTemperatureK) >= k_AirStep ||
                   Math.Abs(a.air.dewPointK - b.air.dewPointK) >= k_DewStep ||
                   Math.Abs(a.air.ozone - b.air.ozone) >= k_OzoneStep ||
                   a.air.altitudeM != b.air.altitudeM || a.farM != b.farM;
        }

        /// <summary>Binds the textures and the layout as shader globals (step 4 reads them).</summary>
        public void Push()
        {
            EnsureTextures();
            Shader.SetGlobalTexture(Ids.Tau, m_Tau);
            Shader.SetGlobalTexture(Ids.Path, m_Path);
            Shader.SetGlobalVector(Ids.Params, new Vector4(Directions, Ranges, (float)RangeScale,
                                                           m_HasBuilt ? (float)m_Built.farM : 1));
        }

        void EnsureTextures()
        {
            if (m_Tau != null && m_Path != null)
                return;
            m_Tau = NewTexture("ThermalAtmosphereTau", 1f);
            m_Path = NewTexture("ThermalAtmospherePath", 0f);
            m_HasBuilt = m_HasStarted = false;
        }

        static Texture2D NewTexture(string name, float value)
        {
            var texture = new Texture2D(Ranges, Directions, TextureFormat.RGBAFloat, false, true)
            {
                name = name,
                // As the LUT: made by an [ExecuteAlways] component, so never saved, never unloaded while bound, and
                // destroyed by Dispose. Read with Load and blended in the shader, never filtered by the hardware.
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var data = new float[Directions * Ranges * 4];
            for (int i = 0; i < data.Length; i++)
                data[i] = value;
            texture.SetPixelData(data, 0);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>Waits for a build in flight (at most one table's time), then frees the textures.</summary>
        public void Dispose()
        {
            try
            {
                m_Build?.Wait();
            }
            catch (AggregateException)
            {
            }
            m_Build = null;
            if (m_Tau != null)
                UnityEngine.Object.DestroyImmediate(m_Tau);
            if (m_Path != null)
                UnityEngine.Object.DestroyImmediate(m_Path);
            m_Tau = m_Path = null;
            m_HasBuilt = m_HasStarted = false;
        }
    }
}
