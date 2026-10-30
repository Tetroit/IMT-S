using System;


namespace IMT.Thermal
{
    public static class ThermalConstants
    {
        public const double BoltzmannConstant = 1.380649e-23;
        public const double SpeedOfLight = 299792458;
        public const double PlanckConstant = 6.62607015e-34;
    }

    public struct SolarAngles
    {
        public double azimuth; // degrees, clockwise from North
        public double elevation; // degrees above horizon, refraction-corrected
        public double declination; // degrees
        public double equationOfTime; // minutes

        public override string ToString()
        {
            return
                $"Azimuth: {azimuth}, Elevation: {elevation}, Declination: {declination}, EquationOfTime: {equationOfTime}";
        }
    }

    public struct SolarIrradiance
    {
        public double directNormal; // W/m2, on a surface facing the sun (DNI)
        public double diffuseHorizontal; // W/m2, scattered sky light on a flat surface (DHI)
        public double globalHorizontal; // W/m2, all of it on a flat surface: DNI * cos(zenith) + DHI

        public override string ToString()
        {
            return $"DNI: {directNormal}, DHI: {diffuseHorizontal}, GHI: {globalHorizontal}";
        }
    }

    public struct SunTimes
    {
        public DateTime sunrise; // UTC
        public DateTime sunset; // UTC

        public override string ToString()
        {
            return $"Sunrise: {sunrise:yyyy-MM-dd HH:mm:ss} UTC, Sunset: {sunset:yyyy-MM-dd HH:mm:ss} UTC";
        }
    }

    /// <summary>The ground's air a ray from the camera starts in (the atmosphere, step 3).</summary>
    public struct AtmosphereAir
    {
        public double airTemperatureK; // at the ground, now (ThermalEnvironment.AirTemperature)
        public double dewPointK; // the site's
        public double altitudeM; // the ground above sea level
        public double ozone; // mixing ratio, e.g. 40e-9
    }

    public static class ThermalMath
    {
        const double Deg2Rad = Math.PI / 180.0;
        const double Rad2Deg = 180.0 / Math.PI;

        static double SinD(double deg) => Math.Sin(deg * Deg2Rad);
        static double CosD(double deg) => Math.Cos(deg * Deg2Rad);
        static double TanD(double deg) => Math.Tan(deg * Deg2Rad);

        static double AsinD(double x) => Math.Asin(x) * Rad2Deg;
        static double AcosD(double x) => Math.Acos(x) * Rad2Deg;
        static double Atan2D(double y, double x) => Math.Atan2(y, x) * Rad2Deg;

        public static double ComputePlanckLaw(double lambdaMeter, double temperatureK)
        {
            double x = (ThermalConstants.PlanckConstant * ThermalConstants.SpeedOfLight) /
                       (lambdaMeter * ThermalConstants.BoltzmannConstant * temperatureK);
            double secondHalf;
            if (x > 30)
            {
                secondHalf = Math.Exp(-x);
            }
            else
            {
                secondHalf = 1.0 / (Math.Exp(x) - 1.0);
            }


            var B = ((2 * ThermalConstants.PlanckConstant * Math.Pow(ThermalConstants.SpeedOfLight, 2)) /
                     Math.Pow(lambdaMeter, 5)) * secondHalf;
            return B;
        }


        public static double BandIntegration(double temperatureK)
        {
            double lo = 8e-6, hi = 14e-6, n = 100;
            var dl = (hi - lo) / n;
            double integral = 0;
            for (int i = 0; i < n; i++)
            {
                integral += ComputePlanckLaw(lo + (i + 0.5) * dl, temperatureK);
            }

            integral *= dl;

            return integral;
        }

        /// <summary>
        /// The atmosphere's four sub-bands (ThermalAtmosphere), in wavenumber order: 14-12.5, 12.5-10.22, 10.22-9.26
        /// (ozone) and 9.26-8 µm. Their edges fall between BandIntegration's samples (every 0.06 µm from 8 µm), so
        /// SubBandIntegration splits exactly those samples and the four sum to BandIntegration.
        /// </summary>
        public static readonly double[] SubBandEdges = { 14e-6, 12.5e-6, 10.22e-6, 9.26e-6, 8e-6 };

