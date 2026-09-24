using System;
using System.Collections.Generic;
using System.Globalization;

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
    }
}