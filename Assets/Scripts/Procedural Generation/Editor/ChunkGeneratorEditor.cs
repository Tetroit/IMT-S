using UnityEditor;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(ChunkGenerator))]
    public class ChunkGeneratorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
        }
    }
}