        /// <summary>BandIntegration per sub-band: <paramref name="result"/> takes the four, in SubBandEdges' order.</summary>
        public static void SubBandIntegration(double temperatureK, double[] result)
        {
            double lo = 8e-6, hi = 14e-6, n = 100;
            var dl = (hi - lo) / n;
            Array.Clear(result, 0, 4);
            for (int i = 0; i < n; i++)
            {
                double lambda = lo + (i + 0.5) * dl;
                int k = 0;
                while (lambda < SubBandEdges[k + 1])
                    k++;
                result[k] += ComputePlanckLaw(lambda, temperatureK);
            }

            for (int k = 0; k < 4; k++)
                result[k] *= dl;
        }

        // all calcs in double since we need the precision
        public static SolarAngles Solar(double latDeg, double lonDeg, DateTime utc)
        {
            double mod360(double x)
            {
                return ((x % 360) + 360) % 360;
            }

            // Julian day and century
            double year = utc.Year;
            double month = utc.Month;
            double dayFraction = utc.Day + (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;

            if (month <= 2)
            {
                year -= 1.0;
                month += 12.0; // julian calendar makes jan/feb month 13/14 in previous year
            }

            double century = Math.Floor(year / 100.0);
            double gregorian = 2.0 - century + Math.Floor(century / 4.0);

            double julianDay = Math.Floor(365.25 * (year + 4716.0)) + Math.Floor(30.6001 * (month + 1)) + dayFraction +
                gregorian - 1524.5;

            double julianCentury = (julianDay - 2451545.0) / 36525.0;
            double t = julianCentury;

            // sun calculations
            double meanLongitude = mod360(280.46646 + t * (36000.76983 + t * 0.0003032));
            double meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
            double eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

            double equationOfCentre = SinD(meanAnomaly) * (1.914602 - t * (0.004817 + 0.000014 * t))
                                      + SinD(2 * meanAnomaly) * (0.019993 - 0.000101 * t)
                                      + SinD(3 * meanAnomaly) * 0.000289;

            double trueLongitude = meanLongitude + equationOfCentre;
            double omega = 125.04 - 1934.136 * t;
            double apperentLongitude = trueLongitude - 0.00569 - 0.00478 * SinD(omega);

            // obliquity
            double meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0;
            double obliquity = meanObliquity + 0.00256 * CosD(omega);
            double declination = AsinD(SinD(obliquity) * SinD(apperentLongitude));

            // time and hour angle
            double eqTimeY = Math.Pow(TanD(obliquity / 2), 2.0);

            double equationOfTime = 4 * Rad2Deg *
                                    (eqTimeY * SinD(2 * meanLongitude) - 2 * eccentricity * SinD(meanAnomaly)
                                     + 4 * eccentricity * eqTimeY * SinD(meanAnomaly) * CosD(2 * meanLongitude)
                                     - 0.5 * eqTimeY * eqTimeY * SinD(4 * meanLongitude)
                                     - 1.25 * eccentricity * eccentricity * SinD(2 * meanAnomaly)); // in minutes

            double utcMinutes = utc.Hour * 60 + utc.Minute + utc.Second / 60.0;
            double trueSolarTime = utcMinutes + equationOfTime + 4 * lonDeg; // in minutes

            double hourAngle = mod360(trueSolarTime / 4.0) - 180; // [-180, 180)

            // elevation and azimuth
            double cosZenith = SinD(latDeg) * SinD(declination)
                               + CosD(latDeg) * CosD(declination) * CosD(hourAngle);

            double elevation = 90 - AcosD(Math.Clamp(cosZenith, -1, 1));

            double azimuth = Atan2D(SinD(hourAngle), CosD(hourAngle) * SinD(latDeg) - TanD(declination) * CosD(latDeg));
            azimuth = mod360(azimuth + 180); // clockwise from north

            // refraction
            double te = TanD(elevation);
            double refraction;
            if (elevation > 85) refraction = 0;
            else if (elevation > 5)
                refraction = 58.1 / te - 0.07 / (te * te * te) + 0.000086 / (te * te * te * te * te);
            else if (elevation > -0.575)
                refraction = 1735 + elevation * (-518.2 + elevation * (103.4
                                                                       + elevation * (-12.79 + elevation * 0.711)));
            else refraction = -20.772 / te;

            elevation += refraction / 3600.0;

            SolarAngles solarAngles = new SolarAngles
            {
                elevation = elevation,
                azimuth = azimuth,
                declination = declination,
                equationOfTime = equationOfTime
            };

            return solarAngles;
        }

        /// <summary>
        /// Sky emissivity looking along a direction <paramref name="cosZenith"/> from straight up
        /// (1 = zenith, 0 = horizon). Must give <paramref name="epsZenith"/> at the zenith and 1 at the
        /// horizon - the bake samples cosZenith = 0 exactly, and the shader returns that entry for every
        /// direction at or below the horizon.
        /// </summary>
        public static double SkyEmissivity(double cosZenith, double epsZenith)
        {
            if (cosZenith <= 0)
                return 1;

            return 1 - Math.Pow((1 - epsZenith), (1 / cosZenith));
        }

        /// <summary>
        /// Visible-sky diffuse factor for an unobstructed surface over flat ground whose normal has
        /// vertical component <paramref name="normalY"/>: SkyEmissivity times the cosine to the normal,
        /// integrated over the part of the sky the surface faces, divided by pi. It contains the view
        /// factor - for a uniform sky it equals eps * 0.5 * (1 + normalY).
        /// Reference, epsZenith 0.191 (independent quadrature): 0 at normalY -1, 0.151 at -0.5,
        /// 0.245 at 0, 0.306 at 0.5, 0.310 at 1.
        /// </summary>
        public static double SkyDiffuse(double normalY, double epsZenith)
        {
            var nx = Math.Sqrt(1 - normalY * normalY);

            double sum = 0;

            for (int i = 0; i < 64; ++i)
            {
                double theta = (i + 0.5) * (Math.PI / 2) / 64;
                for (int j = 0; j < 128; ++j)
                {
                    double phi = (j + 0.5) * 2 * Math.PI / 128;
                    double cosToNormal = nx * Math.Sin(theta) * Math.Cos(phi) + normalY * Math.Cos(theta);
                    if (cosToNormal > 0)
                    {
                        sum += SkyEmissivity(Math.Cos(theta), epsZenith) * cosToNormal * Math.Sin(theta);
                    }
                }
            }

            return sum * (Math.PI / 2 / 64) * (2 * Math.PI / 128) / Math.PI;
        }

        /// <summary>
        /// Broadband (all-longwave) clear-sky emissivity seen by a flat, upward-facing surface, from air
        /// temperature in kelvin and relative humidity 0-1. Called by ThermalEnvironment whenever either
        /// changes; the result sets the balance's sky through <see cref="ZenithEmissivityForFlat"/>.
        /// Reference, Brutsaert (1975) with Tetens vapour pressure: 0.8157 at 295 K and 0.6,
        /// 0.7388 at 0.3, 0.8643 at 0.9.
        /// </summary>
        public static double ClearSkyEmissivity(double airTemperatureK, double relativeHumidity)
        {
            double t = airTemperatureK - 273.15; // convert to degrees C
            double e = relativeHumidity * 6.1078 * Math.Exp(17.27 * t / (t + 237.3));
            double emissivity = 1.24 * Math.Pow((e / airTemperatureK), (1.0 / 7.0));
            return emissivity;
        }

        /// <summary>
        /// The zenith emissivity at which SkyDiffuse on a flat, upward-facing surface equals
        /// <paramref name="flatEmissivity"/>. Numerics, not physics: a clear-sky formula gives the flat
        /// value, and the airmass form needs the zenith one to reach every other tilt. Bisection, because
        /// SkyDiffuse rises steadily with the zenith value, from 0 at 0 to 1 at 1; 30 halvings reach
        /// ~1e-9. Reference: 0.8157 -> 0.6767.
        /// </summary>
        public static double ZenithEmissivityForFlat(double flatEmissivity)
        {
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 30; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (SkyDiffuse(1.0, mid) < flatEmissivity)
                    lo = mid;
                else
                    hi = mid;
            }

            return 0.5 * (lo + hi);
        }

