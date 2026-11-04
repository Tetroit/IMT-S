using System;

namespace IMT.Thermal
{
    /// <summary>
    /// The standard upper air the atmosphere joins at 3 km (the atmosphere's step 5, decided 2026-11-03): the AFGL
    /// mid-latitude summer and winter atmospheres, levels 0-60 km above sea level (Anderson, Clough, Kneizys, Chetwynd
    /// &amp; Shettle 1986, AFGL Atmospheric Constituent Profiles (0-120 km), AFGL-TR-86-0110; FASCOD's Models 2 and 3, as
    /// RFM's mls.atm and mlw.atm). This class only holds and reads them; the join is ThermalMath.AirAbove.
    /// IMTNS/atmosphere/upper_air.py reads the same files, and Thermal → Atmosphere Check holds <see cref="Standard"/>
    /// to it.
    /// </summary>
    public static class ThermalUpperAir
    {
        // From IMTNS/atmosphere/upper_air.py emit (profiles/mls.atm, mlw.atm), levels to 60 km above sea level;
        // the mixing ratios in ppmv, as the files have them
        static readonly double[] k_Km = { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0, 27.5, 30.0, 32.5, 35.0, 37.5, 40.0, 42.5, 45.0, 47.5, 50.0, 55.0, 60.0 };
        static readonly double[] k_SummerTemperatureK = { 294.2, 289.7, 285.2, 279.2, 273.2, 267.2, 261.2, 254.7, 248.2, 241.7, 235.3, 228.8, 222.3, 215.8, 215.7, 215.7, 215.7, 215.7, 216.8, 217.9, 219.2, 220.4, 221.6, 222.8, 223.9, 225.1, 228.45, 233.7, 239.0, 245.2, 251.3, 257.5, 263.7, 269.9, 275.2, 275.7, 269.3, 257.1 };
        static readonly double[] k_SummerPressureHPa = { 1013.0, 902.0, 802.0, 710.0, 628.0, 554.0, 487.0, 426.0, 372.0, 324.0, 281.0, 243.0, 209.0, 179.0, 153.0, 130.0, 111.0, 95.0, 81.2, 69.5, 59.5, 51.0, 43.7, 37.6, 32.2, 27.7, 19.07, 13.2, 9.3, 6.52, 4.64, 3.33, 2.41, 1.76, 1.29, 0.951, 0.515, 0.272 };
        static readonly double[] k_SummerH2OPpmv = { 18760.0, 13780.0, 9680.0, 5984.0, 3813.0, 2225.0, 1510.0, 1020.0, 646.4, 412.9, 247.2, 95.56, 29.44, 8.0, 5.0, 3.4, 3.3, 3.2, 3.15, 3.2, 3.3, 3.45, 3.6, 3.85, 4.0, 4.2, 4.45, 4.7, 4.85, 4.95, 5.0, 5.1, 5.3, 5.45, 5.5, 5.5, 5.35, 5.0 };
        static readonly double[] k_SummerO3Ppmv = { 0.03017, 0.03337, 0.03694, 0.04222, 0.04821, 0.05512, 0.06408, 0.07764, 0.09126, 0.1111, 0.1304, 0.1793, 0.223, 0.3, 0.44, 0.5, 0.6, 0.7, 1.0, 1.5, 2.0, 2.4, 2.9, 3.4, 4.0, 4.8, 6.0, 7.0, 8.1, 8.9, 8.7, 7.55, 5.9, 4.5, 3.5, 2.8, 1.8, 1.3 };
        static readonly double[] k_WinterTemperatureK = { 272.2, 268.7, 265.2, 261.7, 255.7, 249.7, 243.7, 237.7, 231.7, 225.7, 219.7, 219.2, 218.7, 218.2, 217.7, 217.2, 216.7, 216.2, 215.7, 215.2, 215.2, 215.2, 215.2, 215.2, 215.2, 215.2, 215.5, 217.4, 220.4, 227.9, 235.5, 243.2, 250.8, 258.5, 265.1, 265.7, 260.6, 250.8 };
        static readonly double[] k_WinterPressureHPa = { 1018.0, 897.3, 789.7, 693.8, 608.1, 531.3, 462.7, 401.6, 347.3, 299.3, 256.8, 219.9, 188.2, 161.1, 137.8, 117.8, 100.7, 86.1, 73.6, 62.8, 53.7, 45.8, 39.1, 33.4, 28.6, 24.4, 16.46, 11.1, 7.56, 5.18, 3.6, 2.53, 1.8, 1.29, 0.94, 0.683, 0.362, 0.188 };
        static readonly double[] k_WinterH2OPpmv = { 4316.0, 3454.0, 2788.0, 2088.0, 1280.0, 824.1, 510.3, 232.1, 107.7, 55.66, 29.6, 10.0, 6.0, 5.0, 4.8, 4.7, 4.6, 4.5, 4.5, 4.5, 4.5, 4.5, 4.53, 4.55, 4.6, 4.65, 4.7, 4.75, 4.8, 4.85, 4.9, 4.95, 5.0, 5.0, 5.0, 4.95, 4.85, 4.5 };
        static readonly double[] k_WinterO3Ppmv = { 0.02778, 0.028, 0.02849, 0.032, 0.03567, 0.0472, 0.05837, 0.07891, 0.1039, 0.1567, 0.237, 0.3624, 0.5232, 0.7036, 0.8, 0.9, 1.1, 1.4, 1.8, 2.3, 2.9, 3.5, 3.9, 4.3, 4.7, 5.1, 5.6, 6.1, 6.8, 7.1, 7.2, 6.9, 5.9, 4.6, 3.7, 2.75, 1.7, 1.0 };
        public const double SummerTropopauseKm = 13.0, WinterTropopauseKm = 10.0;

