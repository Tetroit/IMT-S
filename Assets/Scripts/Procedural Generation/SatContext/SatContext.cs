using System;
using System.IO;
using ProceduralGeneration.ImageProcessing;
using UnityEngine;

namespace ProceduralGeneration.SatContext
{
    [CreateAssetMenu(menuName = "Procedural Generation/SatContext")]
    public class SatContext : ScriptableObject
    {
        public string folderPath = "Test Data";
        public string absoluteFolderPath => Path.Combine(Application.dataPath, folderPath);
        public string metadataPath => Path.Combine(absoluteFolderPath, SegmentationApiClient.MetadataFile);
        // The tile API (/api/tile-requests) names its files in the metadata, a full job output (/api/jobs) uses the legacy names.
        public string segmentationImagePath => ResolvePath(_metadata?.export?.segmentation?.file, SegmentationApiClient.SegmentationFile, "segmentation_class.tif");
        /// <summary>Optional, only a full job output has it.</summary>
        public string segmentationConfidenceImagePath => Path.Combine(absoluteFolderPath,"segmentation_confidence.png");
        public string colorImagePath => ResolvePath(_metadata?.export?.color?.file, SegmentationApiClient.ColorFile, "sat_color.png");
        public string heightmapImagePath => ResolvePath(_metadata?.export?.height?.file, SegmentationApiClient.HeightFile, "height_meters.npy");

        // Pipeline caches written by TerrainGenerator.RunPipeline.
        public const string HousesCacheFile = "houses.json";
        public const string TreesCacheFile = "trees.json";
        public const string RoadsCacheFile = "roads.json";
        public string housesCachePath => Path.Combine(absoluteFolderPath, HousesCacheFile);
        public string treesCachePath => Path.Combine(absoluteFolderPath, TreesCacheFile);
        public string roadsCachePath => Path.Combine(absoluteFolderPath, RoadsCacheFile);
        public string[] cachePaths => new[] { housesCachePath, treesCachePath, roadsCachePath };
        /// <summary>Folders with this file are tile folders.</summary>
        public static bool IsTileFolder(string folder) => File.Exists(Path.Combine(folder, SegmentationApiClient.MetadataFile));

        
        [SerializeField] private SatMetadata _metadata;
        [SerializeField] [HideInInspector] private TifImage _segmentationImage;
        [SerializeField] [HideInInspector] private Texture2D _segmentationConfidenceImage;
        [SerializeField] [HideInInspector] private Texture2D _colorImage;
        [SerializeField] [HideInInspector] private NpyArray _heightmapImage;
        
        
        public SatMetadata metadata => _metadata;
        public TifImage segmentationImage => _segmentationImage;
        public Texture2D segmentationConfidenceImage => _segmentationConfidenceImage;
        public Texture2D colorImage => _colorImage;
        public NpyArray heightmapImage => _heightmapImage;

        // Image data is not serialized, it is gone after a domain reload.
        public bool hasMetadata => _metadata?.output?.mercatorBoundsM != null && _metadata.imagery != null;
        public bool hasSegmentation => _segmentationImage != null && _segmentationImage.data != null;
        public bool hasHeightmap => _heightmapImage != null && _heightmapImage.data != null && _heightmapImage.shape != null && _heightmapImage.shape.Length >= 2;
        public bool hasColor => _colorImage != null;
        /// <summary>Everything <see cref="CheckPaths"/> requires, the confidence image is optional.</summary>
        public bool essentialsLoaded => hasMetadata && hasSegmentation && hasHeightmap && hasColor;

        private const double earthCircumference = 2 * Math.PI * 6378137.0;

        /// <summary>
        /// Mercator units per pixel, fixed per Web Mercator zoom level (0.597 at zoom 18).
        /// </summary>
        public double scale => earthCircumference / (_metadata.imagery.tileSize * (1L << _metadata.imagery.zoom));

        /// <summary>
        /// Mercator position of the image's top-left corner (pixel 0,0) relative to origin.
        /// </summary>
        public DoubleVector2 GetWorldPos(DoubleVector2 origin)
        {
            double[] bounds = _metadata.output.mercatorBoundsM; // [minX, minY, maxX, maxY]
            return new DoubleVector2(bounds[0], bounds[3]) - origin;
        }
        /// <summary>
        /// Mercator bounds [minX, minY, maxX, maxY] relative to origin.
        /// </summary>
        public double[] GetWorldSpaceBounds(DoubleVector2 origin)
        {
            // Copy, the metadata array must not be modified.
            double[] bounds = (double[])_metadata.output.mercatorBoundsM.Clone();
            bounds[0] -= origin.x;
            bounds[1] -= origin.y;
            bounds[2] -= origin.x;
            bounds[3] -= origin.y;
            return bounds;
        }

        /// <summary>
        /// The file named by the metadata, otherwise the tile API's file name or the legacy one, whichever exists.
        /// </summary>
        private string ResolvePath(string metadataFile, string fileName, string legacyFileName)
        {
            if (!string.IsNullOrEmpty(metadataFile))
                return Path.Combine(absoluteFolderPath, metadataFile);
            string path = Path.Combine(absoluteFolderPath, fileName);
            string legacyPath = Path.Combine(absoluteFolderPath, legacyFileName);
            return File.Exists(path) || !File.Exists(legacyPath) ? path : legacyPath;
        }

