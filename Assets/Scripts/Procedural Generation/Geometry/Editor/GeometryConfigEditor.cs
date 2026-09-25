using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(GeometryContext))] 
    public class GeometryConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            GeometryContext geometryContext = (GeometryContext)target;
            if (GUILayout.Button("Set Origin to context"))
            {
                serializedObject.Update();

                Undo.RecordObject(geometryContext, "Set Geometry Origin");

                geometryContext.SetOriginAtContextReference();

                EditorUtility.SetDirty(geometryContext);

                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}