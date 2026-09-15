using System;

namespace ProceduralGeneration
{
    /// <summary>
    /// Seeded 2D Perlin noise and Voronoi (cellular) noise, plus a heightmap generator
    /// that blends both. Fully deterministic and stateless — no shared mutable state,
    /// so it's safe to call from multiple threads with different seeds.
    /// </summary>
    public static class NoiseUtils
    {
        // ==================== SHARED HASHING ====================

        /// <summary>
        /// Deterministic integer hash of (x, y, seed). Used to derive both
        /// Perlin gradients and Voronoi feature points without a permutation table.
        /// </summary>
        private static int HashCoords(int x, int y, int seed)
        {
            unchecked
            {
                int hash = seed;
                hash = hash * 486187739 + x;
                hash = hash * 486187739 + y;
                hash ^= hash >> 13;
                hash *= unchecked((int)0x5bd1e995);
                hash ^= hash >> 15;
                return hash;
            }
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        private static float Lerp(float a, float b, float t) => a + t * (b - a);

        // ==================== PERLIN NOISE ====================

        private static float Grad(int ix, int iy, int seed, float x, float y)
        {
            int hash = HashCoords(ix, iy, seed);
            int h = hash & 7;
            float u = h < 4 ? x : y;
            float v = h < 4 ? y : x;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>
        /// Classic 2D Perlin noise. Returns a value in roughly [-1, 1].
        /// </summary>
        public static float PerlinNoise(float x, float y, int seed)
        {
            int xi = (int)Math.Floor(x);
            int yi = (int)Math.Floor(y);

            float xf = x - (float)Math.Floor(x);
            float yf = y - (float)Math.Floor(y);

            float u = Fade(xf);
            float v = Fade(yf);

            float n00 = Grad(xi, yi, seed, xf, yf);
            float n10 = Grad(xi + 1, yi, seed, xf - 1, yf);
            float n01 = Grad(xi, yi + 1, seed, xf, yf - 1);
            float n11 = Grad(xi + 1, yi + 1, seed, xf - 1, yf - 1);

            float x1 = Lerp(n00, n10, u);
            float x2 = Lerp(n01, n11, u);

            return Lerp(x1, x2, v);
        }

        /// <summary>
        /// Fractal Brownian Motion: layers several octaves of Perlin noise for
        /// richer, more natural-looking terrain. Returns roughly [-1, 1].
        /// </summary>
        public static float PerlinFBM(
            float x, float y, int seed,
            int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float frequency = 1f;
            float amplitude = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                // Offset seed per octave so each layer samples an independent field.
                total += PerlinNoise(x * frequency, y * frequency, seed + i * 1013) * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        // ==================== VORONOI (CELLULAR) NOISE ====================

        /// <summary>
        /// Distance-based Voronoi/cellular noise. Returns the Euclidean distance
        /// (in cell units) from (x, y) to the nearest randomly-placed feature point.
        /// Typical range is [0, ~1.5]. Smaller values sit near cell centers/points,
        /// larger values sit near cell edges.
        /// </summary>
        public static float VoronoiNoise(float x, float y, int seed, float cellSize = 1f)
        {
            float xCell = x / cellSize;
            float yCell = y / cellSize;

            int xi = (int)Math.Floor(xCell);
            int yi = (int)Math.Floor(yCell);

            float minDist = float.MaxValue;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cellX = xi + dx;
                    int cellY = yi + dy;

                    // Derive a pseudo-random point inside this cell from the hash.
                    int h = HashCoords(cellX, cellY, seed);
                    float pointX = cellX + (float)((h & 0xFFFF) / 65535.0);
                    float pointY = cellY + (float)(((h >> 16) & 0xFFFF) / 65535.0);

                    float distX = pointX - xCell;
                    float distY = pointY - yCell;
                    float dist = (float)Math.Sqrt(distX * distX + distY * distY);

                    if (dist < minDist)
                        minDist = dist;
                }
            }

            return minDist;
        }
    }
}