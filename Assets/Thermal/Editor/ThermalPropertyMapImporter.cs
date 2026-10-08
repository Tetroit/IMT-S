using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Locks the import of every texture named <c>*_Thermal</c> - the trim sheet's thermal map, R = solar
    /// absorptivity and G = emissivity painted as values (D-010) - to the settings that keep those values intact:
    /// linear, uncompressed, not resized, and mipmapped with a box filter, so each level is the mean of the one
    /// above and a surface reads the same at every distance. Unity's defaults would decode it as sRGB and compress
    /// it, and nothing would look wrong.
    ///
    /// Each import logs the commonest value pairs, and warns about the two export mistakes that otherwise pass
    /// silently: texels never painted (0, 0 is a perfect mirror to the thermal camera) and R and G swapped.
    /// </summary>
    class ThermalPropertyMapImporter : AssetPostprocessor
    {
        /// <summary>Raise when the settings change, to reimport every thermal map.</summary>
        const uint k_Version = 1;

        /// <summary>Thermal → Property Map Check turns this off to see the check fail on wrong settings.</summary>
        internal static bool s_Enabled = true;

        public override uint GetVersion() => k_Version;

        public static bool IsPropertyMap(string path) =>
            Path.GetFileNameWithoutExtension(path).EndsWith("_Thermal", StringComparison.OrdinalIgnoreCase);

        void OnPreprocessTexture()
        {
            if (!s_Enabled || !IsPropertyMap(assetPath))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 16384;
            importer.mipmapEnabled = true;
            importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
            importer.mipMapsPreserveCoverage = false;
            importer.fadeout = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Trilinear;
            importer.isReadable = false;
            importer.ClearPlatformTextureSettings("Standalone");
        }

        void OnPostprocessTexture(Texture2D texture)
        {
            if (!s_Enabled || !IsPropertyMap(assetPath))
                return;

            string name = Path.GetFileName(assetPath);
            Color32[] pixels = texture.GetPixels32(0);
            int n = pixels.Length, unpainted = 0;
            var pairs = new Dictionary<int, int>();
            var emissivity = new int[256];
            foreach (Color32 p in pixels)
            {
                int key = p.r << 8 | p.g;
                pairs.TryGetValue(key, out int count);
                pairs[key] = count + 1;
                emissivity[p.g]++;
                if (p.r == 0 && p.g == 0)
                    unpainted++;
            }

            var commonest = pairs.OrderByDescending(kv => kv.Value).Take(6).Select(kv =>
                FormattableString.Invariant($"a {(kv.Key >> 8) / 255.0:0.00} e {(kv.Key & 255) / 255.0:0.00} {100.0 * kv.Value / n:0.#}%"));
            Debug.Log(FormattableString.Invariant(
                $"[Thermal] {name}, {texture.width} x {texture.height}: {pairs.Count} distinct value pairs; commonest {string.Join(", ", commonest)}."));

            if (unpainted > n / 200)
                context.LogImportWarning(FormattableString.Invariant($"{name}: {100.0 * unpainted / n:0.#}% of texels") +
                                         " are absorptivity 0, emissivity 0 - never painted, or padding without " +
                                         "dilation. The thermal camera reads them as a perfect mirror.");

            int median = 0;
            for (int v = 0, below = 0; v < 256; v++)
                if ((below += emissivity[v]) > n / 2)
                {
                    median = v;
                    break;
                }
            if (median < 128)
                context.LogImportWarning($"{name}: most texels have emissivity under 0.5, which only bare metal has. If " +
                                         "R and G are swapped in the export, fix it (R = absorptivity, G = emissivity); " +
                                         "if the sheet really is mostly bare metal, ignore this.");
        }
    }
}
