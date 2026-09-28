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
        [SerializeField] float m_AirTemperature = 295f;
        [SerializeField] float m_ConvectiveCoefficient = 15f;
        [Tooltip("Relative humidity near the ground, 0-1. Sets the broadband sky the energy balance " +
                 "feels (D-013): water vapour is most of what makes the sky warm outside the camera's band.")]
        [SerializeField, Range(0.01f, 1f)] float m_RelativeHumidity = 0.6f;

        [Header("Sky (D-013)")]
        [Tooltip("In-band (8-14 um) sky emissivity straight up. 0.191 is the fit to the reference " +
                 "footage under a linear map: a 216 K zenith at 295 K air.")]
        [SerializeField, Range(0.01f, 0.99f)] float m_SkyZenithEmissivity = 0.191f;
        // The broadband zenith emissivity is not a setting: it follows from air temperature and humidity
        // (EnsureBroadband), and the inspector shows it under Current.

        [Header("Sunlight (clear sky)")]
        [Tooltip("Linke turbidity: how hazy the air is. ~2 very clean, ~3 a clear mid-latitude sky, " +
                 "5 and up hazy or humid. More of it trades direct sunlight for diffuse.")]
        [SerializeField, Range(1f, 8f)] float m_LinkeTurbidity = 3f;
        [Tooltip("Ground albedo, for sunlight the ground reflects onto tilted surfaces. A stand-in until " +
                 "the terrain supplies its own; ~0.2 for grass or soil.")]
        [SerializeField, Range(0f, 1f)] float m_GroundAlbedo = 0.2f;

        [Header("Location")]
        [SerializeField] double m_Latitude = 52.0;
        [Tooltip("Degrees, EAST positive.")]
        [SerializeField] double m_Longitude = 5.0;
        [Tooltip("Height of the site above sea level, metres. Less air overhead: more direct sunlight.")]
        [SerializeField] float m_SiteAltitude = 0f;
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
        // A reload still restarts the clock (see OnEnable), so with wall clock off a paused sun sits
        // at Start Utc afterwards - the same place every time, which keeps scene saves unchanged.
        [Tooltip("Stops simulated time, so the sun stops moving. Unpausing carries on from the same " +
                 "instant.")]
        [SerializeField] bool m_Paused;

        [Header("Sun")]
        [Tooltip("Leave empty to use the Lighting sun source, or the first directional light.")]
        [SerializeField] Light m_Sun;

        // D-005: uniform in temperature, 1024 entries over 180-1200 K.
        const int k_LutSamples = 1024;
        const double k_LutLowK = 180, k_LutHighK = 1200;

        const double Deg2Rad = Math.PI / 180.0;

        static readonly int k_Lut = Shader.PropertyToID("_ThermalLut");
        static readonly int k_LutParams = Shader.PropertyToID("_ThermalLutParams");
        static readonly int k_AirT = Shader.PropertyToID("_ThermalAirTemperature");
        static readonly int k_SunDir = Shader.PropertyToID("_ThermalSunDirection");
        static readonly int k_DirectNormal = Shader.PropertyToID("_ThermalDirectNormalIrradiance");
        static readonly int k_DiffuseHorizontal = Shader.PropertyToID("_ThermalDiffuseHorizontalIrradiance");
        static readonly int k_GlobalHorizontal = Shader.PropertyToID("_ThermalGlobalHorizontalIrradiance");
        static readonly int k_GroundAlbedo = Shader.PropertyToID("_ThermalGroundAlbedo");
        static readonly int k_Convective = Shader.PropertyToID("_ThermalConvectiveCoefficient");

        // D-013 sky tables. They hold dimensionless emissivities, so the diurnal air temperature never
        // forces a rebake - only an emissivity setting does. View is spaced evenly in sqrt(direction.y),
        // which keeps 256 entries within 0.05 NETD of the formula at the horizon, where evenly in
        // direction.y would reach 0.18; diffuse is spaced evenly in N.y and stays within 0.03.
        const int k_SkyViewSamples = 256;
        const int k_SkyDiffuseSamples = 256;

        static readonly int k_SkyView = Shader.PropertyToID("_ThermalSkyView");
        static readonly int k_SkyDiffuse = Shader.PropertyToID("_ThermalSkyDiffuse");
        static readonly int k_SkyParams = Shader.PropertyToID("_ThermalSkyParams");

        // Built once, not per frame: the old per-frame build allocated a Texture2D every update and
        // never freed it, which a continuously running sim cannot survive.
        [NonSerialized] Texture2D m_Lut;

        [NonSerialized] Texture2D m_SkyView;      // R: sky emissivity by sqrt(direction.y)
        [NonSerialized] Texture2D m_SkyDiffuse;   // RG: in-band, broadband diffuse factor by N.y
        // The settings the sky tables were baked for. NaN equals nothing, so the first push bakes.
        [NonSerialized] float m_BakedZenith = float.NaN;
        [NonSerialized] float m_BakedZenithBroadband = float.NaN;
        [NonSerialized] bool m_WarnedNoSkyModel;

        // The broadband zenith emissivity, derived from air temperature and humidity and recomputed only
        // when either changes - finding it inverts the diffuse integral, far too slow to do per frame. The
        // fallback is the old placeholder, which keeps flat surfaces where the 220 K sky had them.
        const float k_BroadbandFallback = 0.191f;
        [NonSerialized] float m_BroadbandZenith = k_BroadbandFallback;
        [NonSerialized] float m_BroadbandFlat = float.NaN;
        [NonSerialized] float m_BroadbandForAir = float.NaN;
        [NonSerialized] float m_BroadbandForHumidity = float.NaN;
        [NonSerialized] bool m_WarnedNoClearSky;

        // Clear-sky sunlight. Until ThermalMath's clear-sky functions exist, the old flat 900 W/m2 direct
        // beam stands in with no diffuse - exactly what the balance had before, so nothing changes early.
        const double k_FallbackIrradiance = 900.0;
        [NonSerialized] bool m_ClearSkyMissing;
        [NonSerialized] bool m_WarnedSunlight;

        // simulatedUtc = m_ClockStart + (Time.timeAsDouble - m_Epoch) * m_ClockScale
        // Computed from the anchor rather than accumulated per frame, so the same elapsed time
        // always gives the same instant regardless of frame timing - D-008 depends on that.
        [NonSerialized] DateTime m_ClockStart;
        [NonSerialized] double m_Epoch;
        [NonSerialized] double m_ClockScale;   // 0 while paused
        [NonSerialized] bool m_ClockValid;

        // Remembered so OnValidate can tell which setting changed: a new start must jump, a new
        // scale or a pause must not.
        [NonSerialized] bool m_LastUseWallClock;
        [NonSerialized] string m_LastStartUtc;
        [NonSerialized] double m_LastTimeScale;
        [NonSerialized] bool m_LastPaused;

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

        /// <summary>
        /// The simulated instant <see cref="Sun"/> was computed for, at the last update. Not
        /// serialised, like Sun: stored values would change the scene file on every save.
        /// </summary>
        public DateTime SunUtc { get; private set; }

        /// <summary>
        /// Broadband clear-sky emissivity of a flat surface at the current air temperature and humidity,
        /// from ThermalMath.ClearSkyEmissivity. NaN while that function is not written.
        /// </summary>
        public float BroadbandFlatEmissivity => m_BroadbandFlat;

        /// <summary>The broadband zenith emissivity the balance's sky table is baked with.</summary>
        public float BroadbandZenithEmissivity => m_BroadbandZenith;

        /// <summary>Clear-sky sunlight at the last update: direct normal, diffuse and global horizontal.</summary>
        public SolarIrradiance Sunlight { get; private set; }

        /// <summary>True while the clear-sky functions are not written and the old 900 W/m2 stands in.</summary>
        public bool SunlightIsPlaceholder => m_ClearSkyMissing;

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
            DestroyTable(ref m_SkyView);
            DestroyTable(ref m_SkyDiffuse);
            m_BakedZenith = m_BakedZenithBroadband = float.NaN;
        }

        void OnValidate()
        {
            if (!m_ClockValid || m_UseWallClock != m_LastUseWallClock || m_StartUtc != m_LastStartUtc)
                ResetClock();
            else if (m_TimeScale != m_LastTimeScale || m_Paused != m_LastPaused)
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

        double EffectiveScale => m_Paused ? 0.0 : m_TimeScale;

        void ResetClock()
        {
            m_ClockStart = m_UseWallClock ? DateTime.UtcNow : ParseStartUtc();
            m_Epoch = Time.timeAsDouble;
            m_ClockScale = EffectiveScale;
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
            m_ClockScale = EffectiveScale;
            Remember();
        }

        void Remember()
        {
            m_LastUseWallClock = m_UseWallClock;
            m_LastStartUtc = m_StartUtc;
            m_LastTimeScale = m_TimeScale;
            m_LastPaused = m_Paused;
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

            EnsureSky();
            if (m_SkyView != null && m_SkyDiffuse != null)
            {
                Shader.SetGlobalTexture(k_SkyView, m_SkyView);
                Shader.SetGlobalTexture(k_SkyDiffuse, m_SkyDiffuse);
                Shader.SetGlobalVector(k_SkyParams, new Vector4(k_SkyViewSamples, k_SkyDiffuseSamples, 0, 0));
            }
            
            Shader.SetGlobalFloat(k_AirT, m_AirTemperature);
            Shader.SetGlobalFloat(k_Convective, m_ConvectiveCoefficient);

            DateTime utc = SimulatedUtc;
            SolarAngles sun = ThermalMath.Solar(m_Latitude, m_Longitude, utc);
            Sun = sun;
            SunUtc = utc;

            Vector3 sunDirection = SunDirection(sun, m_NorthRotation);
            Shader.SetGlobalVector(k_SunDir, sunDirection);

            SolarIrradiance sunlight = ComputeSunlight(sun, utc);
            Sunlight = sunlight;
            Shader.SetGlobalFloat(k_DirectNormal, (float)sunlight.directNormal);
            Shader.SetGlobalFloat(k_DiffuseHorizontal, (float)sunlight.diffuseHorizontal);
            Shader.SetGlobalFloat(k_GlobalHorizontal, (float)sunlight.globalHorizontal);
            Shader.SetGlobalFloat(k_GroundAlbedo, m_GroundAlbedo);

            Light light = ResolveSun();
            if (light != null)
            {
                // A light's forward is the direction light TRAVELS; sunDirection points TOWARD the
                // sun. Deriving the rotation from the same vector is what makes the shadow map and
                // the balance agree by construction rather than by care.
                light.transform.rotation = Quaternion.LookRotation(-sunDirection);
            }
        }

        /// <summary>
        /// Clear-sky sunlight for this instant. Zero with the sun at or below the horizon, whatever a
        /// model gives there: below it sunDirection.y &lt; 0, so dot(N, sunDirection) is POSITIVE for
        /// downward-facing surfaces, and they would be lit by a sun shining up through the ground.
        /// </summary>
        SolarIrradiance ComputeSunlight(SolarAngles sun, DateTime utc)
        {
            if (sun.elevation <= 0.0)
                return default;

            if (!m_ClearSkyMissing)
            {
                try
                {
                    double e0 = ThermalMath.ExtraterrestrialIrradiance(utc.DayOfYear);
                    SolarIrradiance s = ThermalMath.ClearSkyIrradiance(sun.elevation, m_SiteAltitude,
                                                                       m_LinkeTurbidity, e0);
                    CheckSunlight(s, e0);
                    return s;
                }
                catch (NotImplementedException)
                {
                    // Tried once per domain reload: writing the functions recompiles, which clears this.
                    m_ClearSkyMissing = true;
                    Debug.LogWarning("[Thermal] ThermalMath's clear-sky functions are not written yet, so the old " +
                                     $"flat {k_FallbackIrradiance} W/m2 direct beam stands in, with no diffuse");
                }
            }

            return new SolarIrradiance
            {
                directNormal = k_FallbackIrradiance,
                diffuseHorizontal = 0.0,
                globalHorizontal = k_FallbackIrradiance * Math.Sin(sun.elevation * Deg2Rad),
            };
        }

        /// <summary>
        /// Physical bounds on the clear-sky result, checked every push but reported once: finite, not
        /// negative, and no more direct sunlight than arrives above the atmosphere. The exact values are
        /// Thermal > Clear Sky Check's job.
        /// </summary>
        void CheckSunlight(SolarIrradiance s, double e0)
        {
            if (m_WarnedSunlight)
                return;
            string problem = null;
            if (!(s.directNormal >= 0.0 && s.diffuseHorizontal >= 0.0 && s.globalHorizontal >= 0.0))
                problem = "is negative or NaN";   // the comparisons are false for NaN too
            else if (s.directNormal > e0)
                problem = $"has more direct sunlight than the {e0:F1} W/m2 above the atmosphere";
            if (problem == null)
                return;
            Debug.LogWarning($"[Thermal] clear-sky sunlight {problem}: {s}");
            m_WarnedSunlight = true;
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

            foreach (var l in FindObjectsByType<Light>())
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

        // ---------------------------------------------------------------------- sky tables

        void EnsureSky()
        {
            EnsureBroadband();
            if (m_BakedZenith == m_SkyZenithEmissivity && m_BakedZenithBroadband == m_BroadbandZenith)
                return;
            // Recorded before baking, so a sky model that is missing or throws is tried once per
            // setting, not every frame.
            m_BakedZenith = m_SkyZenithEmissivity;
            m_BakedZenithBroadband = m_BroadbandZenith;

            var view = new float[k_SkyViewSamples];
            var diffuse = new float[2 * k_SkyDiffuseSamples];
            try
            {
                for (int i = 0; i < k_SkyViewSamples; i++)
                {
                    // the shader's inverse is sqrt(direction.y) - see SkyViewEmissivity
                    double t = i / (double)(k_SkyViewSamples - 1);
                    view[i] = (float)ThermalMath.SkyEmissivity(t * t, m_SkyZenithEmissivity);
                }
                for (int i = 0; i < k_SkyDiffuseSamples; i++)
                {
                    double normalY = -1.0 + 2.0 * i / (k_SkyDiffuseSamples - 1);
                    diffuse[2 * i] = (float)ThermalMath.SkyDiffuse(normalY, m_SkyZenithEmissivity);
                    diffuse[2 * i + 1] = (float)ThermalMath.SkyDiffuse(normalY, m_BroadbandZenith);
                }
            }
            catch (NotImplementedException)
            {
                if (!m_WarnedNoSkyModel)
                {
                    Debug.LogWarning("[Thermal] ThermalMath.SkyEmissivity / SkyDiffuse are not written yet, " +
                                     "so the sky tables are not bound (D-013)");
                    m_WarnedNoSkyModel = true;
                }
                return;
            }

            CheckSkyTables(view, diffuse);

            if (m_SkyView == null)
                m_SkyView = CreateTable("ThermalSkyView", k_SkyViewSamples, TextureFormat.RFloat);
            if (m_SkyDiffuse == null)
                m_SkyDiffuse = CreateTable("ThermalSkyDiffuse", k_SkyDiffuseSamples, TextureFormat.RGFloat);

            // Left readable, unlike the LUT: a changed emissivity writes into the same textures.
            m_SkyView.SetPixelData(view, 0);
            m_SkyView.Apply(false, false);
            m_SkyDiffuse.SetPixelData(diffuse, 0);
            m_SkyDiffuse.Apply(false, false);
        }

        /// <summary>
        /// The broadband zenith emissivity, from air temperature and humidity. The clear-sky formula
        /// gives a flat surface's value; ZenithEmissivityForFlat finds the zenith value that reproduces
        /// it through SkyDiffuse, so every other tilt follows from the same airmass form. A changed
        /// result makes EnsureSky rebake.
        /// </summary>
        void EnsureBroadband()
        {
            // Exact comparison is right while air temperature is a setting. A diurnal one (Next, item 3)
            // changes every frame and will need a tolerance here instead.
            if (m_BroadbandForAir == m_AirTemperature && m_BroadbandForHumidity == m_RelativeHumidity)
                return;
            m_BroadbandForAir = m_AirTemperature;
            m_BroadbandForHumidity = m_RelativeHumidity;

            double flat, zenith;
            try
            {
                flat = ThermalMath.ClearSkyEmissivity(m_AirTemperature, m_RelativeHumidity);
                if (!(flat > 0.0 && flat < 1.0))   // also catches NaN
                {
                    Debug.LogWarning($"[Thermal] clear-sky emissivity {flat} is not in (0, 1), so the " +
                                     $"broadband sky stays at {m_BroadbandZenith}");
                    return;
                }
                zenith = ThermalMath.ZenithEmissivityForFlat(flat);
            }
            catch (NotImplementedException)
            {
                m_BroadbandFlat = float.NaN;
                m_BroadbandZenith = k_BroadbandFallback;
                if (!m_WarnedNoClearSky)
                {
                    Debug.LogWarning("[Thermal] ThermalMath.ClearSkyEmissivity is not written yet, so the " +
                                     $"balance keeps the placeholder broadband sky ({k_BroadbandFallback}) (D-013)");
                    m_WarnedNoClearSky = true;
                }
                return;
            }

            // The round trip must land back on the formula's value - it cannot if SkyDiffuse misbehaves.
            double back = ThermalMath.SkyDiffuse(1.0, zenith);
            if (Math.Abs(back - flat) > 1e-4)
                Debug.LogWarning($"[Thermal] broadband zenith {zenith} gives a flat-surface sky of {back}, " +
                                 $"not the clear-sky {flat}");

            m_BroadbandFlat = (float)flat;
            m_BroadbandZenith = (float)zenith;
        }

        /// <summary>
        /// The contract ThermalMath's sky functions must meet, checked on every bake: finite
        /// emissivities in [0, 1], exactly 1 at the horizon (so horizon-grazing sky reads L(T_air)), the
        /// zenith setting at the zenith, and no sky at all for a downward-facing surface (so F = 0 still
        /// reads L(T_air)). A warning, not an exception - a table that breaks it still renders.
        /// </summary>
        void CheckSkyTables(float[] view, float[] diffuse)
        {
            const float tol = 1e-4f;
            for (int i = 0; i < view.Length; i++)
            {
                if (!(view[i] >= 0f && view[i] <= 1f))   // also catches NaN
                {
                    Debug.LogWarning($"[Thermal] sky emissivity {view[i]} at entry {i} is not in [0, 1]");
                    break;
                }
            }
            for (int i = 0; i < diffuse.Length; i++)
            {
                if (!(diffuse[i] >= 0f && diffuse[i] <= 1f))
                {
                    Debug.LogWarning($"[Thermal] sky diffuse factor {diffuse[i]} at entry {i / 2} " +
                                     $"({(i % 2 == 0 ? "in-band" : "broadband")}) is not in [0, 1]");
                    break;
                }
            }
            if (Mathf.Abs(view[0] - 1f) > tol)
                Debug.LogWarning($"[Thermal] sky emissivity at the horizon is {view[0]}, must be 1");
            if (Mathf.Abs(view[view.Length - 1] - m_SkyZenithEmissivity) > tol)
                Debug.LogWarning($"[Thermal] sky emissivity at the zenith is {view[view.Length - 1]}, " +
                                 $"must equal the zenith setting {m_SkyZenithEmissivity}");
            if (Mathf.Abs(diffuse[0]) > tol || Mathf.Abs(diffuse[1]) > tol)
                Debug.LogWarning($"[Thermal] a downward-facing surface sees sky ({diffuse[0]}, {diffuse[1]}), " +
                                 "must see none");
        }

        static Texture2D CreateTable(string name, int samples, TextureFormat format)
        {
            return new Texture2D(samples, 1, format, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,   // why: see EnsureLut
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        static void DestroyTable(ref Texture2D table)
        {
            if (table == null)
                return;
            DestroyImmediate(table);
            table = null;
        }
    }
}
