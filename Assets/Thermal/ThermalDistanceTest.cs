using UnityEngine;
using UnityEngine.Rendering;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Create Distance Test (D-010: maps at a distance). Sits on a backdrop quad that has no thermal
    /// material - it shows the override's own values - and draws a ladder of 1 m panels just in front of it with
    /// Graphics.DrawMesh, each with its own property block, the way the house tool draws its roofs. Rung k tiles its
    /// maps 2^k times, so it samples the mip level a panel tiled once would at 2^k times the distance: one capture
    /// holds every distance, each rung at the same pixel count and nothing else changing. A normal-mapped relief in
    /// the top row, a fine mix of bare metal and paint in the bottom row, and at the end of that row a control
    /// painted uniformly with the mix's average. The rungs cast no shadows, so the backdrop around them keeps one
    /// value and marks their edges to the pixel. Captures/distance_test.py predicts every rung and reads the capture.
    /// </summary>
    [ExecuteAlways]
    public class ThermalDistanceTest : MonoBehaviour
    {
        public ThermalMaterial relief;
        public ThermalMaterial mix;
        public ThermalMaterial averaged;
        [Min(1)] public int rungs = 8;

        /// <summary>Metres: a rung's side, rung centre to rung centre, the backdrop's border, the rungs' standoff.</summary>
        public const float k_Rung = 1f, k_Pitch = 1.25f, k_Border = 0.5f, k_Standoff = 0.01f;

        Material m_Material;
        MaterialPropertyBlock[] m_Blocks;

        /// <summary>The backdrop for a ladder of <paramref name="rungs"/>: one column more, for the control.</summary>
        public static Vector2 BackdropSize(int rungs) =>
            new Vector2(rungs * k_Pitch + k_Rung + 2 * k_Border, k_Pitch + k_Rung + 2 * k_Border);

        void Update()
        {
            var filter = GetComponent<MeshFilter>();
            var meshRenderer = GetComponent<MeshRenderer>();
            if (filter == null || meshRenderer == null || relief == null || mix == null || averaged == null)
                return;

            // DrawMesh wants a material. The thermal camera replaces it anyway.
            if (m_Material == null)
                m_Material = new Material(meshRenderer.sharedMaterial) { hideFlags = HideFlags.HideAndDontSave };
            if (m_Blocks == null || m_Blocks.Length != 2 * rungs + 1)
            {
                m_Blocks = new MaterialPropertyBlock[2 * rungs + 1];
                for (int i = 0; i < m_Blocks.Length; i++)
                    m_Blocks[i] = new MaterialPropertyBlock();
            }

            Mesh mesh = filter.sharedMesh;
            for (int k = 0; k < rungs; k++)
            {
                Draw(mesh, k, 0.5f, relief, 1 << k, m_Blocks[2 * k]);
                Draw(mesh, k, -0.5f, mix, 1 << k, m_Blocks[2 * k + 1]);
            }
            Draw(mesh, rungs, -0.5f, averaged, 1f, m_Blocks[2 * rungs]);
        }

        void Draw(Mesh mesh, int column, float row, ThermalMaterial material, float tiling, MaterialPropertyBlock block)
        {
            // Refilled every frame, so an edit to a material shows at once. The tiling multiplies the material's own.
            material.WriteTo(block);
            block.SetVector(ThermalMaterial.k_MapST, new Vector4(tiling * material.m_Tiling.x, tiling * material.m_Tiling.y,
                                                                 material.m_Offset.x, material.m_Offset.y));
            // Columns centred on the backdrop; a quad faces its own -z, so the standoff is towards the viewer.
            var offset = new Vector3((column - rungs * 0.5f) * k_Pitch, row * k_Pitch, -k_Standoff);
            Matrix4x4 matrix = Matrix4x4.TRS(transform.position + transform.rotation * offset, transform.rotation,
                                             new Vector3(k_Rung, k_Rung, 1f));
            Graphics.DrawMesh(mesh, matrix, m_Material, gameObject.layer, null, 0, block, ShadowCastingMode.Off);
        }

        void OnDisable()
        {
            if (m_Material == null)
                return;
            if (Application.isPlaying) Destroy(m_Material); else DestroyImmediate(m_Material);
            m_Material = null;
        }
    }
}