        // clear-sky sunlight - called by ThermalEnvironment every push, and checked against pvlib by
        // Thermal > Clear Sky Check

        /// <summary>
        /// Relative airmass: how many atmospheres thick the path to the sun is, 1 straight up. Kasten &amp;
        /// Young (1989), which stays finite at the horizon. <paramref name="elevationDeg"/> is the
        /// refraction-corrected elevation, as Solar returns it.
        /// </summary>
        public static double RelativeAirmass(double elevationDeg)
        {
            double m = 1 / (SinD(elevationDeg) + 0.50572 * Math.Pow(elevationDeg + 6.07995, -1.6364));
            return m;
        }

        /// <summary>
        /// Solar irradiance above the atmosphere, W/m2, on day <paramref name="dayOfYear"/> (1 = 1 January):
        /// the solar constant times Spencer's (1971) Earth-Sun distance factor.
        /// </summary>
        public static double ExtraterrestrialIrradiance(int dayOfYear)
        {
            double B = 2 * Math.PI * (dayOfYear - 1) / 365.0;
            double factor = 1.00011 + 0.034221 * Math.Cos(B) + 0.00128 * Math.Sin(B) + 0.000719 * Math.Cos(2 * B) +
                            0.000077 * Math.Sin(2 * B);
            double E0 = 1366.1 * factor;
            return E0;
        }

