using UnityEngine;
using UnityEngine.Serialization;

namespace ProceduralGeneration
{
    /// <summary>
    /// Starting point of the generation: spawns a <see cref="TerrainGenerator"/> per sat tile and runs its pipeline
    /// with <see cref="terrainConfig"/>.
    /// </summary>
    public class TerrainCoordinator : MonoBehaviour
    {
        [FormerlySerializedAs("terrainDataConfig")]
        [SerializeField] private TerrainConfig _terrainConfig;
        [Tooltip("Instantiated once per tile, needs the house, tree and road generators next to it to build them.")]
        [SerializeField] private TerrainGenerator _terrainGeneratorPrefab;
        [Tooltip("World seed, every tile gets its own seed derived from it and the tile coordinates.")]
        [SerializeField] private int _seed = 0;

        [Header("Gizmos")]
        [Tooltip("Draw the terrain config tiles in the scene view while this object is selected.")]
        public bool drawTileGizmos = true;

        public TerrainConfig terrainConfig => _terrainConfig;

        /// <summary>
        /// Instantiates <see cref="_terrainGeneratorPrefab"/> for the tile of <paramref name="satContext"/>, replacing a previous
        /// instance of the same tile, and runs <see cref="TerrainGenerator.RunPipeline"/> on it.
        /// </summary>
        /// <returns>The generator, null if the pipeline was terminated.</returns>
        public TerrainGenerator SpawnTile(SatContext.SatContext satContext)
        {
            if (satContext == null)
            {
                Logger.Logger.LogError($"Sat context is null", "ProcGen", this);
                return null;
            }
            if (_terrainConfig == null)
            {
                Logger.Logger.LogError($"Terrain config is null", "ProcGen", this);
                return null;
            }
            if (_terrainGeneratorPrefab == null)
            {
                Logger.Logger.LogError($"Terrain generator prefab is null", "ProcGen", this);
                return null;
            }

            foreach (var existing in GetComponentsInChildren<TerrainGenerator>())
            {
                if (existing.satContext == satContext)
                    DestroyObject(existing.gameObject);
            }

            // Chunks are placed in Unity space by the terrain config, the tile itself sits at the coordinator.
            TerrainGenerator generator = Instantiate(_terrainGeneratorPrefab, transform);
            generator.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            var tile = satContext.metadata?.export?.tile;
            generator.name = tile != null ? $"Tile {tile.z}-{tile.x}-{tile.y}" : $"Tile {satContext.name}";
            // Without tile coordinates the world seed is used as is.
            int seed = tile != null ? NoiseUtils.HashCoords(tile.x, tile.y, NoiseUtils.HashCoords(tile.z, 0, _seed)) : _seed;
            generator.BindSources(satContext, _terrainConfig, seed);

            if (!generator.RunPipeline())
            {
                Logger.Logger.LogError($"Pipeline of {generator.name} was terminated", "ProcGen", this);
                DestroyObject(generator.gameObject);
                return null;
            }
            return generator;
        }

        /// <summary>
        /// Destroys every tile spawned by <see cref="SpawnTile"/>.
        /// </summary>
        /// <returns>Number of destroyed tiles.</returns>
        public int DestroyTiles()
        {
            var generators = GetComponentsInChildren<TerrainGenerator>();
            foreach (var generator in generators)
                DestroyObject(generator.gameObject);
            return generators.Length;
        }

        private static void DestroyObject(GameObject target)
        {
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawTileGizmos || _terrainConfig == null)
                return;
            var lod = _terrainConfig.lodSettings;
            foreach (var tile in _terrainConfig.tiles)
            {
                DoubleVector2 min = _terrainConfig.MercatorToWorld(tile.min);
                DoubleVector2 max = _terrainConfig.MercatorToWorld(tile.max);
                Gizmos.color = TerrainConfig.GetZoomColor(tile.zoom, lod.minZoom, lod.maxZoom);
                Vector3 center = new Vector3((float)(min.x + max.x) / 2, 0, (float)(min.y + max.y) / 2);
                Vector3 size = new Vector3((float)(max.x - min.x), 0, (float)(max.y - min.y));
                Gizmos.DrawWireCube(center, size);
            }
        }
    }
}
