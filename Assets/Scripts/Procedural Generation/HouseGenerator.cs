using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using UnityEngine;

namespace ProceduralGeneration
{
    public class HouseGenerator : MonoBehaviour
    {
        [SerializeField] private SatContext.SatContext _satContext;
        [Tooltip("Leave empty to use the GlobalConfig in the scene.")]
        [SerializeField] private GeometryConfig _geometryConfig;
        private GeometryConfig geometryConfig => GlobalConfig.ResolveGeometry(_geometryConfig);

        [Header("Detection")]
        [Tooltip("Segmentation id of building pixels (\"building\" class).")]
        [SerializeField] private int _buildingSegID = 8;
        [Tooltip("Houses with fewer pixels than this are discarded as noise.")]
        [Min(1)]
        [SerializeField] private int _minPixels = 20;
        [Tooltip("Diagonal pixels count as adjacent (8-connectivity), otherwise only edges do (4-connectivity).")]
        [SerializeField] private bool _diagonalAdjacency = false;

        [Header("Buildings")]
        [Tooltip("Prefab with a ProceduralBuildingDrawer, instantiated once per house.")]
        [SerializeField] private ProceduralBuildingDrawer _buildingPrefab;
        [Tooltip("Floor count per building including the roof floor, both ends inclusive.")]
        [SerializeField] private Vector2Int _floorRange = new Vector2Int(2, 3);
        [SerializeField] private int _seed = 0;
        // Serialized, unlike the houses, so the buildings can still be destroyed after a domain reload.
        [SerializeField, HideInInspector] private List<ProceduralBuildingDrawer> _buildings = new List<ProceduralBuildingDrawer>();

        [Header("Gizmos")]
        [SerializeField] private Color _gizmoColor = new Color(0.9f, 0.2f, 0.15f);
        [Tooltip("Height of the drawn boxes, in Unity units.")]
        [Min(0f)]
        [SerializeField] private float _gizmoHeight = 5f;

        /// <summary>
        /// Oriented bounding box of one house, in Unity space. The box sits on <see cref="center"/>.y.
        /// </summary>
        public struct House
        {
            public Vector3 center;
            // Rotation around Y, local x is the principal (longest) axis.
            public Quaternion rotation;
            // Local x and z extents, full size not half.
            public Vector2 size;
            public int pixelCount;

            public bool Overlaps(Vector2 point)
            {
                Vector3 dir1 = rotation * Vector3.right;
                Vector3 dir2 = rotation * Vector3.forward;

                Vector2 dir1Flat = new Vector2(dir1.x, dir1.z);
                Vector2 dir2Flat = new Vector2(dir2.x, dir2.z);

                Vector2 cornerToCenter = (dir1Flat * size.x + dir2Flat * size.y) * 0.5f;

                Vector2 corner = new Vector2(center.x, center.z) - cornerToCenter;
                Vector2 local = point - corner;

                float fac1 = Vector2.Dot(local, dir1Flat);
                float fac2 = Vector2.Dot(local, dir2Flat);

                return fac1 >= 0 && fac1 <= size.x &&
                       fac2 >= 0 && fac2 <= size.y;
            }
        }

        // Not serialized, like the sat images it is gone after a domain reload.
        private readonly List<House> _houses = new List<House>();
        // Each house is 12 edges, 24 points.
        private Vector3[] _gizmoLines;

        public IReadOnlyList<House> houses => _houses;
        public IReadOnlyList<ProceduralBuildingDrawer> buildings => _buildings;


