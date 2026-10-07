using System;
using UnityEngine;

namespace IMT.Thermal
{
    [CreateAssetMenu(fileName = "ThermalMaterial", menuName = "Thermal/Material")]
    public class ThermalMaterial : ScriptableObject
    {
        [SerializeField, Range(0, 1)] public float m_Emissivity = 0.98f;
        [SerializeField, Range(0, 1)] public float m_SolarAbsorptivity = 0.9f;

        // D-010's MVP: the values painted per texel. Either map is optional; without one, the values above
        // hold for the whole object, exactly as before.
        [Header("Maps (optional)")]
        [Tooltip("R = solar absorptivity, G = emissivity (8-14 um), both linear 0-1. Where set, replaces the two " +
                 "values above. Import with sRGB off and compression None (or BC5); mipmaps on.")]
        [SerializeField] public Texture2D m_PropertyMap;
        [Tooltip("Tangent-space normal map, imported as Normal map. Bends the normal the balance and the sky see; " +
                 "shadows stay with the geometry. The mesh needs tangents.")]
        [SerializeField] public Texture2D m_NormalMap;
        [Tooltip("0 = the geometry's own normal, 1 = the map as painted.")]
        [SerializeField, Range(0, 1)] public float m_NormalStrength = 1f;
        [Tooltip("Samples the normal map this many mip levels coarser. A per-texel balance has no sideways heat " +
                 "conduction, which in reality washes out relief finer than a few cm: log2(smoothing length / " +
                 "texel size) stands in for it - 1 mm texels and ~3 cm give 5. 0 = the map as painted.")]
        [SerializeField, Min(0)] public float m_NormalMipBias = 0f;
        [Tooltip("UV tiling and offset, for both maps.")]
        [SerializeField] public Vector2 m_Tiling = Vector2.one;
        [SerializeField] public Vector2 m_Offset = Vector2.zero;

        public static readonly int k_Emissivity = Shader.PropertyToID("_Emissivity");
        public static readonly int k_SolarAbsorptivity = Shader.PropertyToID("_SolarAbsorptivity");
        public static readonly int k_PropertyMap = Shader.PropertyToID("_ThermalPropertyMap");
        public static readonly int k_PropertyMapOn = Shader.PropertyToID("_ThermalPropertyMapOn");
        public static readonly int k_NormalMap = Shader.PropertyToID("_ThermalNormalMap");
        public static readonly int k_NormalStrength = Shader.PropertyToID("_ThermalNormalStrength");
        public static readonly int k_NormalMipBias = Shader.PropertyToID("_ThermalNormalMipBias");
        public static readonly int k_MapST = Shader.PropertyToID("_ThermalMapST");

        /// <summary>
        /// Writes these properties into a block, for Thermal/Base to read per draw. ThermalObject hands the block to
        /// its Renderer, code that draws through Graphics passes it to the draw call.
        /// </summary>
        public void ApplyTo(MaterialPropertyBlock block)
        {
            block.SetFloat(k_Emissivity, m_Emissivity);
            block.SetFloat(k_SolarAbsorptivity, m_SolarAbsorptivity);

            // Maps: a missing one is switched off, not faked - the shader skips it, so an object without
            // maps renders exactly as before. The stand-in textures only keep the slots bound.
            bool properties = m_PropertyMap != null, normals = m_NormalMap != null;
            block.SetTexture(k_PropertyMap, properties ? m_PropertyMap : Texture2D.whiteTexture);
            block.SetFloat(k_PropertyMapOn, properties ? 1f : 0f);
            block.SetTexture(k_NormalMap, normals ? m_NormalMap : Texture2D.normalTexture);
            block.SetFloat(k_NormalStrength, normals ? m_NormalStrength : 0f);
            block.SetFloat(k_NormalMipBias, m_NormalMipBias);
            block.SetVector(k_MapST, new Vector4(m_Tiling.x, m_Tiling.y, m_Offset.x, m_Offset.y));
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            foreach (var o in FindObjectsByType<ThermalObject>())
                if (o.material == this)
                    o.Push();
#endif
        }
    }
}