        /// <summary>
        /// Clear-sky sunlight at the ground, Ineichen &amp; Perez (2002): direct normal, diffuse horizontal
        /// and global horizontal. <paramref name="altitudeM"/> is the site's height above sea level,
        /// <paramref name="linkeTurbidity"/> how hazy the air is (~3 for a clear mid-latitude sky),
        /// <paramref name="extraterrestrial"/> from ExtraterrestrialIrradiance. All three must be 0 with the
        /// sun at or below the horizon, and globalHorizontal = directNormal * sin(elevation) + diffuseHorizontal.
        /// </summary>
        public static SolarIrradiance ClearSkyIrradiance(double elevationDeg, double altitudeM,
            double linkeTurbidity, double extraterrestrial)
        {
            if (elevationDeg <= 0)
                return new SolarIrradiance() { diffuseHorizontal = 0, directNormal = 0, globalHorizontal = 0 };

            double cosZ = SinD(elevationDeg);
            double pressure = 100 * Math.Pow((44331.514 - altitudeM) / 11880.516, 1 / 0.1902632);
            double airMass = RelativeAirmass(elevationDeg) * pressure / 101325.0;

            double fh1 = Math.Exp(-altitudeM / 8000.0);
            double fh2 = Math.Exp(-altitudeM / 1250.0);
            double cg1 = 5.09e-5 * altitudeM + 0.868;
            double cg2 = 3.92e-5 * altitudeM + 0.0387;
            double TL = linkeTurbidity;
            double E0 = extraterrestrial;

            double ghi = cg1 * E0 * cosZ * Math.Exp(-cg2 * airMass * (fh1 + fh2 * (TL - 1)));
            double dniA = (0.664 + 0.163 / fh1) * E0 * Math.Exp(-0.09 * airMass * (TL - 1));
            double dniB = ghi * (1 - (0.1 - 0.2 * Math.Exp(-TL)) / (0.1 + 0.882 / fh1)) / cosZ;
            double dni = Math.Min(dniA, dniB);
            double dhi = ghi - dni * cosZ;

            return new SolarIrradiance() { directNormal = dni, diffuseHorizontal = dhi, globalHorizontal = ghi };
        }

        // air through the day (Next, item 3) - called by ThermalEnvironment every push