        public bool OverlapsHouses(Vector2 point)
        {
            foreach (var house in houses)
            {
                if (house.Overlaps(point)) return true;
            }
            return false;
        }
        /// <summary>
        /// Splits building pixels into connected components, discards the ones smaller than <see cref="_minPixels"/>
        /// and fits an oriented bounding box to each remaining one with PCA.
        /// </summary>
        public void FindHouses()
        {
            if (_satContext == null)
            {
                Logger.Logger.LogError($"Sat context is null", "ProcGen", this);
                return;
            }
            if (geometryConfig == null)
            {
                Logger.Logger.LogError($"Geometry config is null, assign one or add a GlobalConfig to the scene", "ProcGen", this);
                return;
            }
            var segmentation = _satContext.segmentationImage;
            // Image data is not serialized, it is gone after a domain reload.
            if (segmentation == null || segmentation.data == null)
            {
                Logger.Logger.LogError($"Segmentation image is not loaded", "ProcGen", this);
                return;
            }
            var heightmap = _satContext.heightmapImage;
            bool hasHeightmap = heightmap != null && heightmap.data != null && heightmap.shape != null && heightmap.shape.Length >= 2;
            if (!hasHeightmap)
                Logger.Logger.LogWarning($"Heightmap is not loaded, houses are placed at height 0", "ProcGen", this);

            int imageWidth = segmentation.width;
            int imageHeight = segmentation.height;

            // Same pixel -> world mapping as TerrainGenerator.CreateMeshForContext.
            double[] bounds = _satContext.GetWorldSpaceBounds(geometryConfig.origin);
            double pixelSizeX = (bounds[2] - bounds[0]) / imageWidth;
            double pixelSizeY = (bounds[3] - bounds[1]) / imageHeight;
            float heightScale = geometryConfig.GetMeterScale();

            int[] offsetsX = _diagonalAdjacency ? new[] { 1, -1, 0, 0, 1, 1, -1, -1 } : new[] { 1, -1, 0, 0 };
            int[] offsetsY = _diagonalAdjacency ? new[] { 0, 0, 1, -1, 1, -1, 1, -1 } : new[] { 0, 0, 1, -1 };

            bool[] visited = new bool[(long)imageWidth * imageHeight];
            // Pixel indices of the current house, also used as the flood fill queue.
            var pixels = new List<int>();
            int discarded = 0;

            _houses.Clear();
            for (int start = 0; start < visited.Length; start++)
            {
                if (visited[start] || segmentation.data[start * segmentation.channels] != _buildingSegID)
                    continue;

                // Breadth first flood fill, the list grows while it is walked.
                pixels.Clear();
                pixels.Add(start);
                visited[start] = true;
                for (int head = 0; head < pixels.Count; head++)
                {
                    int x = pixels[head] % imageWidth;
                    int y = pixels[head] / imageWidth;
                    for (int i = 0; i < offsetsX.Length; i++)
                    {
                        int nx = x + offsetsX[i];
                        int ny = y + offsetsY[i];
                        if (nx < 0 || ny < 0 || nx >= imageWidth || ny >= imageHeight)
                            continue;
                        int neighbour = ny * imageWidth + nx;
                        if (visited[neighbour] || segmentation.Get(nx, ny) != _buildingSegID)
                            continue;
                        visited[neighbour] = true;
                        pixels.Add(neighbour);
                    }
                }

                if (pixels.Count < _minPixels)
                {
                    discarded++;
                    continue;
                }

                FitBox(pixels, imageWidth, out Vector2 center, out float angle, out Vector2 size);

                float height = hasHeightmap ? heightScale * TreeGenerator.SampleHeight(heightmap, center.x / imageWidth, center.y / imageHeight) : 0;
                _houses.Add(new House
                {
                    // Image y goes down so unity z goes negative.
                    center = new Vector3(
                        (float)((bounds[0] + center.x * pixelSizeX) * geometryConfig.scale),
                        height,
                        (float)((bounds[3] - center.y * pixelSizeY) * geometryConfig.scale)),
                    // Flipping y turns the image's clockwise angle into Unity's clockwise-from-above yaw,
                    // yaw a maps local x to (cos a, 0, -sin a), which is the image axis (cos a, sin a) with y flipped.
                    rotation = Quaternion.Euler(0, angle * Mathf.Rad2Deg, 0),
                    size = new Vector2(
                        (float)(size.x * pixelSizeX * geometryConfig.scale),
                        (float)(size.y * pixelSizeY * geometryConfig.scale)),
                    pixelCount = pixels.Count,
                });
            }

            _gizmoLines = null;

            Logger.Logger.Log($"Found {_houses.Count.ToString()} houses, discarded {discarded.ToString()} smaller than {_minPixels} pixels", "ProcGen");
        }

