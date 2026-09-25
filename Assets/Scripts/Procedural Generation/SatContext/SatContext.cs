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
        public string metadataPath => Path.Combine(absoluteFolderPath, "metadata.json");
        public string segmentationImagePath => Path.Combine(absoluteFolderPath,"segmentation_class.tif");
        public string segmentationConfidenceImagePath => Path.Combine(absoluteFolderPath,"segmentation_confidence.png");
        public string colorImagePath => Path.Combine(absoluteFolderPath,"sat_color.png");
        public string heightmapImagePath => Path.Combine(absoluteFolderPath,"height_meters.npy");

        
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
            
            
            if (!File.Exists(segmentationConfidenceImagePath))
            {
                Logger.Logger.LogError($"Segmentation confidence image doesn't exist: {segmentationConfidenceImagePath}", "ProcGen", this);
                result = false;
            }
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
        public void LoadData(){
            if (!CheckPaths())
            {
                Logger.Logger.LogError("CheckPaths failed", "ProcGen", this);
                return;
            }
            
            _metadata = SatMetadata.Load(metadataPath);
            _segmentationImage = TiffReader.Load(segmentationImagePath);
            _heightmapImage = NpyReader.Load(heightmapImagePath);
            _colorImage = LoadTexture(colorImagePath);
            _segmentationConfidenceImage = LoadTexture(segmentationConfidenceImagePath, TextureFormat.R8);
        }
    }
}