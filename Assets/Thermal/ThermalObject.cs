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

            // Maps: a missing one is switched off, not faked - the shader skips it, so an object without
            // maps renders exactly as before. The stand-in textures only keep the slots bound.
            bool properties = material.m_PropertyMap != null, normals = material.m_NormalMap != null;
            m_Block.SetTexture(ThermalMaterial.k_PropertyMap, properties ? material.m_PropertyMap : Texture2D.whiteTexture);
            m_Block.SetFloat(ThermalMaterial.k_PropertyMapOn, properties ? 1f : 0f);
            m_Block.SetTexture(ThermalMaterial.k_NormalMap, normals ? material.m_NormalMap : Texture2D.normalTexture);
            m_Block.SetFloat(ThermalMaterial.k_NormalStrength, normals ? material.m_NormalStrength : 0f);
            m_Block.SetFloat(ThermalMaterial.k_NormalMipBias, material.m_NormalMipBias);
            m_Block.SetVector(ThermalMaterial.k_MapST, new Vector4(material.m_Tiling.x, material.m_Tiling.y,
                                                                   material.m_Offset.x, material.m_Offset.y));
            m_Renderer.SetPropertyBlock(m_Block);
        }

    }
}