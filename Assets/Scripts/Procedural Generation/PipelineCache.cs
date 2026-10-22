using System;
using System.Collections.Generic;
using System.IO;
using ProceduralGeneration.ImageProcessing;
using UnityEngine;

namespace ProceduralGeneration
{
    /// <summary>
    /// Json cache of one <see cref="TerrainGenerator.RunPipeline"/> stage, stored next to the sat images.
    /// The data is in Unity space, so every cache is stamped with the terrain config origin and scale it was made with.
    /// </summary>
    [Serializable]
    public abstract class PipelineCache
    {
        public DoubleVector2 origin;
        public double scale;

        public bool Matches(TerrainConfig terrainConfig)
        {
            // Far below a pixel, only guards against the json round trip of the doubles.
            const double originTolerance = 1e-3;
            const double scaleTolerance = 1e-9;
            return Math.Abs(origin.x - terrainConfig.origin.x) < originTolerance
                && Math.Abs(origin.y - terrainConfig.origin.y) < originTolerance
                && Math.Abs(scale - terrainConfig.scale) <= scaleTolerance * Math.Abs(terrainConfig.scale);
        }

        /// <summary>
        /// Reads the cache at <paramref name="path"/>. Fails if it is missing, unreadable or made with another terrain config origin or scale.
        /// </summary>
        public static bool TryLoad<T>(string path, TerrainConfig terrainConfig, out T cache) where T : PipelineCache
        {
            cache = null;
            if (!File.Exists(path))
                return false;
            try
            {
                cache = JsonUtility.FromJson<T>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Logger.Logger.LogWarning($"Failed to read cache {path}, rebuilding: {e.Message}", "ProcGen");
                return false;
            }
            if (cache == null)
                return false;
            if (!cache.Matches(terrainConfig))
            {
                Logger.Logger.LogWarning($"Cache {path} was made with a different terrain config origin or scale, rebuilding", "ProcGen");
                cache = null;
                return false;
            }
            return true;
        }

        public bool Save(string path, TerrainConfig terrainConfig)
        {
            origin = terrainConfig.origin;
            scale = terrainConfig.scale;
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(this));
            }
            catch (Exception e)
            {
                Logger.Logger.LogError($"Failed to write cache {path}: {e.Message}", "ProcGen");
                return false;
            }
            Logger.Logger.Log($"Saved cache {path}", "ProcGen");
            return true;
        }
    }

    [Serializable]
    public class HousesCache : PipelineCache
    {
        public List<HouseGenerator.House> houses = new List<HouseGenerator.House>();
    }

    [Serializable]
    public class TreesCache : PipelineCache
    {
        public List<Vector3> points = new List<Vector3>();
    }

    [Serializable]
    public class RoadsCache : PipelineCache
    {
        public RoadGraph graph = new RoadGraph();
    }
}
