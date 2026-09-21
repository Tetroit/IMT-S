using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralGeneration
{
    public class TerrainGenerator : MonoBehaviour
    {
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        [SerializeField] private ChunkGenerator _chunkGenerator;
        [SerializeField] private Transform _camera;


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
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            foreach (var tile in tiles)
            {
                float edgeLength = _chunkGenerator.ZoomScale(tile);
                Vector2 mapPos = tile.posf * edgeLength;
                float MapHeight(float x, float y) => 100f * SampleHeightmap(x, y, 0, 0.001f);
                Vector3[] quadVerts = new Vector3[4]
                {
                    new Vector3(mapPos.x, MapHeight(mapPos.x, mapPos.y), mapPos.y),
                    new Vector3(mapPos.x, MapHeight(mapPos.x, mapPos.y + edgeLength), mapPos.y + edgeLength),
                    new Vector3(mapPos.x + edgeLength, MapHeight(mapPos.x + edgeLength, mapPos.y + edgeLength), mapPos.y + edgeLength),
                    new Vector3(mapPos.x + edgeLength, MapHeight(mapPos.x + edgeLength, mapPos.y), mapPos.y),
                };
                MeshOperations.AddQuad(quadVerts, vertices, triangles, normals, uvs);
            }
            _meshFilter.sharedMesh = new Mesh();
            Logger.Logger.Log($"Vertices: {vertices.Count.ToString()}", "ProcGen");
            Logger.Logger.Log($"Normals: {normals.Count.ToString()}", "ProcGen");
            Logger.Logger.Log($"UVs: {uvs.Count.ToString()}", "ProcGen");
            Logger.Logger.Log($"Triangles: {triangles.Count.ToString()}", "ProcGen");
            _meshFilter.sharedMesh.SetVertices(vertices);
            _meshFilter.sharedMesh.SetTriangles(triangles, 0);
            _meshFilter.sharedMesh.SetNormals(normals);
            _meshFilter.sharedMesh.SetUVs(0, uvs);
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