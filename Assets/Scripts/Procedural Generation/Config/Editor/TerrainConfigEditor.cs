using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(TerrainConfig))]
    public class TerrainConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            TerrainConfig terrainConfig = (TerrainConfig)target;
            if (GUILayout.Button("Set Origin to context"))
            {
                serializedObject.Update();

                Undo.RecordObject(terrainConfig, "Set Geometry Origin");

                terrainConfig.SetOriginAtContextReference();

                EditorUtility.SetDirty(terrainConfig);

                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}