// Editor-only. UnityEditor does not exist in player assemblies, and this file lives outside an
// Editor/ folder, so without the guard the first player build fails on the using line below.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace IMT.Thermal
{
    public static class ThermalTools
    {
        [MenuItem("Thermal/Planck Law Test")]
        public static void TestPlanckLawImplementation()
        {
            double thermalK = 300;
            double waveLengthM = 9.659 * 1e-6;

            double B = ThermalMath.ComputePlanckLaw(waveLengthM, thermalK);

            Debug.Log($"ThermalK: {thermalK}K, wavelength: {waveLengthM}m, result: {B}");
        }

        [MenuItem("Thermal/PredictRadiance")]
        public static void PredictRadiance()
        {
            
        }

        /// <summary>
        /// Checks ThermalMath.Solar against reference values from a line-for-line port of the same
        /// algorithm. Exact agreement proves the transcription, not the algorithm - the reference
        /// was separately cross-checked against the geometric noon invariants, mirror symmetry,
        /// the published equation-of-time curve and Dutch equinox sunrise/sunset. NOAA's calculator
        /// (gml.noaa.gov/grad/solcalc) is the fully independent check.
        /// </summary>
        [MenuItem("Thermal/Solar Check")]
        public static void SolarCheck()
        {
            var cases = new (string name, double lat, double lon, int y, int mo, int d, int h, int mi,
                             double elev, double az, double decl, double eot)[]
            {
                ("N noon equinox",         52.0,  5.0, 2026,  3, 20, 11, 47, 37.97370363, 179.86187215,  -0.04686641, -7.43566349),
                ("N noon Jun solstice",    52.0,  5.0, 2026,  6, 21, 11, 42, 61.44682592, 180.08560572,  23.43806426, -1.82156184),
                ("N noon Dec solstice",    52.0,  5.0, 2026, 12, 21, 11, 38, 14.62376934, 179.98353831, -23.43726403,  1.93053781),
                ("S noon equinox",        -52.0,  5.0, 2026,  3, 20, 11, 47, 38.06736710,   0.13830451,  -0.04686641, -7.43566349),
                ("S noon Jun solstice",   -52.0,  5.0, 2026,  6, 21, 11, 42, 14.62296393, 359.95771275,  23.43806426, -1.82156184),
                ("S noon Dec solstice",   -52.0,  5.0, 2026, 12, 21, 11, 38, 61.44604339,   0.03332385, -23.43726403,  1.93053781),
                ("N noon equinox, lon 20", 52.0, 20.0, 2026,  3, 20, 10, 47, 37.95724100, 179.85799387,  -0.06333661, -7.44799637),
                ("N sunrise equinox",      52.0,  5.0, 2026,  3, 20,  5, 45,  0.06026084,  89.59554755,  -0.14624189, -7.51001899),
                ("N sunset equinox",       52.0,  5.0, 2026,  3, 20, 17, 51,  0.04646183, 270.74961701,   0.05304473, -7.36077149),
                ("N arbitrary",            52.0,  5.0, 2026,  9, 23, 14, 30, 25.91886021, 231.06479938,  -0.23254433,  7.64949203),
            };
            
            const double tol = 1e-6;
            
            double AngleDiff(double a, double b) => ((a - b + 540.0) % 360.0) - 180.0;

            var results = new SolarAngles[cases.Length];
            int failed = 0;

            for (int i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var utc = new DateTime(c.y, c.mo, c.d, c.h, c.mi, 0, DateTimeKind.Utc);
                var s = ThermalMath.Solar(c.lat, c.lon, utc);
                results[i] = s;

                double dE = s.elevation - c.elev;
                double dA = AngleDiff(s.azimuth, c.az);
                double dD = s.declination - c.decl;
                double dT = s.equationOfTime - c.eot;

                bool ok = Math.Abs(dE) < tol && Math.Abs(dA) < tol
                       && Math.Abs(dD) < tol && Math.Abs(dT) < tol;
                if (!ok) failed++;
                
                string line = string.Format(
                    "{0,-24} elev {1,12:F8} ({2:+0.0e+0;-0.0e+0})   az {3,12:F8} ({4:+0.0e+0;-0.0e+0})   " +
                    "decl {5:+0.0e+0;-0.0e+0}   eot {6:+0.0e+0;-0.0e+0}   {7}",
                    c.name, s.elevation, dE, s.azimuth, dA, dD, dT, ok ? "ok" : "FAIL");

                if (ok) Debug.Log("[Solar] " + line);
                else Debug.LogError("[Solar] " + line);
            }
            
            double mirrorA = Math.Abs(results[4].elevation - results[2].elevation);
            double mirrorB = Math.Abs(results[5].elevation - results[1].elevation);
            bool mirrorOk = mirrorA < 0.01 && mirrorB < 0.01;
            if (!mirrorOk) failed++;
            string mirror = string.Format(
                "mirror symmetry: |S Jun - N Dec| = {0:F5}   |S Dec - N Jun| = {1:F5}   {2}",
                mirrorA, mirrorB, mirrorOk ? "ok" : "FAIL");
            if (mirrorOk) Debug.Log("[Solar] " + mirror); else Debug.LogError("[Solar] " + mirror);

            if (failed == 0)
                Debug.Log(string.Format("[Solar] all {0} checks passed", cases.Length + 1));
            else
                Debug.LogError(string.Format("[Solar] {0} of {1} checks FAILED", failed, cases.Length + 1));
        }
        

        [MenuItem("Thermal/LutTest")]
        public static void CreateLUT()
        {
            int samples = 1024;
            double lower = 180, higher = 1200;

            Texture2D tex = new Texture2D(samples, 1, TextureFormat.RFloat, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            float[] data = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                double t = i / (double)(samples - 1);
                double T = lower + t * (higher - lower);

                data[i] = (float)ThermalMath.BandIntegration(T);
            }

            double maxError = 0;
            double maxErrorT = 0;
            double maxErrori = 0;

            for (int i = 0; i < samples - 2; i++)
            {
                double T = lower + (i + 0.5) / (samples - 1) * (higher - lower);
                float lut = 0.5f * (data[i] + data[i + 1]);
                double err = Math.Abs(lut - ThermalMath.BandIntegration(T)) / ThermalMath.BandIntegration(T);

                if (err > maxError)
                {
                    maxError = err;
                    maxErrorT = T;
                    maxErrori = i;
                }
            }

            Debug.Log($"Max error: {maxError}, produced by: {maxErrorT}K, produced by index: {maxErrori}");
            
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            
            Shader.SetGlobalTexture("_ThermalLut", tex);
            Shader.SetGlobalVector("_ThermalLutParams", new Vector4((float)lower, (float)higher, samples, 0));
            
            //Shader.SetGlobalFloat("_SkyTemp", skyTempK);
        }

        [MenuItem("Thermal/Convergence Check")]
        public static void ConvergenceCheckBandIntergration()
        {
            double temperatureK = 300;
            double lo = 8e-6, hi = 14e-6, n = 1000;
            List<int> iters = new List<int> { 10, 100, 1000, 10000, 100000 };
            foreach (var iter in iters)
            {
                var dl = (hi - lo) / iter;
                double integral = 0;
                for (int i = 0; i < iter; i++)
                {
                    integral += ThermalMath.ComputePlanckLaw(lo + (i + 0.5) * dl, temperatureK);
                }

                integral *= dl;
                Debug.Log(
                    $"iter={iter}  dl={dl:E3}  covers {lo * 1e6:F3} to {(lo + iter * dl) * 1e6:F3} um  -> {integral}");
            }
        }
    }
}
#endif