        /// <summary>
        /// Sunrise and sunset, both UTC, for the day whose solar noon falls on <paramref name="utcDate"/>'s
        /// UTC date: when the centre of the sun is 0.833 degrees below the horizon (refraction plus the
        /// sun's radius), NOAA's convention. Declination and equation of time from Solar at 12:00 UTC of
        /// that date - fixed here so a check can be exact; other instants move the result by seconds.
        /// Only for latitudes where the sun rises and sets that day.
        /// </summary>
        public static SunTimes SunriseSunset(DateTime utcDate, double latDeg, double lonDeg)
        {
            DateTime day = new DateTime(utcDate.Year, utcDate.Month, utcDate.Day, 0, 0, 0, DateTimeKind.Utc);
            SolarAngles s = Solar(latDeg, lonDeg, day.AddHours(12));
            double HA = AcosD(CosD(90.833) / (CosD(latDeg) * CosD(s.declination)) - TanD(latDeg) * TanD(s.declination));
            double noon = 720 - 4 * lonDeg - s.equationOfTime;
            DateTime sunrise = day.AddMinutes(noon - 4 * HA);
            DateTime sunset = day.AddMinutes(noon + 4 * HA);
            return new SunTimes() { sunrise = sunrise, sunset = sunset };
        }

        /// <summary>
        /// Air temperature at <paramref name="utc"/>, Parton &amp; Logan (1981): a truncated sine from the
        /// day's minimum, near sunrise, through its maximum in the afternoon to sunset, then an exponential
        /// decline through the night to the next minimum. <paramref name="minK"/> and <paramref name="maxK"/>
        /// are the day's extremes, equal for a constant air temperature. Must be a pure function of time -
        /// D-008's history evaluates it at past instants - and continuous: no step at sunrise or sunset.
        /// The original night curve does not end at the next minimum, which leaves a step at dawn unless
        /// it is rescaled to.
        /// Coefficients, in hours: <paramref name="a"/> the maximum's lag, <paramref name="b"/> the night
        /// decay, <paramref name="c"/> the minimum's time after sunrise. Fitted to KNMI De Bilt clear days
        /// 2021-2026, rescaled night form (IMTNS/fit_air_temperature.py): a 2.19, b 2.58, c 0.22.
        /// </summary>
        public static double AirTemperature(DateTime utc, double latDeg, double lonDeg, double minK, double maxK,
            double a, double b, double c)
        {
            DateTime day = utc.AddHours(lonDeg / 15).Date;
            SunTimes today = SunriseSunset(day, latDeg, lonDeg);
            DateTime tMin = today.sunrise.AddHours(c);

            Func<SunTimes, double, double> DayCurve = (s, m) =>
            {
                double y = (s.sunset - s.sunrise).TotalHours;
                return minK + (maxK - minK) * Math.Sin(Math.PI * m / (y + 2 * a));
            };

            Func<SunTimes, DateTime, double> Night = (s, next) =>
            {
                double TSS = DayCurve(s, (s.sunset - s.sunrise.AddHours(c)).TotalHours);
                double n = (utc - s.sunset).TotalHours;
                double Z = (next - s.sunset).TotalHours;
                return minK + (TSS - minK) * (Math.Exp(-b * n / Z) - Math.Exp(-b)) / (1 - Math.Exp(-b));
            };

            if (utc < tMin)
            {
                return Night(SunriseSunset((day.AddDays(-1)), latDeg, lonDeg), tMin);
            }

            if (utc <= today.sunset)
            {
                return DayCurve(today, (utc - tMin).TotalHours);
            }

            return Night(today, (SunriseSunset(day.AddDays(1), latDeg, lonDeg).sunrise).AddHours(c));
        }

        /// <summary>
        /// Relative humidity 0-1 from air temperature and dew point, both kelvin: saturation vapour pressure
        /// at the dew point over that at the air temperature (Tetens, as in ClearSkyEmissivity), capped at
        /// 1 for a dew point above the air temperature. Reference: 0.6000 at 295 K and 286.8908 K;
        /// 0.4961 at 295 K and 284 K; 0.9359 at 285 K and 284 K.
        /// </summary>
        public static double RelativeHumidity(double airTemperatureK, double dewPointK)
        {
            Func<double, double> E = (double temp) =>
            {
                double t = temp - 273.15; // normalize to degrees C
                return 6.1078 * Math.Exp(17.27 * t / (t + 237.3));
            };

            double RH = Math.Min(1, E(dewPointK) / E(airTemperatureK));
            return RH;
        }

