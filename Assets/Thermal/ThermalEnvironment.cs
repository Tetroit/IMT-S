using System;
using System.Globalization;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Owns the thermal environment: builds the radiance LUT once, runs the simulated clock, and
    /// pushes every shader global the physics reads.
    ///
    /// The sun is driven from <see cref="ThermalMath.Solar"/>. One vector feeds both the energy
    /// balance (<c>_ThermalSunDirection</c>) and the directional light's rotation, so the shadow
    /// map and the balance can never describe different suns. The light is therefore
    /// script-controlled: rotating it by hand does nothing, it snaps back on the next update.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Thermal/Thermal Environment")]
    public class ThermalEnvironment : MonoBehaviour
    {
        [Header("Atmosphere")]
        [SerializeField, Range(180f, 320f)] float m_SkyTemperature = 220f;
        [SerializeField] float m_AirTemperature = 295f;
        [Tooltip("Direct solar irradiance with the sun above the horizon. Zeroed below it.")]
        [SerializeField] float m_SolarIrradiance = 900f;
        [SerializeField] float m_ConvectiveCoefficient = 15f;

        [Header("Location")]
        [SerializeField] double m_Latitude = 52.0;
        [Tooltip("Degrees, EAST positive.")]
        [SerializeField] double m_Longitude = 5.0;
        [Tooltip("Where true north points, in degrees clockwise from world +Z. 0 if the geodata " +
                 "is imported north-up along +Z. Check it: at solar noon shadows must point north.")]
        [SerializeField] float m_NorthRotation = 0f;

        [Header("Time")]
        [Tooltip("Track real UTC. Turn off to start from Start Utc below - do that for any capture " +
                 "you want to reproduce, or for testing at night.")]
        [SerializeField] bool m_UseWallClock = true;
        [Tooltip("ISO 8601, e.g. 2026-06-21T12:00:00Z. Used when wall clock is off.")]
        [SerializeField] string m_StartUtc = "2026-06-21T12:00:00Z";
        [Tooltip("1 = real time. Changing it rebases the clock, so the sun never jumps.")]
        [SerializeField] double m_TimeScale = 1.0;

        [Header("Sun")]
        [Tooltip("Leave empty to use the Lighting sun source, or the first directional light.")]
        [SerializeField] Light m_Sun;

        [Header("Current (overwritten every update)")]
        [SerializeField] string m_SimulatedUtcDisplay;
        [SerializeField] float m_SunElevation;
        [SerializeField] float m_SunAzimuth;

        // D-005: uniform in temperature, 1024 entries over 180-1200 K.
        const int k_LutSamples = 1024;
        const double k_LutLowK = 180, k_LutHighK = 1200;

        const double Deg2Rad = Math.PI / 180.0;

        static readonly int k_Lut = Shader.PropertyToID("_ThermalLut");
        static readonly int k_LutParams = Shader.PropertyToID("_ThermalLutParams");
        static readonly int k_SkyT = Shader.PropertyToID("_ThermalSkyTemperature");
        static readonly int k_SkyT4 = Shader.PropertyToID("_ThermalSkyTemperature4");
        static readonly int k_AirT = Shader.PropertyToID("_ThermalAirTemperature");
        static readonly int k_AirT4 = Shader.PropertyToID("_ThermalAirTemperature4");
        static readonly int k_SunDir = Shader.PropertyToID("_ThermalSunDirection");
        static readonly int k_Irradiance = Shader.PropertyToID("_ThermalSolarIrradiance");
        static readonly int k_Convective = Shader.PropertyToID("_ThermalConvectiveCoefficient");

        // Built once, not per frame: the old per-frame build allocated a Texture2D every update and
        // never freed it, which a continuously running sim cannot survive.
        [NonSerialized] Texture2D m_Lut;

        // simulatedUtc = m_ClockStart + (Time.timeAsDouble - m_Epoch) * m_ClockScale
        // Computed from the anchor rather than accumulated per frame, so the same elapsed time
        // always gives the same instant regardless of frame timing - D-008 depends on that.
        [NonSerialized] DateTime m_ClockStart;
        [NonSerialized] double m_Epoch;
        [NonSerialized] double m_ClockScale;
        [NonSerialized] bool m_ClockValid;

        // Remembered so OnValidate can tell which setting changed: a new start must jump, a new
        // scale must not.
        [NonSerialized] bool m_LastUseWallClock;
        [NonSerialized] string m_LastStartUtc;
        [NonSerialized] double m_LastTimeScale;

        [NonSerialized] bool m_WarnedNoSun;

        /// <summary>The simulated instant driving the sun. Always DateTimeKind.Utc.</summary>
        public DateTime SimulatedUtc
        {
            get
            {
                if (!m_ClockValid)
                    ResetClock();
                double elapsed = (Time.timeAsDouble - m_Epoch) * m_ClockScale;
                return m_ClockStart.AddSeconds(elapsed);
            }
        }

        /// <summary>Solar position at the current simulated instant.</summary>
        public SolarAngles Sun { get; private set; }

        void OnEnable()
        {
            // Runtime state is not serialised, so after a domain reload the anchor is gone. In
            // set-time mode that restarts the scenario from its start time - reproducible, which
            // is the behaviour a scenario should have.
            ResetClock();
            Push();
        }

        void OnDisable()
        {
            if (m_Lut != null)
            {
                DestroyImmediate(m_Lut);
                m_Lut = null;
            }
        }

        void OnValidate()
        {
            if (!m_ClockValid || m_UseWallClock != m_LastUseWallClock || m_StartUtc != m_LastStartUtc)
                ResetClock();
            else if (m_TimeScale != m_LastTimeScale)
                Rebase();

            // OnValidate also fires on disabled components and during loading. Only an enabled one
            // pushes, so a LUT is never built by something whose OnDisable will not run.
            if (isActiveAndEnabled)
                Push();
        }

        void Update()
        {
            Push();
        }

        // ---------------------------------------------------------------------- clock

        void ResetClock()
        {
            m_ClockStart = m_UseWallClock ? DateTime.UtcNow : ParseStartUtc();
            m_Epoch = Time.timeAsDouble;
            m_ClockScale = m_TimeScale;
            m_ClockValid = true;
            Remember();
        }

        /// <summary>
        /// Fold the current simulated time into the start before a new scale applies. Without this
        /// the new scale would act retroactively on all elapsed time: ten real minutes at 1x is
        /// +10 min, but switch to 60x and the same elapsed becomes +10 h in one step.
        /// </summary>
        void Rebase()
        {
            m_ClockStart = SimulatedUtc;
            m_Epoch = Time.timeAsDouble;
            m_ClockScale = m_TimeScale;
            Remember();
        }

        void Remember()
        {
            m_LastUseWallClock = m_UseWallClock;
            m_LastStartUtc = m_StartUtc;
            m_LastTimeScale = m_TimeScale;
        }

        DateTime ParseStartUtc()
        {
            // AssumeUniversal treats a string without an offset as UTC; AdjustToUniversal
            // guarantees Kind == Utc either way. A Kind.Unspecified DateTime is silently treated
            // as local in some conversions, which shifts the sun by the timezone offset - and that
            // error looks exactly like a longitude sign bug.
            if (DateTime.TryParse(m_StartUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                return utc;

            // TryParse, not Parse: OnValidate runs on every keystroke while the field is being
            // edited, and throwing from it is miserable.
            Debug.LogWarning("[Thermal] could not parse Start Utc '" + m_StartUtc +
                             "' - using the current time until it is fixed");
            return DateTime.UtcNow;
        }

        // ---------------------------------------------------------------------- push

        void Push()
        {
            EnsureLut();
            Shader.SetGlobalTexture(k_Lut, m_Lut);
            Shader.SetGlobalVector(k_LutParams,
                new Vector4((float)k_LutLowK, (float)k_LutHighK, k_LutSamples, 0));

            Shader.SetGlobalFloat(k_SkyT, m_SkyTemperature);
            Shader.SetGlobalFloat(k_SkyT4, (float)Math.Pow(m_SkyTemperature, 4.0));
            Shader.SetGlobalFloat(k_AirT, m_AirTemperature);
            Shader.SetGlobalFloat(k_AirT4, (float)Math.Pow(m_AirTemperature, 4.0));
            Shader.SetGlobalFloat(k_Convective, m_ConvectiveCoefficient);

            DateTime utc = SimulatedUtc;
            SolarAngles sun = ThermalMath.Solar(m_Latitude, m_Longitude, utc);
            Sun = sun;

            Vector3 sunDirection = SunDirection(sun, m_NorthRotation);
            Shader.SetGlobalVector(k_SunDir, sunDirection);

            // Below the horizon sunDirection.y < 0, so dot(N, sunDirection) is POSITIVE for
            // downward-facing surfaces: they would be lit by a sun shining up through the ground.
            // The hand-placed sun never went below the horizon, so this was invisible before.
            //
            // Known gap: irradiance should also fall with airmass toward the horizon - 900 W/m2 at
            // 2 degrees elevation is wrong too, just less obviously. That needs a clear-sky model.
            float irradiance = sun.elevation > 0.0 ? m_SolarIrradiance : 0f;
            Shader.SetGlobalFloat(k_Irradiance, irradiance);

            Light light = ResolveSun();
            if (light != null)
            {
                // A light's forward is the direction light TRAVELS; sunDirection points TOWARD the
                // sun. Deriving the rotation from the same vector is what makes the shadow map and
                // the balance agree by construction rather than by care.
                light.transform.rotation = Quaternion.LookRotation(-sunDirection);
            }

            m_SimulatedUtcDisplay = utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            m_SunElevation = (float)sun.elevation;
            m_SunAzimuth = (float)sun.azimuth;
        }

        /// <summary>
        /// Toward-the-sun unit vector. World convention: +X east, +Y up, +Z north, rotated by
        /// <paramref name="northRotation"/> if the geodata is not imported north-up. The convention
        /// lives here rather than in ThermalMath because it is a property of this world, not of
        /// the physics - a C++ renderer may use +Z up.
        /// </summary>
        static Vector3 SunDirection(SolarAngles sun, float northRotation)
        {
            double az = (sun.azimuth + northRotation) * Deg2Rad;
            double el = sun.elevation * Deg2Rad;
            return new Vector3(
                (float)(Math.Sin(az) * Math.Cos(el)),
                (float)Math.Sin(el),
                (float)(Math.Cos(az) * Math.Cos(el)));
        }

        Light ResolveSun()
        {
            if (m_Sun != null)
                return m_Sun;

            if (RenderSettings.sun != null)
                return m_Sun = RenderSettings.sun;

            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type == LightType.Directional)
                    return m_Sun = l;
            }

            if (!m_WarnedNoSun)
            {
                Debug.LogWarning("[Thermal] no directional light found - the balance still gets the " +
                                 "computed sun, but nothing casts shadows from it");
                m_WarnedNoSun = true;
            }
            return null;
        }

        // ---------------------------------------------------------------------- LUT

        void EnsureLut()
        {
            if (m_Lut != null)
                return;

            m_Lut = new Texture2D(k_LutSamples, 1, TextureFormat.RFloat, false)
            {
                name = "ThermalLut",
                // Created by code in an [ExecuteAlways] component, so it exists in edit mode:
                // without this it can be saved into the scene, it logs "leaked" warnings, and
                // UnloadUnusedAssets can free it while still bound - a shader global is not a
                // reference Unity tracks. The price is that OnDisable must destroy it.
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            // Same arithmetic, in the same order, as the table verified in D-005, so the LUT - and
            // every cluster value downstream of it - is byte-identical to before.
            var data = new float[k_LutSamples];
            for (int i = 0; i < k_LutSamples; i++)
            {
                double t = i / (double)(k_LutSamples - 1);
                double T = k_LutLowK + t * (k_LutHighK - k_LutLowK);
                data[i] = (float)ThermalMath.BandIntegration(T);
            }

            m_Lut.SetPixelData(data, 0);
            m_Lut.Apply(false, true);
        }
    }
}
