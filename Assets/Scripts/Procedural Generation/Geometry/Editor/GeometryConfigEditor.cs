using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(GeometryConfig))] 
    public class GeometryConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            GeometryConfig geometryConfig = (GeometryConfig)target;
            if (GUILayout.Button("Set Origin to context"))
            {
                serializedObject.Update();

                Undo.RecordObject(geometryConfig, "Set Geometry Origin");

                geometryConfig.SetOriginAtContextReference();

                EditorUtility.SetDirty(geometryConfig);

                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}