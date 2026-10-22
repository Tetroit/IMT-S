using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    [CustomEditor(typeof(TerrainCoordinator))]
    public class TerrainCoordinatorEditor : UnityEditor.Editor
    {
        // Editor only input of Spawn Tile, not part of the coordinator.
        private SatContext.SatContext _satContext;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            TerrainCoordinator coordinator = (TerrainCoordinator)target;

            GUILayout.Space(10);
            _satContext = (SatContext.SatContext)EditorGUILayout.ObjectField("Tile Sat Context", _satContext, typeof(SatContext.SatContext), false);
            using (new EditorGUI.DisabledScope(_satContext == null))
            {
                if (GUILayout.Button("Spawn Tile"))
                {
                    TerrainGenerator generator = coordinator.SpawnTile(_satContext);
                    if (generator != null)
                        EditorGUIUtility.PingObject(generator);
                    SceneView.RepaintAll();
                }
            }
        }
    }
}
