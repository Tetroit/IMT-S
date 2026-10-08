using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace IMT.Thermal
{
    /// <summary>
    /// The thermal camera's copy of a normal map (D-010, maps at a distance). Its texels are the unit normals
    /// Thermal/Base took from the map until 2026-10-06 - URP's UnpackNormalScale (z rebuilt from x and y, the
    /// strength applied), normalised - but each mip level is the plain average of the level above, NOT renormalised.
    /// A coarse texel therefore holds the mean of the normals it covers, shorter than 1 where they disagree. The sun on
    /// that mean normal is the mean sun on the normals, so a relief's radiance holds as coarser mips take over. Unity's
    /// normal maps keep only x and y and rebuild a unit z from them, so the length is gone from them; hence a copy.
    ///
    /// Made on the GPU (Resources/ThermalNormalBake.compute), so a whole trim sheet takes milliseconds and no CPU
    /// memory. It reads the imported map's texels as the hardware decodes them, whatever the format. Each level is
    /// averaged in float from the one above and rounded once into the copy. Stored 16-bit (float where unsupported),
    /// uncompressed, trilinear and with the map's wrap. Never saved: made when needed, by ThermalMaterial.
    /// </summary>
    public static class ThermalNormalBake
    {
        static ComputeShader s_Shader;

        /// <summary>The thermal copy of <paramref name="map"/> at <paramref name="strength"/>; null if this GPU cannot
        /// run compute shaders. The caller owns it - give it back with <see cref="Release"/>.</summary>
        public static RenderTexture Bake(Texture map, float strength)
        {
            if (s_Shader == null)
                s_Shader = Resources.Load<ComputeShader>("ThermalNormalBake");
            if (s_Shader == null || !SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[Thermal] cannot bake the thermal copy of " + map.name +
                               ": no compute shaders here, so the normal map is left out");
                return null;
            }

            int width = map.width, height = map.height, levels = 1;
            for (int s = Math.Max(width, height); s > 1; s >>= 1)
                levels++;
            GraphicsFormat format = SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_UNorm, GraphicsFormatUsage.LoadStore)
                ? GraphicsFormat.R16G16B16A16_UNorm
                : GraphicsFormat.R32G32B32A32_SFloat;
            var baked = new RenderTexture(new RenderTextureDescriptor(width, height, format, 0, levels)
            {
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true,
            })
            {
                name = map.name + " (thermal)",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Trilinear,
                wrapModeU = map.wrapModeU,
                wrapModeV = map.wrapModeV,
                anisoLevel = map.anisoLevel,
            };
            baked.Create();

            int decode = s_Shader.FindKernel("Decode"), halve = s_Shader.FindKernel("Halve");
            s_Shader.SetFloat("_Strength", strength);
            RenderTexture above = null;
            int w = width, h = height, aboveW = 0, aboveH = 0;
            for (int m = 0; m < levels; m++)
            {
                RenderTexture mean = RenderTexture.GetTemporary(
                    new RenderTextureDescriptor(w, h, GraphicsFormat.R32G32B32A32_SFloat, 0) { enableRandomWrite = true });
                int kernel = m == 0 ? decode : halve;
                s_Shader.SetTexture(kernel, "_Source", m == 0 ? map : above);
                s_Shader.SetTexture(kernel, "_Mean", mean);
                s_Shader.SetTexture(kernel, "_Baked", baked, m);
                s_Shader.SetInts("_Size", w, h);
                s_Shader.SetInts("_SourceSize", aboveW, aboveH);
                s_Shader.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);

                if (above != null)
                    RenderTexture.ReleaseTemporary(above);
                above = mean;
                aboveW = w;
                aboveH = h;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            RenderTexture.ReleaseTemporary(above);
            return baked;
        }

        /// <summary>Gives back a texture made here or a texture made for the bake, in edit mode as in play mode.</summary>
        public static void Release(Texture texture)
        {
            if (texture == null)
                return;
            if (texture is RenderTexture rt)
                rt.Release();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
