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
            if (GUILayout.Button("Generate Buildings"))
            {
                houseGenerator.GenerateBuildings();
            }
            if (GUILayout.Button("Destroy Buildings"))
            {
                houseGenerator.DestroyBuildings();
            }
            EditorGUILayout.LabelField("Houses", houseGenerator.houses.Count.ToString());
            EditorGUILayout.LabelField("Buildings", houseGenerator.buildings.Count.ToString());
        }
    }
}
