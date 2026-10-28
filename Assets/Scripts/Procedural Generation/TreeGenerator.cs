using System;
using System.Collections.Generic;
using IMT.Thermal;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace ProceduralGeneration
{
    /// <summary>
    /// Distributes tree points over the forest segment and draws them with GPU instancing, no GameObject per tree.
    /// Trees are grouped in square cells, a cell is skipped per camera when it is out of view or further than
    /// <see cref="_maxDrawDistance"/>.
    /// </summary>
    [ExecuteAlways]
    public class TreeGenerator : MonoBehaviour
    {
        [SerializeField] private SatContext.SatContext _satContext;
        [SerializeField] private TerrainConfig _terrainConfig;
        [SerializeField] private HouseGenerator _houseGenerator;

        [Header("Distribution")]
        [Tooltip("Segmentation id of forest pixels (\"tree\" class).")]
        [SerializeField] private int _forestSegID = 5;
        [Tooltip("Minimum distance between two points, in metres.")]
        [Min(0.01f)]
        [SerializeField] private float _radius = 5f;
        [Tooltip("Points closer than this to another segment are rejected, in metres. 0 disables.")]
        [Min(0f)]
        [SerializeField] private float _margin = 2f;
        [Tooltip("Candidates tried around each point before it is retired (Bridson's k).")]
        [Range(1, 64)]
        [SerializeField] private int _maxAttempts = 30;
        [Tooltip("Seed of the point distribution and the tree sizes, same seed and inputs give the same trees.")]
        [SerializeField] private int _seed = 0;
        [Header("Trees")]
        [Tooltip("Tree meshes, every tree picks one at random. Every submesh is drawn with the tree material.")]
        [SerializeField] private List<Mesh> _treeMeshes = new List<Mesh>();
        [Tooltip("Material of every tree mesh, GPU instancing must be enabled on it.")]
        [SerializeField] private Material _treeMaterial;
        [Tooltip("Thermal properties of the trees, passed to every draw like a ThermalObject. Empty = the thermal shader's defaults.")]
        [SerializeField] private ThermalMaterial _thermalMaterial;
        // Prefab before the mesh pool, its mesh, material and thermal material move to the fields above.
        [SerializeField, HideInInspector] private GameObject _treePrefab;
        [Tooltip("Random uniform scale range of a tree, in metres per mesh unit.")]
        [SerializeField] private Vector2 _sizeRange = new Vector2(5f, 10f);

        [Header("Level Of Detail")]
        [Tooltip("Tiles with a lower zoom (larger tiles) get no trees, not even points. A zoom 13 tile is about 4.9 km " +
                 "wide at the equator, every zoom below doubles that. Tiles without a zoom in their metadata always get trees.")]
        [SerializeField] private int _minTileZoom = 13;
        [Tooltip("Trees further from the camera than this are not drawn, in metres.")]
        [Min(0f)]
        [SerializeField] private float _maxDrawDistance = 2000f;
        [Tooltip("Trees are culled per square cell of this size, in metres.")]
        [Min(1f)]
        [SerializeField] private float _cellSize = 250f;

        [Header("Gizmos")]
        [SerializeField] private Color _gizmoColor = new Color(0.1f, 0.8f, 0.2f);

        // Nothing drawn is serialized, the trees are restored from the trees cache after a domain reload or scene load.
        [SerializeField, HideInInspector] private bool _treesGenerated;

        // Not serialized, like the sat images it is gone after a domain reload.
        private readonly List<Vector3> _points = new List<Vector3>();
        // Point i is drawn as the line [2i, 2i+1].
        private Vector3[] _gizmoLines;

        // Instances per draw call, the instancing limit of most platforms.
        private const int MaxInstancesPerDraw = 1023;

        private class TreeBatch
        {
            public Mesh mesh;
            // Generator space, world is recomputed from it when the transform moves.
            public Matrix4x4[] local;
            public Matrix4x4[] world;
        }

        private class TreeCell
        {
            public Bounds localBounds;
            public Bounds worldBounds;
            public readonly List<TreeBatch> batches = new List<TreeBatch>();
        }

        private readonly List<TreeCell> _cells = new List<TreeCell>();
        private Matrix4x4 _cellsLocalToWorld;
        // Thermal properties of every draw, null without a thermal material.
        private MaterialPropertyBlock _properties;
        private int _treeCount;
        private static readonly Plane[] FrustumPlanes = new Plane[6];

        public IReadOnlyList<Vector3> points => _points;
        public int treeCount => _treeCount;

        /// <summary>Zoom of the sat context tile, -1 if its metadata has none.</summary>
        public int tileZoom
        {
            get
            {
                var tile = _satContext != null ? _satContext.metadata?.export?.tile : null;
                // Unity serialization leaves an empty tile instead of null, no real tile is zoom 0.
                return tile != null && tile.z > 0 ? tile.z : -1;
            }
        }

        /// <summary>False for tiles below <see cref="_minTileZoom"/>, they get no trees.</summary>
        public bool allowsTrees => tileZoom < 0 || tileZoom >= _minTileZoom;
        public int minTileZoom => _minTileZoom;

        private float meterScale => _terrainConfig != null ? _terrainConfig.GetMeterScale() : 1f;

        private void OnEnable()
        {
            MigrateTreePrefab();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            // Prefab assets are never rendered.
            if (_treesGenerated && _cells.Count == 0 && gameObject.scene.IsValid())
                RestoreFromCache();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        }

        private void OnValidate()
        {
            // Also runs on prefab assets, OnEnable does not.
            MigrateTreePrefab();
        }

        /// <summary>Moves the mesh, material and thermal material of the old <see cref="_treePrefab"/> to the mesh pool fields.</summary>
        private void MigrateTreePrefab()
        {
            if (_treePrefab == null)
                return;
            var filter = _treePrefab.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && filter.sharedMesh != null && !_treeMeshes.Contains(filter.sharedMesh))
            {
                _treeMeshes.Add(filter.sharedMesh);
                if (filter.transform != _treePrefab.transform && !IsIdentity(_treePrefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix))
                    Logger.Logger.LogWarning($"Mesh {filter.sharedMesh.name} is moved, rotated or scaled inside tree prefab {_treePrefab.name}, the mesh pool draws it without that", "ProcGen", this);
            }
            var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
            if (_treeMaterial == null && renderer != null)
                _treeMaterial = renderer.sharedMaterial;
            var thermal = filter != null ? filter.GetComponent<ThermalObject>() : null;
            if (_thermalMaterial == null && thermal != null)
                _thermalMaterial = thermal.material;
            _treePrefab = null;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        private static bool IsIdentity(Matrix4x4 matrix)
        {
            const float tolerance = 1e-4f;
            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(matrix[i] - Matrix4x4.identity[i]) > tolerance)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Rebuilds the drawn trees from the trees cache, nothing is drawn after a domain reload or scene load otherwise.
        /// </summary>
        private void RestoreFromCache()
        {
            if (_satContext == null || _terrainConfig == null || !allowsTrees)
                return;
            if (!PipelineCache.TryLoad(_satContext.treesCachePath, _terrainConfig, out TreesCache cache))
            {
                Logger.Logger.LogWarning($"Trees of {name} could not be restored from {_satContext.treesCachePath}, run the pipeline again", "ProcGen", this);
                return;
            }
            SetPoints(cache.points);
            GenerateTrees();
        }

        /// <summary>
        /// Points the generator at the given sources, used by <see cref="TerrainGenerator.RunPipeline"/>.
        /// A null <paramref name="houseGenerator"/> keeps the current one.
        /// </summary>
        /// <returns>True if anything changed.</returns>
        public bool BindSources(SatContext.SatContext satContext, TerrainConfig terrainConfig, HouseGenerator houseGenerator, int seed)
        {
            if (houseGenerator == null)
                houseGenerator = _houseGenerator;
            bool changed = _satContext != satContext || _terrainConfig != terrainConfig || _houseGenerator != houseGenerator || _seed != seed;
            _satContext = satContext;
            _terrainConfig = terrainConfig;
            _houseGenerator = houseGenerator;
            _seed = seed;
            return changed;
        }

        /// <summary>
        /// Replaces the points, e.g. with ones loaded from a cache instead of <see cref="DistributePoints"/>.
        /// </summary>
        public void SetPoints(IEnumerable<Vector3> points)
        {
            _points.Clear();
            _points.AddRange(points);
            _gizmoLines = null;
        }

        /// <summary>
        /// Poisson disk sampling (Bridson) over forest segment pixels, points are at least <see cref="_radius"/> apart.
        /// Forest regions are disconnected, so every grid cell without a point is tried as a new seed.
        /// </summary>
        /// <returns>False if the inputs are missing.</returns>
        public bool DistributePoints()
        {
            if (_satContext == null)
            {
                Logger.Logger.LogError($"Sat context is null", "ProcGen", this);
                return false;
            }
            if (_terrainConfig == null)
            {
                Logger.Logger.LogError($"Terrain config is null", "ProcGen", this);
                return false;
            }
            var segmentation = _satContext.segmentationImage;
            // Image data is not serialized, it is gone after a domain reload.
            if (segmentation == null || segmentation.data == null)
            {
                Logger.Logger.LogError($"Segmentation image is not loaded", "ProcGen", this);
                return false;
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
            double[] bounds = _satContext.GetWorldSpaceBounds(_terrainConfig.origin);
            double pixelSizeX = (bounds[2] - bounds[0]) / imageWidth;
            double pixelSizeY = (bounds[3] - bounds[1]) / imageHeight;

            // Mercator pixels are square, sampling runs in pixel space.
            // Distances are in metres, the meter scale brings them to Unity units and the pixel size to pixels.
            float meterScale = _terrainConfig.GetMeterScale();
            double unityPerPixel = pixelSizeX * _terrainConfig.scale;
            float radius = (float)(_radius * meterScale / unityPerPixel);
            float radiusSqr = radius * radius;

            // Background grid, a cell holds at most one point since its diagonal is the radius.
            float cellSize = radius / Mathf.Sqrt(2);
            int gridWidth = Mathf.CeilToInt(imageWidth / cellSize);
            int gridHeight = Mathf.CeilToInt(imageHeight / cellSize);
            long gridCells = (long)gridWidth * gridHeight;
            if (gridCells > int.MaxValue)
            {
                Logger.Logger.LogError($"Radius {_radius} is too small for a {imageWidth}x{imageHeight} image", "ProcGen", this);
                return false;
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
                    (float)((bounds[0] + p.x * pixelSizeX) * _terrainConfig.scale),
                    (float)((bounds[3] - p.y * pixelSizeY) * _terrainConfig.scale));
                return _houseGenerator.OverlapsHouses(world);
            }

            // Margin check probes 2 rings (margin and margin / 2) of 8 directions instead of a full disk,
            // thin gaps between probes can be missed.
            float margin = (float)(_margin * meterScale / unityPerPixel);
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

            _points.Clear();
            foreach (var sample in samples)
            {
                float height = hasHeightmap ? meterScale * SampleHeight(heightmap, sample.x / imageWidth, sample.y / imageHeight) : 0;
                // Image y goes down so unity z goes negative.
                _points.Add(new Vector3(
                    (float)((bounds[0] + sample.x * pixelSizeX) * _terrainConfig.scale),
                    height,
                    (float)((bounds[3] - sample.y * pixelSizeY) * _terrainConfig.scale)));
            }
            
            _gizmoLines = null;

            Logger.Logger.Log($"Distributed {_points.Count.ToString()} tree points with radius {_radius} m, margin {_margin} m", "ProcGen");
            return true;
        }

        /// <summary>
        /// Builds the instanced trees on <see cref="points"/>, replacing the previous ones. Every tree gets a random
        /// mesh of <see cref="_treeMeshes"/> and size of <see cref="_sizeRange"/>.
        /// </summary>
        public void GenerateTrees()
        {
            if (_terrainConfig == null)
            {
                Logger.Logger.LogError($"Terrain config is null", "ProcGen", this);
                return;
            }
            MigrateTreePrefab();
            if (_treeMaterial == null)
            {
                Logger.Logger.LogError($"Tree material is null", "ProcGen", this);
                return;
            }
            // Instanced draws fail on it.
            if (!_treeMaterial.enableInstancing)
            {
                Logger.Logger.LogError($"Tree material {_treeMaterial.name} has GPU instancing disabled, enable it to draw the trees", "ProcGen", _treeMaterial);
                return;
            }
            // The random pick chooses an index into these.
            var meshes = _treeMeshes.FindAll(m => m != null);
            if (meshes.Count == 0)
            {
                Logger.Logger.LogError($"No tree meshes", "ProcGen", this);
                return;
            }
            // Distance from the mesh origin to its furthest bounds corner, unscaled.
            var radii = meshes.ConvertAll(m => m.bounds.center.magnitude + m.bounds.extents.magnitude);

            DestroyTrees();

            // Same as the buildings, the size is in metres and the meter scale brings it to Unity units.
            float meterScale = this.meterScale;
            float cellSize = _cellSize * meterScale;
            // Own generator, UnityEngine.Random is reseeded by every building drawer rebuild.
            // Not shared with DistributePoints, the points may come from a cache instead.
            var random = new System.Random(_seed);

            // Cell -> mesh index -> matrices, in generator space.
            var cells = new Dictionary<Vector2Int, (Bounds bounds, Dictionary<int, List<Matrix4x4>> matrices)>();
            foreach (var point in _points)
            {
                float size = Mathf.Lerp(_sizeRange.x, _sizeRange.y, (float)random.NextDouble()) * meterScale;
                int mesh = random.Next(meshes.Count);

                var key = new Vector2Int(Mathf.FloorToInt(point.x / cellSize), Mathf.FloorToInt(point.z / cellSize));
                // Sphere around the origin, covers the mesh in any rotation.
                var treeBounds = new Bounds(point, Vector3.one * (2 * radii[mesh] * size));
                if (!cells.TryGetValue(key, out var cell))
                    cell = (treeBounds, new Dictionary<int, List<Matrix4x4>>());
                else
                    cell.bounds.Encapsulate(treeBounds);

                if (!cell.matrices.TryGetValue(mesh, out var matrices))
                {
                    matrices = new List<Matrix4x4>();
                    cell.matrices[mesh] = matrices;
                }
                matrices.Add(Matrix4x4.TRS(point, Quaternion.identity, Vector3.one * size));
                cells[key] = cell;
            }

            foreach (var cell in cells.Values)
            {
                var treeCell = new TreeCell { localBounds = cell.bounds };
                foreach (var pair in cell.matrices)
                {
                    var local = pair.Value.ToArray();
                    treeCell.batches.Add(new TreeBatch { mesh = meshes[pair.Key], local = local, world = new Matrix4x4[local.Length] });
                }
                _cells.Add(treeCell);
            }
            UpdateWorldMatrices();
            _treeCount = _points.Count;
            _treesGenerated = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif
            Logger.Logger.Log($"Generated {_treeCount.ToString()} trees from {meshes.Count.ToString()} meshes in {_cells.Count.ToString()} cells", "ProcGen", this);
        }

        public void DestroyTrees()
        {
            bool hadTrees = _treesGenerated || _cells.Count > 0;
            _cells.Clear();
            _treeCount = 0;
            _treesGenerated = false;
#if UNITY_EDITOR
            if (hadTrees && !Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif
        }

        private void UpdateWorldMatrices()
        {
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            foreach (var cell in _cells)
            {
                cell.worldBounds = TransformBounds(localToWorld, cell.localBounds);
                foreach (var batch in cell.batches)
                {
                    for (int i = 0; i < batch.local.Length; i++)
                        batch.world[i] = localToWorld * batch.local[i];
                }
            }
            _cellsLocalToWorld = localToWorld;
        }

        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            // Extents of the rotated box along the world axes.
            Vector3 extents = bounds.extents;
            Vector3 x = matrix.MultiplyVector(new Vector3(extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, extents.z));
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(center, worldExtents * 2);
        }

        /// <summary>
        /// Draws the cells in view of <paramref name="camera"/> and within <see cref="_maxDrawDistance"/>.
        /// </summary>
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // The material may have been removed or lost its instancing since the trees were generated.
            if (_cells.Count == 0 || camera.cameraType == CameraType.Preview || _treeMaterial == null || !_treeMaterial.enableInstancing)
                return;
            if (transform.localToWorldMatrix != _cellsLocalToWorld)
                UpdateWorldMatrices();

            GeometryUtility.CalculateFrustumPlanes(camera, FrustumPlanes);
            Vector3 cameraPosition = camera.transform.position;
            float maxDistance = _maxDrawDistance * meterScale;
            float maxDistanceSqr = maxDistance * maxDistance;
            var renderParams = new RenderParams
            {
                camera = camera,
                layer = gameObject.layer,
                // Same as a MeshRenderer, 0 would hide the trees from passes that filter by layer.
                renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,
                material = _treeMaterial,
                matProps = ThermalProperties(),
            };

            foreach (var cell in _cells)
            {
                if (cell.worldBounds.SqrDistance(cameraPosition) > maxDistanceSqr)
                    continue;
                if (!GeometryUtility.TestPlanesAABB(FrustumPlanes, cell.worldBounds))
                    continue;
                renderParams.worldBounds = cell.worldBounds;
                foreach (var batch in cell.batches)
                {
                    for (int submesh = 0; submesh < batch.mesh.subMeshCount; submesh++)
                    {
                        for (int start = 0; start < batch.world.Length; start += MaxInstancesPerDraw)
                        {
                            Graphics.RenderMeshInstanced(renderParams, batch.mesh, submesh, batch.world,
                                Mathf.Min(MaxInstancesPerDraw, batch.world.Length - start), start);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The thermal pass reads the per draw property block. Refilled every frame so edits to the thermal material
        /// show up without regenerating, null without one.
        /// </summary>
        private MaterialPropertyBlock ThermalProperties()
        {
            if (_thermalMaterial == null)
                return null;
            if (_properties == null)
                _properties = new MaterialPropertyBlock();
            _thermalMaterial.ApplyTo(_properties);
            return _properties;
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
                float lineLength = _terrainConfig != null ? _radius * _terrainConfig.GetMeterScale() : _radius;
                _gizmoLines = new Vector3[_points.Count * 2];
                for (int i = 0; i < _points.Count; i++)
                {
                    _gizmoLines[2 * i] = _points[i];
                    _gizmoLines[2 * i + 1] = _points[i] + Vector3.up * lineLength;
                }
            }
            Gizmos.color = _gizmoColor;
            Gizmos.DrawLineList(_gizmoLines);
        }
    }
}
