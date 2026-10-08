using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// Thermal → Create Instancing Test. Sits on a map test panel and draws four more panels through
    /// Graphics.DrawMeshInstanced, the way the house tool draws its modules, so one capture holds instanced draws
    /// against the panel's own renderer. A row of five, west to east: uniform, mapped copy, the panel itself,
    /// mapped copy, uniform turned 30° about the vertical - the turn makes a wrong per-instance normal a wrong
    /// value, which an unturned copy facing south could not show.
    /// </summary>
    [ExecuteAlways]
    public class ThermalInstancingTest : MonoBehaviour
    {
        public ThermalMaterial mapped;
        public ThermalMaterial uniform;

        Material m_Instanced;
        readonly Matrix4x4[] m_Mapped = new Matrix4x4[2];
        readonly Matrix4x4[] m_Uniform = new Matrix4x4[2];

        void Update()
        {
            var filter = GetComponent<MeshFilter>();
            var meshRenderer = GetComponent<MeshRenderer>();
            if (filter == null || meshRenderer == null || mapped == null || uniform == null)
                return;

            // DrawMeshInstanced wants a material with instancing on. The thermal camera replaces it anyway.
            if (m_Instanced == null)
                m_Instanced = new Material(meshRenderer.sharedMaterial)
                {
                    enableInstancing = true,
                    hideFlags = HideFlags.HideAndDontSave,
                };

            // Offsets in metres along the panel's own axes, each copy the panel's size.
            Vector3 position = transform.position, scale = transform.lossyScale;
            Quaternion rotation = transform.rotation;
            Matrix4x4 At(float east, float turn) => Matrix4x4.TRS(position + rotation * new Vector3(east, 0f, 0f),
                                                                  rotation * Quaternion.Euler(0f, turn, 0f), scale);
            m_Uniform[0] = At(-5f, 0f);
            m_Mapped[0] = At(-2.5f, 0f);
            m_Mapped[1] = At(2.5f, 0f);
            m_Uniform[1] = At(5f, 30f);

            Mesh mesh = filter.sharedMesh;
            Graphics.DrawMeshInstanced(mesh, 0, m_Instanced, m_Mapped, m_Mapped.Length, mapped.PropertyBlock);
            Graphics.DrawMeshInstanced(mesh, 0, m_Instanced, m_Uniform, m_Uniform.Length, uniform.PropertyBlock);
        }

        void OnDisable()
        {
            if (m_Instanced == null)
                return;
            if (Application.isPlaying) Destroy(m_Instanced); else DestroyImmediate(m_Instanced);
            m_Instanced = null;
        }
    }
}
