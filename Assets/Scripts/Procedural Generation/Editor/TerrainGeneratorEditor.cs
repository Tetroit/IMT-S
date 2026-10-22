using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(TerrainGenerator))]
    public class TerrainGeneratorEditor : UnityEditor.Editor
    {
        private Dictionary<int, SatContext.SatMetadata.ClassInfo> _classInfos = new Dictionary<int, SatContext.SatMetadata.ClassInfo>();
        private Dictionary<int, GUIStyle> _classStyles = new Dictionary<int, GUIStyle>();
        
        override public void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            TerrainGenerator terrainGenerator = (TerrainGenerator)target;
            
            var classes = terrainGenerator.segClasses;
            if (classes != null)
            {
                foreach (var segClass in terrainGenerator.segClasses)
                {
                    if (_classInfos.TryAdd(segClass.index, segClass))
                    {
                        var style = new GUIStyle();
                        style.normal.textColor = segClass.color32;
                        _classStyles.Add(segClass.index, style);
                    }
                }
                using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("ID", GUILayout.Width(30));
                    GUILayout.Label("Name", GUILayout.Width(120));
                    GUILayout.Label("Material", GUILayout.Width(120));
                    GUILayout.Label("UV", GUILayout.Width(120));
                }
                foreach (var (index, segClass) in _classInfos)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(segClass.index.ToString(), GUILayout.Width(30));
                        GUILayout.Label(segClass.name, _classStyles[index], GUILayout.Width(120));
                        GUILayout.Label(terrainGenerator.GetMaterial(segClass.index).name, GUILayout.Width(120));
                        GUILayout.Label(terrainGenerator.GetUV(segClass.index).ToString(), GUILayout.Width(120));
                    }
                }
            }
            
            if (GUILayout.Button("Generate Random Mesh"))
            {
                terrainGenerator.BindReferences();
                terrainGenerator.CreateRandomMesh();
            }
            if (GUILayout.Button("Generate Mesh"))
            {
                terrainGenerator.BindReferences();
                terrainGenerator.CreateMeshForContext();
            }
            if (GUILayout.Button("Destroy children"))
            {
                terrainGenerator.DestroyGenerated();
            }

            GUILayout.Space(10);
            if (GUILayout.Button("Run Full Pipeline"))
            {
                terrainGenerator.RunPipeline();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Clear Pipeline Cache"))
            {
                var satContext = terrainGenerator.satContext;
                if (satContext == null)
                    terrainGenerator.ClearPipelineCache();
                else if (EditorUtility.DisplayDialog("Clear Pipeline Cache",
                             $"Delete the cached pipeline files of {satContext.name}?\n\n" +
                             string.Join("\n", satContext.cachePaths) +
                             "\n\nSat images and metadata are kept.",
                             "Delete", "Cancel"))
                {
                    terrainGenerator.ClearPipelineCache();
                    AssetDatabase.Refresh();
                }
            }
        }
    }
}