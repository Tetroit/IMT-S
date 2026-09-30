using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(HouseGenerator))]
    public class HouseGeneratorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            HouseGenerator houseGenerator = (HouseGenerator)target;

            if (GUILayout.Button("Find Houses"))
            {
                houseGenerator.FindHouses();
                SceneView.RepaintAll();
            }
            EditorGUILayout.LabelField("Houses", houseGenerator.houses.Count.ToString());
        }
    }
}
