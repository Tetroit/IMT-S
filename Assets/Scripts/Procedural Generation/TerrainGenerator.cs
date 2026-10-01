using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProceduralGeneration
{
    public class TerrainGenerator : MonoBehaviour
    {
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        [SerializeField] private ChunkGenerator _chunkGenerator;
        [SerializeField] private Transform _camera;
        [SerializeField] private GameObject _tilePrefab;
        [SerializeField] private SatContext.SatContext _satContext;
        [SerializeField] private GeometryContext _geometryContext;

        [Header("Mesh config")]
        [Range(1,16)]
        [SerializeField] private int _tileSubdivision = 4;
        [Min(0.01f)]    
        [SerializeField] private float _uvScale = 1;
        [Tooltip("Largest quad size in pixels, power of 2. Quads are split down to 1 pixel where segments differ.")]
        [Range(1, ChunkSize)]
        [SerializeField] private int _pixelStep = 4;

        [Header("Materials")]
        [SerializeField] private Material _defaultMaterial;
        [Tooltip("Fills materialBySegID, segments without an entry use the default material.")]
        [SerializeField] private Dictionary<int, Material> _segmentMaterials = new Dictionary<int, Material>();
        [SerializeField] private int _agricultureSegID;
        [SerializeField] private Dictionary<Color32, Material> _agricultureMaterials = new Dictionary<Color32, Material>();

        public struct SubmeshRef
        {
            public MeshRenderer renderer;
            public int submeshIndex;
            // Matched _agricultureMaterials key, only set for agriculture submeshes.
            public Color32? agricultureColor;

            public SubmeshRef(MeshRenderer renderer, int submeshIndex, Color32? agricultureColor = null)
            {
                this.renderer = renderer;
                this.submeshIndex = submeshIndex;
                this.agricultureColor = agricultureColor;
            }
        }

        /// <summary>
        /// Segmentation id -> every submesh generated for it by <see cref="CreateMeshForContext"/>.
        /// </summary>
        public readonly Dictionary<int, List<SubmeshRef>> submeshBySegID = new Dictionary<int, List<SubmeshRef>>();

        public SatContext.SatMetadata.ClassInfo[] segClasses => _satContext?.metadata?.classes;

        // Chunk size in pixels, also the largest allowed pixel step.
        private const int ChunkSize = 256;
        // Quadtree markers, a block with differing segments / a block fully outside the image.
        private const int Mixed = int.MinValue;
        private const int Outside = int.MinValue + 1;

        void Awake()
        {
            BindReferences();
        }
        void OnValidate()
        {
            _pixelStep = Mathf.ClosestPowerOfTwo(Mathf.Clamp(_pixelStep, 1, ChunkSize));
        }
        public void BindReferences()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
        }
        public Material GetMaterial(int id)
        {
            if (!_segmentMaterials.TryGetValue(id, out var material)) return _defaultMaterial;
            return material;
        }
        public void CreateMeshForContext()
        {
            if (_satContext == null)
            {
                Logger.Logger.LogError($"Sat context is null", "ProcGen", this);
                return;
            }
            if (_geometryContext == null)
            {
                Logger.Logger.LogError($"Geometry context is null", "ProcGen", this);
                return;
            }
            if (_tilePrefab == null)
            {
                Logger.Logger.LogError($"Tile prefab is null", "ProcGen", this);
                return;
            }
            if (_defaultMaterial == null)
                Logger.Logger.LogWarning($"Default material is null, unmapped segments will render without material", "ProcGen", this);

            var segmentation = _satContext.segmentationImage;
            var heightmap = _satContext.heightmapImage;
            // Image data is not serialized, it is gone after a domain reload.
            if (segmentation == null || segmentation.data == null)
            {
                Logger.Logger.LogError($"Segmentation image is not loaded", "ProcGen", this);
                return;
            }
            if (heightmap == null || heightmap.data == null || heightmap.shape == null || heightmap.shape.Length < 2)
            {
                Logger.Logger.LogError($"Heightmap is not loaded", "ProcGen", this);
                return;
            }

            int imageWidth = segmentation.width;
            int imageHeight = segmentation.height;

            // Heightmap is (rows, cols), it may have a different resolution than the segmentation.
            int heightmapHeight = heightmap.shape[0];
            int heightmapWidth = heightmap.shape[1];
            double heightmapScaleX = (double)heightmapWidth / imageWidth;
            double heightmapScaleY = (double)heightmapHeight / imageHeight;

            // Mercator bounds [minX, minY, maxX, maxY] relative to the geometry origin.
            double[] bounds = _satContext.GetWorldSpaceBounds(_geometryContext.origin);
            double pixelSizeX = (bounds[2] - bounds[0]) / imageWidth;
            double pixelSizeY = (bounds[3] - bounds[1]) / imageHeight;

            // Mercator stretches distances by 1/cos(lat), heights (real metres) need the same stretch to keep proportions.
            const double earthRadius = 6378137.0;
            double centerMercatorY = (_satContext.metadata.output.mercatorBoundsM[1] + _satContext.metadata.output.mercatorBoundsM[3]) / 2;
            double latitude = 2 * Math.Atan(Math.Exp(centerMercatorY / earthRadius)) - Math.PI / 2;
            float heightScale = (float)(_geometryContext.scale / Math.Cos(latitude));

            // Bilinear sample, x and y are pixel coordinates in heightmap space (pixel centers at .5).
            float SampleHeightMap(double x, double y)
            {
                try
                {
                    x = Math.Clamp(x - 0.5, 0, heightmapWidth - 1);
                    y = Math.Clamp(y - 0.5, 0, heightmapHeight - 1);
                    int x0 = (int)x;
                    int y0 = (int)y;
                    int x1 = Math.Min(x0 + 1, heightmapWidth - 1);
                    int y1 = Math.Min(y0 + 1, heightmapHeight - 1);
                    float tx = (float)(x - x0);
                    float ty = (float)(y - y0);

                    float Height(int hx, int hy)
                    {
                        float h = heightmap.data[hy * heightmapWidth + hx];
                        return float.IsNaN(h) || float.IsInfinity(h) ? 0 : h;
                    }

                    float top = Mathf.Lerp(Height(x0, y0), Height(x1, y0), tx);
                    float bottom = Mathf.Lerp(Height(x0, y1), Height(x1, y1), tx);
                    return Mathf.Lerp(top, bottom, ty);
                }
                catch (Exception e)
                {
                    Logger.Logger.LogError($"Failed to sample heightmap: {e.Message}", "ProcGen", this);
                    return 0;
                }
            }

            // Largest quad is step x step pixels, step = 2^topLevel.
            int step = Mathf.ClosestPowerOfTwo(Mathf.Clamp(_pixelStep, 1, ChunkSize));
            int topLevel = 0;
            while ((1 << topLevel) < step)
                topLevel++;

            // Quadtree pyramid of a chunk, levels[k] holds one segment id per 2^k x 2^k block
            // (Mixed if the block has several segments). Each level is 4x smaller, so building it is O(n).
            int[][] levels = new int[topLevel + 1][];
            for (int k = 0; k <= topLevel; k++)
                levels[k] = new int[(ChunkSize >> k) * (ChunkSize >> k)];

            // Lazily sampled height per pixel corner of a chunk, NaN = not sampled yet.
            const int cornerStride = ChunkSize + 1;
            float[] cornerHeights = new float[cornerStride * cornerStride];

            // Reused between chunks.
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            // Submesh key: segment id + index into agricultureColors (-1 when not agriculture).
            var trianglesBySubmesh = new Dictionary<(int segId, int agricultureIndex), List<int>>();
            var submeshKeys = new List<(int segId, int agricultureIndex)>();
            var quadVerts = new Vector3[4];

            float pixelEdgeX = (float)(pixelSizeX * _geometryContext.scale);
            float pixelEdgeZ = (float)(pixelSizeY * _geometryContext.scale);

            // Agriculture quads pick the _agricultureMaterials entry whose key is closest to their sat color.
            Texture2D colorImage = _satContext.colorImage;
            var agricultureColors = new Color32[_agricultureMaterials.Count];
            _agricultureMaterials.Keys.CopyTo(agricultureColors, 0);
            var agricultureScores = new float[agricultureColors.Length];
            for (int i = 0; i < agricultureColors.Length; i++)
                agricultureScores[i] = AgricultureColorScore(agricultureColors[i]);
            bool sampleAgriculture = agricultureColors.Length > 0 && colorImage != null && colorImage.isReadable;
            if (agricultureColors.Length > 0 && !sampleAgriculture)
                Logger.Logger.LogWarning($"Color image is not loaded or not readable, agriculture uses the segment material", "ProcGen", this);
            double colorScaleX = sampleAgriculture ? (double)colorImage.width / imageWidth : 0;
            double colorScaleY = sampleAgriculture ? (double)colorImage.height / imageHeight : 0;

            // Average sat color over image pixels [x0, x1) x [y0, y1), returns the closest agricultureColors index.
            int ClosestAgricultureColor(int x0, int y0, int x1, int y1)
            {
                // Capped sample grid keeps big quads cheap, total cost stays O(n).
                const int maxSamples = 4;
                int samplesX = Math.Min(maxSamples, x1 - x0);
                int samplesY = Math.Min(maxSamples, y1 - y0);
                long r = 0, g = 0, b = 0;
                for (int sy = 0; sy < samplesY; sy++)
                {
                    for (int sx = 0; sx < samplesX; sx++)
                    {
                        double px = x0 + (sx + 0.5) * (x1 - x0) / samplesX;
                        double py = y0 + (sy + 0.5) * (y1 - y0) / samplesY;
                        int tx = Math.Clamp((int)(px * colorScaleX), 0, colorImage.width - 1);
                        // Texture rows start at the bottom, image rows at the top.
                        int ty = colorImage.height - 1 - Math.Clamp((int)(py * colorScaleY), 0, colorImage.height - 1);
                        Color32 c = colorImage.GetPixel(tx, ty);
                        r += c.r;
                        g += c.g;
                        b += c.b;
                    }
                }
                int count = samplesX * samplesY;
                var average = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
                float score = AgricultureColorScore(average);

                int closest = 0;
                float closestDistance = float.MaxValue;
                for (int i = 0; i < agricultureScores.Length; i++)
                {
                    float distance = Math.Abs(agricultureScores[i] - score);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closest = i;
                    }
                }
                return closest;
            }

            submeshBySegID.Clear();

            int chunksX = (imageWidth + ChunkSize - 1) / ChunkSize;
            int chunksY = (imageHeight + ChunkSize - 1) / ChunkSize;
            int quadCount = 0;

            for (int chunkY = 0; chunkY < chunksY; chunkY++)
            {
                for (int chunkX = 0; chunkX < chunksX; chunkX++)
                {
                    int startX = chunkX * ChunkSize;
                    int startY = chunkY * ChunkSize;
                    int pixelsX = Math.Min(ChunkSize, imageWidth - startX);
                    int pixelsY = Math.Min(ChunkSize, imageHeight - startY);

                    // Level 0: segment per pixel, padding outside the image is Outside.
                    int[] level0 = levels[0];
                    for (int y = 0; y < ChunkSize; y++)
                        for (int x = 0; x < ChunkSize; x++)
                            level0[y * ChunkSize + x] = x < pixelsX && y < pixelsY
                                ? segmentation.Get(startX + x, startY + y)
                                : Outside;

                    // Level k: merge the 4 child blocks of level k-1.
                    for (int k = 1; k <= topLevel; k++)
                    {
                        int[] child = levels[k - 1];
                        int[] parent = levels[k];
                        int childDim = ChunkSize >> (k - 1);
                        int dim = ChunkSize >> k;
                        for (int y = 0; y < dim; y++)
                        {
                            for (int x = 0; x < dim; x++)
                            {
                                int c = 2 * y * childDim + 2 * x;
                                parent[y * dim + x] = MergeSegments(
                                    MergeSegments(child[c], child[c + 1]),
                                    MergeSegments(child[c + childDim], child[c + childDim + 1]));
                            }
                        }
                    }

                    Array.Fill(cornerHeights, float.NaN);

                    // x, y are pixel corners relative to the chunk start.
                    float CornerHeight(int x, int y)
                    {
                        int i = y * cornerStride + x;
                        if (float.IsNaN(cornerHeights[i]))
                            cornerHeights[i] = heightScale * SampleHeightMap(
                                (startX + x) * heightmapScaleX,
                                (startY + y) * heightmapScaleY);
                        return cornerHeights[i];
                    }

                    // Chunk origin is its top-left corner, image y goes down so unity z goes negative.
                    Vector3 chunkPos = new Vector3(
                        (float)((bounds[0] + startX * pixelSizeX) * _geometryContext.scale),
                        0,
                        (float)((bounds[3] - startY * pixelSizeY) * _geometryContext.scale));

                    // Quad over pixels [x0, x0+size) x [y0, y0+size) of the chunk, clamped to the image edge.
                    void AddBlockQuad(int x0, int y0, int size, int segId)
                    {
                        int x1 = Math.Min(x0 + size, pixelsX);
                        int y1 = Math.Min(y0 + size, pixelsY);
                        float left = x0 * pixelEdgeX;
                        float right = x1 * pixelEdgeX;
                        float top = -y0 * pixelEdgeZ;
                        float bottom = -y1 * pixelEdgeZ;

                        // Same winding as CreateRandomMesh: (x,z), (x,z+e), (x+e,z+e), (x+e,z).
                        quadVerts[0] = new Vector3(left, CornerHeight(x0, y1), bottom);
                        quadVerts[1] = new Vector3(left, CornerHeight(x0, y0), top);
                        quadVerts[2] = new Vector3(right, CornerHeight(x1, y0), top);
                        quadVerts[3] = new Vector3(right, CornerHeight(x1, y1), bottom);

                        int agricultureIndex = sampleAgriculture && segId == _agricultureSegID
                            ? ClosestAgricultureColor(startX + x0, startY + y0, startX + x1, startY + y1)
                            : -1;
                        var key = (segId, agricultureIndex);
                        if (!trianglesBySubmesh.TryGetValue(key, out var triangles))
                        {
                            triangles = new List<int>();
                            trianglesBySubmesh[key] = triangles;
                        }
                        MeshOperations.AddQuad(quadVerts, vertices, triangles, normals);

                        // World space UVs, same as CreateRandomMesh.
                        float mapLeft = chunkPos.x + left;
                        float mapRight = chunkPos.x + right;
                        float mapTop = chunkPos.z + top;
                        float mapBottom = chunkPos.z + bottom;
                        uvs.Add(new Vector2(mapLeft, mapBottom)/_uvScale);
                        uvs.Add(new Vector2(mapLeft, mapTop)/_uvScale);
                        uvs.Add(new Vector2(mapRight, mapTop)/_uvScale);
                        uvs.Add(new Vector2(mapRight, mapBottom)/_uvScale);
                        quadCount++;
                    }

                    // Uniform block -> one quad, mixed block -> its 4 children. Level 0 is never mixed.
                    void EmitBlock(int level, int x, int y)
                    {
                        int segId = levels[level][y * (ChunkSize >> level) + x];
                        if (segId == Outside)
                            return;
                        int size = 1 << level;
                        if (segId != Mixed)
                        {
                            AddBlockQuad(x * size, y * size, size, segId);
                            return;
                        }
                        EmitBlock(level - 1, 2 * x, 2 * y);
                        EmitBlock(level - 1, 2 * x + 1, 2 * y);
                        EmitBlock(level - 1, 2 * x, 2 * y + 1);
                        EmitBlock(level - 1, 2 * x + 1, 2 * y + 1);
                    }

                    int blocksX = (pixelsX + step - 1) / step;
                    int blocksY = (pixelsY + step - 1) / step;
                    for (int y = 0; y < blocksY; y++)
                        for (int x = 0; x < blocksX; x++)
                            EmitBlock(topLevel, x, y);

                    var created = Instantiate(_tilePrefab, gameObject.transform);
                    created.transform.localPosition = chunkPos;
                    created.transform.localRotation = Quaternion.identity;
                    created.name = $"Chunk{chunkX},{chunkY}";
                    var meshFilter = created.GetComponent<MeshFilter>();
                    if (meshFilter == null)
                        meshFilter = created.AddComponent<MeshFilter>();
                    var meshRenderer = created.GetComponent<MeshRenderer>();
                    if (meshRenderer == null)
                        meshRenderer = created.AddComponent<MeshRenderer>();

                    var mesh = new Mesh();
                    mesh.name = $"Mesh{chunkX},{chunkY}";
                    // Up to 256x256 quads * 4 vertices, above the 16 bit index limit.
                    mesh.indexFormat = vertices.Count > ushort.MaxValue
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16;
                    mesh.SetVertices(vertices);
                    mesh.SetNormals(normals);
                    mesh.SetUVs(0, uvs);

                    // One submesh per segment (and agriculture color) present in this chunk,
                    // lists are reused so skip empty ones.
                    submeshKeys.Clear();
                    foreach (var pair in trianglesBySubmesh)
                        if (pair.Value.Count > 0)
                            submeshKeys.Add(pair.Key);
                    submeshKeys.Sort();

                    mesh.subMeshCount = submeshKeys.Count;
                    for (int i = 0; i < submeshKeys.Count; i++)
                    {
                        var (segId, agricultureIndex) = submeshKeys[i];
                        mesh.SetTriangles(trianglesBySubmesh[submeshKeys[i]], i, false);

                        if (!submeshBySegID.TryGetValue(segId, out var submeshes))
                        {
                            submeshes = new List<SubmeshRef>();
                            submeshBySegID[segId] = submeshes;
                        }
                        submeshes.Add(new SubmeshRef(meshRenderer, i,
                            agricultureIndex >= 0 ? agricultureColors[agricultureIndex] : (Color32?)null));
                    }
                    mesh.RecalculateBounds();

                    meshFilter.sharedMesh = mesh;
                    // Filled by AssignMaterials.
                    meshRenderer.sharedMaterials = new Material[submeshKeys.Count];

                    vertices.Clear();
                    normals.Clear();
                    uvs.Clear();
                    foreach (var triangles in trianglesBySubmesh.Values)
                        triangles.Clear();
                }
            }

            AssignMaterials();
            Logger.Logger.Log($"Created {(chunksX * chunksY).ToString()} chunks, {quadCount.ToString()} quads for {imageWidth}x{imageHeight} pixels, pixel step {step}", "ProcGen");
        }

        // Agriculture colors are matched by closest score, hue and saturation in [0, 1].
        private static float AgricultureColorScore(Color32 color)
        {
            Color.RGBToHSV(color, out float hue, out float saturation, out float brightness);
            return hue + saturation * 0.3f + brightness * 0.2f;
        }

        // Segment of two merged blocks, Outside blocks take the other block's segment.
        private static int MergeSegments(int a, int b)
        {
            if (a == Outside)
                return b;
            if (b == Outside || a == b)
                return a;
            return Mixed;
        }

        /// <summary>
        /// Assigns materials to every submesh in <see cref="submeshBySegID"/>: agriculture submeshes use
        /// <see cref="_agricultureMaterials"/> of their matched color, others <see cref="_segmentMaterials"/> (or the default material).
        /// </summary>
        public void AssignMaterials()
        {

            // sharedMaterials must be set as a whole array per renderer.
            var materialsByRenderer = new Dictionary<MeshRenderer, Material[]>();
            foreach (var pair in submeshBySegID)
            {
                Material segmentMaterial = _segmentMaterials.TryGetValue(pair.Key, out var mapped) && mapped != null
                    ? mapped
                    : _defaultMaterial;

                foreach (var submesh in pair.Value)
                {
                    Material material = submesh.agricultureColor.HasValue
                        && _agricultureMaterials.TryGetValue(submesh.agricultureColor.Value, out var agricultureMaterial)
                        && agricultureMaterial != null
                            ? agricultureMaterial
                            : segmentMaterial;

                    // Chunk may have been destroyed since generation.
                    if (submesh.renderer == null)
                        continue;
                    if (!materialsByRenderer.TryGetValue(submesh.renderer, out var materials))
                    {
                        materials = submesh.renderer.sharedMaterials;
                        materialsByRenderer[submesh.renderer] = materials;
                    }
                    if (submesh.submeshIndex < materials.Length)
                        materials[submesh.submeshIndex] = material;
                }
            }
            foreach (var pair in materialsByRenderer)
                pair.Key.sharedMaterials = pair.Value;
        }
        public void CreateRandomMesh()
        {
            var tiles = _chunkGenerator.GetCoordsInRenderDistance(_camera.position);
            foreach (var tile in tiles)
            {
                List<Vector3> vertices = new List<Vector3>();
                List<int> triangles = new List<int>();
                List<Vector3> normals = new List<Vector3>();
                List<Vector2> uvs = new List<Vector2>();
                
                float tileLength = _chunkGenerator.ZoomScale(tile);
                float edgeLength = tileLength/_tileSubdivision;
                Vector2 tileOrigin = tile.posf * tileLength;
                
                var created = Instantiate(_tilePrefab, new Vector3(tile.x, 0, tile.y) * tileLength, Quaternion.identity, gameObject.transform);
                created.name = $"Tile{tile.x},{tile.y},{tile.zoom}";
                var meshFilter = created.GetComponent<MeshFilter>();
                
                var mesh = new Mesh();
                mesh.name = $"Mesh{tile.x},{tile.y},{tile.zoom}";
                
                for (int subX = 0; subX < _tileSubdivision; subX++)
                {
                    for (int subY = 0; subY < _tileSubdivision; subY++)
                    {
                        Vector2 localTilePos = new Vector2(subX * edgeLength, subY * edgeLength);
                        Vector2 mapPos = localTilePos + tileOrigin;
                        float MapHeight(float x, float y) => 100f * SampleRandomHeightmap(x, y, 0, 0.003f);
                        float MapNormal(float x, float y) => 100f * SampleRandomHeightmap(x, y, 0, 0.003f);
                        Vector3[] quadVerts = new Vector3[4]
                        {
                            new Vector3(localTilePos.x, MapHeight(mapPos.x, mapPos.y), localTilePos.y),
                            new Vector3(localTilePos.x, MapHeight(mapPos.x, mapPos.y + edgeLength), localTilePos.y + edgeLength),
                            new Vector3(localTilePos.x + edgeLength, MapHeight(mapPos.x + edgeLength, mapPos.y + edgeLength), localTilePos.y + edgeLength),
                            new Vector3(localTilePos.x + edgeLength, MapHeight(mapPos.x + edgeLength, mapPos.y), localTilePos.y),
                        };
                        MeshOperations.AddQuad(quadVerts, vertices, triangles, normals);
                        uvs.Add(new Vector2(mapPos.x, mapPos.y)/_uvScale);
                        uvs.Add(new Vector2(mapPos.x, mapPos.y + edgeLength)/_uvScale);
                        uvs.Add(new Vector2(mapPos.x + edgeLength, mapPos.y + edgeLength)/_uvScale);
                        uvs.Add(new Vector2(mapPos.x + edgeLength, mapPos.y)/_uvScale);
                        
                    }
                }
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                
                meshFilter.sharedMesh = mesh;
            }
            Logger.Logger.Log($"Created {tiles.Count.ToString()} tiles", "ProcGen");
        }
        
        // ==================== HEIGHTMAP ====================

        /// <summary>
        /// Generates a width x height heightmap by blending fractal Perlin noise
        /// with Voronoi noise, normalized to [0, 1].
        /// </summary>
        /// <param name="x">X coord</param>
        /// <param name="y">Y coord</param>
        /// <param name="seed">Seed for deterministic generation.</param>
        /// <param name="scale">Sampling scale; smaller = larger, smoother features.</param>
        /// <param name="perlinWeight">Contribution of Perlin noise to the final height.</param>
        /// <param name="voronoiWeight">Contribution of Voronoi noise to the final height.</param>
        /// <param name="octaves">Perlin FBM octave count.</param>
        public float SampleRandomHeightmap(
            float x, float y, int seed,
            float scale = 0.05f,
            float perlinWeight = 0.7f,
            float voronoiWeight = 0.3f,
            int octaves = 4)
        {

            float perlin = NoiseUtils.PerlinFBM(x * scale, y * scale, seed, octaves);
            float voronoi = NoiseUtils.VoronoiNoise(x * scale, y * scale, seed);

            float perlinNorm = (perlin + 1f) / 2f; // [-1,1] -> [0,1]
            float voronoiNorm = Math.Min(voronoi / 1.5f, 1f); // [0,~1.5] -> [0,1]

            // Invert Voronoi so cell centers (low distance) read as peaks.
            float combined = perlinNorm * perlinWeight + (1f - voronoiNorm) * voronoiWeight;

            return Math.Clamp(combined, 0f, 1f);

        }
    }
}