        /// <summary>
        /// PCA box fit in pixel space. The principal axis is the eigenvector of the pixel covariance with the largest
        /// eigenvalue, the box is the pixel squares' extent along it and its perpendicular.
        /// </summary>
        /// <param name="angle">Principal axis angle in radians, from image x towards image y.</param>
        /// <param name="size">Box size along the principal axis (x) and the perpendicular one (y).</param>
        private static void FitBox(List<int> pixels, int imageWidth, out Vector2 center, out float angle, out Vector2 size)
        {
            // Doubles, the sums of squares lose precision in floats for large houses.
            double meanX = 0, meanY = 0;
            foreach (int pixel in pixels)
            {
                // Pixel centers are at .5.
                meanX += pixel % imageWidth + 0.5;
                meanY += pixel / imageWidth + 0.5;
            }
            meanX /= pixels.Count;
            meanY /= pixels.Count;

            double covXX = 0, covXY = 0, covYY = 0;
            foreach (int pixel in pixels)
            {
                double dx = pixel % imageWidth + 0.5 - meanX;
                double dy = pixel / imageWidth + 0.5 - meanY;
                covXX += dx * dx;
                covXY += dx * dy;
                covYY += dy * dy;
            }

            // Closed form eigenvector angle of the symmetric 2x2 covariance, the 1/n factor cancels out.
            // Isotropic shapes (squares, single pixels) give atan2(0, 0) = 0, an axis aligned box.
            double theta = 0.5 * Math.Atan2(2 * covXY, covXX - covYY);
            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (int pixel in pixels)
            {
                double dx = pixel % imageWidth + 0.5 - meanX;
                double dy = pixel / imageWidth + 0.5 - meanY;
                double u = dx * cos + dy * sin;
                double v = -dx * sin + dy * cos;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            // Projected pixel centers miss half a pixel on each side, a unit square projects to |cos| + |sin| on both axes.
            double pad = 0.5 * (Math.Abs(cos) + Math.Abs(sin));
            minU -= pad;
            maxU += pad;
            minV -= pad;
            maxV += pad;

            // Box center is the middle of the extents, not the mean, since shapes are not symmetric.
            double midU = (minU + maxU) / 2;
            double midV = (minV + maxV) / 2;
            center = new Vector2(
                (float)(meanX + midU * cos - midV * sin),
                (float)(meanY + midU * sin + midV * cos));
            angle = (float)theta;
            size = new Vector2((float)(maxU - minU), (float)(maxV - minV));
        }

        /// <summary>
        /// Instantiates <see cref="_buildingPrefab"/> on every house box, replacing the previously generated buildings.
        /// </summary>
        public void GenerateBuildings()
        {
            if (_buildingPrefab == null)
            {
                Logger.Logger.LogError($"Building prefab is null", "ProcGen", this);
                return;
            }
            if (geometryConfig == null)
            {
                Logger.Logger.LogError($"Geometry config is null, assign one or add a GlobalConfig to the scene", "ProcGen", this);
                return;
            }
            if (_houses.Count == 0)
            {
                Logger.Logger.LogWarning($"No houses, run Find Houses first", "ProcGen", this);
                return;
            }

            DestroyBuildings();

            // Building modules are in metres, the scale brings them to Unity units so the box size is passed in metres.
            float meterScale = geometryConfig.GetMeterScale();
            // Own generator, the drawers reseed UnityEngine.Random on every rebuild.
            var random = new System.Random(_seed);
            int minFloors = Mathf.Max(1, Mathf.Min(_floorRange.x, _floorRange.y));
            int maxFloors = Mathf.Max(1, Mathf.Max(_floorRange.x, _floorRange.y));

            for (int i = 0; i < _houses.Count; i++)
            {
                House house = _houses[i];
                ProceduralBuildingDrawer building = InstantiateBuilding();
                building.name = $"Building {i.ToString()}";
                building.transform.SetPositionAndRotation(house.center, house.rotation);
                building.transform.localScale = Vector3.one * meterScale;
                // Keeps the world scale, the generator itself may be scaled.
                building.transform.SetParent(transform, true);

                building.floors = random.Next(minFloors, maxFloors + 1);
                building.seed = random.Next();
                building.setRectangle(house.size / meterScale);
                _buildings.Add(building);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
            Logger.Logger.Log($"Generated {_buildings.Count.ToString()} buildings", "ProcGen");
        }

        private ProceduralBuildingDrawer InstantiateBuilding()
        {
#if UNITY_EDITOR
            // Keeps the prefab link in edit mode, so changes to the prefab reach every building.
            if (!Application.isPlaying && PrefabUtility.IsPartOfPrefabAsset(_buildingPrefab))
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(_buildingPrefab.gameObject);
                return instance.GetComponent<ProceduralBuildingDrawer>();
            }
#endif
            return Instantiate(_buildingPrefab);
        }

        public void DestroyBuildings()
        {
            foreach (var building in _buildings)
            {
                // Already gone if it was deleted by hand.
                if (building == null)
                    continue;
#if UNITY_EDITOR
                DestroyImmediate(building.gameObject);
#else
                Destroy(building.gameObject);
#endif
            }
            _buildings.Clear();

#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        }

        private void OnDrawGizmosSelected()
        {
            if (_houses.Count == 0)
                return;

            // One line list for all boxes, stays fast for large house counts.
            if (_gizmoLines == null || _gizmoLines.Length != _houses.Count * 24)
            {
                _gizmoLines = new Vector3[_houses.Count * 24];
                var corners = new Vector3[8];
                for (int i = 0; i < _houses.Count; i++)
                {
                    House house = _houses[i];
                    Vector3 halfX = house.rotation * Vector3.right * (house.size.x / 2);
                    Vector3 halfZ = house.rotation * Vector3.forward * (house.size.y / 2);
                    Vector3 up = Vector3.up * _gizmoHeight;
                    // Bottom ring 0-3, top ring 4-7.
                    corners[0] = house.center - halfX - halfZ;
                    corners[1] = house.center + halfX - halfZ;
                    corners[2] = house.center + halfX + halfZ;
                    corners[3] = house.center - halfX + halfZ;
                    for (int c = 0; c < 4; c++)
                        corners[c + 4] = corners[c] + up;

                    int line = i * 24;
                    for (int c = 0; c < 4; c++)
                    {
                        int next = (c + 1) % 4;
                        _gizmoLines[line++] = corners[c];
                        _gizmoLines[line++] = corners[next];
                        _gizmoLines[line++] = corners[c + 4];
                        _gizmoLines[line++] = corners[next + 4];
                        _gizmoLines[line++] = corners[c];
                        _gizmoLines[line++] = corners[c + 4];
                    }
                }
            }
            Gizmos.color = _gizmoColor;
            Gizmos.DrawLineList(_gizmoLines);
        }

        private void OnValidate()
        {
            // Rebuild the gizmo lines with the new height.
            _gizmoLines = null;
        }
    }
}
