using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;

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
        public double azimuth;         // degrees, clockwise from North
        public double elevation;       // degrees above horizon, refraction-corrected
        public double declination;     // degrees
        public double equationOfTime;  // minutes

        public override string ToString()
        {
            return $"Azimuth: {azimuth}, Elevation: {elevation}, Declination: {declination}, EquationOfTime: {equationOfTime}";
        }
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
            double dayFraction = utc.Day + (utc.Hour + utc.Minute/60.0 + utc.Second/3600.0) / 24.0;

            if (month <= 2)
            {
                year -= 1.0;
                month += 12.0; // julian calendar makes jan/feb month 13/14 in previous year
            }

            double century = Math.Floor(year / 100.0);
            double gregorian = 2.0 - century + Math.Floor(century / 4.0);

            double julianDay = Math.Floor(365.25 * (year + 4716.0)) + Math.Floor(30.6001 * (month + 1)) + dayFraction + gregorian - 1524.5;

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
            
            double utcMinutes = utc.Hour * 60 + utc.Minute + utc.Second/60.0;
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
    }
}