        /// <summary>
        /// Item 5 step 2, stored heat: how much each past sample counts, for a surface on a semi-infinite solid of
        /// effusivity <paramref name="effusivity"/> (e = √(kρc), J m⁻² K⁻¹ s^-½) behind a surface conductance
        /// <paramref name="conductance"/> (H, W m⁻² K⁻¹). <paramref name="lagSeconds"/>: how long ago each sample is,
        /// ascending, the first 0 (now), all under a day. Fills <paramref name="weights"/>, the same length; they sum
        /// to 1, so unchanging inputs give the steady state.
        /// After its inputs step by 1, the surface has moved by R(t) = 1 − erfcx(β√t), β = H / e (Carslaw &amp;
        /// Jaeger), and R's mean over [a, b] is 1 − (G(β√b) − G(β√a)) / (β²(b − a)), G(x) = erfcx(x) + 2x/√π − 1
        /// (<see cref="ThermalSpecialFunctions.Erfcx"/>). The inputs are linear between samples, and after the last one
        /// run back to sample 0's value at 24 h: every earlier day is taken to be the same as the last 24 h, so the
        /// past folds onto one day. Sixty days of intervals, then the rest, 1 − R(60 d), shared by each sample's part
        /// of the day. Decided 2026-10-26; Thermal → History Check holds it to IMTNS/history_check_ref.py.
        /// </summary>
        public static void HistoryWeights(double effusivity, double conductance, double[] lagSeconds, double[] weights)
        {
            double responseTime = Math.Pow(effusivity / conductance, 2);

            Func<double, double> R = (t) => { return 1 - ThermalSpecialFunctions.Erfcx(Math.Sqrt(t / responseTime)); };

            Func<double, double> G = (x) =>
            {
                return ThermalSpecialFunctions.Erfcx(x) + 2 * x / Math.Sqrt(Math.PI) - 1;
            };

            var n = lagSeconds.Length;
            Array.Clear(weights, 0, n);

            for (int day = 0; day < 60; day++)
            {
                for (int k = 0; k < n; k++)
                {
                    double a = 86400 * day + lagSeconds[k];
                    double b;
                    if (k == n - 1)
                    {
                        b = 86400 * day + 86400;
                    }
                    else
                    {
                        b = 86400 * day + lagSeconds[k + 1];
                    }

                    double mean = 1 - responseTime * (G(Math.Sqrt(b / responseTime)) -
                                                      G(Math.Sqrt(a / responseTime))) / (b - a);

                    weights[k] += mean - R(a);
                    weights[(k + 1) % n] += R(b) - mean;
                }
            }

            double rest = 1 - R(60 * 86400);

            for (int k = 0; k < n; k++)
            {
                double before = k > 0 ? lagSeconds[k - 1] : lagSeconds[n - 1] - 86400;
                double after = k < n - 1 ? lagSeconds[k + 1] : 86400;
                weights[k] += rest * (after - before) / (2 * 86400);
            }
        }

        // The atmosphere's march (step 3, decided 2026-10-29; IMTNS/atmosphere/path_options.py): a step at most this
        // high and this long, within 0.19 NETD of 1 m steps; and the heights the air is held to, so rays that leave
        // the scene stay finite.
        public const double AtmosphereStepHeight = 50, AtmosphereStepLength = 1000; // m
        public const double AtmosphereFloor = -500, AtmosphereCeiling = 3000; // m above the ground

