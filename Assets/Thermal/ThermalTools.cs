// Editor-only. UnityEditor does not exist in player assemblies, and this file lives outside an
// Editor/ folder, so without the guard the first player build fails on the using line below.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Reference values from IMTNS/air_check_ref.py, generated 2026-09-28. Sunrise and sunset: NOAA's
        // formula on solar_ref's line-for-line port of Solar, declination and equation of time at 12:00 UTC;
        // pvlib's SPA agrees within 37 s at the equinox, where the declination moves fastest, and 7 s
        // elsewhere. Air: Parton-Logan with the rescaled night, 285-295 K, a 2.19, b 2.58, c 0.22, the cycle
        // running from one day's minimum to the next. Humidity: Tetens.
        [MenuItem("Thermal/Air Temperature Check")]
        public static void AirTemperatureCheck()
        {
            const double tMin = 285.0, tMax = 295.0, curveA = 2.19, curveB = 2.58, curveC = 0.22;
            int failed = 0, checks = 0, skipped = 0;

            void Report(bool ok, string line)
            {
                checks++;
                if (ok) Debug.Log("[Air] " + line);
                else { failed++; Debug.LogError("[Air] " + line); }
            }

            // A function not written yet skips its section instead of ending the check.
            void Section(string function, Action run)
            {
                try { run(); }
                catch (NotImplementedException e)
                {
                    skipped++;
                    Debug.LogWarning($"[Air] {function} skipped, not written yet ({e.Message})");
                }
            }

            double Air(DateTime utc, double lat, double lon) =>
                ThermalMath.AirTemperature(utc, lat, lon, tMin, tMax, curveA, curveB, curveC);

            // Seconds after 00:00 UTC of the date: below 0, or past a day's 86400, when the event falls on
            // the neighbouring UTC date. Each date comes with a time of day: the contract takes the date of
            // any instant, and the plumbing passes the current one.
            Section("SunriseSunset", () =>
            {
                var cases = new (string name, double lat, double lon, int y, int mo, int d, int h, int mi,
                                 double rise, double set)[]
                {
                    ("N52 equinox",            52.0,   5.0, 2026,  3, 20,  0,  0,  20534.540784518533, 64357.41812554024),
                    ("N52 June solstice",      52.0,   5.0, 2026,  6, 21, 15, 37,  11990.73892442197,  72228.17467533957),
                    ("N52 December solstice",  52.0,   5.0, 2026, 12, 21, 23, 59,  27951.873664875104, 55817.37019846809),
                    ("S52 June solstice",     -52.0,   5.0, 2026,  6, 21,  6,  0,  28177.025588809727, 56041.88801095181),
                    ("equator equinox",         0.0,   0.0, 2026,  3, 20, 12,  0,  21846.059397941284, 65445.899512117496),
                    ("far west, June",         40.0, -75.0, 2026,  6, 21,  8,  0,  34283.04178477129,  88335.87181499024),
                    ("far east, June",        -34.0, 150.0, 2026,  6, 21, 20,  0, -10483.899251427712, 25102.81285118925),
                };
                const double tolS = 0.01;   // DateTime arithmetic rounds to the millisecond

                foreach (var c in cases)
                {
                    var day = new DateTime(c.y, c.mo, c.d, 0, 0, 0, DateTimeKind.Utc);
                    var instant = new DateTime(c.y, c.mo, c.d, c.h, c.mi, 0, DateTimeKind.Utc);
                    SunTimes s = ThermalMath.SunriseSunset(instant, c.lat, c.lon);
                    double dRise = (s.sunrise - day).TotalSeconds - c.rise;
                    double dSet = (s.sunset - day).TotalSeconds - c.set;
                    bool utcKind = s.sunrise.Kind == DateTimeKind.Utc && s.sunset.Kind == DateTimeKind.Utc;
                    bool ok = Math.Abs(dRise) < tolS && Math.Abs(dSet) < tolS && utcKind;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "{0,-24} sunrise {1:yyyy-MM-dd HH:mm:ss.fff} ({2:+0.0e+0;-0.0e+0} s)   " +
                        "sunset {3:yyyy-MM-dd HH:mm:ss.fff} ({4:+0.0e+0;-0.0e+0} s){5}   {6}",
                        c.name, s.sunrise, dRise, s.sunset, dSet,
                        utcKind ? "" : "   not DateTimeKind.Utc", ok ? "ok" : "FAIL"));
                }
            });

            Section("RelativeHumidity", () =>
            {
                var cases = new (double air, double dew, double rh)[]
                {
                    (295.0, 286.8908, 0.6000000775599315),
                    (295.0, 284.0,    0.4960895670406168),
                    (285.0, 284.0,    0.9358641558425497),
                    (285.0, 286.0,    1.0),                  // dew point above the air: capped
                    (290.0, 290.0,    1.0),
                };

                foreach (var c in cases)
                {
                    double rh = ThermalMath.RelativeHumidity(c.air, c.dew);
                    bool ok = Math.Abs(rh - c.rh) < 1e-9;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "humidity at {0:F1} K, dew point {1:F4} K   {2:F10} ({3:+0.0e+0;-0.0e+0})   {4}",
                        c.air, c.dew, rh, rh - c.rh, ok ? "ok" : "FAIL"));
                }
            });

            // N52 E5 on 21 June 2026: sunrise 03:19:50.7, minimum 03:33:02.7, peak 14:06:25.5 and sunset
            // 20:03:48.2 UTC, each case to the whole second. Then two sites whose day straddles a UTC date.
            Section("AirTemperature", () =>
            {
                var cases = new (string name, double lat, double lon, int y, int mo, int d, int h, int mi, int s,
                                 double air)[]
                {
                    ("N52 before dawn",          52.0,   5.0, 2026,  6, 21,  2,  0,  0, 285.3659229590008),
                    ("N52 at the minimum",       52.0,   5.0, 2026,  6, 21,  3, 33,  3, 285.00010791239526),
                    ("N52 mid-morning",          52.0,   5.0, 2026,  6, 21,  8,  0,  0, 291.14738273809036),
                    ("N52 at the peak",          52.0,   5.0, 2026,  6, 21, 14,  6, 25, 294.9999999982175),
                    ("N52 at sunset",            52.0,   5.0, 2026,  6, 21, 20,  3, 48, 291.32281669408667),
                    ("N52 UTC midnight",         52.0,   5.0, 2026,  6, 22,  0,  0,  0, 286.24499940802843),
                    ("N52 December noon",        52.0,   5.0, 2026, 12, 21, 12,  0,  0, 293.62826503793355),
                    ("far west, before sunset",  40.0, -75.0, 2026,  6, 22,  0, 15,  0, 292.1156800824953),
                    ("far east, morning",       -34.0, 150.0, 2026,  6, 20, 23,  0,  0, 288.6410173796965),
                };

                foreach (var c in cases)
                {
                    var utc = new DateTime(c.y, c.mo, c.d, c.h, c.mi, c.s, DateTimeKind.Utc);
                    double air = Air(utc, c.lat, c.lon);
                    bool ok = Math.Abs(air - c.air) < 1e-6;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "{0,-24} {1:yyyy-MM-dd HH:mm:ss}   {2,14:F9} K ({3:+0.0e+0;-0.0e+0})   {4}",
                        c.name, utc, air, air - c.air, ok ? "ok" : "FAIL"));
                }

                // Minute by minute for 48 hours: inside the day's extremes, and no step anywhere. The
                // reference's largest one-minute change is 0.043 K (N52 December). The unscaled night leaves
                // 0.44-0.68 K at dawn at these sites, and a cycle taken from the wrong day leaves kelvins.
                var sweeps = new (string name, double lat, double lon, int y, int mo, int d)[]
                {
                    ("N52 June",       52.0,   5.0, 2026,  6, 20),
                    ("N52 December",   52.0,   5.0, 2026, 12, 20),
                    ("far west June",  40.0, -75.0, 2026,  6, 20),
                    ("far east June", -34.0, 150.0, 2026,  6, 20),
                };
                const int minutes = 48 * 60;
                const double maxStep = 0.1;

                foreach (var w in sweeps)
                {
                    var start = new DateTime(w.y, w.mo, w.d, 0, 0, 0, DateTimeKind.Utc);
                    double low = double.MaxValue, high = double.MinValue, step = 0, previous = 0;
                    DateTime stepAt = start;
                    for (int i = 0; i <= minutes; i++)
                    {
                        DateTime utc = start.AddMinutes(i);
                        double air = Air(utc, w.lat, w.lon);
                        low = Math.Min(low, air);                 // NaN sticks, and fails the bounds below
                        high = Math.Max(high, air);
                        if (i > 0 && Math.Abs(air - previous) > step)
                        {
                            step = Math.Abs(air - previous);
                            stepAt = utc;
                        }
                        previous = air;
                    }
                    bool ok = low >= tMin - 1e-9 && high <= tMax + 1e-9 && step < maxStep;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "sweep {0,-14} 48 h from {1:yyyy-MM-dd}   range {2:F4}-{3:F4} K   " +
                        "largest 1-min step {4:F4} K, at {5:yyyy-MM-dd HH:mm}   {6}",
                        w.name, start, low, high, step, stepAt, ok ? "ok" : "FAIL"));
                }

                // The same values sweeping backwards: nothing carried from one call to the next, because
                // D-008's history evaluates past instants in any order.
                {
                    var start = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
                    var forward = new double[minutes + 1];
                    for (int i = 0; i <= minutes; i++)
                        forward[i] = Air(start.AddMinutes(i), 52.0, 5.0);
                    int differ = 0;
                    for (int i = minutes; i >= 0; i--)
                        if (Air(start.AddMinutes(i), 52.0, 5.0) != forward[i])
                            differ++;
                    Report(differ == 0, $"backwards sweep N52 June: {differ} of {minutes + 1} minutes differ   " +
                                        (differ == 0 ? "ok" : "FAIL"));
                }

                // Minimum = maximum is a constant air temperature, at any time of day or night.
                {
                    var start = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
                    double worst = 0;
                    for (int i = 0; i <= minutes; i += 7)
                    {
                        double air = ThermalMath.AirTemperature(start.AddMinutes(i), 52.0, 5.0, 290.0, 290.0,
                                                                curveA, curveB, curveC);
                        worst = Math.Max(worst, Math.Abs(air - 290.0));   // NaN sticks, and fails
                    }
                    bool ok = worst < 1e-9;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "constant air, 290-290 K: largest deviation {0:0.0e+0} K   {1}", worst, ok ? "ok" : "FAIL"));
                }
            });

            if (failed == 0 && skipped == 0)
                Debug.Log($"[Air] all {checks} checks passed");
            else if (failed == 0)
                Debug.LogWarning($"[Air] {checks} checks passed, {skipped} section(s) skipped");
            else
                Debug.LogError($"[Air] {failed} of {checks} checks FAILED" +
                               (skipped > 0 ? $", {skipped} section(s) skipped" : ""));
        }


        // Every ThermalSiteProfile in the project (D-016): imported whole, and blended without a step. On the
        // 15th at 00:00 UTC it gives its months exactly; hour by hour across 2027-2028, a leap year included,
        // nothing jumps - the year boundary and every month boundary among them.
        [MenuItem("Thermal/Site Profile Check")]
        public static void SiteProfileCheck()
        {
            string[] guids = AssetDatabase.FindAssets("t:ThermalSiteProfile");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[Site] no site profiles in the project - drop a .siteprofile under Assets");
                return;
            }
            int failed = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var p = AssetDatabase.LoadAssetAtPath<ThermalSiteProfile>(path);
                if (p == null || !p.IsValid)
                {
                    failed++;
                    Debug.LogError($"[Site] {path}: not a whole profile - reimport it");
                    continue;
                }

                double anchor = 0;
                for (int m = 1; m <= 12; m++)
                {
                    ThermalSiteProfile.Month want = p.GetMonth(m);
                    ThermalSiteProfile.Month got = p.Sample(new DateTime(2027, m, 15, 0, 0, 0, DateTimeKind.Utc));
                    anchor = Math.Max(anchor, Math.Max(Math.Abs(got.minK - want.minK), Math.Abs(got.maxK - want.maxK)));
                    anchor = Math.Max(anchor, Math.Max(Math.Abs(got.dewPointK - want.dewPointK), Math.Abs(got.a - want.a)));
                    anchor = Math.Max(anchor, Math.Max(Math.Abs(got.b - want.b), Math.Abs(got.c - want.c)));
                    anchor = Math.Max(anchor, Math.Abs(got.linkeTurbidity - want.linkeTurbidity));
                }

                // The seasons move a clear day's extremes by ~10 K in three months: ~0.005 K an hour.
                double stepK = 0, stepH = 0, stepTl = 0;
                DateTime at = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), worstAt = at;
                ThermalSiteProfile.Month prev = p.Sample(at);
                for (int i = 1; i <= 2 * 8784; i++)
                {
                    DateTime t = at.AddHours(i);
                    ThermalSiteProfile.Month s = p.Sample(t);
                    double k = Math.Max(Math.Abs(s.minK - prev.minK), Math.Max(Math.Abs(s.maxK - prev.maxK), Math.Abs(s.dewPointK - prev.dewPointK)));
                    if (k > stepK) { stepK = k; worstAt = t; }
                    stepH = Math.Max(stepH, Math.Max(Math.Abs(s.a - prev.a), Math.Max(Math.Abs(s.b - prev.b), Math.Abs(s.c - prev.c))));
                    stepTl = Math.Max(stepTl, Math.Abs(s.linkeTurbidity - prev.linkeTurbidity));
                    prev = s;
                }
                bool ok = anchor < 1e-9 && stepK < 0.05 && stepH < 0.01 && stepTl < 0.01;
                if (!ok) failed++;
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1})   months on the 15th {2:0.0e+0}   largest hourly change: {3:F4} K at {4:yyyy-MM-dd HH:mm}, " +
                    "{5:F5} h, turbidity {6:F5}   {7}",
                    p.DisplayName, path, anchor, stepK, worstAt, stepH, stepTl, ok ? "ok" : "FAIL");
                if (ok) Debug.Log("[Site] " + line);
                else Debug.LogError("[Site] " + line);
            }
            if (failed == 0)
                Debug.Log($"[Site] all {guids.Length} profiles passed");
            else
                Debug.LogError($"[Site] {failed} of {guids.Length} profiles FAILED");
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
