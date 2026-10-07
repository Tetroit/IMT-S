using UnityEngine;

namespace ProceduralGeneration
{
    /// <summary>
    /// Scene entry point for the procedural generation configs.
    /// Generators without their own <see cref="GeometryConfig"/> use the one assigned here.
    /// </summary>
    public class GlobalConfig : MonoBehaviour
    {
        public GeometryConfig geometryConfig;
        public TerrainDataConfig terrainDataConfig;

        [Header("Gizmos")]
        [Tooltip("Draw the terrain data tiles in the scene view while this object is selected.")]
        public bool drawTileGizmos = true;

        private static GlobalConfig _instance;

        public static GlobalConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindAnyObjectByType<GlobalConfig>();
                return _instance;
            }
        }

        public static GeometryConfig Geometry => Instance != null ? Instance.geometryConfig : null;

        /// <summary><paramref name="geometryConfig"/> if assigned, otherwise the scene's global one.</summary>
        public static GeometryConfig ResolveGeometry(GeometryConfig geometryConfig) =>
            geometryConfig != null ? geometryConfig : Geometry;

        private void OnEnable()
        {
            if (_instance == null)
                _instance = this;
        }

        private void OnDisable()
        {
            if (_instance == this)
                _instance = null;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawTileGizmos || geometryConfig == null || terrainDataConfig == null)
                return;
            var lod = terrainDataConfig.lodSettings;
            foreach (var tile in terrainDataConfig.tiles)
            {
                DoubleVector2 min = geometryConfig.MercatorToWorld(tile.min);
                DoubleVector2 max = geometryConfig.MercatorToWorld(tile.max);
                Gizmos.color = TerrainDataConfig.GetZoomColor(tile.zoom, lod.minZoom, lod.maxZoom);
                Vector3 center = new Vector3((float)(min.x + max.x) / 2, 0, (float)(min.y + max.y) / 2);
                Vector3 size = new Vector3((float)(max.x - min.x), 0, (float)(max.y - min.y));
                Gizmos.DrawWireCube(center, size);
            }
        }
    }
}
