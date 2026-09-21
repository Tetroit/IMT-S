using UnityEngine;

namespace IMT.Thermal
{
    [ExecuteAlways]
    public class ThermalObject : MonoBehaviour
    {
        public ThermalMaterial material;
        
        private MaterialPropertyBlock m_Block;
        private Renderer m_Renderer;
        
        void OnValidate() { Push(); }
        void OnEnable()     { Push(); }

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
            m_Block.SetFloat(ThermalMaterial.k_Emissivity, material.m_Emissivity);
            m_Block.SetFloat(ThermalMaterial.k_SolarAbsorptivity, material.m_SolarAbsorptivity);
            m_Renderer.SetPropertyBlock(m_Block);
        }

    }
}