        /// <summary>
        /// The atmosphere, step 3: the air at <paramref name="heightM"/> above the ground, the height first held to
        /// <see cref="AtmosphereFloor"/>..<see cref="AtmosphereCeiling"/>. Temperature falls 6.5 K/km from
        /// <c>air.airTemperatureK</c>; pressure is 1013.25 hPa at sea level with an 8400 m scale height, so
        /// 1013.25 exp(−(altitude + z) / 8400); water vapour's partial pressure is the saturation pressure at the
        /// dew point (Magnus, as <see cref="RelativeHumidity"/>) with a 2000 m scale height. Outputs K, hPa, hPa.
        /// Reference: air 297.8282 K, dew point 283.9055 K, altitude 2 m, at 450 m: 294.9032 K, 960.1685 hPa,
        /// 10.31275 hPa; at 5000 m (held to 3000): 278.3282 K, 708.7744 hPa, 2.88170 hPa.
        /// Thermal → Atmosphere Check holds it to IMTNS/atmosphere/path_ref.py.
        /// </summary>
        public static void AirAt(AtmosphereAir air, double heightM, out double temperatureK, out double pressureHPa,
            out double vapourHPa)
        {
            double z = Math.Clamp(heightM, AtmosphereFloor, AtmosphereCeiling);

            Func<double, double> E = (double temp) =>
            {
                double t = temp - 273.15; // normalize to degrees C
                return 6.1078 * Math.Exp(17.27 * t / (t + 237.3));
            };

            temperatureK = air.airTemperatureK - 0.0065 * z;
            pressureHPa = 1013.25 * Math.Exp(-(air.altitudeM + z) / 8400);
            vapourHPa = E(air.dewPointK) * Math.Exp(-z / 2000);
        }

