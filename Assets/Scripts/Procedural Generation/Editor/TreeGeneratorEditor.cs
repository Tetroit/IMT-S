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

            if (GUILayout.Button("Distribute Points"))
            {
                treeGenerator.DistributePoints();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Generate Trees"))
            {
                treeGenerator.GenerateTrees();
            }
            if (GUILayout.Button("Destroy Trees"))
            {
                treeGenerator.DestroyTrees();
            }
        }
    }
}
