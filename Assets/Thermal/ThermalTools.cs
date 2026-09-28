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

        // Reference values from pvlib 0.16.1, generated 2026-09-28: clearsky.ineichen, atmosphere
        // get_relative_airmass ('kastenyoung1989'), alt2pres, get_absolute_airmass, and
        // irradiance.get_extra_radiation ('spencer', solar constant 1366.1).
        [MenuItem("Thermal/Clear Sky Check")]
        public static void ClearSkyCheck()
        {
            var cases = new (string name, double elev, double alt, double tl, int doy,
                             double e0, double am, double dni, double dhi, double ghi)[]
            {
                ("summer high sun",  60.0,    0.0, 3.0, 172, 1321.6235925714136,  1.1539922333636758, 887.9780913035092,  99.89285770988272,  868.9044427827395),
                ("equinox mid",      30.0,    0.0, 3.0, 266, 1356.5996934592044,  1.9942928525292494, 783.5331175020957,  75.30780416111679,  467.07436291216476),
                ("low sun",          10.0,    0.0, 3.0, 266, 1356.5996934592044,  5.5860358798512,    410.4686651656092,  35.62462693181351,  106.90176262719899),
                ("near horizon",      2.0,    0.0, 3.0, 266, 1356.5996934592044, 19.433245107572006,  33.94705016002431,   3.119891894644013,   4.30462685976342),
                ("grazing",           0.5,    0.0, 3.0, 172, 1321.6235925714136, 31.349026292879188,   3.8722704212861268,  0.22911519648427184, 0.2629067017749285),
                ("clean air",        45.0,    0.0, 2.0, 172, 1321.6235925714136,  1.4125952520262743, 951.9812806764011,  54.005902967282736, 727.15832209622),
                ("hazy",             45.0,    0.0, 5.0, 172, 1321.6235925714136,  1.4125952520262743, 657.2939860323107, 152.39136612165692,  617.1684008782397),
                ("500 m site",       45.0,  500.0, 3.0, 172, 1321.6235925714136,  1.4125952520262743, 871.0902171074677,  83.62096195345566,  699.574761495408),
                ("1500 m, January",  45.0, 1500.0, 3.0,   1, 1413.981805,         1.4125952520262743, 984.2404762319774, 104.99871151959223,  800.9618265815004),
                ("near zenith",      89.0,    0.0, 3.0, 172, 1321.6235925714136,  0.9998592586926793, 912.9590302590623, 108.4648110660562,  1021.2847932427937),
            };

            const double tolW = 1e-6, tolAm = 1e-9;
            int failed = 0, checks = 0;

            foreach (var c in cases)
            {
                double e0 = ThermalMath.ExtraterrestrialIrradiance(c.doy);
                double am = ThermalMath.RelativeAirmass(c.elev);
                // pvlib's E0 goes in, so a wrong ExtraterrestrialIrradiance cannot pass itself off as a wrong DNI
                SolarIrradiance s = ThermalMath.ClearSkyIrradiance(c.elev, c.alt, c.tl, c.e0);
                double closure = s.globalHorizontal
                                 - (s.directNormal * Math.Sin(c.elev * Math.PI / 180.0) + s.diffuseHorizontal);

                bool ok = Math.Abs(e0 - c.e0) < tolW && Math.Abs(am - c.am) < tolAm
                       && Math.Abs(s.directNormal - c.dni) < tolW
                       && Math.Abs(s.diffuseHorizontal - c.dhi) < tolW
                       && Math.Abs(s.globalHorizontal - c.ghi) < tolW
                       && Math.Abs(closure) < 1e-9;
                checks++;
                if (!ok) failed++;

                string line = string.Format(
                    "{0,-16} DNI {1,11:F6} ({2:+0.0e+0;-0.0e+0})   DHI {3,10:F6} ({4:+0.0e+0;-0.0e+0})   " +
                    "GHI {5,11:F6} ({6:+0.0e+0;-0.0e+0})   E0 {7:+0.0e+0;-0.0e+0}   AM {8:+0.0e+0;-0.0e+0}   {9}",
                    c.name, s.directNormal, s.directNormal - c.dni, s.diffuseHorizontal, s.diffuseHorizontal - c.dhi,
                    s.globalHorizontal, s.globalHorizontal - c.ghi, e0 - c.e0, am - c.am, ok ? "ok" : "FAIL");
                if (ok) Debug.Log("[ClearSky] " + line);
                else Debug.LogError("[ClearSky] " + line);
            }

            // No sunlight at or below the horizon. At 0 the formula itself divides 0 by 0.
            foreach (double elev in new[] { 0.0, -5.0 })
            {
                SolarIrradiance s = ThermalMath.ClearSkyIrradiance(elev, 0.0, 3.0, 1361.0);
                bool ok = s.directNormal == 0.0 && s.diffuseHorizontal == 0.0 && s.globalHorizontal == 0.0;
                checks++;
                if (!ok) failed++;
                string line = $"elevation {elev,5:F1}   {s}   {(ok ? "ok" : "FAIL")}";
                if (ok) Debug.Log("[ClearSky] " + line);
                else Debug.LogError("[ClearSky] " + line);
            }

            if (failed == 0)
                Debug.Log($"[ClearSky] all {checks} checks passed");
            else
                Debug.LogError($"[ClearSky] {failed} of {checks} checks FAILED");
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
