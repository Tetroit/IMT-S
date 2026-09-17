using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(TerrainGenerator))]
    public class TerrainGeneratorEditor : UnityEditor.Editor
    {
        override public void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            TerrainGenerator terrainGenerator = (TerrainGenerator)target;
            if (GUILayout.Button("Generate Mesh"))
            {
                terrainGenerator.BindReferences();
                terrainGenerator.CreateMesh();
            }
            if (GUILayout.Button("Destroy children"))
            {
                foreach (var child in terrainGenerator.GetComponentsInChildren<Transform>(true))
                {
                    if (child == terrainGenerator.transform)
                        continue;

                    DestroyImmediate(child.gameObject);
                }
            }
        }
    }
}