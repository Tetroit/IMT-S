using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// When a texture is reimported, every ThermalMaterial whose normal map it is rebakes its thermal copy
    /// (ThermalNormalBake) and pushes it to whatever draws with it: the artist exports, the thermal camera follows.
    /// </summary>
    class ThermalNormalBakeRefresh : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var textures = new HashSet<string>();
            foreach (string path in imported)
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(Texture2D))
                    textures.Add(path);
            if (textures.Count == 0)
                return;

            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(ThermalMaterial)))
            {
                var material = AssetDatabase.LoadAssetAtPath<ThermalMaterial>(AssetDatabase.GUIDToAssetPath(guid));
                if (material != null && material.m_NormalMap != null &&
                    textures.Contains(AssetDatabase.GetAssetPath(material.m_NormalMap)))
                    EditorApplication.delayCall += material.Refresh;
            }
        }
    }
}
