using System;
using System.Collections;
using System.Collections.Generic;
using ProceduralGeneration;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;

public struct TileCoord
{
    public int x;
    public int y;
    public int zoom;
    public Vector2Int pos => new Vector2Int(x, y);
    public Vector2 posf => new Vector2(x, y);

    public TileCoord(int x, int y, int zoom)
    {
        this.x = x;
        this.y = y;
        this.zoom = zoom;
    }
    public TileCoord(Vector2Int coord, int zoom)
    {
        this.x = coord.x;
        this.y = coord.y;
        this.zoom = zoom;
    }
    public List<TileCoord> children
    {
        get
        {
            int xc = x << 1;
            int yc = y << 1;
            int childZoom = zoom + 1;
            return new List<TileCoord>
            {
                new TileCoord(xc, yc, childZoom),
                new TileCoord(xc + 1, yc, childZoom),
                new TileCoord(xc, yc + 1, childZoom),
                new TileCoord(xc + 1, yc + 1, childZoom),
            };
        }
    }
}

public class ChunkCollection : IEnumerable<TileCoord>
{
    Dictionary<int, HashSet<Vector2Int>> _chunks = new Dictionary<int, HashSet<Vector2Int>>();
    public int Count
    {
        get
        {
            int res = 0;
            foreach (var (zoom, arr) in _chunks)
                res += arr.Count;
            return res;
        }
    }
    public void Add(TileCoord coord)
    {
        if (!_chunks.ContainsKey(coord.zoom))
            _chunks.Add(coord.zoom, new HashSet<Vector2Int>());
        _chunks[coord.zoom].Add(coord.pos);
    }
    public void Add(ChunkCollection other)
    {
        foreach (var (zoom, tiles) in other._chunks)
        {
            if (!_chunks.ContainsKey(zoom))
                _chunks.Add(zoom, new HashSet<Vector2Int>());
            _chunks[zoom].UnionWith(tiles);
        }
    }
    public void Remove(ChunkCollection other)
    {
        foreach (var (zoom, tiles) in other._chunks)
            _chunks[zoom].ExceptWith(tiles);
    }
    public void Remove(TileCoord coord)
    {
        if (!_chunks.ContainsKey(coord.zoom)) return;
        if (_chunks[coord.zoom].Contains(coord.pos))
            _chunks[coord.zoom].Remove(coord.pos);
    }
    public HashSet<Vector2Int> this[int index]
    {
        get => _chunks[index];
        set => _chunks[index] = value;
    }
    public IEnumerator<TileCoord> GetEnumerator()
    {
        foreach (var (zoom, positions) in _chunks)
        {
            foreach (var pos in positions)
            {
                yield return new TileCoord
                {
                    zoom = zoom,
                    x = pos.x,
                    y = pos.y,
                };
            }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ChunkGenerator : MonoBehaviour
{

    private readonly List<float> _chunkScales = new List<float>()
    {
        1.0f,
        0.5f,
        0.25f,
        0.125f,
        0.0625f,
        0.03125f,
        0.015625f,
        0.0078125f,
        0.00390625f,
        0.001953125f,
    };

    private ChunkCollection _loadedChunks = new ChunkCollection();

    private Vector3 _lastObservation = new Vector3(0,0,0);
    private bool _firstScan = true;

    [Header("References")]
    [SerializeField] private Transform _cameraTransform;

    [Header("Chunk Settings")]
    [Tooltip("Size of the largest chunk (LOD0)")]
    [SerializeField] private float _baseChunkSize = 10.0f;
    [Range(1, 6)]
    [SerializeField] private int _layers = 5;
    [SerializeField] private float _renderDistance = 200.0f;

    [Header("Preview")]
    [SerializeField] private bool _showGizmos = false;
    [SerializeField] private bool _layerMask = false;
    [Range(0, 6)]
    [SerializeField] private int _previewLayer = 5;

    [Header("Events")]
    [SerializeField] private UnityEvent<(ChunkCollection, ChunkCollection)> _onChunksUpdated;

    public float ZoomScale(int zoom) => _chunkScales[zoom] * _baseChunkSize;
    public float ZoomScale(TileCoord tile) => _chunkScales[tile.zoom] * _baseChunkSize;
    Vector3 GetWorldSpaceOfChunk(TileCoord coord) => new Vector3(coord.x, 0, coord.y) * ZoomScale(coord.zoom);
    Vector3 GetWorldSpaceOfChunkCenter(TileCoord coord) => new Vector3(coord.x + 0.5f, 0, coord.y + 0.5f) * ZoomScale(coord.zoom);
    TileCoord GetChunkFromWorldSpace(Vector3 worldPos, int zoom)
    {
        float scale = ZoomScale(zoom);
        return new TileCoord(Mathf.FloorToInt(worldPos.x / scale), Mathf.FloorToInt(worldPos.z / scale), zoom);
    }

    float DistanceToChunk(TileCoord coord, Vector3 worldPosition)
    {
        float scale = ZoomScale(coord.zoom);
        Vector2 center = worldPosition;
        float chunkSize = _baseChunkSize * _chunkScales[coord.zoom];
        Vector2 worldPos2D = new Vector2(worldPosition.x, worldPosition.z);
        Vector2 r = VecEx.Abs(worldPos2D - (coord.posf * chunkSize)) - new Vector2(chunkSize, chunkSize);
        r.x = Math.Max(r.x, 0);
        r.y = Math.Max(r.y, 0);
        return r.magnitude;
    }

    void SubdivideChunk(TileCoord coord, ChunkCollection children, Vector3 pos)
    {

        foreach (var child in coord.children)
        {
            if (DistanceToChunk(child, pos) < _renderDistance * _chunkScales[child.zoom + 1] && coord.zoom != _layers - 1)
                SubdivideChunk(child, children, pos);
            else
                children.Add(child);
        }
    }
    void SubdivideChunkDiff(TileCoord coord, ChunkCollection toAdd, ChunkCollection toRemove, Vector3 prev, Vector3 curr)
    {
        foreach (var child in coord.children)
        {
            var prevDist =  DistanceToChunk(child, prev);
            var currDist =  DistanceToChunk(child, curr);
            float threshold = _renderDistance * _chunkScales[child.zoom + 1];
            bool prevSub = prevDist < threshold && coord.zoom != _layers - 1;
            bool currSub = currDist < threshold && coord.zoom != _layers - 1;
            if (prevSub && currSub) SubdivideChunkDiff(child, toAdd, toRemove, prev, curr);
            else if (!prevSub && currSub)
            {
                toRemove.Add(child);
                SubdivideChunk(child, toAdd, curr);
            }
            else if (prevSub && !currSub)
            {
                toAdd.Add(child);
                SubdivideChunk(child, toRemove, prev);
            }
        }
    }
    public ChunkCollection GetCoordsInRenderDistance(Vector3 point) => GetCoordsInRadius(point, _renderDistance);
    public ChunkCollection GetCoordsInRadius(Vector3 point, float radius)
    {
        if (radius > _baseChunkSize * 100)
        {
            Logger.Logger.LogWarning("Too many tiles to load", "ProcGen", this);
            return new ChunkCollection();
        }
        ChunkCollection toRender = new ChunkCollection();

        int minX = Mathf.FloorToInt((point.x - radius) / _baseChunkSize);
        int maxX = Mathf.CeilToInt((point.x + radius) / _baseChunkSize);

        int minY = Mathf.FloorToInt((point.z - radius) / _baseChunkSize);
        int maxY = Mathf.CeilToInt((point.z + radius) / _baseChunkSize);

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var candidate = new TileCoord(x, y, 0);
                float dist = DistanceToChunk(candidate, point);
                if (dist <= radius)
                {

                    if (dist < _renderDistance * _chunkScales[1])
                        SubdivideChunk(candidate, toRender, point);
                    else
                        toRender.Add(candidate);
                }
            }
        }
        return toRender;
    }

    public (ChunkCollection, ChunkCollection) GetDiff(Vector3 prev, Vector3 curr)
    {
        ChunkCollection toAdd = new ChunkCollection();
        ChunkCollection toRemove = new ChunkCollection();

        int minX = Mathf.Min(
            Mathf.FloorToInt((curr.x - _renderDistance) / _baseChunkSize),
            Mathf.FloorToInt((prev.x - _renderDistance) / _baseChunkSize));
        int maxX = Mathf.Max(
            Mathf.FloorToInt((curr.x + _renderDistance) / _baseChunkSize),
            Mathf.FloorToInt((curr.x + _renderDistance) / _baseChunkSize));

        int minY = Mathf.Min(
            Mathf.FloorToInt((curr.y - _renderDistance) / _baseChunkSize),
            Mathf.FloorToInt((prev.y - _renderDistance) / _baseChunkSize));
        int maxY = Mathf.Max(
            Mathf.FloorToInt((curr.y + _renderDistance) / _baseChunkSize),
            Mathf.FloorToInt((curr.y + _renderDistance) / _baseChunkSize));

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var candidate = new TileCoord(x, y, 0);
                float distPrev = DistanceToChunk(candidate, prev);
                float distCurr = DistanceToChunk(candidate, prev);
                if (distPrev <= _renderDistance || distCurr <= _renderDistance)
                {
                    float threshold = _renderDistance * _chunkScales[candidate.zoom + 1];
                    bool prevSub = distPrev < threshold && candidate.zoom != _layers - 1;
                    bool currSub = distCurr < threshold && candidate.zoom != _layers - 1;
                    if (prevSub && currSub) SubdivideChunkDiff(candidate, toAdd, toRemove, prev, curr);
                    else if (!prevSub && currSub)
                    {
                        toRemove.Add(candidate);
                        SubdivideChunk(candidate, toAdd, curr);
                    }
                    else if (prevSub && !currSub)
                    {
                        toAdd.Add(candidate);
                        SubdivideChunk(candidate, toRemove, prev);
                    }
                }
            }
        }
        return (toAdd, toRemove);
    }
    private void OnEnable()
    {
        _lastObservation = _cameraTransform.position;
        _loadedChunks = GetCoordsInRenderDistance(_lastObservation);
        _onChunksUpdated?.Invoke((_loadedChunks, new ChunkCollection()));
    }
    void OnDisable()
    {
        _firstScan = false;
    }
    private void Update()
    {
        if (_firstScan)
        {
            _lastObservation = _cameraTransform.position;
            _loadedChunks = GetCoordsInRenderDistance(_lastObservation);
            _onChunksUpdated?.Invoke((_loadedChunks, new ChunkCollection()));
        }
        else
        {
            var (toAdd, toRemove) = GetDiff(_lastObservation, _cameraTransform.position);
            _lastObservation = _cameraTransform.position;

        }

    }
    private void OnDrawGizmos()
    {
        if (!_showGizmos) return;
        if (_cameraTransform != null)
        {
            foreach (var tile in GetCoordsInRadius(_cameraTransform.position, _renderDistance))
            {
                if (!_layerMask || _layerMask && tile.zoom == _previewLayer)
                {
                    Gizmos.color = Color.HSVToRGB(tile.zoom * 0.1f, 1, 1);
                    float side = ZoomScale(tile.zoom);
                    Gizmos.DrawWireCube(GetWorldSpaceOfChunkCenter(tile), new Vector3(side, 0, side));
                }
            }
        }
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
    public float SampleHeightmap(
        int x, int y, int seed,
        float scale = 0.05f,
        float perlinWeight = 0.7f,
        float voronoiWeight = 0.3f,
        int octaves = 4)
    {

        float perlin = NoiseUtils.PerlinFBM(x * scale, y * scale, seed, octaves);
        float voronoi = NoiseUtils.VoronoiNoise(x * scale, y * scale, seed);

        float perlinNorm = (perlin + 1f) / 2f;            // [-1,1] -> [0,1]
        float voronoiNorm = Math.Min(voronoi / 1.5f, 1f); // [0,~1.5] -> [0,1]

        // Invert Voronoi so cell centers (low distance) read as peaks.
        float combined = perlinNorm * perlinWeight + (1f - voronoiNorm) * voronoiWeight;

        return  Math.Clamp(combined, 0f, 1f);

    }
}
