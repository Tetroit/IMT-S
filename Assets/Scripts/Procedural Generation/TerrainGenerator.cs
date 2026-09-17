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

        [Header("Mesh config")]
        [Range(1,16)]
        [SerializeField] private int _tileSubdivision = 4;
        [Min(0.01f)]    
        [SerializeField] private float _uvScale = 1;

        void Awake()
        {
            BindReferences();
        }
        public void BindReferences()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
        }
        public void CreateMesh()
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
                        float MapHeight(float x, float y) => 100f * SampleHeightmap(x, y, 0, 0.003f);
                        float MapNormal(float x, float y) => 100f * SampleHeightmap(x, y, 0, 0.003f);
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
        public float SampleHeightmap(
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