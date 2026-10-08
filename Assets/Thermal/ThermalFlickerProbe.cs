using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace IMT.Thermal
{
    /// <summary>
    /// Next, item 10: frames of a static scene that alternate between exact states. Sits beside a ThermalCapture
    /// and, in Play mode, watches every frame without changing anything. It hashes the radiance target and snapshots
    /// what the CPU hands the shader: the _Thermal globals to the bit, the LUT's and sky tables' update counts, each
    /// directional light's rotation and shadow settings, the camera's matrices. It also records which cameras
    /// rendered, in order. It logs only what changes, each with its frame, so a toggle made while it runs (a
    /// light's shadows, the Scene view) lands in the log beside the image states before and after it. If the image
    /// alternates while no input does, the cause is on the GPU side. Nothing is written to disk.
    /// </summary>
    [AddComponentMenu("Thermal/Thermal Flicker Probe")]
    public class ThermalFlickerProbe : MonoBehaviour
    {
        [Tooltip("Frames per line of the state sequence in the log.")]
        [SerializeField, Min(10)] int m_Window = 120;

        static readonly string[] k_Floats =
        {
            "_ThermalAirTemperature", "_ThermalConvectiveCoefficient", "_ThermalDirectNormalIrradiance",
            "_ThermalDiffuseHorizontalIrradiance", "_ThermalGlobalHorizontalIrradiance", "_ThermalGroundAlbedo",
        };
        static readonly string[] k_Vectors = { "_ThermalSunDirection", "_ThermalLutParams", "_ThermalSkyParams" };
        static readonly string[] k_Textures = { "_ThermalLut", "_ThermalSkyView", "_ThermalSkyDiffuse" };

        ThermalCapture m_Capture;
        Camera m_Camera;
        Light[] m_Lights;
        Dictionary<string, string> m_Inputs = new Dictionary<string, string>();
        readonly Dictionary<string, int> m_Changes = new Dictionary<string, int>();
        readonly Dictionary<int, string> m_CamerasByFrame = new Dictionary<int, string>();
        readonly Dictionary<string, char> m_CameraSets = new Dictionary<string, char>();
        readonly Dictionary<ulong, char> m_States = new Dictionary<ulong, char>();
        readonly Dictionary<char, int> m_StateCounts = new Dictionary<char, int>();
        readonly Dictionary<string, int> m_Pairs = new Dictionary<string, int>();
        readonly StringBuilder m_Sequence = new StringBuilder(), m_CameraSequence = new StringBuilder();
        uint[] m_Bits;
        float[] m_First;            // state A, to say where the others differ from it
        int m_WindowStart = -1, m_LastRendered = -1, m_Pending, m_FirstFrame;

        // The first frames after Play can still be the empty target or a half-pushed environment.
        const int k_WarmUp = 5;

        void OnEnable()
        {
            m_Capture = GetComponent<ThermalCapture>();
            m_Camera = GetComponent<Camera>();
            if (m_Capture == null || m_Camera == null)
            {
                Debug.LogError("[Flicker] put the probe on the GameObject with the Thermal Capture");
                enabled = false;
                return;
            }
            m_Lights = FindObjectsByType<Light>().Where(l => l.type == LightType.Directional).ToArray();
            m_FirstFrame = Time.frameCount + k_WarmUp;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var cameraData = m_Camera.GetUniversalAdditionalCameraData();
            Debug.Log(FormattableString.Invariant(
                $"[Flicker] watching {name}: the radiance target every frame, {k_Floats.Length + k_Vectors.Length + k_Textures.Length} ") +
                $"shader inputs, {m_Lights.Length} directional light(s), this camera's matrices, and which cameras render. " +
                "Only changes are logged. " + (urp == null ? "Not URP." : FormattableString.Invariant(
                $"Shadows: distance {urp.shadowDistance} m, {urp.shadowCascadeCount} cascade(s), soft shadows ") +
                (urp.supportsSoftShadows ? "supported" : "off") + "; this camera renders its own: " +
                (cameraData != null && cameraData.renderShadows ? "yes." : "NO.")));
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Flush();
            Summary();
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            int frame = Time.frameCount;
            string label = camera.cameraType == CameraType.SceneView ? "Scene view"
                         : camera.cameraType == CameraType.Preview ? "a preview" : camera.name;
            m_CamerasByFrame[frame] = m_CamerasByFrame.TryGetValue(frame, out string so) ? so + " > " + label : label;
        }

        void LateUpdate()
        {
            Snapshot();

            // The target holds last frame's render now; this frame's comes after LateUpdate.
            RenderTexture target = m_Capture.target;
            if (target == null || m_Pending > 2 || Time.frameCount < m_FirstFrame)
                return;
            int rendered = Time.frameCount - 1;
            m_Pending++;
            AsyncGPUReadback.Request(target, 0, TextureFormat.RFloat, request => OnReadback(request, rendered));
        }

        // ------------------------------------------------------------------------------------------- inputs

        void Snapshot()
        {
            var now = new Dictionary<string, string>();
            foreach (string n in k_Floats)
                now[n] = Bits(Shader.GetGlobalFloat(n));
            foreach (string n in k_Vectors)
                now[n] = Bits(Shader.GetGlobalVector(n));
            foreach (string n in k_Textures)
            {
                Texture t = Shader.GetGlobalTexture(n);
                now[n] = t == null ? "none" : $"texture {t.GetEntityId()}, update {t.updateCount}";
            }
            foreach (Light l in m_Lights)
            {
                if (l == null)
                    continue;
                now[l.name + " rotation"] = Bits(l.transform.rotation);
                now[l.name + " shadows"] = string.Format(CultureInfo.InvariantCulture,
                    "{0}, strength {1:R}, bias {2:R}, normal bias {3:R}, {4}", l.shadows, l.shadowStrength,
                    l.shadowBias, l.shadowNormalBias, l.isActiveAndEnabled ? "on" : "off");
            }
            now["camera view matrix"] = Bits(m_Camera.worldToCameraMatrix);
            now["camera projection matrix"] = Bits(m_Camera.projectionMatrix);

            foreach (var kv in now)
            {
                if (!m_Inputs.TryGetValue(kv.Key, out string old) || old == kv.Value)
                    continue;
                m_Changes.TryGetValue(kv.Key, out int count);
                m_Changes[kv.Key] = count + 1;
                if (count < 10)
                    Debug.Log($"[Flicker] frame {Time.frameCount}: {kv.Key} changed\n  from {old}\n  to   {kv.Value}");
            }
            m_Inputs = now;
        }

        static string Bits(float v) =>
            BitConverter.SingleToInt32Bits(v).ToString("X8") + "(" + v.ToString("R", CultureInfo.InvariantCulture) + ")";

        static string Bits(Vector4 v) => $"{Bits(v.x)} {Bits(v.y)} {Bits(v.z)} {Bits(v.w)}";

        static string Bits(Quaternion q) => $"{Bits(q.x)} {Bits(q.y)} {Bits(q.z)} {Bits(q.w)}";

        static string Bits(Matrix4x4 m)
        {
            var s = new StringBuilder();
            for (int i = 0; i < 16; i++)
                s.Append(BitConverter.SingleToInt32Bits(m[i]).ToString("X8")).Append(i % 4 == 3 ? " | " : " ");
            return s.ToString();
        }

        // --------------------------------------------------------------------------------------------- image

        void OnReadback(AsyncGPUReadbackRequest request, int rendered)
        {
            m_Pending--;
            if (this == null || request.hasError || !isActiveAndEnabled)    // readbacks still land after Play stops
                return;

            var data = request.GetData<uint>();
            if (m_Bits == null || m_Bits.Length != data.Length)
                m_Bits = new uint[data.Length];
            data.CopyTo(m_Bits);
            ulong hash = 14695981039346656037UL;            // FNV-1a over every pixel's bits
            for (int i = 0; i < m_Bits.Length; i++)
                hash = (hash ^ m_Bits[i]) * 1099511628211UL;

            if (!m_States.TryGetValue(hash, out char state))
            {
                state = (char)('A' + m_States.Count);
                m_States[hash] = state;
                Describe(state, rendered);
            }
            Record(rendered, state);
        }

        void Describe(char state, int rendered)
        {
            if (m_First == null)
            {
                m_First = new float[m_Bits.Length];
                for (int i = 0; i < m_Bits.Length; i++)
                    m_First[i] = BitConverter.Int32BitsToSingle((int)m_Bits[i]);
                Debug.Log($"[Flicker] frame {rendered}: image state {state}");
                return;
            }

            // Rows from the top, as the analysis scripts and an image viewer count them; the target is bottom-up.
            int width = m_Capture.target.width, height = m_Bits.Length / width;
            int differ = 0, x0 = int.MaxValue, x1 = -1, top = int.MaxValue, bottom = -1;
            float most = 0;
            for (int i = 0; i < m_Bits.Length; i++)
            {
                float d = BitConverter.Int32BitsToSingle((int)m_Bits[i]) - m_First[i];
                if (d == 0)
                    continue;
                differ++;
                most = Mathf.Max(most, Mathf.Abs(d));
                int x = i % width, row = height - 1 - i / width;
                x0 = Math.Min(x0, x);
                x1 = Math.Max(x1, x);
                top = Math.Min(top, row);
                bottom = Math.Max(bottom, row);
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[Flicker] frame {0}: new image state {1} - against A, {2} px differ by up to {3:0.######}, rows {4}-{5} " +
                "from the top, columns {6}-{7}", rendered, state, differ, most, top, bottom, x0, x1));
        }

        void Record(int rendered, char state)
        {
            if (!m_CamerasByFrame.TryGetValue(rendered, out string cameras))
                cameras = "(none recorded)";
            m_CamerasByFrame.Remove(rendered);
            if (!m_CameraSets.TryGetValue(cameras, out char set))
            {
                set = (char)('1' + m_CameraSets.Count);
                m_CameraSets[cameras] = set;
                Debug.Log($"[Flicker] frame {rendered}: cameras rendering, in order: {cameras} - set {set}");
            }

            m_StateCounts[state] = m_StateCounts.TryGetValue(state, out int n) ? n + 1 : 1;
            string pair = $"state {state}, cameras {set}";
            m_Pairs[pair] = m_Pairs.TryGetValue(pair, out int p) ? p + 1 : 1;

            // A dot for a frame whose readback was skipped, so the two sequences stay aligned with frame numbers.
            if (m_WindowStart < 0)
                m_WindowStart = rendered;
            for (int f = m_LastRendered + 1; m_LastRendered >= 0 && f < rendered; f++)
            {
                m_Sequence.Append('.');
                m_CameraSequence.Append('.');
            }
            m_LastRendered = rendered;
            m_Sequence.Append(state);
            m_CameraSequence.Append(set);
            if (m_Sequence.Length >= m_Window)
                Flush();
        }

        void Flush()
        {
            if (m_Sequence.Length == 0)
                return;
            Debug.Log($"[Flicker] frames {m_WindowStart}-{m_WindowStart + m_Sequence.Length - 1}\n" +
                      $"  states  {m_Sequence}\n  cameras {m_CameraSequence}");
            m_Sequence.Clear();
            m_CameraSequence.Clear();
            m_WindowStart = -1;
        }

        void Summary()
        {
            if (m_StateCounts.Count == 0)
                return;
            var s = new StringBuilder("[Flicker] summary - image states: ");
            s.Append(string.Join(", ", m_StateCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}")));
            s.Append("\n  camera sets: ").Append(string.Join("; ", m_CameraSets.Select(kv => $"{kv.Value} = {kv.Key}")));
            s.Append("\n  frames by state and camera set: ")
             .Append(string.Join(", ", m_Pairs.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}")));
            s.Append(m_Changes.Count == 0
                ? "\n  no input changed"
                : "\n  inputs that changed: " + string.Join(", ", m_Changes.Select(kv => $"{kv.Key} x{kv.Value}")));
            Debug.Log(s.ToString());
        }
    }
}
