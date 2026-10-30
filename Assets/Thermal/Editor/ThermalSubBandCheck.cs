using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Sub-band Check (the atmosphere's build, step 2): the radiance split into the atmosphere's four
    /// sub-bands, against values worked out in double precision by IMTNS/atmosphere/subband_check_ref.py.
    /// <list type="bullet">
    /// <item><c>ThermalMath.SubBandEdges</c> on BandIntegration's samples (every 0.06 µm from 8 µm), and equal to
    /// every ThermalAtmosphere asset's edges, so the radiance's sub-bands are the atmosphere's.</item>
    /// <item><c>ThermalMath.SubBandIntegration</c> at 11 temperatures from 180 to 1200 K, each sub-band to 1e-12
    /// relative, and the four summing to BandIntegration to 1e-13.</item>
    /// <item>With a ThermalEnvironment in the scene: its LUT, read per sub-band, against the one-channel LUT before
    /// step 2 read the same way, over 200-400 K every 0.01 K - the sum to 1e-6 relative (the reference's bound is
    /// 8e-8) - and the air's radiance it pushes, the same.</item>
    /// </list>
    /// The reference's harness caught a sub-band edge one sample off, the sub-bands in wavelength order, and the
    /// interval width left out; and the edges unsnapped (980 and 1080 cm⁻¹), which split the samples alike and only
    /// the edges' checks catch.
    /// </summary>
    static class ThermalSubBandCheck
    {
        const double k_ValueTolerance = 1e-12;     // relative, per sub-band
        const double k_SumTolerance = 1e-13;       // relative, the four against BandIntegration
        const double k_LutTolerance = 1e-6;        // relative, the four-channel LUT's sum against the one-channel LUT

        // From IMTNS/atmosphere/subband_check_ref.py --emit, 2026-10-28: the midpoint rule's samples per sub-band, W/m²/sr
        static readonly (double t, double[] values)[] k_SubBands =
        {
            (180.0, new double[] { 1.0487873338982265, 1.2532454547021097, 0.35584540137465975, 0.29894191429185735 }),
            (220.0, new double[] { 3.1578240791953194, 4.506943842149874, 1.580978037992394, 1.5996379266976664 }),
            (250.0, new double[] { 5.748143923276035, 9.033483627249877, 3.5434120316385265, 3.967395239907378 }),
            (273.15, new double[] { 8.36084355140566, 13.947690661313372, 5.857827286135583, 6.985815533691834 }),
            (290.0, new double[] { 10.591164249769683, 18.33949677254721, 8.03535993667926, 9.969393291846524 }),
            (300.0, new double[] { 12.041326948147912, 21.273163946447365, 9.5345306080162, 12.084728178532448 }),
            (320.0, new double[] { 15.212737107427017, 27.86623209411287, 13.008566372203235, 17.137302696946662 }),
            (350.0, new double[] { 20.605594037545917, 39.52413662256073, 19.42634496003846, 26.88871086998674 }),
            (500.0, new double[] { 56.49428902585284, 124.89226093739563, 71.79142576816379, 115.9415541757913 }),
            (800.0, new double[] { 152.58433722910485, 377.69905571688685, 245.29496556167845, 448.63789142970415 }),
            (1200.0, new double[] { 299.64607798359106, 784.6474962961597, 540.8130716322665, 1052.0132576148792 }),
        };

        [MenuItem("Thermal/Sub-band Check")]
        static void Run()
        {
            int failed = 0, checks = 0;
            void Report(bool ok, string line)
            {
                checks++;
                if (ok) Debug.Log("[Sub-band] " + line);
                else { failed++; Debug.LogError("[Sub-band] " + line); }
            }

            // The edges: on BandIntegration's samples, and the atmosphere's
            double[] edges = ThermalMath.SubBandEdges;
            bool onSamples = edges.Length == 5;
            for (int k = 0; k < edges.Length && onSamples; k++)
            {
                double j = (edges[k] - 8e-6) / 0.06e-6;
                onSamples = Math.Abs(j - Math.Round(j)) < 1e-9;
            }
            Report(onSamples, $"edges {Microns(edges)} µm on BandIntegration's samples   {(onSamples ? "ok" : "FAIL")}");

            string[] guids = AssetDatabase.FindAssets("t:ThermalAtmosphere");
            if (guids.Length == 0)
                Report(false, "no ThermalAtmosphere asset to compare the edges with   FAIL");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var atmosphere = AssetDatabase.LoadAssetAtPath<ThermalAtmosphere>(path);
                bool ok = atmosphere != null && atmosphere.IsValid && atmosphere.BandEdges.Count == edges.Length;
                var theirs = new double[edges.Length];
                for (int k = 0; ok && k < edges.Length; k++)
                {
                    theirs[k] = 1e-2 / atmosphere.BandEdges[k];         // cm⁻¹ to m; stored as float, so 1e-6
                    ok = Math.Abs(theirs[k] - edges[k]) <= 1e-6 * edges[k];
                }
                Report(ok, $"edges of {path}: {Microns(theirs)} µm   {(ok ? "ok" : "FAIL")}");
            }

            // The values, and their sum
            var got = new double[4];
            double worstValue = 0, worstSum = 0;
            foreach (var (t, values) in k_SubBands)
            {
                ThermalMath.SubBandIntegration(t, got);
                double off = 0, sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    double d = Math.Abs(got[k] - values[k]) / values[k];
                    off = double.IsNaN(d) ? double.PositiveInfinity : Math.Max(off, d);
                    sum += got[k];
                }
                double band = ThermalMath.BandIntegration(t), sumOff = Math.Abs(sum - band) / band;
                worstValue = Math.Max(worstValue, off);
                worstSum = Math.Max(worstSum, double.IsNaN(sumOff) ? double.PositiveInfinity : sumOff);
                bool ok = off <= k_ValueTolerance && sumOff <= k_SumTolerance;
                Report(ok, string.Format(CultureInfo.InvariantCulture,
                    "{0,7:F2} K  {1:E6}  {2:E6}  {3:E6}  {4:E6}  worst {5:0.0e+0} relative, sum - band {6:0.0e+0}   {7}",
                    t, got[0], got[1], got[2], got[3], off, sumOff, ok ? "ok" : "FAIL"));
            }

            string lut = RunLut(Report);

            string summary = string.Format(CultureInfo.InvariantCulture,
                "worst sub-band {0:0.0e+0} relative; worst sum against the band {1:0.0e+0}; {2}", worstValue, worstSum, lut);
            if (failed == 0)
                Debug.Log($"[Sub-band] all {checks} checks passed: {summary}");
            else
                Debug.LogError($"[Sub-band] {failed} of {checks} checks FAILED: {summary}");
        }

        // The live environment's LUT, read per sub-band and summed, against the one-channel LUT it replaced (the same
        // temperatures, (float)BandIntegration at each) read the same way.
        static string RunLut(Action<bool, string> report)
        {
            var environment = UnityEngine.Object.FindAnyObjectByType<ThermalEnvironment>();
            if (environment == null || !environment.isActiveAndEnabled)
            {
                Debug.LogWarning("[Sub-band] no enabled ThermalEnvironment in the scene: its LUT is not checked");
                return "the LUT not checked";
            }
            Vector4 p = Shader.GetGlobalVector("_ThermalLutParams");
            double low = p.x, high = p.y;
            int n = (int)p.z;
            float Entry(int i) => (float)ThermalMath.BandIntegration(low + i / (double)(n - 1) * (high - low));
            double Before(double t)
            {
                double x = Math.Max(0.0, Math.Min(1.0, (t - low) / (high - low))) * (n - 1);
                int i = Math.Min((int)Math.Floor(x), n - 2);
                float a = Entry(i), b = Entry(i + 1);
                return a + (b - a) * (x - i);
            }

            var four = new double[4];
            double worst = 0, worstAt = 0;
            for (int s = 0; s <= 20000; s++)
            {
                double t = 200 + 0.01 * s;
                environment.SubBandRadiance(t, four);
                double before = Before(t), off = Math.Abs(four[0] + four[1] + four[2] + four[3] - before) / before;
                if (!(off <= worst))
                {
                    worst = double.IsNaN(off) ? double.PositiveInfinity : off;
                    worstAt = t;
                }
            }
            bool ok = worst <= k_LutTolerance;
            report(ok, string.Format(CultureInfo.InvariantCulture,
                "the LUT per sub-band, summed, against the one-channel LUT, 200-400 K: worst {0:0.0e+0} relative at " +
                "{1:F2} K (limit {2:0e+0})   {3}", worst, worstAt, k_LutTolerance, ok ? "ok" : "FAIL"));

            double[] air = environment.AirSubBandRadiance;
            double airBefore = Before(environment.AirTemperature);
            double airOff = Math.Abs(air[0] + air[1] + air[2] + air[3] - airBefore) / airBefore;
            bool airOk = airOff <= k_LutTolerance;
            report(airOk, string.Format(CultureInfo.InvariantCulture,
                "the air at {0:F2} K: {1:F6} + {2:F6} + {3:F6} + {4:F6} W/m²/sr against {5:F6}, {6:0.0e+0} relative   {7}",
                environment.AirTemperature, air[0], air[1], air[2], air[3], airBefore, airOff, airOk ? "ok" : "FAIL"));

            return string.Format(CultureInfo.InvariantCulture, "the LUT's sum within {0:0.0e+0} of the one-channel LUT",
                                 worst);
        }

        static string Microns(double[] edges)
        {
            var s = new string[edges.Length];
            for (int k = 0; k < edges.Length; k++)
                s[k] = (edges[k] * 1e6).ToString("0.####", CultureInfo.InvariantCulture);
            return string.Join(" / ", s);
        }
    }
}
