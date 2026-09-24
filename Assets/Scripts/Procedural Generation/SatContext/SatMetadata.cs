using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.SatContext
{
    /// <summary>
    /// Mirrors metadata.json written by SAT-tile-Segmentation.
    /// Field names match the JSON keys so it can be parsed with JsonUtility.
    /// Bboxes are [minLon, minLat, maxLon, maxLat]; mercator bounds are [minX, minY, maxX, maxY] in metres (EPSG:3857).
    /// </summary>
    [Serializable]
    public class SatMetadata
    {
        public string tool;
        public string generatedUtc;
        public double elapsedSeconds;
        public HostInfo host;
        public RequestInfo request;
        public string crs;
        public OutputInfo output;
        public ResourcesInfo resources;
        public SourceInfo imagery;
        public TerrainInfo terrain;
        public ModelInfo model;
        public ClassInfo[] classes;
        public ClassStats[] classStats;
        public string[] attributions;
        public string[] warnings;
        public string[] files;

        public static SatMetadata Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Metadata not found at: {filePath}");

            return JsonUtility.FromJson<SatMetadata>(File.ReadAllText(filePath));
        }

        [Serializable]
        public class HostInfo
        {
            public string python;
            public string platform;
        }

        [Serializable]
        public class RequestInfo
        {
            public double[] bbox;
            public string imagery;
            public string terrain;
            public int imageryZoom;
            public int terrainZoom;
            public string inferenceGsd;     // "auto" or a number
            public string hillshadeZFactor; // "auto" or a number
            public bool heightNpy;
            public float overlayAlpha;
            public bool wantGeotiff;
            public string label;
        }

        [Serializable]
        public class OutputInfo
        {
            public int widthPx;
            public int heightPx;
            public double megapixels;
            public double gsdMetres;
            public double inferenceGsdMetres;
            public double[] bboxRequested;
            public double[] bboxSnapped;
            public double[] mercatorBoundsM;
            public double areaKm2;
            public bool fullResolutionPng;
            public bool geotiff;
        }

        [Serializable]
        public class ResourcesInfo
        {
            public long rasterBytes;
            public bool rastersOnDisk;
            public string scratchDir;
            public long rasterBudgetBytes;
            public InferenceBlocksInfo inferenceBlocks;
        }

        [Serializable]
        public class InferenceBlocksInfo
        {
            public int blocks;
            public int blockPx;
            public int haloPx;
            public long peakProbabilityBytes;
            public double windowOverhead;
            public bool singlePass;
        }

        [Serializable]
        public class TileRange
        {
            public int x0;
            public int y0;
            public int x1;
            public int y1;
        }

        [Serializable]
        public class FetchInfo
        {
            public int requested;
            public int fromCache;
            public int downloaded;
            public int missing;
            public long bytesDownloaded;
            public string[] failures;
        }

        [Serializable]
        public class SourceInfo
        {
            public string source;
            public string sourceName;
            public int zoom;
            public int tileSize;
            public TileRange tileRange;
            public int tileCount;
            public int widthPx;
            public int heightPx;
            public double gsdMetres;
            public double[] requestedBbox;
            public double[] snappedBbox;
            public string attribution;
            public FetchInfo fetch;
        }

        [Serializable]
        public class TerrainInfo : SourceInfo
        {
            public string encoding;
            public float heightMinM;
            public float heightMaxM;
            public float nodataFraction;
            public bool resampledOntoImageryGrid;
            public float hillshadeZFactor;
            public bool heightNpyWritten;
        }

        [Serializable]
        public class ModelInfo
        {
            public bool available;
            public string reason;
            public string device;
            public string deviceRequest;
            public string deviceLabel;
            public long vramTotalBytes;
            public long vramFreeBytes;
            public int batchSize;
            public int window;
            public float overlap;
            public bool tta;
            public string weightsPath;
            public string weightsHash;
            public string architecture;
            public int classes;
            public string torchVersion;
            public bool cuda;
            public string[] notes;
        }

        [Serializable]
        public class ClassInfo
        {
            public int index;
            public string name;
            public int[] color; // RGB 0-255

            public Color32 color32 => new Color32((byte)color[0], (byte)color[1], (byte)color[2], 255);
        }

        [Serializable]
        public class ClassStats : ClassInfo
        {
            public long pixels;
            public double fraction;
            public double areaM2;
        }
    }
    
    #if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(SatMetadata))]
    public class MyDataDrawer : PropertyDrawer
    {
        public override void OnGUI(
            Rect position,
            SerializedProperty property,
            GUIContent label)
        {
            EditorGUI.BeginDisabledGroup(true);

            EditorGUI.PropertyField(
                position,
                property,
                label,
                true
            );

            EditorGUI.EndDisabledGroup();
        }

        public override float GetPropertyHeight(
            SerializedProperty property,
            GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }
    }
#endif
}
