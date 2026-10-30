using System;
using System.Collections.Generic;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// The atmosphere's band tables (decision log, the atmosphere; baked 2026-10-27): per sub-band and gas, the
    /// Planck-weighted transmission of a uniform path, from IMTNS/atmosphere/bake_atmosphere.py's .atmosphere file
    /// (ThermalAtmosphereImporter). Four sub-bands cut at the absorbers, in wavenumber order: 14-12.5, 12.5-10.22,
    /// 10.22-9.26 (ozone) and 9.26-8 µm, the edges on ThermalMath.BandIntegration's samples so the sub-bands' radiances
    /// sum to the band's. HITRAN's lines with Roberts et al.'s (1976) water vapour continuum, licence-clean; the
    /// citations are in <see cref="Provenance"/>.
    /// The band model (step 3, Lucas's physics) builds each slant path from these, layer by layer; this class only
    /// holds and reads the tables.
    /// </summary>
    public class ThermalAtmosphere : ScriptableObject
    {
        public const int SubBands = 4;

        [Serializable]
        public class Gas
        {
            public string name;
            public float[] e;                       // atm: H2O's partial pressure axis; a single 0 for the others
            public float log10UStart, log10UStep;   // molecules cm⁻² along the path, log10
            public int uCount;
            public float[] table;                   // [p, T, e, u, sub-band], row-major
        }

        [SerializeField] float[] m_BandEdges;       // cm⁻¹, SubBands + 1
        [SerializeField] float m_WeightTemperature; // K: the Planck weighting across each sub-band
        [SerializeField] float[] m_Pressure;        // hPa
        [SerializeField] float[] m_Temperature;     // K
        [SerializeField] Gas[] m_Gases;
        [SerializeField, TextArea(3, 12)] string m_Provenance;

        public IReadOnlyList<float> BandEdges => m_BandEdges;
        public float WeightTemperature => m_WeightTemperature;
        public IReadOnlyList<float> Pressures => m_Pressure;
        public IReadOnlyList<float> Temperatures => m_Temperature;
        public int GasCount => m_Gases?.Length ?? 0;
        public string GasName(int gas) => m_Gases[gas].name;
        public string Provenance => m_Provenance;

        public bool IsValid => m_BandEdges != null && m_BandEdges.Length == SubBands + 1 && m_Gases != null &&
                               m_Gases.Length > 0 && m_Pressure != null && m_Pressure.Length >= 2 &&
                               m_Temperature != null && m_Temperature.Length >= 2;

        /// <summary>The gas's index by name (H2O, CO2, O3, N2O, CH4), or -1.</summary>
        public int GasIndex(string name)
        {
            for (int i = 0; i < GasCount; i++)
                if (m_Gases[i].name == name)
                    return i;
            return -1;
        }

        /// <summary>Called by the importer only.</summary>
        public void Set(float[] bandEdges, float weightTemperature, float[] pressure, float[] temperature, Gas[] gases,
                        string provenance)
        {
            m_BandEdges = bandEdges;
            m_WeightTemperature = weightTemperature;
            m_Pressure = pressure;
            m_Temperature = temperature;
            m_Gases = gases;
            m_Provenance = provenance;
        }

        /// <summary>
        /// The sub-band transmissions of a uniform path of one gas: <paramref name="u"/> molecules cm⁻² along it, at
        /// <paramref name="pressureHPa"/>, <paramref name="temperatureK"/>, and for H2O its partial pressure
        /// <paramref name="partialAtm"/> (atm; ignored for the others). Multilinear in (p, T, e, log10 u), clamped to the
        /// tables' grids, exactly as bake_atmosphere.lookup reads them. u ≤ 0 is an empty path: 1 in every sub-band.
        /// <paramref name="result"/> takes <see cref="SubBands"/> values.
        /// </summary>
        public void Transmission(int gas, double u, double pressureHPa, double temperatureK, double partialAtm,
                                 double[] result)
        {
            if (u <= 0)
            {
                for (int k = 0; k < SubBands; k++)
                    result[k] = 1.0;
                return;
            }
            Gas g = m_Gases[gas];
            Weight(m_Pressure, pressureHPa, out int ip, out double fp);
            Weight(m_Temperature, temperatureK, out int it, out double ft);
            int ie = 0;
            double fe = 0;
            if (g.e.Length > 1)
                Weight(g.e, partialAtm, out ie, out fe);
            double x = (Math.Log10(u) - g.log10UStart) / g.log10UStep;
            x = Math.Max(0.0, Math.Min(g.uCount - 1, x));
            int iu = Math.Min((int)Math.Floor(x), g.uCount - 2);
            double fu = x - iu;

            int nT = m_Temperature.Length, nE = g.e.Length, nU = g.uCount;
            for (int k = 0; k < SubBands; k++)
                result[k] = 0.0;
            for (int a = 0; a < 2; a++)
            {
                double wa = a == 0 ? 1 - fp : fp;
                for (int b = 0; b < 2; b++)
                {
                    double wb = b == 0 ? 1 - ft : ft;
                    for (int c = 0; c < (nE > 1 ? 2 : 1); c++)
                    {
                        double wc = nE > 1 ? (c == 0 ? 1 - fe : fe) : 1.0;
                        int row = (((ip + a) * nT + it + b) * nE + ie + c) * nU + iu;
                        double w = wa * wb * wc;
                        for (int k = 0; k < SubBands; k++)
                            result[k] += w * (g.table[row * SubBands + k] * (1 - fu) + g.table[(row + 1) * SubBands + k] * fu);
                    }
                }
            }
        }

        static void Weight(float[] grid, double x, out int i, out double f)
        {
            x = Math.Max(grid[0], Math.Min(grid[grid.Length - 1], x));
            i = 0;
            while (i < grid.Length - 2 && x > grid[i + 1])
                i++;
            f = (x - grid[i]) / (grid[i + 1] - grid[i]);
        }
    }
}
