using UnityEngine;

namespace IMT.Thermal
{
    [ExecuteAlways]
    public class ThermalObject : MonoBehaviour
    {
        public ThermalMaterial material;
        
        private MaterialPropertyBlock m_Block;
        private Renderer m_Renderer;
        
        void OnEnable()     { Push(); }

        void OnValidate()
        {
#if UNITY_EDITOR
            // Once the edit has settled: a push can bake a normal map, and OnValidate is no place to render or destroy.
            UnityEditor.EditorApplication.delayCall += () => { if (this != null) Push(); };
#endif
        }

        public void Push()
        {
            if (m_Block == null)
                m_Block = new MaterialPropertyBlock();
            if (m_Renderer == null)
                m_Renderer = GetComponent<Renderer>();
            if (m_Renderer == null)
            {
                Debug.LogError("Thermal Object is missing a Renderer component.");
                return;
            }

            if (material == null)
            {
                Debug.LogError("Thermal Object is missing a Material component.");
                return;
            }
            m_Renderer.GetPropertyBlock(m_Block);
            material.WriteTo(m_Block);
            m_Renderer.SetPropertyBlock(m_Block);
        }

    }
}