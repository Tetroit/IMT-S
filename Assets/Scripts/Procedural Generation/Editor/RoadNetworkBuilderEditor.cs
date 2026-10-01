using ProceduralGeneration.ImageProcessing;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(RoadNetworkBuilder))]
    public class RoadNetworkBuilderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            RoadNetworkBuilder roadBuilder =  target as RoadNetworkBuilder;
            if (GUILayout.Button("Generate"))
            {
                roadBuilder.Build();
            }
        }
    }
}