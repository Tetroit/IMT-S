using System;
using System.Collections.Generic;
using System.IO;
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
            TerrainConfig config = coordinator.terrainConfig;

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

            GUILayout.Space(10);
            EditorGUILayout.LabelField("All Tiles", EditorStyles.boldLabel);
            bool hasFolder = config != null && !string.IsNullOrWhiteSpace(config.downloadDstFolder);
            if (config != null && !hasFolder)
                EditorGUILayout.HelpBox("The terrain config has no download folder, the tiles are looked up in it.", MessageType.Info);

            using (new EditorGUI.DisabledScope(!hasFolder || config.tiles.Count == 0))
            {
                if (GUILayout.Button(hasFolder ? $"Generate All ({config.tiles.Count} tiles)" : "Generate All"))
                {
                    GenerateAll(coordinator);
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(!hasFolder))
            {
                if (GUILayout.Button(new GUIContent("Clear Area Cache", "Deletes the houses, trees and roads caches of the tiles in the terrain config.")))
                    ClearAreaCache(config);
                if (GUILayout.Button(new GUIContent("Clear Folder Cache", "Deletes the houses, trees and roads caches of every tile in the download folder.")))
                    ClearFolderCache(config);
            }
        }

        /// <summary>
        /// Replaces all spawned tiles with one per terrain config tile, from the SatContext in its download folder.
        /// Tiles without one are skipped. The images of every tile are unloaded once it is built, so memory does not
        /// grow with the tile count.
        /// </summary>
        private static void GenerateAll(TerrainCoordinator coordinator)
        {
            TerrainConfig config = coordinator.terrainConfig;
            string root = Path.GetFullPath(config.downloadDstFolder);
            int destroyed = coordinator.DestroyTiles();
            if (destroyed > 0)
                Logger.Logger.Log($"Destroyed {destroyed.ToString()} previous tiles", "ProcGen", coordinator);

            int generated = 0, skipped = 0, failed = 0;
            bool cancelled = false;
            try
            {
                for (int i = 0; i < config.tiles.Count; i++)
                {
                    TileBounds tile = config.tiles[i];
                    string name = TileDownloadBatch.FolderName(tile);
                    if (EditorUtility.DisplayCancelableProgressBar("Generate All Tiles", $"{name} ({i + 1}/{config.tiles.Count})", i / (float)config.tiles.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    var satContext = LoadSatContext(Path.Combine(root, name));
                    if (satContext == null)
                    {
                        Logger.Logger.LogWarning($"Tile {name} has no {TileDownloadBatch.SatContextFile} in {root}, skipping", "ProcGen", coordinator);
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (coordinator.SpawnTile(satContext) != null)
                            generated++;
                        else
                            failed++;
                    }
                    catch (Exception e)
                    {
                        // One broken tile never stops the others.
                        Logger.Logger.LogError($"Tile {name} failed: {e}", "ProcGen", coordinator);
                        failed++;
                    }
                    finally
                    {
                        satContext.UnloadData();
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string summary = $"Generated {generated.ToString()}/{config.tiles.Count.ToString()} tiles, {skipped.ToString()} skipped (not downloaded), {failed.ToString()} failed";
            if (cancelled)
                summary += ", cancelled";
            if (skipped > 0 || failed > 0 || cancelled)
                Logger.Logger.LogWarning(summary, "ProcGen", coordinator);
            else
                Logger.Logger.Log(summary, "ProcGen", coordinator);
        }

        /// <summary>The SatContext in <paramref name="folder"/>, null if there is none or the folder is outside Assets.</summary>
        private static SatContext.SatContext LoadSatContext(string folder)
        {
            string assetFolder = TileDownloadBatch.ToAssetPath(folder);
            return assetFolder != null
                ? AssetDatabase.LoadAssetAtPath<SatContext.SatContext>($"{assetFolder}/{TileDownloadBatch.SatContextFile}")
                : null;
        }

        /// <summary>Deletes the pipeline caches of the terrain config tiles, the downloaded files stay.</summary>
        private static void ClearAreaCache(TerrainConfig config)
        {
            string root = Path.GetFullPath(config.downloadDstFolder);
            int deleted = 0, tiles = 0;
            foreach (var tile in config.tiles)
            {
                string folder = Path.Combine(root, TileDownloadBatch.FolderName(tile));
                if (!Directory.Exists(folder))
                    continue;
                deleted += SatContext.SatContext.ClearCache(folder);
                tiles++;
            }
            AssetDatabase.Refresh();
            Logger.Logger.Log($"Cleared {deleted.ToString()} pipeline caches in {tiles.ToString()} area tiles of {root}", "ProcGen", config);
        }

        /// <summary>Deletes the pipeline caches of every tile folder under the download folder, the downloaded files stay.</summary>
        private static void ClearFolderCache(TerrainConfig config)
        {
            string root = Path.GetFullPath(config.downloadDstFolder);
            if (!Directory.Exists(root))
            {
                Logger.Logger.LogWarning($"Download folder {root} does not exist", "ProcGen", config);
                return;
            }
            int deleted = 0, tiles = 0;
            // A single tile download writes into the download folder itself.
            var folders = new List<string> { root };
            folders.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
            foreach (string folder in folders)
            {
                if (!SatContext.SatContext.IsTileFolder(folder))
                    continue;
                deleted += SatContext.SatContext.ClearCache(folder);
                tiles++;
            }
            AssetDatabase.Refresh();
            Logger.Logger.Log($"Cleared {deleted.ToString()} pipeline caches in {tiles.ToString()} tiles of {root}", "ProcGen", config);
        }
    }
}
