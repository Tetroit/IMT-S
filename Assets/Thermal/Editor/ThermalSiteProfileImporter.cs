using System;
using System.Globalization;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Imports IMTNS/site_profile.py's output - a .siteprofile file, JSON - as a <see cref="ThermalSiteProfile"/>.
    /// Drop the file under Assets and it appears as a profile to pick on ThermalEnvironment. Profiles for
    /// sites that must stay private go in Assets/Thermal/Profiles/Internal, which git ignores.
    /// </summary>
    [ScriptedImporter(1, "siteprofile")]
    public class ThermalSiteProfileImporter : ScriptedImporter
    {
        // Mirrors of the builder's JSON, field for field: JsonUtility matches names exactly and skips the rest.
#pragma warning disable 649   // assigned by JsonUtility
        [Serializable] class Site { public string name, note; public double latitude, longitude, altitude_m; }
        [Serializable] class Source { public string dataset, built; public string[] period; }
        [Serializable] class Method { public string clear_cycles, dew_point, decision; public double t_max_shift_K, t_min_shift_K; }
        [Serializable] class Row { public int month; public double t_min_K, t_max_K, dew_point_K, a, b, c, linke_turbidity; }
        [Serializable] class Document { public Site site; public Source source; public Method method; public Row[] months; }
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
                ctx.LogImportError($"{ctx.assetPath}: not a site profile ({e.Message})");
                return;
            }

            string problem = Check(doc);
            if (problem != null)
            {
                ctx.LogImportError($"{ctx.assetPath}: {problem}");
                return;
            }

            var months = new ThermalSiteProfile.Month[12];
            foreach (Row r in doc.months)
            {
                months[r.month - 1] = new ThermalSiteProfile.Month
                {
                    minK = r.t_min_K, maxK = r.t_max_K, dewPointK = r.dew_point_K,
                    a = r.a, b = r.b, c = r.c, linkeTurbidity = r.linke_turbidity,
                };
            }

            var profile = ScriptableObject.CreateInstance<ThermalSiteProfile>();
            profile.Set(doc.site.name, doc.site.latitude, doc.site.longitude, (float)doc.site.altitude_m,
                        Provenance(doc), months);
            ctx.AddObjectToAsset("profile", profile);
            ctx.SetMainObject(profile);
        }

        /// <summary>What the sim needs of a profile, or why this one will not do.</summary>
        static string Check(Document doc)
        {
            if (doc?.site == null || doc.months == null)
                return "no site or months - not a site profile";
            if (doc.months.Length != 12)
                return $"{doc.months.Length} months, not 12";
            if (!(Math.Abs(doc.site.latitude) <= 66.0) || !(Math.Abs(doc.site.longitude) <= 180.0))
                return $"location {doc.site.latitude}, {doc.site.longitude} is outside what the air model covers (|latitude| <= 66)";
            var seen = new bool[12];
            foreach (Row r in doc.months)
            {
                if (r.month < 1 || r.month > 12 || seen[r.month - 1])
                    return $"month {r.month} is out of range or repeated";
                seen[r.month - 1] = true;
                if (!(r.t_min_K > 150.0 && r.t_max_K >= r.t_min_K && r.t_max_K < 350.0))   // false for NaN too
                    return $"month {r.month}: minimum {r.t_min_K} K and maximum {r.t_max_K} K are not a day";
                if (!(r.dew_point_K > 150.0 && r.dew_point_K <= r.t_max_K))
                    return $"month {r.month}: dew point {r.dew_point_K} K";
                if (!(r.a > 0.0 && r.b > 0.0 && r.c > -3.0 && r.c < 4.0))
                    return $"month {r.month}: coefficients a {r.a}, b {r.b}, c {r.c}";
                if (!(r.linke_turbidity >= 1.0 && r.linke_turbidity <= 8.0))
                    return $"month {r.month}: Linke turbidity {r.linke_turbidity}";
            }
            return null;
        }

        static string Provenance(Document doc)
        {
            var inv = CultureInfo.InvariantCulture;
            string period = doc.source?.period != null && doc.source.period.Length == 2
                ? $"{doc.source.period[0]} to {doc.source.period[1]}" : "period not recorded";
            string text = $"{doc.source?.dataset}, {period}.";
            if (!string.IsNullOrEmpty(doc.site.note))
                text += " " + doc.site.note + ".";
            if (doc.method != null)
            {
                text += $" Clear days: {doc.method.clear_cycles}. Maximum {doc.method.t_max_shift_K.ToString("+0.0;-0.0;0", inv)} K, " +
                        $"minimum {doc.method.t_min_shift_K.ToString("+0.0;-0.0;0", inv)} K";
                if (!string.IsNullOrEmpty(doc.method.dew_point))
                    text += $"; dew point {doc.method.dew_point}";
                text += $" ({doc.method.decision}).";
            }
            if (!string.IsNullOrEmpty(doc.source?.built))
                text += $" Built {doc.source.built}.";
            return text;
        }
    }
}
