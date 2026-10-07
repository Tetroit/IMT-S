using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ProceduralGeneration
{
    public class TreeGenerator : MonoBehaviour
    {
        [SerializeField] private SatContext.SatContext _satContext;
        [Tooltip("Leave empty to use the GlobalConfig in the scene.")]
        [SerializeField] private GeometryConfig _geometryConfig;
        private GeometryConfig geometryConfig => GlobalConfig.ResolveGeometry(_geometryConfig);
        [SerializeField] private HouseGenerator _houseGenerator;

        [Header("Distribution")]
        [Tooltip("Segmentation id of forest pixels (\"tree\" class).")]
        [SerializeField] private int _forestSegID = 5;
        [Tooltip("Minimum distance between two points, in Unity units.")]
        [Min(0.01f)]
        [SerializeField] private float _radius = 5f;
        [Tooltip("Points closer than this to another segment are rejected, in Unity units. 0 disables.")]
        [Min(0f)]
        [SerializeField] private float _margin = 2f;
        [Tooltip("Candidates tried around each point before it is retired (Bridson's k).")]
        [Range(1, 64)]
        [SerializeField] private int _maxAttempts = 30;
        [SerializeField] private int _seed = 0;
        [SerializeField] private GameObject _treePrefab;
        private List<GameObject> _trees = new List<GameObject>();

        [Header("Gizmos")]
        [SerializeField] private Color _gizmoColor = new Color(0.1f, 0.8f, 0.2f);

        // Not serialized, like the sat images it is gone after a domain reload.
        private readonly List<Vector3> _points = new List<Vector3>();
        // Point i is drawn as the line [2i, 2i+1].
        private Vector3[] _gizmoLines;

        public IReadOnlyList<Vector3> points => _points;

        /// <summary>
        /// Poisson disk sampling (Bridson) over forest segment pixels, points are at least <see cref="_radius"/> apart.
        /// Forest regions are disconnected, so every grid cell without a point is tried as a new seed.
        /// </summary>
        public void DistributePoints()
        {
            if (_satContext == null)
            {
                Logger.Logger.LogError($"Sat context is null", "ProcGen", this);
                return;
            }
            if (geometryConfig == null)
            {
                Logger.Logger.LogError($"Geometry config is null, assign one or add a GlobalConfig to the scene", "ProcGen", this);
                return;
            }
            var segmentation = _satContext.segmentationImage;
            // Image data is not serialized, it is gone after a domain reload.
            if (segmentation == null || segmentation.data == null)
            {
                Logger.Logger.LogError($"Segmentation image is not loaded", "ProcGen", this);
                return;
            }
            var heightmap = _satContext.heightmapImage;
            bool hasHeightmap = heightmap != null && heightmap.data != null && heightmap.shape != null && heightmap.shape.Length >= 2;
            if (!hasHeightmap)
                Logger.Logger.LogWarning($"Heightmap is not loaded, points are placed at height 0", "ProcGen", this);
            // Houses are not serialized, they are gone after a domain reload.
            if (_houseGenerator != null && _houseGenerator.houses.Count == 0)
                Logger.Logger.LogWarning($"House generator has no houses, run Find Houses first", "ProcGen", this);

            int imageWidth = segmentation.width;
            int imageHeight = segmentation.height;

            // Same pixel -> world mapping as TerrainGenerator.CreateMeshForContext.
            double[] bounds = _satContext.GetWorldSpaceBounds(geometryConfig.origin);
            double pixelSizeX = (bounds[2] - bounds[0]) / imageWidth;
            double pixelSizeY = (bounds[3] - bounds[1]) / imageHeight;

            // Mercator pixels are square, sampling runs in pixel space.
            float radius = (float)(_radius / (pixelSizeX * geometryConfig.scale));
            float radiusSqr = radius * radius;

            // Background grid, a cell holds at most one point since its diagonal is the radius.
            float cellSize = radius / Mathf.Sqrt(2);
            int gridWidth = Mathf.CeilToInt(imageWidth / cellSize);
            int gridHeight = Mathf.CeilToInt(imageHeight / cellSize);
            long gridCells = (long)gridWidth * gridHeight;
            if (gridCells > int.MaxValue)
            {
                Logger.Logger.LogError($"Radius {_radius} is too small for a {imageWidth}x{imageHeight} image", "ProcGen", this);
                return;
            }
            // Index into samples, -1 = empty.
            int[] grid = new int[gridCells];
            Array.Fill(grid, -1);

            var samples = new List<Vector2>();
            var active = new List<int>();
            var random = new System.Random(_seed);

            bool IsForest(Vector2 p)
            {
                if (p.x < 0 || p.y < 0 || p.x >= imageWidth || p.y >= imageHeight)
                    return false;
                return segmentation.Get((int)p.x, (int)p.y) == _forestSegID;
            }

            // Samples are in pixel space, houses in Unity space, same mapping as the final points below.
            bool IsInBuilding(Vector2 p)
            {
                if (_houseGenerator == null)
                    return false;
                var world = new Vector2(
                    (float)((bounds[0] + p.x * pixelSizeX) * geometryConfig.scale),
                    (float)((bounds[3] - p.y * pixelSizeY) * geometryConfig.scale));
                return _houseGenerator.OverlapsHouses(world);
            }

            // Margin check probes 2 rings (margin and margin / 2) of 8 directions instead of a full disk,
            // thin gaps between probes can be missed.
            float margin = (float)(_margin / (pixelSizeX * geometryConfig.scale));
            var marginOffsets = new List<Vector2>();
            if (margin > 0)
            {
                for (int ring = 1; ring <= 2; ring++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        // Rings are rotated by half a step so their probes don't line up.
                        float angle = (i + (ring - 1) * 0.5f) * Mathf.PI / 4;
                        marginOffsets.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (margin * ring / 2));
                    }
                }
            }

            bool IsAwayFromBorder(Vector2 p)
            {
                foreach (var offset in marginOffsets)
                {
                    // Clamped, the image edge is not a segment border.
                    int x = Math.Clamp((int)(p.x + offset.x), 0, imageWidth - 1);
                    int y = Math.Clamp((int)(p.y + offset.y), 0, imageHeight - 1);
                    if (segmentation.Get(x, y) != _forestSegID)
                        return false;
                }
                return true;
            }

            bool IsValid(Vector2 p) => IsForest(p) && IsFarEnough(p) && IsAwayFromBorder(p) && !IsInBuilding(p);

            bool IsFarEnough(Vector2 p)
            {
                int cellX = (int)(p.x / cellSize);
                int cellY = (int)(p.y / cellSize);
                // Points within the radius are at most 2 cells away.
                int minX = Math.Max(cellX - 2, 0);
                int maxX = Math.Min(cellX + 2, gridWidth - 1);
                int minY = Math.Max(cellY - 2, 0);
                int maxY = Math.Min(cellY + 2, gridHeight - 1);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int sample = grid[y * gridWidth + x];
                        if (sample >= 0 && (samples[sample] - p).sqrMagnitude < radiusSqr)
                            return false;
                    }
                }
                return true;
            }

            void AddSample(Vector2 p)
            {
                int index = samples.Count;
                samples.Add(p);
                active.Add(index);
                grid[(int)(p.y / cellSize) * gridWidth + (int)(p.x / cellSize)] = index;
            }

            // Grows points from the active list until the region around them is filled.
            void Grow()
            {
                while (active.Count > 0)
                {
                    int activeIndex = random.Next(active.Count);
                    Vector2 center = samples[active[activeIndex]];
                    bool found = false;
                    for (int attempt = 0; attempt < _maxAttempts; attempt++)
                    {
                        // Uniform in the annulus [r, 2r].
                        float angle = (float)(random.NextDouble() * 2 * Math.PI);
                        float distance = radius * Mathf.Sqrt(1 + 3 * (float)random.NextDouble());
                        Vector2 candidate = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                        if (IsValid(candidate))
                        {
                            AddSample(candidate);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        // Swap remove, order of the active list does not matter.
                        active[activeIndex] = active[active.Count - 1];
                        active.RemoveAt(active.Count - 1);
                    }
                }
            }

            // Seed every empty cell, this reaches forest patches not connected to the previous ones.
            for (int cellY = 0; cellY < gridHeight; cellY++)
            {
                for (int cellX = 0; cellX < gridWidth; cellX++)
                {
                    if (grid[cellY * gridWidth + cellX] >= 0)
                        continue;
                    for (int attempt = 0; attempt < _maxAttempts; attempt++)
                    {
                        var candidate = new Vector2(
                            (cellX + (float)random.NextDouble()) * cellSize,
                            (cellY + (float)random.NextDouble()) * cellSize);
                        if (IsValid(candidate))
                        {
                            AddSample(candidate);
                            Grow();
                            break;
                        }
                    }
                }
            }
            
            float heightScale = geometryConfig.GetMeterScale();

            _points.Clear();
            foreach (var sample in samples)
            {
                float height = hasHeightmap ? heightScale * SampleHeight(heightmap, sample.x / imageWidth, sample.y / imageHeight) : 0;
                // Image y goes down so unity z goes negative.
                _points.Add(new Vector3(
                    (float)((bounds[0] + sample.x * pixelSizeX) * geometryConfig.scale),
                    height,
                    (float)((bounds[3] - sample.y * pixelSizeY) * geometryConfig.scale)));
            }
            
            _gizmoLines = null;

            Logger.Logger.Log($"Distributed {_points.Count.ToString()} tree points with radius {_radius}, margin {_margin}", "ProcGen");
        }

        public void GenerateTrees()
        {
            foreach (var point in _points)
            {
                var created = Instantiate(_treePrefab, point, Quaternion.identity);
                created.transform.SetParent(transform);
                float size = Random.value * 5 + 5;
                created.transform.localScale = new Vector3(size,size,size);
                _trees.Add(created);
            }
        }
        public void DestroyTrees()
        {
            foreach (var tree in _trees)
            {
#if UNITY_EDITOR
                DestroyImmediate(tree);
#else
                Destroy(tree);
#endif
            }
        }
        // Bilinear sample, u and v are in [0, 1] of the image (v goes down).
        internal static float SampleHeight(ImageProcessing.NpyArray heightmap, float u, float v)
        {
            // Heightmap is (rows, cols), pixel centers at .5.
            int height = heightmap.shape[0];
            int width = heightmap.shape[1];
            float x = Mathf.Clamp(u * width - 0.5f, 0, width - 1);
            float y = Mathf.Clamp(v * height - 0.5f, 0, height - 1);
            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);

            float Height(int hx, int hy)
            {
                float h = heightmap.data[hy * width + hx];
                return float.IsNaN(h) || float.IsInfinity(h) ? 0 : h;
            }

            float top = Mathf.Lerp(Height(x0, y0), Height(x1, y0), x - x0);
            float bottom = Mathf.Lerp(Height(x0, y1), Height(x1, y1), x - x0);
            return Mathf.Lerp(top, bottom, y - y0);
        }

        private void OnDrawGizmosSelected()
        {
            if (_points.Count == 0)
                return;

            // One vertical line per point, a line list stays fast for large point counts.
            if (_gizmoLines == null || _gizmoLines.Length != _points.Count * 2)
            {
                _gizmoLines = new Vector3[_points.Count * 2];
                for (int i = 0; i < _points.Count; i++)
                {
                    _gizmoLines[2 * i] = _points[i];
                    _gizmoLines[2 * i + 1] = _points[i] + Vector3.up * _radius;
                }
            }
            Gizmos.color = _gizmoColor;
            Gizmos.DrawLineList(_gizmoLines);
        }
    }
}
