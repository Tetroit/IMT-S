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

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Simulated UTC",
                environment.SunUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("Sun Elevation", Degrees(sun.elevation));
            EditorGUILayout.LabelField("Sun Azimuth", Degrees(sun.azimuth));
        }

        // Nothing serialised changes as the clock runs, so without this the values would only
        // refresh when the mouse moves over the inspector.
        public override bool RequiresConstantRepaint() => true;

        static string Degrees(double value) =>
            value.ToString("F4", CultureInfo.InvariantCulture) + "°";
    }
}
