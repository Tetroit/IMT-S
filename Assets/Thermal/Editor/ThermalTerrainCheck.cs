using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Terrain Check (2026-11-02): Thermal/Terrain draws what Thermal/Base draws.
    /// <list type="bullet">
    /// <item>The thermal renderer (Thermal_Renerer) has the Thermal Terrain Feature, and its RenderObjects pass draws the
    /// check's layer.</item>
    /// <item>The same flat 10 m quad, rendered offscreen by a camera on the thermal renderer: once drawn with
    /// Thermal/Terrain through the feature's pass, once as a scene renderer through the RenderObjects pass (Thermal/
    /// Base), once with neither for the background. Both read the same 4 x 4 property map (every texel its own values, nearest) with the same surface
    /// group. They must cover the same pixels and agree within 1e-5 W/m²/sr (3e-4 NETD: Thermal/Terrain's normal is
    /// exactly up, the mesh's interpolated and normalised), for a masonry case and a grass case.</item>
    /// </list>
    /// Needs a ThermalEnvironment and the thermal camera in the scene.
    /// </summary>
    static class ThermalTerrainCheck
    {
        const string k_Renderer = "Assets/Settings/Thermal_Renerer.asset";
        const int k_BaseLayer = 31;       // the Thermal/Base quad's: the check's camera sees nothing else
        const int k_Size = 384;
        const double k_Tolerance = 1e-5;  // W/m²/sr

        [MenuItem("Thermal/Terrain Check")]
        static void Run()
        {
            int failed = 0, checks = 0;
            void Report(bool ok, string line)
            {
                checks++;
                if (ok) Debug.Log("[Terrain] " + line);
                else { failed++; Debug.LogError("[Terrain] " + line); }
            }

            // The renderer: the feature on it, and the RenderObjects pass drawing the check's layer
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(k_Renderer);
            if (rendererData == null)
            {
                Report(false, $"no renderer at {k_Renderer}   FAIL");
                Summary(failed, checks);
                return;
            }
            bool feature = false, drawsBase = false;
            foreach (ScriptableRendererFeature f in rendererData.rendererFeatures)
            {
                if (f is ThermalTerrainFeature && f.isActive)
                    feature = true;
                if (f is RenderObjects objects && objects.isActive &&
                    (objects.settings.filterSettings.LayerMask & (1 << k_BaseLayer)) != 0)
                    drawsBase = true;
            }
            bool rendererOk = feature && drawsBase;
            Report(rendererOk, $"the thermal renderer: the Thermal Terrain Feature on it {(feature ? "yes" : "NO")}, its " +
                               $"RenderObjects pass drawing layer {k_BaseLayer} {(drawsBase ? "yes" : "NO")}   " +
                               $"{(rendererOk ? "ok" : "FAIL")}");
            if (!rendererOk)
            {
                Summary(failed, checks);
                return;
            }

            var environment = UnityEngine.Object.FindAnyObjectByType<ThermalEnvironment>();
            Camera source = Camera.main;
            if (source == null)
            {
                var capture = UnityEngine.Object.FindAnyObjectByType<ThermalCapture>();
                source = capture != null ? capture.GetComponent<Camera>() : null;
            }
            if (environment == null || !environment.isActiveAndEnabled || source == null)
            {
                Report(false, "needs an enabled ThermalEnvironment and the thermal camera in the scene   FAIL");
                Summary(failed, checks);
                return;
            }

            GameObject cameraObject = null, baseObject = null;
            RenderTexture target = null;
            Mesh mesh = null;
            Material terrainMaterial = null, baseMaterial = null;
            Texture2D[] maps = { PropertyMap(1), PropertyMap(2) };
            try
            {
                target = new RenderTexture(k_Size, k_Size, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    name = "Thermal Terrain Check", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave,
                };
                target.Create();

                cameraObject = EditorUtility.CreateGameObjectWithHideFlags("Thermal Terrain Check camera",
                                                                           HideFlags.HideAndDontSave, typeof(Camera));
                var camera = cameraObject.GetComponent<Camera>();
                camera.CopyFrom(source);
                camera.targetTexture = target;
                camera.cullingMask = 1 << k_BaseLayer;
                camera.allowMSAA = false;
                camera.transform.SetPositionAndRotation(new Vector3(0, 6, 12),
                                                        Quaternion.LookRotation(new Vector3(0, -6, 13)));
                var cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.SetRenderer(RendererIndex(source));
                cameraData.renderPostProcessing = false;
                cameraData.antialiasing = AntialiasingMode.None;

                // The quad, x -5..5, z 20..30 at y 0, uv 0-1 across
                Vector3 corner = new Vector3(-5, 0, 20);
                mesh = new Mesh { name = "Thermal Terrain Check quad", hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[] { corner, corner + new Vector3(0, 0, 10), corner + new Vector3(10, 0, 10),
                                        corner + new Vector3(10, 0, 0) };
                mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
                mesh.tangents = new Vector4[4];
                mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };

                terrainMaterial = new Material(Shader.Find("Thermal/Terrain")) { hideFlags = HideFlags.HideAndDontSave };
                baseMaterial = new Material(Shader.Find("Thermal/Base")) { hideFlags = HideFlags.HideAndDontSave };
                MeshRenderer baseQuad = Quad("Thermal Terrain Check base", k_BaseLayer, mesh, baseMaterial, out baseObject);

                float[] background = Render(camera, target);

                foreach (var (name, group, map) in new[]
                         {
                             ("masonry", ThermalSurfaceGroup.Object, maps[0]),
                             ("grass", ThermalSurfaceGroup.Grass, maps[1]),
                         })
                {
                    // What ThermalMaterial writes for an object with a property map
                    var block = new MaterialPropertyBlock();
                    (float a, float b) = ThermalSurfaceGroups.Wind(group);
                    block.SetFloat("_ThermalWindA", a);
                    block.SetFloat("_ThermalWindB", b);
                    block.SetFloat("_ThermalBackside", ThermalSurfaceGroups.Back(group));
                    block.SetFloat("_ThermalHistoryRow", ThermalSurfaceGroups.HistoryRow(group));
                    block.SetTexture("_ThermalPropertyMap", map);
                    block.SetFloat("_ThermalPropertyMapOn", 1);
                    block.SetFloat("_ThermalNormalStrength", 0);
                    block.SetVector("_ThermalMapST", new Vector4(1, 1, 0, 0));

                    ThermalTerrainFeature.Solo = (cmd, _) =>
                        cmd.DrawMesh(mesh, Matrix4x4.identity, terrainMaterial, 0, 0, block);
                    float[] byTerrain = Render(camera, target);
                    ThermalTerrainFeature.Solo = null;

                    baseQuad.SetPropertyBlock(block);
                    baseObject.SetActive(true);
                    float[] byBase = Render(camera, target);
                    baseObject.SetActive(false);

                    int coverTerrain = 0, coverBase = 0, coverDiffers = 0, identical = 0;
                    double worst = 0, sum = 0;
                    for (int i = 0; i < background.Length; i++)
                    {
                        bool t = !Same(byTerrain[i], background[i]), q = !Same(byBase[i], background[i]);
                        if (t) coverTerrain++;
                        if (q) coverBase++;
                        if (t != q)
                        {
                            coverDiffers++;
                            continue;
                        }
                        if (!t)
                            continue;
                        sum += byTerrain[i];
                        if (Same(byTerrain[i], byBase[i]))
                            identical++;
                        double d = Math.Abs((double)byTerrain[i] - byBase[i]);
                        worst = double.IsNaN(d) ? double.PositiveInfinity : Math.Max(worst, d);
                    }
                    if (coverTerrain == 0 || coverBase == 0)
                    {
                        Report(false, $"{name}: {(coverTerrain == 0 ? "Thermal/Terrain" : "Thermal/Base")} drew nothing" +
                                      "   FAIL");
                        continue;
                    }
                    bool ok = coverDiffers == 0 && worst <= k_Tolerance;
                    int both = coverTerrain - coverDiffers;
                    Report(ok, string.Format(CultureInfo.InvariantCulture,
                        "{0}: Thermal/Terrain covers {1} pixels, Thermal/Base {2} ({3} not both); there, mean {4:F4} " +
                        "W/m²/sr, {5:P1} bit-identical, worst {6:0.0e+0} (limit {7:0e+0})   {8}", name, coverTerrain,
                        coverBase, coverDiffers, sum / Math.Max(1, both), identical / (double)Math.Max(1, both), worst,
                        k_Tolerance, ok ? "ok" : "FAIL"));
                }
            }
            finally
            {
                ThermalTerrainFeature.Solo = null;
                foreach (UnityEngine.Object o in new UnityEngine.Object[]
                         { cameraObject, baseObject, mesh, terrainMaterial, baseMaterial, maps[0], maps[1] })
                    if (o != null)
                        UnityEngine.Object.DestroyImmediate(o);
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
            Summary(failed, checks);
        }

        static void Summary(int failed, int checks)
        {
            if (failed == 0)
                Debug.Log($"[Terrain] all {checks} checks passed: Thermal/Terrain draws what Thermal/Base draws");
            else
                Debug.LogError($"[Terrain] {failed} of {checks} checks FAILED");
        }

        static MeshRenderer Quad(string name, int layer, Mesh mesh, Material material, out GameObject quad)
        {
            quad = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, typeof(MeshFilter),
                                                               typeof(MeshRenderer));
            quad.layer = layer;
            quad.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            quad.SetActive(false);
            return renderer;
        }

        static float[] Render(Camera camera, RenderTexture target)
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
                RenderPipeline.SubmitRenderRequest(camera, request);
            else
                camera.Render();
            AsyncGPUReadbackRequest readback = AsyncGPUReadback.Request(target, 0, TextureFormat.RFloat);
            readback.WaitForCompletion();
            if (readback.hasError)
                throw new InvalidOperationException("the check's readback failed");
            return readback.GetData<float>().ToArray();
        }

        // 4 x 4, linear, nearest: every texel its own absorptivity (R) and emissivity (G).
        static Texture2D PropertyMap(int seed)
        {
            var map = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            {
                name = "Thermal Terrain Check map", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)(60 + (i * 37 + seed * 11) % 190), (byte)(40 + (i * 53 + seed * 7) % 215), 0, 255);
            map.SetPixels32(pixels);
            map.Apply(false, false);
            return map;
        }

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        // The thermal camera's renderer: URP keeps the index private; -1, the asset's default, if it cannot be read.
        static int RendererIndex(Camera camera)
        {
            var data = camera.GetUniversalAdditionalCameraData();
            FieldInfo field = typeof(UniversalAdditionalCameraData).GetField("m_RendererIndex",
                                                                             BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null ? (int)field.GetValue(data) : -1;
        }
    }
}
