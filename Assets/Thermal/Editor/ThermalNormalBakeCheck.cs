using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Normal Bake Check (D-010, maps at a distance): ThermalNormalBake against values worked out here, in
    /// double precision. Normal maps of rows tilted 20 degrees down and up in turn and columns flat and tilted east in
    /// turn - asymmetric both ways, so a flip in either axis shows - imported as normal maps, baked on the GPU and read
    /// back at every mip level; each level must hold the plain means of the unit normals below it, to 16-bit precision.
    /// Two sizes: 64 x 64, and 96 x 40, whose odd levels average three texels at an edge. Then Unity's own 64 x 64
    /// map, read as Thermal/Base read it until 2026-10-06 (z rebuilt from the averaged x and y), must miss those means
    /// at the coarse levels, where it reads length 1: the failing case, next to the pass. Last, a 4096 x 4096 sheet,
    /// timed - the size a trim sheet has.
    /// </summary>
    static class ThermalNormalBakeCheck
    {
        const string k_Folder = "Assets/Thermal/MapTest";
        const double k_Tolerance = 4e-5;    // 16-bit storage rounds to 1.5e-5 once per level; float sums add a little

        [MenuItem("Thermal/Normal Bake Check")]
        static void Run()
        {
            int failed = 0, checks = 0;
            string first = null;
            void Expect(bool ok, string what)
            {
                checks++;
                if (ok)
                    return;
                failed++;
                first ??= what;
            }

            Texture2D square = Import("NormalBakeCheck.png", 64, 64, out Color32[] squarePixels);
            Texture2D odd = Import("NormalBakeCheck_Odd.png", 96, 40, out Color32[] oddPixels);
            double coarsest = Verify(square, squarePixels, "64 x 64", Expect);
            Verify(odd, oddPixels, "96 x 40", Expect);

            // The failing case: Unity's own map, read as the shader read it before the bake.
            int missed = 0;
            double worst = 0;
            bool readable = true;
            double[][] means = Means(squarePixels, 64, 64);
            for (int m = 0; m < square.mipmapCount && readable; m++)
            {
                double[] got = ReadUnity(square, m);
                if (got == null)
                {
                    readable = false;
                    break;
                }
                Compare(means[m], got, out double lengthOff);
                if (lengthOff > 0.01)
                    missed++;
                worst = Math.Max(worst, lengthOff);
            }
            string before = !readable
                ? $"Unity's own normal map ({square.graphicsFormat}) cannot be read back, so the failing case is not shown"
                : string.Format(CultureInfo.InvariantCulture,
                    "Unity's own normal map read as before: {0} of {1} levels miss the mean normal's length, by up to " +
                    "{2:0.000}{3}", missed, square.mipmapCount, worst,
                    missed > 0 ? ", as they must" : " - the check cannot tell the bake from the old path");

            string sheet = TimeSheet(4096);

            if (failed == 0)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[NormalBake] all {0} checks passed: every level the plain means of the unit normals below it; at " +
                    "the coarsest the mean normal is {1:0.000} long. {2}. {3}.", checks, coarsest, before, sheet));
            else
                Debug.LogError($"[NormalBake] {failed} of {checks} checks FAILED - first: {first}. {before}. {sheet}.");
        }

        // The pattern, written and imported as a normal map: uncompressed, mipmapped, trilinear, repeating.
        static Texture2D Import(string file, int width, int height, out Color32[] pixels)
        {
            string path = k_Folder + "/" + file;
            pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = Texel(x, y);
            var source = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            source.SetPixels32(pixels);
            File.WriteAllBytes(path, source.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.NormalMap;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
            importer.filterMode = FilterMode.Trilinear;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Rows 8 texels tall tilted down and up in turn (G 84, 172); columns 16 wide flat and tilted east in turn
        // (R 128, 171). B is the matching z, which the decode ignores.
        static Color32 Texel(int x, int y)
        {
            byte r = x / 16 % 2 == 1 ? (byte)171 : (byte)128, g = y / 8 % 2 == 1 ? (byte)172 : (byte)84;
            double nx = r / 255.0 * 2 - 1, ny = g / 255.0 * 2 - 1;
            byte b = (byte)Math.Round((Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny)) + 1) / 2 * 255);
            return new Color32(r, g, b, 255);
        }

        // Every level of the bake against the means; returns the mean normal's length at the coarsest level.
        static double Verify(Texture2D map, Color32[] pixels, string label, Action<bool, string> expect)
        {
            RenderTexture baked = ThermalNormalBake.Bake(map, 1f);
            if (baked == null)
            {
                expect(false, label + ": no bake");
                return 0;
            }
            try
            {
                double[][] means = Means(pixels, map.width, map.height);
                expect(baked.filterMode == FilterMode.Trilinear, $"{label}: trilinear, not {baked.filterMode}");
                expect(baked.mipmapCount == means.Length, $"{label}: {means.Length} mip levels, not {baked.mipmapCount}");
                for (int m = 0; m < Math.Min(baked.mipmapCount, means.Length); m++)
                {
                    double[] got = ReadBaked(baked, m);
                    if (got == null)
                    {
                        expect(false, $"{label}, level {m}: {baked.graphicsFormat} cannot be read back");
                        continue;
                    }
                    double off = Compare(means[m], got, out _);
                    expect(off <= k_Tolerance, string.Format(CultureInfo.InvariantCulture,
                        "{0}, level {1} off by {2:0.000000}", label, m, off));
                }
                double[] last = means[means.Length - 1];
                return Math.Sqrt(last[0] * last[0] + last[1] * last[1] + last[2] * last[2]);
            }
            finally
            {
                ThermalNormalBake.Release(baked);
            }
        }

        // Each level's xyz per texel, in double: level 0 each texel's unit normal, decoded as URP's UnpackNormalScale
        // does (x from red, alpha being 1; y from green; z rebuilt); each next level the plain means of the texels
        // above it - 2 x 2, or 3 across the edge of an odd size.
        static double[][] Means(Color32[] pixels, int width, int height)
        {
            var level = new double[pixels.Length * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                double x = pixels[i].r / 255.0 * 2 - 1, y = pixels[i].g / 255.0 * 2 - 1;
                double z = Math.Sqrt(Math.Max(0, 1 - x * x - y * y)), length = Math.Sqrt(x * x + y * y + z * z);
                level[3 * i] = x / length;
                level[3 * i + 1] = y / length;
                level[3 * i + 2] = z / length;
            }
            int levels = 1;
            for (int s = Math.Max(width, height); s > 1; s >>= 1)
                levels++;
            var all = new double[levels][];
            all[0] = level;
            int w = width, h = height;
            for (int m = 1; m < levels; m++)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var next = new double[nw * nh * 3];
                for (int v = 0; v < nh; v++)
                    for (int u = 0; u < nw; u++)
                    {
                        int x0 = u * w / nw, x1 = (u + 1) * w / nw, y0 = v * h / nh, y1 = (v + 1) * h / nh;
                        for (int j = y0; j < y1; j++)
                            for (int i = x0; i < x1; i++)
                                for (int c = 0; c < 3; c++)
                                    next[3 * (v * nw + u) + c] += all[m - 1][3 * (j * w + i) + c];
                        double n = (x1 - x0) * (y1 - y0);
                        for (int c = 0; c < 3; c++)
                            next[3 * (v * nw + u) + c] /= n;
                    }
                all[m] = next;
                w = nw;
                h = nh;
            }
            return all;
        }

        // Worst component difference, and the worst difference in length.
        static double Compare(double[] expected, double[] got, out double lengthOff)
        {
            double worst = 0;
            lengthOff = 0;
            for (int t = 0; t + 2 < Math.Min(expected.Length, got.Length); t += 3)
            {
                double e = 0, g = 0;
                for (int c = 0; c < 3; c++)
                {
                    worst = Math.Max(worst, Math.Abs(got[t + c] - expected[t + c]));
                    e += expected[t + c] * expected[t + c];
                    g += got[t + c] * got[t + c];
                }
                lengthOff = Math.Max(lengthOff, Math.Abs(Math.Sqrt(g) - Math.Sqrt(e)));
            }
            return expected.Length == got.Length ? worst : double.PositiveInfinity;
        }

        // The bake's level m as xyz, decoded from (n + 1) / 2.
        static double[] ReadBaked(RenderTexture baked, int m)
        {
            GraphicsFormat format = baked.graphicsFormat;
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels))
                return null;
            var request = AsyncGPUReadback.Request(baked, m);
            request.WaitForCompletion();
            if (request.hasError)
                return null;
            if (format == GraphicsFormat.R16G16B16A16_UNorm)
            {
                var data = request.GetData<ushort>();
                var xyz = new double[data.Length / 4 * 3];
                for (int i = 0; i < data.Length / 4; i++)
                    for (int c = 0; c < 3; c++)
                        xyz[3 * i + c] = data[4 * i + c] / 65535.0 * 2 - 1;
                return xyz;
            }
            var floats = request.GetData<float>();
            var result = new double[floats.Length / 4 * 3];
            for (int i = 0; i < floats.Length / 4; i++)
                for (int c = 0; c < 3; c++)
                    result[3 * i + c] = floats[4 * i + c] * 2.0 - 1;
            return result;
        }

        // Unity's own normal map at level m, decoded as Thermal/Base did before the bake: x from alpha times red,
        // y from green, z rebuilt - always unit length.
        static double[] ReadUnity(Texture2D map, int m)
        {
            if (!SystemInfo.IsFormatSupported(map.graphicsFormat, GraphicsFormatUsage.ReadPixels))
                return null;
            var request = AsyncGPUReadback.Request(map, m, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError)
                return null;
            var data = request.GetData<byte>();
            var xyz = new double[data.Length / 4 * 3];
            for (int i = 0; i < data.Length / 4; i++)
            {
                double x = data[4 * i + 3] / 255.0 * (data[4 * i] / 255.0) * 2 - 1, y = data[4 * i + 1] / 255.0 * 2 - 1;
                xyz[3 * i] = x;
                xyz[3 * i + 1] = y;
                xyz[3 * i + 2] = Math.Sqrt(Math.Max(0, 1 - x * x - y * y));
            }
            return xyz;
        }

        // A trim sheet's size: the same pattern at size x size, made in memory, baked and timed through to its last
        // level being readable - so the time includes the GPU's work, not just the submission.
        static string TimeSheet(int size)
        {
            var bytes = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color32 c = Texel(x, y);
                    int i = 4 * (y * size + x);
                    bytes[i] = c.r;
                    bytes[i + 1] = c.g;
                    bytes[i + 2] = c.b;
                    bytes[i + 3] = c.a;
                }
            var sheet = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            sheet.SetPixelData(bytes, 0);
            sheet.Apply(false, true);
            var clock = Stopwatch.StartNew();
            RenderTexture baked = ThermalNormalBake.Bake(sheet, 1f);
            if (baked == null)
            {
                UnityEngine.Object.DestroyImmediate(sheet);
                return $"a {size} x {size} sheet could not be baked";
            }
            AsyncGPUReadback.Request(baked, baked.mipmapCount - 1).WaitForCompletion();
            double ms = clock.Elapsed.TotalMilliseconds;
            double mb = size * (double)size * (baked.graphicsFormat == GraphicsFormat.R16G16B16A16_UNorm ? 8 : 16) * 4 / 3 / 1048576;
            ThermalNormalBake.Release(baked);
            UnityEngine.Object.DestroyImmediate(sheet);
            return string.Format(CultureInfo.InvariantCulture,
                "A {0} x {0} sheet bakes in {1:0} ms and keeps {2:0} MB on the GPU", size, ms, mb);
        }
    }
}
