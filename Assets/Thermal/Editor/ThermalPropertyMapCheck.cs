using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Property Map Check: ThermalPropertyMapImporter inside Unity's texture pipeline, against texel values
    /// known in advance. A 64 x 64 stand-in for a trim sheet's thermal map - four strips of absorptivity and
    /// emissivity, and an 8 x 8 block never painted - imported twice. First with the importer off and the settings
    /// an artist's habit would leave (sRGB, compressed, Kaiser mips), when the checks must fail; then with it on,
    /// when they must pass. The texture is read back from the GPU, so this checks what the shader will sample.
    /// </summary>
    static class ThermalPropertyMapCheck
    {
        const string k_Path = "Assets/Thermal/MapTest/PropertyMapCheck_Thermal.png";
        const int k_Size = 64;

        // R = absorptivity, G = emissivity, in bytes: brick, glass, zinc, light paint.
        static readonly byte[,] k_Strips = { { 166, 237 }, { 26, 214 }, { 140, 51 }, { 64, 235 } };

        [MenuItem("Thermal/Property Map Check")]
        static void Run()
        {
            var pixels = new Color32[k_Size * k_Size];
            for (int y = 0; y < k_Size; y++)
            for (int x = 0; x < k_Size; x++)
            {
                int s = x / 16;
                bool unpainted = x >= 4 && x < 12 && y >= 4 && y < 12;
                pixels[y * k_Size + x] = unpainted
                    ? new Color32(0, 0, 0, 255)
                    : new Color32(k_Strips[s, 0], k_Strips[s, 1], 0, 255);
            }
            var source = new Texture2D(k_Size, k_Size, TextureFormat.RGBA32, false, true);
            source.SetPixels32(pixels);
            File.WriteAllBytes(k_Path, source.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(k_Path, ImportAssetOptions.ForceUpdate);

            try
            {
                // An artist's defaults, written with the importer off so they stick for this import.
                ThermalPropertyMapImporter.s_Enabled = false;
                var importer = (TextureImporter)AssetImporter.GetAtPath(k_Path);
                importer.sRGBTexture = true;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.mipmapEnabled = true;
                importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                importer.filterMode = FilterMode.Bilinear;
                importer.ClearPlatformTextureSettings("Standalone");
                importer.SaveAndReimport();
                int failed = Check(pixels, out int total, out string first);
                Debug.Log(failed > 0
                    ? $"[PropertyMap] with the importer off, {failed} of {total} checks fail, as they must ({first})"
                    : "[PropertyMap] with the importer off every check still passes - the check cannot see the importer");

                ThermalPropertyMapImporter.s_Enabled = true;
                AssetDatabase.ImportAsset(k_Path, ImportAssetOptions.ForceUpdate);
                failed = Check(pixels, out total, out first);
                if (failed == 0)
                    Debug.Log($"[PropertyMap] all {total} checks passed. The import's warning about unpainted texels is " +
                              "this test's own block, expected.");
                else
                    Debug.LogError($"[PropertyMap] {failed} of {total} checks FAILED - first: {first}");
            }
            finally
            {
                ThermalPropertyMapImporter.s_Enabled = true;
            }
        }

        static int Check(Color32[] pixels, out int total, out string first)
        {
            int failed = 0, checks = 0;
            string firstFailure = null;
            void Expect(bool ok, string what)
            {
                checks++;
                if (ok)
                    return;
                failed++;
                firstFailure ??= what;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(k_Path);
            GraphicsFormat format = texture.graphicsFormat;
            Expect(!GraphicsFormatUtility.IsSRGBFormat(format), $"linear, not {format}");
            Expect(!GraphicsFormatUtility.IsCompressedFormat(format), $"uncompressed, not {format}");
            Expect(texture.filterMode == FilterMode.Trilinear, $"trilinear, not {texture.filterMode}");
            Expect(texture.mipmapCount == 7, $"7 mip levels, not {texture.mipmapCount}");

            // Level 0 exactly the painted bytes; each level after it within one step of the true mean.
            var a = new double[pixels.Length];
            var e = new double[pixels.Length];
            for (int p = 0; p < pixels.Length; p++)
            {
                a[p] = pixels[p].r;
                e[p] = pixels[p].g;
            }
            int w = k_Size;
            for (int m = 0; m < Math.Min(7, texture.mipmapCount); m++, w /= 2)
            {
                if (m > 0)
                {
                    a = Halve(a, w * 2);
                    e = Halve(e, w * 2);
                }
                // A compressed or three-channel texture cannot be read back, and asking only makes Unity log an error.
                if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels))
                {
                    Expect(false, $"read back level {m}: {format} cannot be read back");
                    continue;
                }
                var request = AsyncGPUReadback.Request(texture, m, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    Expect(false, $"read back level {m}");
                    continue;
                }
                var data = request.GetData<byte>();
                int off = 0, tolerance = m == 0 ? 0 : 1;
                for (int p = 0; p < w * w; p++)
                    if (Math.Abs(data[4 * p] - a[p]) > tolerance + 0.5 || Math.Abs(data[4 * p + 1] - e[p]) > tolerance + 0.5)
                        off++;
                Expect(off == 0, $"level {m}: {off} of {w * w} texels off");
            }
            total = checks;
            first = firstFailure;
            return failed;
        }

        static double[] Halve(double[] src, int w)
        {
            int h = w / 2;
            var dst = new double[h * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
                dst[y * h + x] = (src[2 * y * w + 2 * x] + src[2 * y * w + 2 * x + 1] +
                                  src[(2 * y + 1) * w + 2 * x] + src[(2 * y + 1) * w + 2 * x + 1]) / 4;
            return dst;
        }
    }
}