        private static Texture2D LoadTexture(string filePath, TextureFormat format = TextureFormat.RGB24)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PNG not found at: {filePath}");

            byte[] bytes = File.ReadAllBytes(filePath);
            var tex = new Texture2D(2, 2, format, false);
            if (!tex.LoadImage(bytes))
                throw new Exception($"Failed to decode PNG at: {filePath}");

            return tex;
        }
        public bool CheckPaths()
        {
            bool result = true;
            string absoluteFolderPath = Path.Combine(Application.dataPath, folderPath);
            if (!Directory.Exists(absoluteFolderPath))
            {
                Logger.Logger.LogError($"Folder doesn't exist: {absoluteFolderPath}", "ProcGen", this);
                return false;
            }
            Logger.Logger.Log($"Folder path found: {absoluteFolderPath}", "ProcGen", this);
            
            
            if (!File.Exists(metadataPath))
            {
                Logger.Logger.LogError($"Metadata file doesn't exist: {metadataPath}", "ProcGen", this);
                result = false;
            }
            else
                Logger.Logger.Log($"Metadata file found: {metadataPath}", "ProcGen", this);
            
            
            if (!File.Exists(segmentationImagePath))
            {
                Logger.Logger.LogError($"Segmentation image doesn't exist: {segmentationImagePath}", "ProcGen", this);
                result = false;
            }
            else
                Logger.Logger.Log($"Segmentation image file found: {segmentationImagePath}", "ProcGen", this);
            
            
            // Optional, a missing confidence image does not fail the check.
            if (!File.Exists(segmentationConfidenceImagePath))
                Logger.Logger.Log($"Segmentation confidence image doesn't exist (optional): {segmentationConfidenceImagePath}", "ProcGen", this);
            else 
                Logger.Logger.Log($"Segmentation confidence image file found: {segmentationConfidenceImagePath}", "ProcGen", this);
            
            
            if (!File.Exists(colorImagePath))
            {
                Logger.Logger.LogError($"Color image doesn't exist: {colorImagePath}", "ProcGen", this);
                result = false;
            }
            else 
                Logger.Logger.Log($"Color image file found: {colorImagePath}", "ProcGen", this);
            
            
            if (!File.Exists(heightmapImagePath))
            {
                Logger.Logger.LogError($"Heightmap doesn't exist: {heightmapImagePath}", "ProcGen", this);
                result = false;
            }
            else 
                Logger.Logger.Log($"Heightmap file found: {heightmapImagePath}", "ProcGen", this);
            
            
            return result;
        }
        public void LoadData()
        {
            // Metadata first, it names the other files.
            if (File.Exists(metadataPath))
                _metadata = SatMetadata.Load(metadataPath);
            if (!CheckPaths())
            {
                Logger.Logger.LogError("CheckPaths failed", "ProcGen", this);
                return;
            }
            
            _segmentationImage = TiffReader.Load(segmentationImagePath);
            _heightmapImage = NpyReader.Load(heightmapImagePath);
            _colorImage = LoadTexture(colorImagePath);
            _segmentationConfidenceImage = File.Exists(segmentationConfidenceImagePath)
                ? LoadTexture(segmentationConfidenceImagePath, TextureFormat.R8)
                : null;
        }

        /// <summary>Loads only metadata.json, the images are loaded by <see cref="LoadData"/>.</summary>
        public void LoadMetadata()
        {
            _metadata = SatMetadata.Load(metadataPath);
        }

        /// <summary>
        /// Drops the images from memory, the metadata and the files on disk are kept. <see cref="LoadData"/> brings them back.
        /// </summary>
        public void UnloadData()
        {
            if (_colorImage != null)
                DestroyImmediate(_colorImage);
            if (_segmentationConfidenceImage != null)
                DestroyImmediate(_segmentationConfidenceImage);
            _colorImage = null;
            _segmentationConfidenceImage = null;
            _segmentationImage = null;
            _heightmapImage = null;
        }

        /// <summary>
        /// Deletes the pipeline caches (<see cref="cachePaths"/>) and their .meta files, nothing else in the folder.
        /// </summary>
        /// <returns>Number of deleted caches.</returns>
        public int ClearCache() => ClearCache(absoluteFolderPath);

        /// <summary>
        /// Deletes the pipeline caches in <paramref name="folder"/> (absolute) and their .meta files, nothing else in the folder.
        /// </summary>
        /// <returns>Number of deleted caches.</returns>
        public static int ClearCache(string folder)
        {
            int deleted = 0;
            foreach (string file in new[] { HousesCacheFile, TreesCacheFile, RoadsCacheFile })
            {
                string path = Path.Combine(folder, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                    Logger.Logger.Log($"Deleted cache {path}", "ProcGen");
                }
                string metaPath = path + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
            }
            return deleted;
        }
    }
}