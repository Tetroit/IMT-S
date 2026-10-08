using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace IMT.Thermal
{
    [CreateAssetMenu(fileName = "ThermalMaterial", menuName = "Thermal/Material")]
    public class ThermalMaterial : ScriptableObject
    {
        [SerializeField, Range(0, 1)] public float m_Emissivity = 0.98f;
        [SerializeField, Range(0, 1)] public float m_SolarAbsorptivity = 0.9f;
        [Tooltip("Where the two values come from: dataset, handbook table or measurement.")]
        [SerializeField, TextArea(1, 4)] public string m_Source;

        // D-010's MVP: the values painted per texel. Either map is optional; without one, the values above
        // hold for the whole object, exactly as before. A trim sheet is one material: the sheet's *_Thermal map,
        // its normal map if it has one, tiling 1 and offset 0 - the modules' own UVs address the strips.
        [Header("Maps (optional) - for a trim sheet, the sheet's")]
        [Tooltip("R = solar absorptivity, G = emissivity (8-14 um), both linear 0-1. Where set, replaces the two " +
                 "values above. Name the texture *_Thermal and its import settings are locked to linear, " +
                 "uncompressed and box-filtered mips; otherwise set those by hand.")]
        [SerializeField] public Texture2D m_PropertyMap;
        [Tooltip("Tangent-space normal map, imported as Normal map - the visual material's own. The thermal camera " +
                 "reads a copy made from it whose mips keep the mean normal's length (ThermalNormalBake), remade " +
                 "when the map or the strength changes. Bends the normal the balance and the sky see; shadows stay " +
                 "with the geometry. The mesh needs tangents.")]
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

        [NonSerialized] MaterialPropertyBlock m_Block;

        // The baked normals and what they were baked from.
        [NonSerialized] RenderTexture m_Baked;
        [NonSerialized] Texture2D m_BakedMap;
        [NonSerialized] float m_BakedStrength;
        [NonSerialized] Hash128 m_BakedContents;
        [NonSerialized] Vector2Int m_BakedSize;

        /// <summary>
        /// This material in a block of its own, for draws that have no renderer to hold one: instanced draws
        /// such as the house tool's modules. One block per material, shared by every draw of it, so a frame of
        /// thousands of draws builds none. Rebuilt when the asset changes in the editor.
        /// </summary>
        public MaterialPropertyBlock PropertyBlock
        {
            get
            {
                if (m_Block == null)
                {
                    m_Block = new MaterialPropertyBlock();
                    WriteTo(m_Block);
                }
                return m_Block;
            }
        }

        /// <summary>
        /// The normal map as the thermal shader reads it: ThermalNormalBake's copy, whose mips keep the mean normal's
        /// length. Made on first use and again when the map, its import or the strength changes, or the GPU lost it;
        /// null without a map, at strength 0, or where the copy cannot be made.
        /// </summary>
        public RenderTexture ThermalNormals
        {
            get
            {
                if (m_NormalMap == null || m_NormalStrength <= 0f)
                    return null;
                Hash128 contents = ContentsOf(m_NormalMap);
                var size = new Vector2Int(m_NormalMap.width, m_NormalMap.height);
                if (m_Baked == null || !m_Baked.IsCreated() || m_BakedMap != m_NormalMap ||
                    m_BakedStrength != m_NormalStrength || m_BakedContents != contents || m_BakedSize != size)
                {
                    ThermalNormalBake.Release(m_Baked);
                    m_Baked = ThermalNormalBake.Bake(m_NormalMap, m_NormalStrength);
                    m_BakedMap = m_NormalMap;
                    m_BakedStrength = m_NormalStrength;
                    m_BakedContents = contents;
                    m_BakedSize = size;
                }
                return m_Baked;
            }
        }

        static Hash128 ContentsOf(Texture map)
        {
#if UNITY_EDITOR
            return map.imageContentsHash;       // changes when the map is reimported
#else
            return default;
#endif
        }

        /// <summary>Everything the thermal shader reads from a material, into <paramref name="block"/>.</summary>
        public void WriteTo(MaterialPropertyBlock block)
        {
            block.SetFloat(k_Emissivity, m_Emissivity);
            block.SetFloat(k_SolarAbsorptivity, m_SolarAbsorptivity);

            // Maps: a missing one is switched off, not faked - the shader skips it, so an object without
            // maps renders exactly as before. The stand-in textures only keep the slots bound.
            bool properties = m_PropertyMap != null;
            RenderTexture normals = ThermalNormals;
            block.SetTexture(k_PropertyMap, properties ? m_PropertyMap : Texture2D.whiteTexture);
            block.SetFloat(k_PropertyMapOn, properties ? 1f : 0f);
            // The baked copy, its strength built in: the shader's strength only switches it on.
            block.SetTexture(k_NormalMap, normals != null ? normals : (Texture)Texture2D.normalTexture);
            block.SetFloat(k_NormalStrength, normals != null ? 1f : 0f);
            block.SetFloat(k_NormalMipBias, m_NormalMipBias);
            block.SetVector(k_MapST, new Vector4(m_Tiling.x, m_Tiling.y, m_Offset.x, m_Offset.y));
        }

        void OnDisable()
        {
            ThermalNormalBake.Release(m_Baked);
            m_Baked = null;
        }

        private void OnValidate()
        {
            m_Block = null;
#if UNITY_EDITOR
            // Once the edit has settled: a push can bake, and OnValidate is no place to render or destroy.
            UnityEditor.EditorApplication.delayCall += Refresh;
#endif
        }

#if UNITY_EDITOR
        static readonly HashSet<string> s_Warned = new HashSet<string>();

        /// <summary>
        /// Every renderer and draw using this material picks up its current state, the baked normals included -
        /// after an edit, and when its normal map is reimported (ThermalNormalBakeRefresh).
        /// </summary>
        public void Refresh()
        {
            if (this == null)       // deleted before the call came
                return;
            m_Block = null;
            foreach (var o in FindObjectsByType<ThermalObject>())
                if (o.material == this)
                    o.Push();
            Warn(m_PropertyMap, "property map", ValueProblem(m_PropertyMap) ?? DistanceProblem(m_PropertyMap));
            Warn(m_NormalMap, "normal map", m_NormalMap != null && GraphicsFormatUtility.IsSRGBFormat(m_NormalMap.graphicsFormat)
                ? "is imported as colour, so its values arrive gamma-encoded: set Texture Type to Normal map" : null);
        }

        /// <summary>
        /// What would make a map's values step or shimmer as the camera moves, or null. Unity's default filter,
        /// bilinear, switches mip levels in steps - level-of-detail jumping, the client's first priority (D-010, maps
        /// at a distance). A map without mipmaps aliases instead. The normal map is exempt: its baked copy is
        /// trilinear with its own mips, whatever the map's settings.
        /// </summary>
        public static string DistanceProblem(Texture map)
        {
            if (map == null)
                return null;
            if (map.mipmapCount <= 1)
                return "has no mipmaps, so it shimmers at a distance: tick Generate Mipmaps";
            if (map.filterMode != FilterMode.Trilinear)
                return $"is filtered {map.filterMode}, so its mip levels switch in steps as the camera moves: " +
                       "set Filter Mode to Trilinear";
            return null;
        }

        /// <summary>
        /// What would corrupt the painted values themselves, or null: sRGB decoding or compression. A map named
        /// *_Thermal cannot have either (ThermalPropertyMapImporter locks it); one named otherwise gets Unity's
        /// defaults, which have both, and nothing would look wrong.
        /// </summary>
        public static string ValueProblem(Texture map)
        {
            if (map == null)
                return null;
            GraphicsFormat format = map.graphicsFormat;
            if (GraphicsFormatUtility.IsSRGBFormat(format))
                return $"is decoded as sRGB colour ({format}), which bends every value: name it *_Thermal to lock its import";
            if (GraphicsFormatUtility.IsCompressedFormat(format))
                return $"is compressed ({format}), which blurs every value: name it *_Thermal to lock its import";
            return null;
        }

        // Once per map and problem in a session, however often the material is validated.
        void Warn(Texture map, string what, string problem)
        {
            if (problem == null || !s_Warned.Add($"{GetEntityId()} {map.GetEntityId()} {problem}"))
                return;
            Debug.LogWarning($"[Thermal] {name}: the {what} {map.name} {problem}.", this);
        }
#endif
    }
}
