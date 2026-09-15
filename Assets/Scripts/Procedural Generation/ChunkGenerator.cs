using System;
using System.Collections.Generic;
using ProceduralGeneration;
using UnityEngine;

namespace ProceduralGeneration
{
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

        private Dictionary<Vector2Int, Mesh> _loadedChunks = new Dictionary<Vector2Int, Mesh>();
        
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

        void SubdivideChunk(TileCoord coord, ref List<TileCoord> children, Vector3 pos)
        {

            foreach (var child in coord.children)
            {
                if (DistanceToChunk(child, pos) < _renderDistance * _chunkScales[child.zoom + 1] && coord.zoom != _layers - 1)
                    SubdivideChunk(child, ref children, pos);
                else
                    children.Add(child);
            }
        }
        public List<TileCoord> GetCoordsInRenderDistance(Vector3 point) => GetCoordsInRadius(point, _renderDistance);
        public List<TileCoord> GetCoordsInRadius(Vector3 point, float radius)
        {
            if (radius > _baseChunkSize * 100)
            {
                Logger.Logger.LogWarning("Too many tiles to load", "ProcGen", this);
                return new List<TileCoord>();
            }
            List<TileCoord> toRender = new List<TileCoord>();

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
                            SubdivideChunk(candidate, ref toRender, point);
                        else
                            toRender.Add(candidate);
                    }
                }
            }
            return toRender;
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
    }
}
