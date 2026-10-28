using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(TreeGenerator))]
    public class TreeGeneratorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            TreeGenerator treeGenerator = (TreeGenerator)target;

            if (!treeGenerator.allowsTrees)
                EditorGUILayout.HelpBox($"Tile zoom {treeGenerator.tileZoom} is below Min Tile Zoom, the pipeline skips trees on this tile.", MessageType.Info);
            EditorGUILayout.LabelField("Trees", $"{treeGenerator.points.Count} points, {treeGenerator.treeCount} drawn");

            if (GUILayout.Button("Distribute Points"))
            {
                treeGenerator.DistributePoints();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Generate Trees"))
            {
                treeGenerator.GenerateTrees();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Destroy Trees"))
            {
                treeGenerator.DestroyTrees();
                SceneView.RepaintAll();
            }
        }
    }
}
