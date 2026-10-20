using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Convection Check (item 6, wind): ThermalPhysics.hlsl's convection and balance, run on the GPU through
    /// ThermalConvectionCheck.compute, against values worked out in double precision by IMTNS/convection_check_ref.py.
    /// <list type="bullet">
    /// <item>h itself: <c>ConvectiveCoefficient(windA, windB, windSpeed, dT)</c> must give
    /// √((0.84·|dT|^(1/3))² + (a·U^b)²) to 1e-5, for objects (2.38, 0.89) and land (5.08 and 37.2, b 1), in still air
    /// and in a gale, above and below the air, and at dT = 0.</item>
    /// <item>The balance: <c>SurfaceTemperature(MakeBalance(...))</c> must land within 0.004 K (a tenth of NETD) of
    /// the exact root, with h taken at that temperature. That holds the solver to the new h, not just the formula:
    /// the old seed with two Newton steps misses a calm, sunlit zinc roof by 25 K.</item>
    /// <item>Wind sweeps, 0-15 m/s in steps of 0.05, on a sunlit roof and on grass at night: no NaN, the
    /// temperature moves one way only, and no step larger than the exact answer's own plus 0.008 K. A step there is a
    /// jump in the image whenever the wind changes.</item>
    /// </list>
    /// The reference's harness ran these criteria against a correct float32 implementation and eleven broken ones
    /// (pow of a negative dT, a fixed exponent, no still-air term, a sum for the root of squares, a wrong constant,
    /// a Newton step without dh/dT, 0/0 at equilibrium, one step, h frozen at the seed, dT reversed, and - found in
    /// review on 2026-10-20 - a derivative without h itself) and caught every one. Two of them only one case
    /// catches, so keep these: "zinc roof, calm day" (dh/dT) and "sky at air, calm" (0/0).
    /// </summary>
    static class ThermalConvectionCheck
    {
        const string k_Shader = "Assets/Thermal/Editor/ThermalConvectionCheck.compute";
        const double k_TemperatureTolerance = 0.004;          // K
        const double k_CoefficientRelative = 1e-5, k_CoefficientAbsolute = 1e-6;
        const float k_SweepStep = 0.05f;
        const int k_SweepPoints = 301;                        // 0 to 15 m/s

        [StructLayout(LayoutKind.Sequential)]
        struct CoefficientCase
        {
            public float windA, windB, windSpeed, dT;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BalanceCase
        {
            public float nx, ny, nz, alpha, eps, skyView, sunVis, skyDiffuseBroadband, windA, windB;
            public float airT, windSpeed, dni, dhi, ghi, albedo, sx, sy, sz;
        }

        // From IMTNS/convection_check_ref.py --emit, 2026-10-19. Inputs from Captures/thermal_replica at the test
        // scene's site on 21 June 2026, 11:42Z (day) and 23:42Z (night); U is the 10 m wind.
        static readonly (string name, float nx, float ny, float nz, float alpha, float eps, float skyView, float sunVis,
                         float skyDiffuseBroadband, float windA, float windB, float airT, float windSpeed, float dni,
                         float dhi, float ghi, float albedo, float sx, float sy, float sz, double t)[] k_Balance =
        {
            ("dark roof, day", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 339.21185676504854),
            ("dark roof, night", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.797141f, 2.38f, 0.89f, 286.4369f, 2.2f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 278.92135318520354),
            ("paint wall, day", 0.0f, 0.0f, -1.0f, 0.25f, 0.92f, 0.5f, 1.0f, 0.446644f, 2.38f, 0.89f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 303.00225140506893),
            ("paint wall, night", 0.0f, 0.0f, -1.0f, 0.25f, 0.92f, 0.5f, 1.0f, 0.447606f, 2.38f, 0.89f, 286.4369f, 2.2f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 284.5532032153001),
            ("zinc roof, day", 0.0f, 1.0f, 0.0f, 0.55f, 0.2f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 340.39950069857025),
            ("zinc roof, night", 0.0f, 1.0f, 0.0f, 0.55f, 0.2f, 1.0f, 1.0f, 0.797141f, 2.38f, 0.89f, 286.4369f, 2.2f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 283.85117813962324),
            ("grass, day", 0.0f, 1.0f, 0.0f, 0.75f, 0.97f, 1.0f, 1.0f, 0.794038f, 5.08f, 1.0f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 315.96985247807334),
            ("grass, night", 0.0f, 1.0f, 0.0f, 0.75f, 0.97f, 1.0f, 1.0f, 0.797141f, 5.08f, 1.0f, 286.4369f, 2.2f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 281.83182787029807),
            ("paving, day", 0.0f, 1.0f, 0.0f, 0.9f, 0.95f, 1.0f, 1.0f, 0.794038f, 3.0f, 1.0f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 331.87643359474293),
            ("forest crown, day", 0.0f, 1.0f, 0.0f, 0.85f, 0.98f, 1.0f, 1.0f, 0.794038f, 37.2f, 1.0f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 298.6640165737298),
            ("forest crown, night", 0.0f, 1.0f, 0.0f, 0.85f, 0.98f, 1.0f, 1.0f, 0.797141f, 37.2f, 1.0f, 286.4369f, 2.2f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 285.5651045343312),
            ("shaded wall, day", 0.0f, 0.0f, -1.0f, 0.25f, 0.92f, 0.5f, 0.0f, 0.446644f, 2.38f, 0.89f, 294.3654f, 4.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 295.37974836988576),
            ("dark roof, calm day", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 0.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 357.9841684823257),
            ("zinc roof, calm day", 0.0f, 1.0f, 0.0f, 0.55f, 0.2f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 0.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 379.9601277810973),
            ("dark roof, calm night", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.797141f, 2.38f, 0.89f, 286.4369f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 275.2829755761852),
            ("grass, calm night", 0.0f, 1.0f, 0.0f, 0.75f, 0.97f, 1.0f, 1.0f, 0.797141f, 5.08f, 1.0f, 286.4369f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 275.31309668931306),
            ("dark roof, 0.01 m/s day", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 0.01f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 357.983159620435),
            ("dark roof, gale day", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 1.0f, 1.0f, 0.794038f, 2.38f, 0.89f, 294.3654f, 15.0f, 890.5695f, 100.699f, 882.952f, 0.2f, -0.000714f, 0.878374f, -0.477974f, 315.8885242526644),
            ("grass, gale night", 0.0f, 1.0f, 0.0f, 0.75f, 0.97f, 1.0f, 1.0f, 0.797141f, 5.08f, 1.0f, 286.4369f, 15.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 285.5136032975247),
            // the sky at air temperature and no sun: T = T_air exactly, so dT = 0 inside the solver
            ("sky at air, calm", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 0.3f, 1.0f, 0.3f, 2.38f, 0.89f, 286.4369f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 286.4368896484375),
            ("sky at air, windy", 0.0f, 1.0f, 0.0f, 0.9f, 0.98f, 0.3f, 1.0f, 0.3f, 2.38f, 0.89f, 286.4369f, 4.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.000279f, -0.251074f, 0.967968f, 286.4368896484375),
        };

        static readonly (float windA, float windB, float windSpeed, float dT, double h)[] k_Coefficient =
        {
            (2.38f, 0.89f, 0.0f, -15.0f, 2.0716181424375946),
            (2.38f, 0.89f, 0.0f, -1.0f, 0.84),
            (2.38f, 0.89f, 0.0f, 0.0f, 0.0),
            (2.38f, 0.89f, 0.0f, 0.001f, 0.08400000132992863),
            (2.38f, 0.89f, 0.0f, 1.0f, 0.84),
            (2.38f, 0.89f, 0.0f, 8.0f, 1.68),
            (2.38f, 0.89f, 0.0f, 45.0f, 2.9877903757716524),
            (2.38f, 0.89f, 0.01f, -15.0f, 2.071994650976885),
            (2.38f, 0.89f, 0.01f, -1.0f, 0.8409281215420457),
            (2.38f, 0.89f, 0.01f, 0.0f, 0.03949817211256164),
            (2.38f, 0.89f, 0.01f, 0.001f, 0.09282298111815604),
            (2.38f, 0.89f, 0.01f, 1.0f, 0.8409281215420457),
            (2.38f, 0.89f, 0.01f, 8.0f, 1.6804642529968417),
            (2.38f, 0.89f, 0.01f, 45.0f, 2.9880514445293516),
            (2.38f, 0.89f, 1.0f, -15.0f, 3.155313339878525),
            (2.38f, 0.89f, 1.0f, -1.0f, 2.5238860007414723),
            (2.38f, 0.89f, 1.0f, 0.0f, 2.380000114440918),
            (2.38f, 0.89f, 1.0f, 0.001f, 2.381482006012687),
            (2.38f, 0.89f, 1.0f, 1.0f, 2.5238860007414723),
            (2.38f, 0.89f, 1.0f, 8.0f, 2.9132113800304267),
            (2.38f, 0.89f, 1.0f, 45.0f, 3.819854954614441),
            (2.38f, 0.89f, 2.2f, -15.0f, 5.228896427575682),
            (2.38f, 0.89f, 2.2f, -1.0f, 4.8739466679721835),
            (2.38f, 0.89f, 2.2f, 0.0f, 4.801016155173522),
            (2.38f, 0.89f, 2.2f, 0.001f, 4.801750943401852),
            (2.38f, 0.89f, 2.2f, 1.0f, 4.8739466679721835),
            (2.38f, 0.89f, 2.2f, 8.0f, 5.086467941729029),
            (2.38f, 0.89f, 2.2f, 45.0f, 5.6547897796284925),
            (2.38f, 0.89f, 4.0f, -15.0f, 8.43198730658996),
            (2.38f, 0.89f, 4.0f, -1.0f, 8.216593467515453),
            (2.38f, 0.89f, 4.0f, 0.0f, 8.17354318581713),
            (2.38f, 0.89f, 4.0f, 0.001f, 8.173974810986454),
            (2.38f, 0.89f, 4.0f, 1.0f, 8.216593467515453),
            (2.38f, 0.89f, 4.0f, 8.0f, 8.344411795352482),
            (2.38f, 0.89f, 4.0f, 45.0f, 8.702511105420742),
            (2.38f, 0.89f, 15.0f, -15.0f, 26.584032506795733),
            (2.38f, 0.89f, 15.0f, -1.0f, 26.51650019505394),
            (2.38f, 0.89f, 15.0f, 0.0f, 26.503191932186123),
            (2.38f, 0.89f, 15.0f, 0.001f, 26.50332504789765),
            (2.38f, 0.89f, 15.0f, 1.0f, 26.51650019505394),
            (2.38f, 0.89f, 15.0f, 8.0f, 26.556384968483485),
            (2.38f, 0.89f, 15.0f, 45.0f, 26.67107185554884),
            (5.08f, 1.0f, 0.0f, -8.0f, 1.68),
            (5.08f, 1.0f, 0.0f, 0.0f, 0.0),
            (5.08f, 1.0f, 0.0f, 20.0f, 2.280110797939721),
            (5.08f, 1.0f, 2.2f, -8.0f, 11.301565274893967),
            (5.08f, 1.0f, 2.2f, 0.0f, 11.176000074386593),
            (5.08f, 1.0f, 2.2f, 20.0f, 11.406221237270932),
            (5.08f, 1.0f, 4.0f, -8.0f, 20.389330239065146),
            (5.08f, 1.0f, 4.0f, 0.0f, 20.31999969482422),
            (5.08f, 1.0f, 4.0f, 20.0f, 20.447525347790563),
            (37.2f, 1.0f, 2.2f, -1.0f, 81.84431418902996),
            (37.2f, 1.0f, 2.2f, 4.0f, 81.85086539345971),
            (37.2f, 1.0f, 4.0f, -1.0f, 148.80237400056203),
            (37.2f, 1.0f, 4.0f, 4.0f, 148.8059773960332),
        };

        // Sweeps, from the cases above: the exact answer's largest change between neighbouring 0.05 m/s steps.
        static readonly (int balanceCase, double largestStep)[] k_Sweeps =
        {
            (0, 0.2824745758437075),     // dark roof, day: above the air, so the wind only cools it
            (7, 0.24202976827848488),    // grass, night: below the air, so the wind only warms it
        };

        [MenuItem("Thermal/Convection Check")]
        static void Run()
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_Shader);
            if (shader == null)
            {
                Debug.LogError($"[Convection] {k_Shader} is missing");
                return;
            }

            // A contract the physics does not meet yet shows as compile errors, with ThermalPhysics.hlsl's lines.
            int errors = 0;
            foreach (ShaderMessage m in ShaderUtil.GetComputeShaderMessages(shader))
            {
                if (m.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    continue;
                errors++;
                Debug.LogError($"[Convection] does not compile: {m.message} ({m.file}:{m.line})");
            }
            if (errors > 0)
            {
                Debug.LogError("[Convection] the check is written against ThermalEnvironment.windSpeed, " +
                               "ThermalSurface.windA and .windB, and float ConvectiveCoefficient(float windA, " +
                               "float windB, float windSpeed, float dT): rename them here or there");
                return;
            }

            // The plumbing first, so a layout fault never passes for a physics fault.
            var coefficientCases = new CoefficientCase[k_Coefficient.Length];
            for (int i = 0; i < k_Coefficient.Length; i++)
            {
                var c = k_Coefficient[i];
                coefficientCases[i] = new CoefficientCase { windA = c.windA, windB = c.windB, windSpeed = c.windSpeed, dT = c.dT };
            }
            var balanceCases = new BalanceCase[k_Balance.Length];
            for (int i = 0; i < k_Balance.Length; i++)
                balanceCases[i] = ToCase(i, k_Balance[i].windSpeed);
            int differ = Echo(shader, coefficientCases, balanceCases);
            if (differ > 0)
            {
                Debug.LogError($"[Convection] the check's own buffers are wrong: {differ} fields came back changed " +
                               "from the GPU. The C# and HLSL case structs disagree; nothing below would mean anything");
                return;
            }
            Debug.Log($"[Convection] buffers: {coefficientCases.Length * 4 + balanceCases.Length * 19} fields of " +
                      $"{coefficientCases.Length + balanceCases.Length} cases read back exactly");

            int failed = 0, checks = 0;
            void Report(bool ok, string line)
            {
                checks++;
                if (ok) Debug.Log("[Convection] " + line);
                else { failed++; Debug.LogError("[Convection] " + line); }
            }

            // h itself
            float[] h = RunCoefficient(shader, coefficientCases);
            double worstRelative = 0;
            for (int i = 0; i < k_Coefficient.Length; i++)
            {
                var c = k_Coefficient[i];
                double off = h[i] - c.h;
                bool ok = Math.Abs(off) <= Math.Max(k_CoefficientAbsolute, k_CoefficientRelative * c.h);
                if (c.h > 0)
                    worstRelative = Math.Max(worstRelative, double.IsNaN(off) ? double.PositiveInfinity : Math.Abs(off) / c.h);
                Report(ok, string.Format(CultureInfo.InvariantCulture,
                    "h  a {0,5:F2} b {1:F2} U {2,5:F2} m/s dT {3,7:F3} K   {4,11:F6} ({5:+0.0e+0;-0.0e+0})   {6}",
                    c.windA, c.windB, c.windSpeed, c.dT, h[i], off, ok ? "ok" : "FAIL"));
            }

            // The balance
            Vector2[] balance = RunBalance(shader, balanceCases);
            double worstT = 0;
            for (int i = 0; i < k_Balance.Length; i++)
            {
                var c = k_Balance[i];
                double off = balance[i].x - c.t;
                bool ok = Math.Abs(off) <= k_TemperatureTolerance;
                worstT = Math.Max(worstT, double.IsNaN(off) ? double.PositiveInfinity : Math.Abs(off));
                Report(ok, string.Format(CultureInfo.InvariantCulture,
                    "T  {0,-24} U {1,5:F2} m/s   {2,10:F4} K ({3:+0.0e+0;-0.0e+0}), h there {4,7:F3}   {5}",
                    c.name, c.windSpeed, balance[i].x, off, balance[i].y, ok ? "ok" : "FAIL"));
            }

            // Wind sweeps
            foreach (var (index, largest) in k_Sweeps)
            {
                var sweep = new BalanceCase[k_SweepPoints];
                for (int i = 0; i < k_SweepPoints; i++)
                    sweep[i] = ToCase(index, i * k_SweepStep);
                Vector2[] t = RunBalance(shader, sweep);
                bool above = k_Balance[index].t > k_Balance[index].airT;
                int nan = 0, wrongWay = 0;
                double biggest = 0;
                for (int i = 0; i < k_SweepPoints; i++)
                {
                    if (float.IsNaN(t[i].x) || float.IsInfinity(t[i].x))
                        nan++;
                    if (i == 0)
                        continue;
                    double step = t[i].x - t[i - 1].x;
                    if (above ? step > 1e-4 : step < -1e-4)
                        wrongWay++;
                    biggest = Math.Max(biggest, Math.Abs(step));
                }
                double limit = largest + 2 * k_TemperatureTolerance;
                bool ok = nan == 0 && wrongWay == 0 && biggest <= limit;
                Report(ok, string.Format(CultureInfo.InvariantCulture,
                    "sweep {0,-20} U 0-15 m/s: {1} NaN, {2} steps the wrong way, largest step {3:F4} K " +
                    "(exact {4:F4}, limit {5:F4})   {6}",
                    k_Balance[index].name, nan, wrongWay, biggest, largest, limit, ok ? "ok" : "FAIL"));
            }

            string summary = string.Format(CultureInfo.InvariantCulture,
                "worst h {0:0.0e+0} relative, worst T {1:0.0e+0} K", worstRelative, worstT);
            if (failed == 0)
                Debug.Log($"[Convection] all {checks} checks passed: {summary}");
            else
                Debug.LogError($"[Convection] {failed} of {checks} checks FAILED: {summary}");
        }

        static BalanceCase ToCase(int index, float windSpeed)
        {
            var c = k_Balance[index];
            return new BalanceCase
            {
                nx = c.nx, ny = c.ny, nz = c.nz, alpha = c.alpha, eps = c.eps, skyView = c.skyView, sunVis = c.sunVis,
                skyDiffuseBroadband = c.skyDiffuseBroadband, windA = c.windA, windB = c.windB, airT = c.airT,
                windSpeed = windSpeed, dni = c.dni, dhi = c.dhi, ghi = c.ghi, albedo = c.albedo,
                sx = c.sx, sy = c.sy, sz = c.sz,
            };
        }

        // Every field, compared as bits: the GPU only copied them.
        static int Echo(ComputeShader shader, CoefficientCase[] coefficientCases, BalanceCase[] balanceCases)
        {
            int kernel = shader.FindKernel("Echo");
            int count = coefficientCases.Length * 4 + balanceCases.Length * 19;
            using var coefficients = new ComputeBuffer(coefficientCases.Length, Marshal.SizeOf<CoefficientCase>());
            using var balances = new ComputeBuffer(balanceCases.Length, Marshal.SizeOf<BalanceCase>());
            using var echo = new ComputeBuffer(count, sizeof(float));
            coefficients.SetData(coefficientCases);
            balances.SetData(balanceCases);
            echo.SetData(Filled(count, float.NaN));
            shader.SetBuffer(kernel, "_CoefficientCases", coefficients);
            shader.SetBuffer(kernel, "_BalanceCases", balances);
            shader.SetBuffer(kernel, "_Echo", echo);
            shader.SetInt("_CoefficientCount", coefficientCases.Length);
            shader.SetInt("_Count", balanceCases.Length);
            shader.Dispatch(kernel, (Math.Max(coefficientCases.Length, balanceCases.Length) + 63) / 64, 1, 1);
            var got = new float[count];
            echo.GetData(got);

            var sent = new float[count];
            int k = 0;
            foreach (var c in coefficientCases)
                foreach (float v in new[] { c.windA, c.windB, c.windSpeed, c.dT })
                    sent[k++] = v;
            foreach (var c in balanceCases)
                foreach (float v in new[] { c.nx, c.ny, c.nz, c.alpha, c.eps, c.skyView, c.sunVis, c.skyDiffuseBroadband,
                                            c.windA, c.windB, c.airT, c.windSpeed, c.dni, c.dhi, c.ghi, c.albedo,
                                            c.sx, c.sy, c.sz })
                    sent[k++] = v;

            int differ = 0;
            for (int i = 0; i < count; i++)
                if (BitConverter.SingleToInt32Bits(got[i]) != BitConverter.SingleToInt32Bits(sent[i]))
                    differ++;
            return differ;
        }

        static float[] RunCoefficient(ComputeShader shader, CoefficientCase[] cases)
        {
            int kernel = shader.FindKernel("Coefficient");
            using var input = new ComputeBuffer(cases.Length, Marshal.SizeOf<CoefficientCase>());
            using var output = new ComputeBuffer(cases.Length, sizeof(float));
            input.SetData(cases);
            output.SetData(Filled(cases.Length, float.NaN));      // a kernel that never wrote shows as NaN
            shader.SetBuffer(kernel, "_CoefficientCases", input);
            shader.SetBuffer(kernel, "_CoefficientOut", output);
            shader.SetInt("_Count", cases.Length);
            shader.Dispatch(kernel, (cases.Length + 63) / 64, 1, 1);
            var result = new float[cases.Length];
            output.GetData(result);
            return result;
        }

        static Vector2[] RunBalance(ComputeShader shader, BalanceCase[] cases)
        {
            int kernel = shader.FindKernel("Balance");
            using var input = new ComputeBuffer(cases.Length, Marshal.SizeOf<BalanceCase>());
            using var output = new ComputeBuffer(cases.Length, 2 * sizeof(float));
            input.SetData(cases);
            var unwritten = new Vector2[cases.Length];
            for (int i = 0; i < cases.Length; i++)
                unwritten[i] = new Vector2(float.NaN, float.NaN);
            output.SetData(unwritten);
            shader.SetBuffer(kernel, "_BalanceCases", input);
            shader.SetBuffer(kernel, "_BalanceOut", output);
            shader.SetInt("_Count", cases.Length);
            shader.Dispatch(kernel, (cases.Length + 63) / 64, 1, 1);
            var result = new Vector2[cases.Length];
            output.GetData(result);
            return result;
        }

        static float[] Filled(int length, float value)
        {
            var a = new float[length];
            for (int i = 0; i < length; i++)
                a[i] = value;
            return a;
        }
    }
}
