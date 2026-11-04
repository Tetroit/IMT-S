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
    /// The band model (step 3, Lucas's physics: ThermalMath.MarchRay) builds each path from these, step by step along
    /// a ray from the camera; this class only holds and reads the tables.
    /// Since 2026-11-03 (thermal-atmosphere/2, step 5) the tables reach 65 hPa and 210 K, and H2O comes split: "H2O
    /// lines", its lines alone (no e axis), and the continuum on its own, <see cref="ContinuumTransmission"/>, read by
    /// D = Σ f(T) e u along the path (<see cref="ContinuumFactor"/>).
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

        // H2O's continuum on its own: per sub-band, the transmission of a path of D, linear in log10 D
        [SerializeField] float m_ContinuumT0, m_ContinuumTRef;                  // K: f(T) = exp(t0 (1/T − 1/tRef))
        [SerializeField] float m_ContinuumLog10DStart, m_ContinuumLog10DStep;   // D in atm molecules cm⁻², log10
        [SerializeField] int m_ContinuumCount;
        [SerializeField] float[] m_Continuum;                                   // [D, sub-band], row-major

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

        /// <summary>Called by the importer only: H2O's continuum on its own.</summary>
        public void SetContinuum(float t0, float tRef, float log10DStart, float log10DStep, int count, float[] table)
        {
            m_ContinuumT0 = t0;
            m_ContinuumTRef = tRef;
            m_ContinuumLog10DStart = log10DStart;
            m_ContinuumLog10DStep = log10DStep;
            m_ContinuumCount = count;
            m_Continuum = table;
        }

        public bool HasContinuum => m_Continuum != null && m_ContinuumCount >= 2 &&
                                    m_Continuum.Length == m_ContinuumCount * SubBands;

        /// <summary>
        /// The continuum's temperature dependence, f(T) = exp(t0 (1/T − 1/tRef)) (Roberts et al. 1976: t0 1800 K,
        /// tRef 296 K, from the asset): its cross-section is c(ν) f(T) e, so a path's depth is c(ν) D with
        /// D = Σ f(T) e u over its steps, e in atm and u in molecules cm⁻².
        /// </summary>
        public double ContinuumFactor(double temperatureK) =>
            Math.Exp(m_ContinuumT0 * (1.0 / temperatureK - 1.0 / m_ContinuumTRef));

        /// <summary>
        /// The sub-band transmissions of H2O's continuum alone along a path of <paramref name="d"/> (atm molecules
        /// cm⁻², <see cref="ContinuumFactor"/>). Linear in log10 D, clamped above the last entry; below the first, the
        /// weak limit 1 − τ = (1 − τ(D_first)) D / D_first, as the gases' u. D ≤ 0 gives 1. Exactly as
        /// bake_atmosphere.continuum_lookup reads it. <paramref name="result"/> takes <see cref="SubBands"/> values.
        /// </summary>
        public void ContinuumTransmission(double d, double[] result)
        {
            if (d <= 0)
            {
                for (int k = 0; k < SubBands; k++)
                    result[k] = 1.0;
                return;
            }
            double x = (Math.Log10(d) - m_ContinuumLog10DStart) / m_ContinuumLog10DStep;
            x = Math.Max(0.0, Math.Min(m_ContinuumCount - 1, x));
            int i = Math.Min((int)Math.Floor(x), m_ContinuumCount - 2);
            double f = x - i;
            for (int k = 0; k < SubBands; k++)
                result[k] = m_Continuum[i * SubBands + k] * (1 - f) + m_Continuum[(i + 1) * SubBands + k] * f;

            double first = Math.Pow(10, m_ContinuumLog10DStart);
            if (d < first)
                for (int k = 0; k < SubBands; k++)
                    result[k] = 1 - (1 - result[k]) * (d / first);
        }

        /// <summary>
        /// The sub-band transmissions of a uniform path of one gas: <paramref name="u"/> molecules cm⁻² along it, at
        /// <paramref name="pressureHPa"/>, <paramref name="temperatureK"/>, and for H2O its partial pressure
        /// <paramref name="partialAtm"/> (atm; ignored for the others). Multilinear in (p, T, e, log10 u), clamped to the
        /// tables' grids, exactly as bake_atmosphere.lookup reads them. u ≤ 0 is an empty path: 1 in every sub-band.
        /// Below the first u entry (a path under ~0.3 m for H2O, ~1 m for CO2 and O3, ~6 m for CH4) the absorption
        /// falls linearly to 0, 1 − τ = (1 − τ(u_first)) u / u_first: the weak-line limit, where clamping to the first
        /// entry cost up to 0.46 NETD at 0.1 m (decided 2026-10-29, IMTNS/atmosphere/path_options.py short).
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

            double first = Math.Pow(10, g.log10UStart);
            if (u < first)
                for (int k = 0; k < SubBands; k++)
                    result[k] = 1 - (1 - result[k]) * (u / first);
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
