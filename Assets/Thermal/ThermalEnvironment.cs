using System;
using System.Collections.Generic;
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
        [Header("Site")]
        [Tooltip("Optional: a site profile from Assets/Thermal/Profiles (built by IMTNS/site_profile.py, D-016). " +
                 "When set, it drives the location and, through the year, the air's minimum, maximum, dew point " +
                 "and curve, the Linke turbidity and, if it has one, the wind: those settings below are then not used.")]
        [SerializeField] ThermalSiteProfile m_SiteProfile;

        [Header("Atmosphere")]
        [Tooltip("The day's lowest air temperature, K, reached around sunrise.")]
        [SerializeField] float m_AirTemperatureMin = 285f;
        [Tooltip("The day's highest air temperature, K, reached in the afternoon. Equal to the minimum " +
                 "for a constant air temperature.")]
        [SerializeField] float m_AirTemperatureMax = 295f;
        [Tooltip("Dew point, K: how much water vapour the air holds, near constant through a day while " +
                 "relative humidity swings with temperature. Sets the broadband sky the energy balance " +
                 "feels (D-013). Keep it at or below the minimum, or dawn is supersaturated. " +
                 "286.8908 K at a constant 295 K is the old 60%.")]
        [SerializeField] float m_DewPoint = 284f;
        [Tooltip("Parton-Logan coefficients, hours: a the maximum's lag, b the night decay, c the minimum's " +
                 "time after sunrise. Defaults fitted to KNMI De Bilt clear days, 2021-2026 (D-016).")]
        [SerializeField] float m_AirCurveA = 2.19f;
        [SerializeField] float m_AirCurveB = 2.58f;
        [SerializeField] float m_AirCurveC = 0.22f;
        [Tooltip("Wind speed, m/s, at 10 m over open terrain, as a weather station or ERA5-Land reports it. With each " +
                 "material's surface group it sets the convection (item 6). 3 m/s is De Bilt's median over 2011-2025. " +
                 "A site profile with wind replaces it with the clear day's wind through the day.")]
        [SerializeField, Min(0)] float m_WindSpeed = 3f;

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

        [Header("Atmosphere")]
        [Tooltip("The atmosphere's band tables (Assets/Thermal/Atmosphere/default.atmosphere). Empty: no table is built.")]
        [SerializeField] ThermalAtmosphere m_Atmosphere;
        [Tooltip("The camera the atmosphere's table is built for; world y 0 is the ground. Leave empty to use the main " +
                 "camera.")]
        [SerializeField] Camera m_Camera;

        // D-005: uniform in temperature, 1024 entries over 180-1200 K; since the atmosphere's step 2 one channel per
        // sub-band (ThermalMath.SubBandEdges).
        const int k_LutSamples = 1024;
        const double k_LutLowK = 180, k_LutHighK = 1200;

        const double Deg2Rad = Math.PI / 180.0;

        static readonly int k_Lut = Shader.PropertyToID("_ThermalSubBandLut");
        static readonly int k_LutParams = Shader.PropertyToID("_ThermalLutParams");
        static readonly int k_AirT = Shader.PropertyToID("_ThermalAirTemperature");
        static readonly int k_AirRadiance = Shader.PropertyToID("_ThermalAirSubBandRadiance");
        static readonly int k_SunDir = Shader.PropertyToID("_ThermalSunDirection");
        static readonly int k_DirectNormal = Shader.PropertyToID("_ThermalDirectNormalIrradiance");
        static readonly int k_DiffuseHorizontal = Shader.PropertyToID("_ThermalDiffuseHorizontalIrradiance");
        static readonly int k_GlobalHorizontal = Shader.PropertyToID("_ThermalGlobalHorizontalIrradiance");
        static readonly int k_GroundAlbedo = Shader.PropertyToID("_ThermalGroundAlbedo");
        static readonly int k_WindSpeed = Shader.PropertyToID("_ThermalWindSpeed");

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
        [NonSerialized] float[] m_LutData;    // the LUT's entries, four per temperature, for SubBandRadiance
        [NonSerialized] Action<double, double[]> m_LutRadiance;   // the same read on that array, for worker threads

        // The atmosphere's table (step 3), built off the main thread.
        [NonSerialized] ThermalAtmosphereTable m_AtmosphereTable;

        [NonSerialized] Texture2D m_SkyView;      // R: sky emissivity by sqrt(direction.y)
        [NonSerialized] Texture2D m_SkyDiffuse;   // RG: in-band, broadband diffuse factor by N.y
        // CPU copies of both, so a change in one band rebakes only its own channel.
        [NonSerialized] float[] m_SkyViewData;
        [NonSerialized] float[] m_SkyDiffuseData;
        // The settings the sky tables were baked for. NaN equals nothing, so the first push bakes.
        [NonSerialized] float m_BakedZenith = float.NaN;
        [NonSerialized] float m_BakedZenithBroadband = float.NaN;
        [NonSerialized] bool m_WarnedNoSkyModel;

        // The broadband zenith emissivity, derived from air temperature and humidity. Finding it inverts the
        // diffuse integral and forces a rebake, far too slow per frame - and with the air changing through
        // the day the clear-sky value moves every frame. So it is re-derived only when the clear-sky value
        // has moved by the tolerance: a step that size moves surfaces by ~0.002 K, far under NETD. The
        // fallback is the old placeholder, which keeps flat surfaces where the 220 K sky had them.
        const float k_BroadbandFallback = 0.191f;
        const double k_BroadbandTolerance = 1e-4;
        [NonSerialized] float m_BroadbandZenith = k_BroadbandFallback;
        [NonSerialized] float m_BroadbandFlat = float.NaN;
        [NonSerialized] bool m_WarnedNoClearSky;
        [NonSerialized] bool m_WarnedClearSkyRange;

        // Air through the day. Until ThermalMath's air functions exist, the old constant 295 K and 60%
        // stand in - exactly what the balance had before, so nothing changes early.
        const double k_FallbackAirTemperature = 295.0;
        const double k_FallbackHumidity = 0.6;
        [NonSerialized] bool m_AirModelMissing;
        [NonSerialized] bool m_WarnedAir;
        [NonSerialized] bool m_WarnedAirThrew;

        // Clear-sky sunlight. Until ThermalMath's clear-sky functions exist, the old flat 900 W/m2 direct
        // beam stands in with no diffuse - exactly what the balance had before, so nothing changes early.
        const double k_FallbackIrradiance = 900.0;
        [NonSerialized] bool m_ClearSkyMissing;
        [NonSerialized] bool m_WarnedSunlight;

        // Stored heat (item 5 step 2). The past samples, seconds ago: mixed 8, dense on a log scale to 3 h, then even
        // to 24 h (Captures/history_options.py; decision log, item 5). Sample 0 is now.
        static readonly double[] k_HistoryLags = { 0, 900, 3120, 10800, 25920, 41040, 56160, 71280 };
        const int k_HistoryHours = 24;               // H is the mean over the last day, hourly
        const double k_HistoryEmissivity = 0.95;     // in H's radiation part, one value for every group
        const double k_Sigma = 5.670374419e-8;
        static readonly int k_HistorySun = Shader.PropertyToID("_ThermalHistorySun");
        static readonly int k_HistoryAir = Shader.PropertyToID("_ThermalHistoryAir");
        static readonly int k_HistoryWeights = Shader.PropertyToID("_ThermalHistoryWeights");
        // Fixed lengths: a global array keeps the length it was first set with.
        [NonSerialized] readonly Vector4[] m_HistorySun = new Vector4[8];
        [NonSerialized] readonly Vector4[] m_HistoryAir = new Vector4[8];
        [NonSerialized] readonly Vector4[] m_HistoryWeightRows = new Vector4[2 * ThermalSurfaceGroups.HistoryGroups.Length];
        [NonSerialized] readonly double[] m_DayAir = new double[k_HistoryHours];
        [NonSerialized] readonly double[] m_DayWind = new double[k_HistoryHours];
        [NonSerialized] bool m_HistoryMissing;
        [NonSerialized] bool m_WarnedHistory;

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
        [NonSerialized] bool m_WarnedProfile;

        /// <summary>
        /// The site and the day's air one push works from: the profile's, sampled at the instant, when one is
        /// set, otherwise the settings. One place decides, so the sun, the sunlight and the air can never come
        /// from different sites - and a profile never writes into the settings, so a scene saved while one is
        /// set keeps its own values.
        /// </summary>
        public struct SiteConditions
        {
            public double latitude, longitude, altitude;   // degrees (east positive), m
            public double minK, maxK, dewPointK;           // the day's air extremes and dew point
            public double a, b, c;                         // Parton-Logan, hours
            public double linkeTurbidity;
            public double windSpeed;                       // m/s at 10 m (item 6)
            public bool windFromProfile;
        }

        /// <summary>The site profile driving the site, or null when the settings do.</summary>
        public ThermalSiteProfile SiteProfile => m_SiteProfile != null && m_SiteProfile.IsValid ? m_SiteProfile : null;

        /// <summary>The site conditions of the last update.</summary>
        public SiteConditions Site { get; private set; }

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

        /// <summary>Air temperature at the last update, K, from ThermalMath.AirTemperature.</summary>
        public double AirTemperature { get; private set; } = k_FallbackAirTemperature;

        /// <summary>Relative humidity 0-1 at the last update, from the air temperature and the dew point.</summary>
        public double RelativeHumidity { get; private set; } = k_FallbackHumidity;

        /// <summary>
        /// Sunrise and sunset, UTC, from ThermalMath.SunriseSunset, of the site's own day: its date in local
        /// mean solar time, which runs up to half a day from the UTC date far from Greenwich.
        /// </summary>
        public SunTimes SunriseSunsetUtc { get; private set; }

        /// <summary>True while the air functions are not written and the old constant air stands in.</summary>
        public bool AirIsPlaceholder => m_AirModelMissing;

        /// <summary>
        /// Broadband clear-sky emissivity of a flat surface at the current air temperature and humidity,
        /// from ThermalMath.ClearSkyEmissivity. NaN while that function is not written.
        /// </summary>
        public float BroadbandFlatEmissivity => m_BroadbandFlat;

        /// <summary>The broadband zenith emissivity the balance's sky table is baked with.</summary>
        public float BroadbandZenithEmissivity => m_BroadbandZenith;

        /// <summary>Clear-sky sunlight at the last update: direct normal, diffuse and global horizontal.</summary>
        public SolarIrradiance Sunlight { get; private set; }

        /// <summary>
        /// The air's radiance per sub-band at the last update, W/m²/sr, in ThermalMath.SubBandEdges' order: the LUT at
        /// the air temperature, read as the shader's ThermalSubBandRadiance reads it. The same for every pixel, so
        /// worked out once per frame here (item 10).
        /// </summary>
        public double[] AirSubBandRadiance { get; } = new double[4];

        /// <summary>True while the clear-sky functions are not written and the old 900 W/m2 stands in.</summary>
        public bool SunlightIsPlaceholder => m_ClearSkyMissing;

        /// <summary>The atmosphere's band tables, or null.</summary>
        public ThermalAtmosphere Atmosphere => m_Atmosphere;

        /// <summary>The atmosphere's table for the shader, or null before the first push.</summary>
        public ThermalAtmosphereTable AtmosphereTable => m_AtmosphereTable;

        /// <summary>How long ago each stored-heat sample is, seconds; the first, 0, is now.</summary>
        public static IReadOnlyList<double> HistoryLagSeconds => k_HistoryLags;

        /// <summary>
        /// Each stored-heat group's H at the last update, W/m²K, in <see cref="ThermalSurfaceGroups.HistoryGroups"/>'
        /// order.
        /// </summary>
        public double[] HistoryConductance { get; } = new double[ThermalSurfaceGroups.HistoryGroups.Length];

        /// <summary>Each stored-heat group's weights at the last update, one per sample of HistoryLagSeconds.</summary>
        public double[][] HistoryWeightsOf { get; } = NewWeightRows();

        /// <summary>True while ThermalMath.HistoryWeights is not written and every group weighs now alone.</summary>
        public bool HistoryIsPlaceholder => m_HistoryMissing;

        static double[][] NewWeightRows()
        {
            var rows = new double[ThermalSurfaceGroups.HistoryGroups.Length][];
            for (int r = 0; r < rows.Length; r++)
                rows[r] = new double[k_HistoryLags.Length];
            return rows;
        }

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
            m_LutData = null;
            m_LutRadiance = null;
            m_AtmosphereTable?.Dispose();
            m_AtmosphereTable = null;
            DestroyTable(ref m_SkyView);
            DestroyTable(ref m_SkyDiffuse);
            m_SkyViewData = m_SkyDiffuseData = null;
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

            // One instant and one site for the whole push: the air, the sky derived from it, and the sun.
            DateTime utc = SimulatedUtc;
            SiteConditions site = ConditionsAt(utc);
            Site = site;
            UpdateAir(utc, site);
            PushAtmosphere(utc, site);

            EnsureSky();
            if (m_SkyView != null && m_SkyDiffuse != null)
            {
                Shader.SetGlobalTexture(k_SkyView, m_SkyView);
                Shader.SetGlobalTexture(k_SkyDiffuse, m_SkyDiffuse);
                Shader.SetGlobalVector(k_SkyParams, new Vector4(k_SkyViewSamples, k_SkyDiffuseSamples, 0, 0));
            }

            Shader.SetGlobalFloat(k_AirT, (float)AirTemperature);
            double[] air = AirSubBandRadiance;
            SubBandRadiance(AirTemperature, air);
            Shader.SetGlobalVector(k_AirRadiance, new Vector4((float)air[0], (float)air[1], (float)air[2], (float)air[3]));
            Shader.SetGlobalFloat(k_WindSpeed, (float)site.windSpeed);

            SolarAngles sun = ThermalMath.Solar(site.latitude, site.longitude, utc);
            Sun = sun;
            SunUtc = utc;

            Vector3 sunDirection = SunDirection(sun, m_NorthRotation);
            Shader.SetGlobalVector(k_SunDir, sunDirection);

            SolarIrradiance sunlight = ComputeSunlight(sun, utc, site);
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

            PushHistory(utc, site, sunDirection, sunlight);
        }

        /// <summary>
        /// The site conditions at this instant: the profile's, blended between its mid-month values, or the
        /// settings. A pure function of the instant, like everything the balance is driven by (D-008).
        /// </summary>
        SiteConditions ConditionsAt(DateTime utc)
        {
            if (m_SiteProfile != null)
            {
                if (m_SiteProfile.IsValid)
                {
                    ThermalSiteProfile.Month m = m_SiteProfile.Sample(utc);
                    return new SiteConditions
                    {
                        latitude = m_SiteProfile.Latitude, longitude = m_SiteProfile.Longitude,
                        altitude = m_SiteProfile.Altitude,
                        minK = m.minK, maxK = m.maxK, dewPointK = m.dewPointK,
                        a = m.a, b = m.b, c = m.c, linkeTurbidity = m.linkeTurbidity,
                        windSpeed = m_SiteProfile.HasWind ? m_SiteProfile.WindSpeed(utc) : m_WindSpeed,
                        windFromProfile = m_SiteProfile.HasWind,
                    };
                }
                if (!m_WarnedProfile)
                {
                    Debug.LogWarning($"[Thermal] site profile '{m_SiteProfile.name}' has no twelve months - " +
                                     "the settings drive the site instead. Reimport its .siteprofile");
                    m_WarnedProfile = true;
                }
            }
            return new SiteConditions
            {
                latitude = m_Latitude, longitude = m_Longitude, altitude = m_SiteAltitude,
                minK = m_AirTemperatureMin, maxK = m_AirTemperatureMax, dewPointK = m_DewPoint,
                a = m_AirCurveA, b = m_AirCurveB, c = m_AirCurveC, linkeTurbidity = m_LinkeTurbidity,
                windSpeed = m_WindSpeed,
            };
        }

        /// <summary>
        /// Air temperature and relative humidity at this instant, from ThermalMath. Both are pure
        /// functions of simulated time, so the same instant always gives the same air. A result that
        /// fails CheckAir is not used: the previous air stays.
        /// </summary>
        void UpdateAir(DateTime utc, SiteConditions site)
        {
            if (!m_AirModelMissing)
            {
                try
                {
                    double air = ThermalMath.AirTemperature(utc, site.latitude, site.longitude,
                                                            site.minK, site.maxK, site.a, site.b, site.c);
                    double humidity = ThermalMath.RelativeHumidity(air, site.dewPointK);
                    DateTime siteDate = utc.AddHours(site.longitude / 15.0).Date;
                    SunriseSunsetUtc = ThermalMath.SunriseSunset(siteDate, site.latitude, site.longitude);
                    if (CheckAir(air, humidity, site))
                    {
                        AirTemperature = air;
                        RelativeHumidity = humidity;
                    }
                    return;
                }
                catch (NotImplementedException)
                {
                    // Tried once per domain reload: writing the functions recompiles, which clears this.
                    m_AirModelMissing = true;
                    Debug.LogWarning("[Thermal] ThermalMath's air functions are not written yet, so the air " +
                                     $"stays at the old constant {k_FallbackAirTemperature} K and " +
                                     $"{k_FallbackHumidity * 100:F0}% humidity");
                }
                catch (Exception e)
                {
                    // A bug, or a site outside the contract - the sun must rise and set that day. The sim
                    // runs on, on the previous air, rather than failing every push.
                    if (!m_WarnedAirThrew)
                    {
                        Debug.LogWarning($"[Thermal] the air functions threw {e.GetType().Name} ({e.Message}) " +
                                         "- keeping the previous air");
                        m_WarnedAirThrew = true;
                    }
                    return;
                }
            }
            AirTemperature = k_FallbackAirTemperature;
            RelativeHumidity = k_FallbackHumidity;
        }

        /// <summary>
        /// Bounds the air functions must keep, checked every push but reported once: the temperature
        /// inside the day's minimum and maximum - Parton-Logan never leaves them - and a humidity in (0, 1].
        /// </summary>
        bool CheckAir(double air, double humidity, SiteConditions site)
        {
            string problem = null;
            if (!(air >= site.minK - 1e-6 && air <= site.maxK + 1e-6))   // false for NaN too
                problem = $"air temperature {air} K is outside the day's {site.minK}-{site.maxK} K";
            else if (!(humidity > 0.0 && humidity <= 1.0))
                problem = $"relative humidity {humidity} is not in (0, 1]";
            if (problem == null)
                return true;
            if (!m_WarnedAir)
            {
                Debug.LogWarning($"[Thermal] {problem} - keeping the previous air");
                m_WarnedAir = true;
            }
            return false;
        }

        /// <summary>
        /// Clear-sky sunlight for this instant. Zero with the sun at or below the horizon, whatever a
        /// model gives there: below it sunDirection.y &lt; 0, so dot(N, sunDirection) is POSITIVE for
        /// downward-facing surfaces, and they would be lit by a sun shining up through the ground.
        /// </summary>
        SolarIrradiance ComputeSunlight(SolarAngles sun, DateTime utc, SiteConditions site)
        {
            if (sun.elevation <= 0.0)
                return default;

            if (!m_ClearSkyMissing)
            {
                try
                {
                    double e0 = ThermalMath.ExtraterrestrialIrradiance(utc.DayOfYear);
                    SolarIrradiance s = ThermalMath.ClearSkyIrradiance(sun.elevation, site.altitude,
                                                                       site.linkeTurbidity, e0);
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

        // ---------------------------------------------------------------------- stored heat (item 5 step 2)

        /// <summary>
        /// The past samples and, per group that stores heat, its H and weights (ThermalMath.HistoryWeights). All pure
        /// functions of the instant, like everything the balance reads (D-008); sample 0 is exactly what this push
        /// sent. While HistoryWeights is not written every group weighs now alone, which is the steady state.
        /// </summary>
        void PushHistory(DateTime utc, SiteConditions site, Vector3 sunDirection, SolarIrradiance sunlight)
        {
            for (int k = 0; k < k_HistoryLags.Length; k++)
            {
                Vector3 direction = sunDirection;
                SolarIrradiance light = sunlight;
                double air = AirTemperature, wind = site.windSpeed;
                if (k > 0)
                {
                    DateTime past = utc.AddSeconds(-k_HistoryLags[k]);
                    SiteConditions s = ConditionsAt(past);
                    SolarAngles sun = ThermalMath.Solar(s.latitude, s.longitude, past);
                    direction = SunDirection(sun, m_NorthRotation);
                    light = ComputeSunlight(sun, past, s);
                    air = AirAt(past, s);
                    wind = s.windSpeed;
                }
                m_HistorySun[k] = new Vector4(direction.x, direction.y, direction.z, (float)light.directNormal);
                m_HistoryAir[k] = new Vector4((float)air, (float)wind, (float)light.diffuseHorizontal,
                                              (float)light.globalHorizontal);
            }

            // The air and wind over the last day, hourly, for H.
            for (int i = 0; i < k_HistoryHours; i++)
            {
                if (i == 0)
                {
                    m_DayAir[0] = AirTemperature;
                    m_DayWind[0] = site.windSpeed;
                    continue;
                }
                DateTime past = utc.AddHours(-i);
                SiteConditions s = ConditionsAt(past);
                m_DayAir[i] = AirAt(past, s);
                m_DayWind[i] = s.windSpeed;
            }

            ThermalSurfaceGroup[] groups = ThermalSurfaceGroups.HistoryGroups;
            for (int r = 0; r < groups.Length; r++)
            {
                // H, the surface's linearised conductance to its surroundings: radiation and the wind's convection,
                // averaged over the day the weights look back on (measured with the day's mean, item 5).
                (float a, float b) = ThermalSurfaceGroups.Wind(groups[r]);
                double h = 0;
                for (int i = 0; i < k_HistoryHours; i++)
                {
                    double t = m_DayAir[i];
                    h += 4 * k_HistoryEmissivity * k_Sigma * t * t * t + a * Math.Pow(m_DayWind[i], b);
                }
                h /= k_HistoryHours;
                HistoryConductance[r] = h;

                double[] w = HistoryWeightsOf[r];
                Weigh(ThermalSurfaceGroups.Effusivity(groups[r]), h, w);
                m_HistoryWeightRows[2 * r] = new Vector4((float)w[0], (float)w[1], (float)w[2], (float)w[3]);
                m_HistoryWeightRows[2 * r + 1] = new Vector4((float)w[4], (float)w[5], (float)w[6], (float)w[7]);
            }

            Shader.SetGlobalVectorArray(k_HistorySun, m_HistorySun);
            Shader.SetGlobalVectorArray(k_HistoryAir, m_HistoryAir);
            Shader.SetGlobalVectorArray(k_HistoryWeights, m_HistoryWeightRows);
        }

        /// <summary>
        /// The air at a past instant, without UpdateAir's state: the same function, or the same stand-in while it is
        /// missing. A function that throws has been reported by UpdateAir; the current air stands in.
        /// </summary>
        double AirAt(DateTime utc, SiteConditions site)
        {
            if (m_AirModelMissing)
                return k_FallbackAirTemperature;
            try
            {
                return ThermalMath.AirTemperature(utc, site.latitude, site.longitude, site.minK, site.maxK,
                                                  site.a, site.b, site.c);
            }
            catch (Exception)
            {
                return AirTemperature;
            }
        }

        /// <summary>
        /// One group's weights from ThermalMath.HistoryWeights, held to its contract: finite and summing to 1. Anything
        /// else - not written yet, a throw, a bad sum - weighs now alone, the steady state, and is reported once.
        /// </summary>
        void Weigh(double effusivity, double conductance, double[] weights)
        {
            if (!m_HistoryMissing)
            {
                try
                {
                    ThermalMath.HistoryWeights(effusivity, conductance, k_HistoryLags, weights);
                    double sum = 0;
                    bool finite = true;
                    foreach (double w in weights)
                    {
                        sum += w;
                        finite &= !double.IsNaN(w) && !double.IsInfinity(w);
                    }
                    if (finite && Math.Abs(sum - 1.0) < 1e-6)
                        return;
                    WarnHistory($"ThermalMath.HistoryWeights gave weights summing to {sum}, not 1");
                }
                catch (NotImplementedException)
                {
                    // Tried once per domain reload: writing the function recompiles, which clears this.
                    m_HistoryMissing = true;
                    Debug.LogWarning("[Thermal] ThermalMath.HistoryWeights is not written yet, so masonry and ground " +
                                     "weigh now alone: the steady state, as before (item 5 step 2)");
                }
                catch (Exception e)
                {
                    WarnHistory($"ThermalMath.HistoryWeights threw {e.GetType().Name} ({e.Message})");
                }
            }
            Array.Clear(weights, 0, weights.Length);
            weights[0] = 1.0;
        }

        void WarnHistory(string problem)
        {
            if (m_WarnedHistory)
                return;
            Debug.LogWarning($"[Thermal] {problem} - masonry and ground weigh now alone until it is fixed");
            m_WarnedHistory = true;
        }

        // ---------------------------------------------------------------------- LUT

        void EnsureLut()
        {
            if (m_Lut != null)
                return;

            m_Lut = new Texture2D(k_LutSamples, 1, TextureFormat.RGBAFloat, false)
            {
                name = "ThermalSubBandLut",
                // Created by code in an [ExecuteAlways] component, so it exists in edit mode:
                // without this it can be saved into the scene, it logs "leaked" warnings, and
                // UnloadUnusedAssets can free it while still bound - a shader global is not a
                // reference Unity tracks. The price is that OnDisable must destroy it.
                hideFlags = HideFlags.HideAndDontSave,
                // Read with Load and blended in the shader (ThermalSubBandRadiance), never filtered by the hardware.
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            // The temperatures D-005 verified, each texel BandIntegration's samples split by sub-band: the four
            // channels sum to the one-channel LUT before them within float32 rounding (8e-8 relative over 200-400 K,
            // IMTNS/atmosphere/subband_check_ref.py; Thermal → Sub-band Check).
            var data = new float[k_LutSamples * 4];
            var subBands = new double[4];
            for (int i = 0; i < k_LutSamples; i++)
            {
                double t = i / (double)(k_LutSamples - 1);
                double T = k_LutLowK + t * (k_LutHighK - k_LutLowK);
                ThermalMath.SubBandIntegration(T, subBands);
                for (int k = 0; k < 4; k++)
                    data[i * 4 + k] = (float)subBands[k];
            }

            m_Lut.SetPixelData(data, 0);
            m_Lut.Apply(false, true);
            m_LutData = data;
            m_LutRadiance = (temperatureK, result) => ReadLut(data, temperatureK, result);
        }

        /// <summary>
        /// The LUT at <paramref name="temperatureK"/> per sub-band, as the shader's ThermalSubBandRadiance reads it:
        /// the two neighbouring entries blended linearly, clamped to the table's range. <paramref name="result"/> takes
        /// four, in ThermalMath.SubBandEdges' order.
        /// </summary>
        public void SubBandRadiance(double temperatureK, double[] result)
        {
            if (m_LutData == null)
            {
                ThermalMath.SubBandIntegration(temperatureK, result);
                return;
            }
            ReadLut(m_LutData, temperatureK, result);
        }

        static void ReadLut(float[] data, double temperatureK, double[] result)
        {
            double x = Math.Max(0.0, Math.Min(1.0, (temperatureK - k_LutLowK) / (k_LutHighK - k_LutLowK)))
                     * (k_LutSamples - 1);
            int i = Math.Min((int)Math.Floor(x), k_LutSamples - 2);
            for (int k = 0; k < 4; k++)
            {
                float a = data[i * 4 + k], b = data[(i + 1) * 4 + k];
                result[k] = a + (b - a) * (x - i);
            }
        }

        // ---------------------------------------------------------------------- atmosphere

        /// <summary>
        /// The atmosphere's table (step 3): started off the main thread for the camera's height and the air now when
        /// they have moved, taken when done, and bound for the shader every push.
        /// </summary>
        void PushAtmosphere(DateTime utc, SiteConditions site)
        {
            if (m_AtmosphereTable == null)
                m_AtmosphereTable = new ThermalAtmosphereTable();
            Camera camera = m_Camera != null ? m_Camera : Camera.main;
            if (camera != null && m_LutRadiance != null)
            {
                var inputs = new ThermalAtmosphereTable.Inputs
                {
                    air = AtmosphereAirAt(utc, site),
                    cameraHeightM = camera.transform.position.y,
                    farM = camera.farClipPlane,
                };
                m_AtmosphereTable.Update(m_Atmosphere, inputs, m_LutRadiance);
            }
            m_AtmosphereTable.Push();
        }

        /// <summary>
        /// The ground's air for the atmosphere's march at <paramref name="utc"/>: the air temperature of the last
        /// update, the site's dew point and altitude, and ozone by the season, 25 ppb at the December solstice to 40
        /// at the June one (the values IMTNS/atmosphere measured with), smooth between.
        /// </summary>
        public AtmosphereAir AtmosphereAirAt(DateTime utc, SiteConditions site)
        {
            double day = utc.DayOfYear - 1 + utc.TimeOfDay.TotalDays;
            return new AtmosphereAir
            {
                airTemperatureK = AirTemperature,
                dewPointK = site.dewPointK,
                altitudeM = site.altitude,
                ozone = 32.5e-9 + 7.5e-9 * Math.Cos(2 * Math.PI * (day - 171) / 365.25),
            };
        }

        // ---------------------------------------------------------------------- sky tables

        void EnsureSky()
        {
            EnsureBroadband();
            bool inband = m_BakedZenith != m_SkyZenithEmissivity;            // true for NaN, the first push
            bool broadband = m_BakedZenithBroadband != m_BroadbandZenith;
            if (!inband && !broadband)
                return;
            // Recorded before baking, so a sky model that is missing or throws is tried once per
            // setting, not every frame.
            m_BakedZenith = m_SkyZenithEmissivity;
            m_BakedZenithBroadband = m_BroadbandZenith;

            if (m_SkyViewData == null)
            {
                m_SkyViewData = new float[k_SkyViewSamples];
                m_SkyDiffuseData = new float[2 * k_SkyDiffuseSamples];
                inband = broadband = true;
            }
            float[] view = m_SkyViewData, diffuse = m_SkyDiffuseData;
            try
            {
                // Through the day only the broadband value moves, so a typical rebake is the G channel
                // alone - half the diffuse integrals.
                if (inband)
                {
                    for (int i = 0; i < k_SkyViewSamples; i++)
                    {
                        // the shader's inverse is sqrt(direction.y) - see SkyViewEmissivity
                        double t = i / (double)(k_SkyViewSamples - 1);
                        view[i] = (float)ThermalMath.SkyEmissivity(t * t, m_SkyZenithEmissivity);
                    }
                }
                for (int i = 0; i < k_SkyDiffuseSamples; i++)
                {
                    double normalY = -1.0 + 2.0 * i / (k_SkyDiffuseSamples - 1);
                    if (inband)
                        diffuse[2 * i] = (float)ThermalMath.SkyDiffuse(normalY, m_SkyZenithEmissivity);
                    if (broadband)
                        diffuse[2 * i + 1] = (float)ThermalMath.SkyDiffuse(normalY, m_BroadbandZenith);
                }
            }
            catch (NotImplementedException)
            {
                m_SkyViewData = m_SkyDiffuseData = null;   // part-filled: the next bake starts over
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
            if (m_WarnedNoClearSky)
                return;   // the function is missing: tried once per domain reload, the fallback stays

            double flat, zenith;
            try
            {
                // Cheap - an exp and a pow - so it runs every push; the inversion below does not.
                flat = ThermalMath.ClearSkyEmissivity(AirTemperature, RelativeHumidity);
                if (!(flat > 0.0 && flat < 1.0))   // also catches NaN
                {
                    if (!m_WarnedClearSkyRange)
                    {
                        Debug.LogWarning($"[Thermal] clear-sky emissivity {flat} is not in (0, 1), so the " +
                                         $"broadband sky stays at {m_BroadbandZenith}");
                        m_WarnedClearSkyRange = true;
                    }
                    return;
                }
                if (!float.IsNaN(m_BroadbandFlat) && Math.Abs(flat - m_BroadbandFlat) < k_BroadbandTolerance)
                    return;
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
