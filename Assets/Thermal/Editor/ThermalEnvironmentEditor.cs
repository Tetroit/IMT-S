using System;
using System.Globalization;
using UnityEditor;

namespace IMT.Thermal
{
    /// <summary>
    /// The normal <see cref="ThermalEnvironment"/> settings, plus the clock and sun position read
    /// straight from the component. Those used to be serialised fields, which wrote new values into
    /// the scene file on every save even when nothing had been edited.
    /// </summary>
    [CustomEditor(typeof(ThermalEnvironment))]
    public class ThermalEnvironmentEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var environment = (ThermalEnvironment)target;
            SolarAngles sun = environment.Sun;
            ThermalSiteProfile profile = environment.SiteProfile;
            ThermalEnvironment.SiteConditions site = environment.Site;

            if (profile != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox($"The site profile '{profile.DisplayName}' drives the location, the air's " +
                                        "minimum, maximum, dew point and curve, the Linke turbidity" +
                                        (profile.HasWind ? " and the wind" : "") + " - those " +
                                        "settings above are not used while it is set.\n" + profile.Provenance,
                                        MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Site", profile != null ? profile.DisplayName + "  (profile)" : "the settings above");
            if (profile != null)
            {
                var inv = CultureInfo.InvariantCulture;
                EditorGUILayout.LabelField("Location", string.Format(inv, "{0:F3}°, {1:F3}°, {2:F0} m",
                                                                     site.latitude, site.longitude, site.altitude));
                EditorGUILayout.LabelField("Day's Air Min / Max", string.Format(inv, "{0:F2} / {1:F2} K", site.minK, site.maxK));
                EditorGUILayout.LabelField("Dew Point", string.Format(inv, "{0:F2} K", site.dewPointK));
                EditorGUILayout.LabelField("Air Curve a, b, c", string.Format(inv, "{0:F2}, {1:F2}, {2:F2} h", site.a, site.b, site.c));
                EditorGUILayout.LabelField("Linke Turbidity", string.Format(inv, "{0:F2}", site.linkeTurbidity));
            }
            EditorGUILayout.LabelField("Wind (10 m)", site.windSpeed.ToString("F2", CultureInfo.InvariantCulture) +
                                       " m/s" + (site.windFromProfile ? "  (profile)" : "  (setting)"));
            EditorGUILayout.LabelField("Simulated UTC",
                environment.SunUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("Sun Elevation", Degrees(sun.elevation));
            EditorGUILayout.LabelField("Sun Azimuth", Degrees(sun.azimuth));

            // air through the day
            EditorGUILayout.LabelField("Air Temperature",
                environment.AirTemperature.ToString("F3", CultureInfo.InvariantCulture) + " K" +
                (environment.AirIsPlaceholder ? "  (placeholder: constant)" : ""));
            EditorGUILayout.LabelField("Relative Humidity",
                (environment.RelativeHumidity * 100).ToString("F1", CultureInfo.InvariantCulture) + " %");
            if (!environment.AirIsPlaceholder)
            {
                // far from Greenwich, sunrise or sunset can fall on the UTC date before or after
                SunTimes day = environment.SunriseSunsetUtc;
                string Clock(DateTime t)
                {
                    int days = (t.Date - environment.SunUtc.Date).Days;
                    return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                           (days == 0 ? "" : $" ({days:+0;-0} d)");
                }
                EditorGUILayout.LabelField("Sunrise / Sunset", Clock(day.sunrise) + " / " + Clock(day.sunset) + " UTC");
            }

            // D-013: derived from air temperature and humidity, not set
            bool derived = !float.IsNaN(environment.BroadbandFlatEmissivity);
            EditorGUILayout.LabelField("Broadband Sky, Flat",
                derived ? Fixed(environment.BroadbandFlatEmissivity) : "ClearSkyEmissivity not written yet");
            EditorGUILayout.LabelField("Broadband Sky, Zenith",
                Fixed(environment.BroadbandZenithEmissivity) + (derived ? "" : "  (placeholder)"));

            // clear-sky sunlight
            SolarIrradiance light = environment.Sunlight;
            EditorGUILayout.LabelField("Direct Normal", Watts(light.directNormal) +
                (environment.SunlightIsPlaceholder ? "  (placeholder: flat 900, no diffuse)" : ""));
            EditorGUILayout.LabelField("Diffuse Horizontal", Watts(light.diffuseHorizontal));
            EditorGUILayout.LabelField("Global Horizontal", Watts(light.globalHorizontal));

            // stored heat (item 5 step 2): per group, H and the weight of now and of the oldest sample
            ThermalSurfaceGroup[] groups = ThermalSurfaceGroups.HistoryGroups;
            for (int r = 0; r < groups.Length; r++)
            {
                double[] w = environment.HistoryWeightsOf[r];
                EditorGUILayout.LabelField("Stored Heat, " + groups[r],
                    string.Format(CultureInfo.InvariantCulture, "H {0:F2} W/m²K, now {1:F3}, {2:F1} h ago {3:F3}",
                                  environment.HistoryConductance[r],
                                  w[0], ThermalEnvironment.HistoryLagSeconds[w.Length - 1] / 3600.0, w[w.Length - 1]) +
                    (environment.HistoryIsPlaceholder ? "  (not written: steady)" : ""));
            }
        }

        // Nothing serialised changes as the clock runs, so without this the values would only
        // refresh when the mouse moves over the inspector.
        public override bool RequiresConstantRepaint() => true;

        static string Degrees(double value) =>
            value.ToString("F4", CultureInfo.InvariantCulture) + "°";

        static string Fixed(float value) => value.ToString("F4", CultureInfo.InvariantCulture);

        static string Watts(double value) => value.ToString("F1", CultureInfo.InvariantCulture) + " W/m²";
    }
}
