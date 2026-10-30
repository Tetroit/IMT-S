using System;
using System.IO;
using System.Text;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Imports IMTNS/atmosphere/bake_atmosphere.py's output - a .atmosphere file, JSON - as a
    /// <see cref="ThermalAtmosphere"/>. Checked on import: the format, four sub-bands, ascending grids, each table's
    /// size and sum (every value read as written), every transmission in [0, 1] and none rising with the amount of gas.
    /// </summary>
    [ScriptedImporter(1, "atmosphere")]
    public class ThermalAtmosphereImporter : ScriptedImporter
    {
        // Mirrors of the builder's JSON, field for field: JsonUtility matches names exactly and skips the rest.
#pragma warning disable 649   // assigned by JsonUtility
        [Serializable] class Provenance { public string built, builder, lines, lines_cite, continuum, continuum_cite, weighting, measured; }
        [Serializable] class GasRow { public string name; public float[] e_atm; public float log10_u_start, log10_u_step; public int log10_u_count; public double table_sum; public float[] table; }
        [Serializable] class Document { public string format; public Provenance provenance; public float[] bands_cm; public float weight_temperature_K; public float[] p_hPa, t_K; public GasRow[] gases; }
#pragma warning restore 649

        public override void OnImportAsset(AssetImportContext ctx)
        {
            Document doc;
            try
            {
                doc = JsonUtility.FromJson<Document>(File.ReadAllText(ctx.assetPath));
            }
            catch (Exception e)
            {
                ctx.LogImportError($"{ctx.assetPath}: not an atmosphere file ({e.Message})");
                return;
            }

            string problem = Check(doc);
            if (problem != null)
            {
                ctx.LogImportError($"{ctx.assetPath}: {problem}");
                return;
            }

            var gases = new ThermalAtmosphere.Gas[doc.gases.Length];
            for (int i = 0; i < gases.Length; i++)
            {
                GasRow r = doc.gases[i];
                gases[i] = new ThermalAtmosphere.Gas
                {
                    name = r.name, e = r.e_atm, log10UStart = r.log10_u_start, log10UStep = r.log10_u_step,
                    uCount = r.log10_u_count, table = r.table,
                };
            }
            var atmosphere = ScriptableObject.CreateInstance<ThermalAtmosphere>();
            atmosphere.Set(doc.bands_cm, doc.weight_temperature_K, doc.p_hPa, doc.t_K, gases, Describe(doc.provenance));
            ctx.AddObjectToAsset("atmosphere", atmosphere);
            ctx.SetMainObject(atmosphere);
        }

        static string Check(Document doc)
        {
            if (doc == null || doc.format != "thermal-atmosphere/1")
                return $"format '{doc?.format}', expected thermal-atmosphere/1";
            if (doc.bands_cm == null || doc.bands_cm.Length != ThermalAtmosphere.SubBands + 1 || !Ascending(doc.bands_cm))
                return $"needs {ThermalAtmosphere.SubBands + 1} ascending sub-band edges";
            if (!Ascending(doc.p_hPa) || doc.p_hPa.Length < 2 || !Ascending(doc.t_K) || doc.t_K.Length < 2)
                return "the pressure and temperature grids must ascend, two values at least";
            if (doc.gases == null || doc.gases.Length == 0)
                return "no gases";
            foreach (GasRow g in doc.gases)
            {
                if (g.e_atm == null || g.e_atm.Length == 0 || !Ascending(g.e_atm) || g.log10_u_count < 2 || !(g.log10_u_step > 0))
                    return $"{g.name}: bad axes";
                int nU = g.log10_u_count, per = nU * ThermalAtmosphere.SubBands;
                int want = doc.p_hPa.Length * doc.t_K.Length * g.e_atm.Length * per;
                if (g.table == null || g.table.Length != want)
                    return $"{g.name}: {g.table?.Length ?? 0} values, expected {want}";
                double sum = 0;
                for (int i = 0; i < g.table.Length; i++)
                {
                    float v = g.table[i];
                    sum += v;
                    if (!(v >= 0f && v <= 1f + 1e-6f))
                        return $"{g.name}: transmission {v} at {i} is not in [0, 1]";
                    int u = i / ThermalAtmosphere.SubBands % nU;
                    if (u > 0 && v > g.table[i - ThermalAtmosphere.SubBands] + 1e-6f)
                        return $"{g.name}: transmission rises with the amount at {i}";
                }
                // table_sum is the float32 values' exact sum; this one's rounding stays under 3e-7 for these sizes
                if (!(Math.Abs(sum - g.table_sum) <= 1e-6))
                    return $"{g.name}: the values sum to {sum}, the file says {g.table_sum}: some were misread";
            }
            return null;
        }

        static bool Ascending(float[] a)
        {
            if (a == null)
                return false;
            for (int i = 1; i < a.Length; i++)
                if (!(a[i] > a[i - 1]))
                    return false;
            return true;
        }

        static string Describe(Provenance p)
        {
            if (p == null)
                return "";
            var s = new StringBuilder();
            s.AppendLine($"Built {p.built} by {p.builder}.");
            s.AppendLine($"Lines: {p.lines}. Cite: {p.lines_cite}.");
            s.AppendLine($"Continuum: {p.continuum}. Cite: {p.continuum_cite}.");
            s.AppendLine($"Weighting: {p.weighting}. Measured: {p.measured}.");
            return s.ToString();
        }
    }
}
