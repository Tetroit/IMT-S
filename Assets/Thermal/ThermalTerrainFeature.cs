using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace IMT.Thermal
{
    /// <summary>
    /// The thermal renderer's pass for terrain (2026-11-02). Add it to the thermal renderer (Thermal_Renerer → Add
    /// Renderer Feature → Thermal Terrain Feature). Once per camera per frame it opens a pass into the camera's colour
    /// and depth, at the same point as the RenderObjects pass that draws the scene's renderers with Thermal/Base (so the
    /// two depth-test against each other), and hands its command buffer to whatever subscribed to <see cref="Draw"/>:
    /// the terrain's own code, which records its draws there with Thermal/Terrain (DrawMesh, DrawMeshInstanced,
    /// procedural or indirect).
    /// Draws recorded here are not scene renderers, so the RenderObjects override never replaces their shader.
    /// </summary>
    public class ThermalTerrainFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// Called inside the pass, once per camera per frame: record the terrain's draws into the buffer. Make no
        /// textures or buffers here; prepare them beforehand.
        /// </summary>
        public static event Action<RasterCommandBuffer, Camera> Draw;

        /// <summary>While set, the pass calls this alone instead of <see cref="Draw"/> (Thermal → Terrain Check).</summary>
        public static Action<RasterCommandBuffer, Camera> Solo { get; set; }

        [Tooltip("When the terrain is drawn: with the RenderObjects pass (After Rendering Opaques); added before it in " +
                 "the list, it draws first.")]
        [SerializeField] RenderPassEvent m_Event = RenderPassEvent.AfterRenderingOpaques;

        TerrainPass m_Pass;

        public override void Create()
        {
            m_Pass = new TerrainPass { renderPassEvent = m_Event };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (Draw == null && Solo == null)
                return;
            CameraType type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || type == CameraType.Reflection)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        class TerrainPass : ScriptableRenderPass
        {
            class PassData
            {
                public Camera camera;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Thermal Terrain", out PassData data))
                {
                    data.camera = frameData.Get<UniversalCameraData>().camera;
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Write);
                    builder.UseAllGlobalTextures(true);     // URP's shadow map, which the terrain's frag samples
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData d, RasterGraphContext context) =>
                    {
                        if (Solo != null)
                            Solo(context.cmd, d.camera);
                        else
                            Draw?.Invoke(context.cmd, d.camera);
                    });
                }
            }
        }
    }
}