        /// <summary>
        /// The atmosphere, step 3: march one ray from the camera, <paramref name="cameraHeightM"/> above the ground,
        /// climbing at <paramref name="sinElevation"/> (negative looking down), and record at each of
        /// <paramref name="ranges"/> (m, ascending, the first 0) the transmission from the camera and the path
        /// radiance, per sub-band: <paramref name="tau"/>[m * 4 + k] and <paramref name="path"/>[m * 4 + k], k in
        /// ThermalMath.SubBandEdges' order (W/m²/sr). At range 0, τ 1 and L 0.
        /// <list type="bullet">
        /// <item>The steps: between ranges s_m and s_m+1, L = s_m+1 − s_m, k = max(1, ceil(|L sinElevation| /
        /// <see cref="AtmosphereStepHeight"/>), ceil(L / <see cref="AtmosphereStepLength"/>)) equal steps; step j runs
        /// from s_m + L j / k to s_m + L (j + 1) / k, and takes the air (<see cref="AirAt"/>) at its middle height.</item>
        /// <item>Each gas's amount in a step: u = x n_air l, molecules cm⁻², with n_air = p / (k_B T) per cm³ and l the
        /// step's length in cm. Mixing ratios x: H2O e / p, CO2 425e-6, N2O 335e-9, CH4 1.95e-6, O3
        /// <c>air.ozone</c>.</item>
        /// <item>Curtis-Godson: per gas, running sums from the camera of u, u p, u T and u e (e in atm, hPa /
        /// 1013.25). After each step, each gas's transmission is looked up for the whole path so far,
        /// <paramref name="atmosphere"/>.Transmission(gas, Σu, Σup / Σu, ΣuT / Σu, Σue / Σu), and the five
        /// multiplied: τ(through). Never the steps' own transmissions multiplied.</item>
        /// <item>The step's own radiance, B linear in its own depth: t = τ(through) / τ(above), d = −ln t, and
        /// L += τ(above) [B0 (1 − t) + (B1 − B0) (1 − t (1 + d)) / d], B0 and B1 the sub-band radiance
        /// (<paramref name="subBandRadiance"/>) at the air temperatures of the step's camera end and far end. Under
        /// d 1e-4, use (1 − t (1 + d)) / d ≈ d / 2 − d² / 3; where τ(above) is 0 the step adds nothing.</item>
        /// </list>
        /// Called from worker threads, one ray per call and several at once: no Unity API, nothing shared that it
        /// writes. A table is 128 calls of up to ~500 steps, and while the camera climbs one is built every ~20 ms, so
        /// make its few small arrays once per call, not per step. Thermal → Atmosphere Check holds it to
        /// IMTNS/atmosphere/path_ref.py.
        /// </summary>
        public static void MarchRay(ThermalAtmosphere atmosphere, AtmosphereAir air, double cameraHeightM,
            double sinElevation, double[] ranges, Action<double, double[]> subBandRadiance,
            double[] tau, double[] path)
        {
            int H2O = atmosphere.GasIndex("H2O");
            int CO2 = atmosphere.GasIndex("CO2");
            int O3 = atmosphere.GasIndex("O3");
            int N2O = atmosphere.GasIndex("N2O");
            int CH4 = atmosphere.GasIndex("CH4");
            int[] fixedGases = { CO2, N2O, CH4, O3 };
            double[] fixedRatios = { 425e-6, 335e-9, 1.95e-6, air.ozone };

            double airAmount = 0;
            double airAmountPressure = 0;
            double airAmountTemperature = 0;

            double waterAmount = 0;
            double waterAmountPressure = 0;
            double waterAmountTemperature = 0;
            double waterAmountPartialPressure = 0;

            tau[0] = 1;
            tau[1] = 1;
            tau[2] = 1;
            tau[3] = 1;
            path[0] = 0;
            path[1] = 0;
            path[2] = 0;
            path[3] = 0;

            double[] above = new double[4] { 1, 1, 1, 1 };
            double[] light = new double[4] { 0, 0, 0, 0 };
            double[] through = new double[4] { 0, 0, 0, 0 };
            double[] one = new double[4] { 0, 0, 0, 0 };
            double[] b0 = new double[4] { 0, 0, 0, 0 };
            double[] b1 = new double[4] { 0, 0, 0, 0 };

            for (int m = 0; m < ranges.Length - 1; ++m)
            {
                double length = ranges[m + 1] - ranges[m];
                int steps = Math.Max(1,
                    (int)Math.Max(Math.Ceiling(Math.Abs(length * sinElevation) / AtmosphereStepHeight),
                        Math.Ceiling(length / AtmosphereStepLength)));

                for (int j = 0; j < steps; ++j)
                {
                    double a = ranges[m] + length * j / steps;
                    double b = ranges[m] + length * (j + 1) / steps;
                    double zNear = cameraHeightM + sinElevation * a;
                    double zFar = cameraHeightM + sinElevation * b;

                    AirAt(air, (zNear + zFar) / 2, out double T, out double p, out double e);

                    double airDensity = p * 100 / (ThermalConstants.BoltzmannConstant * T) * 1e-6;
                    double stepLengthCm = length / steps * 100;
                    double column = airDensity * stepLengthCm;

                    for (int k = 0; k < 4; k++)
                        through[k] = 1;

                    airAmount += column;
                    airAmountPressure += column * p;
                    airAmountTemperature += column * T;

                    for (int i = 0; i < 4; ++i)
                    {
                        atmosphere.Transmission(fixedGases[i], fixedRatios[i] * airAmount,
                            airAmountPressure / airAmount, airAmountTemperature / airAmount, 0, one);
                        for (int k = 0; k < 4; k++)
                            through[k] *= one[k];
                    }

                    double waterStep = (e / p) * column;
                    waterAmount += waterStep;
                    waterAmountPressure += waterStep * p;
                    waterAmountTemperature += waterStep * T;
                    waterAmountPartialPressure += waterStep * (e / 1013.25);
                    atmosphere.Transmission(H2O, waterAmount, waterAmountPressure / waterAmount,
                        waterAmountTemperature / waterAmount, waterAmountPartialPressure / waterAmount, one);
                    
                    for (int k = 0; k < 4; k++)
                        through[k] *= one[k];
                    
                    AirAt(air, zNear, out double Tnear, out _, out _);
                    AirAt(air, zFar, out double Tfar, out _, out _);

                    subBandRadiance(Tnear, b0);
                    subBandRadiance(Tfar, b1);

                    for (int k = 0; k < 4; k++)
                    {
                        if (above[k] > 0)
                        {
                            double stepTau = Math.Min(1, through[k] / above[k]);
                            double stepDepth = -Math.Log(stepTau);
                            double slopeWeight = stepDepth < 1e-4
                                ? stepDepth / 2 - stepDepth * stepDepth / 3
                                : (1 - stepTau * (1 + stepDepth)) / stepDepth;
                            
                            light[k] += above[k] * (b0[k] * (1 - stepTau) + (b1[k] - b0[k]) * slopeWeight);
                        }
                        above[k] = through[k];
                    }
                }

                for (int k = 0; k < 4; k++)
                {
                    tau[(m + 1) * 4 + k] = above[k];
                    path[(m + 1) * 4 + k] = light[k];
                }
            }
        }
    }
}