        /// <summary>The tropopause of the season's blend, km above sea level: each standard's (its first level above
        /// 5 km cooling by less than 2 K/km) weighted by <paramref name="summer"/>.</summary>
        public static double TropopauseKm(double summer) =>
            summer * SummerTropopauseKm + (1 - summer) * WinterTropopauseKm;

        /// <summary>
        /// The standard at <paramref name="km"/> above sea level (held to 0-60 km), blended by
        /// <paramref name="summer"/>, the summer standard's weight (1 at the June solstice, 0 at the December one;
        /// AtmosphereAir.summer). Temperature linear between levels and between the two; pressure and the mixing
        /// ratios linear in their logarithms. Outputs K, hPa, and the H2O and O3 mixing ratios (fractions).
        /// </summary>
        public static void Standard(double summer, double km, out double temperatureK, out double pressureHPa,
            out double h2o, out double o3)
        {
            double h = Math.Max(k_Km[0], Math.Min(k_Km[k_Km.Length - 1], km));
            int i = 0;
            while (i < k_Km.Length - 2 && h > k_Km[i + 1])
                i++;
            double f = (h - k_Km[i]) / (k_Km[i + 1] - k_Km[i]);
            double winter = 1 - summer;
            temperatureK = summer * Linear(k_SummerTemperatureK, i, f) + winter * Linear(k_WinterTemperatureK, i, f);
            pressureHPa = Math.Exp(summer * LogLinear(k_SummerPressureHPa, i, f, 1) +
                                   winter * LogLinear(k_WinterPressureHPa, i, f, 1));
            h2o = Math.Exp(summer * LogLinear(k_SummerH2OPpmv, i, f, 1e-6) + winter * LogLinear(k_WinterH2OPpmv, i, f, 1e-6));
            o3 = Math.Exp(summer * LogLinear(k_SummerO3Ppmv, i, f, 1e-6) + winter * LogLinear(k_WinterO3Ppmv, i, f, 1e-6));
        }

        static double Linear(double[] v, int i, double f) => v[i] * (1 - f) + v[i + 1] * f;

        static double LogLinear(double[] v, int i, double f, double scale) =>
            Math.Log(v[i] * scale) * (1 - f) + Math.Log(v[i + 1] * scale) * f;
    }
}
