using System.Collections.Generic;
using IMT.Thermal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
#endif
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;


/// <summary>
/// Builds a house on a rectangle centered on the transform. Everything is generated in local space, the transform
/// places, rotates and scales the building, local x is <see cref="size"/>.x and local z is <see cref="size"/>.y.
/// </summary>
[ExecuteInEditMode]
public class ProceduralBuildingDrawer : MonoBehaviour
{
    public const float ROOF_OVERHANG = 0.2f;
    public const float MIN_SIZE = 0.5f;

    public enum RoofType { Auto, Hip, AFrame }

    [Header("Settings")]
    public bool showOutlines;
    [Tooltip("Footprint rectangle, local x and z, full size not half.")]
    public Vector2 size = new Vector2(10f, 6f);
    [Tooltip("Floor count, the last one is the roof.")]
    public int floors;
    public int seed;
    public bool newRandomPerRule;
    public BuildingAsset buildingAsset;
    public bool populateInstances = false;

    [Header("Roof")]
    public RoofType roofType = RoofType.Auto;
    [Tooltip("Used when overrideRoofForDebug is true.")]
    public RoofType debugRoofType = RoofType.Hip;
    [Tooltip("If true, overrides roofType with debugRoofType. For testing only.")]
    public bool overrideRoofForDebug = false;

    [Header("Thermal")]
    [Tooltip("Thermal properties for the whole building. Stands in for a ThermalObject, which needs a Renderer this " +
             "drawer does not have. Empty = the thermal shader's defaults.")]
    public ThermalMaterial thermalMaterial;

    [HideInInspector]
    public Dictionary<OutlineType, Outline> outlines = new Dictionary<OutlineType, Outline>();
    [HideInInspector]
    public Dictionary<BuildingModule, BuildingModuleInstances> modulesDictionary;

    // Generated at runtime, never saved with the scene.
    Mesh roofMeshSideBottom;
    Mesh roofMeshSideTop;
    // True when roofMeshSideTop contains the A-frame gable wall (render with aFrameGableMaterial).
    bool roofMeshSideTopIsGable = false;
    List<BuildingMeshInstances> instances = new List<BuildingMeshInstances>();

    // Module matrices are local, these are them in world space for the transform they were computed with.
    readonly Dictionary<BuildingModule, Matrix4x4[]> worldMatrices = new Dictionary<BuildingModule, Matrix4x4[]>();
    Matrix4x4 worldMatricesLocalToWorld;
    bool worldMatricesDirty = true;

    MaterialPropertyBlock thermalBlock;

    /// <summary>
    /// Sets the footprint rectangle and rebuilds the building.
    /// </summary>
    public void setRectangle(Vector2 newSize)
    {
        size = newSize;
        updateAll();
    }

    public void clearAll()
    {
        outlines.Clear();
        clearMatrices();
        clearMeshes();
    }

    void clearMeshes()
    {
        if (roofMeshSideBottom != null) roofMeshSideBottom.Clear();
        if (roofMeshSideTop != null) roofMeshSideTop.Clear();
        roofMeshSideTopIsGable = false;
        worldMatricesDirty = true;
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        Undo.undoRedoPerformed += updateAll;
#endif
        // Nothing generated is serialized, rebuild after instantiation, scene load and domain reload.
        updateAll();
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        Undo.undoRedoPerformed -= updateAll;
#endif
    }

    void OnDestroy()
    {
        destroyMesh(roofMeshSideBottom);
        destroyMesh(roofMeshSideTop);
    }

    static void destroyMesh(Mesh mesh)
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh);
        else DestroyImmediate(mesh);
    }

    void OnValidate()
    {
        size = Vector2.Max(size, new Vector2(MIN_SIZE, MIN_SIZE));
        // Prefab assets are never rendered, their meshes would only leak.
        if (!gameObject.scene.IsValid()) return;
        updateAll();
    }

    public void updateSections()
    {
        float x0 = -size.x / 2f;
        float x1 = size.x / 2f;
        float z0 = -size.y / 2f;
        float z1 = size.y / 2f;

        Vector3 o0 = new Vector3(x0, 0f, z0);
        Vector3 o1 = new Vector3(x1, 0f, z0);
        Vector3 o2 = new Vector3(x1, 0f, z1);
        Vector3 o3 = new Vector3(x0, 0f, z1);

        var sectionList = new List<List<Vector3>>();
        sectionList.Add(new List<Vector3> { o1, o0 }); // south
        sectionList.Add(new List<Vector3> { o0, o3 }); // west
        sectionList.Add(new List<Vector3> { o3, o2 }); // north
        sectionList.Add(new List<Vector3> { o2, o1 }); // east

        var capList = new List<List<Vector3>>();

        outlines[OutlineType.General] = new Outline(sectionList, capList);
        outlines[OutlineType.LastFloors] = new Outline(sectionList, capList);
        outlines[OutlineType.Terrace] = new Outline(sectionList, capList);
    }

    public void updateAll()
    {
        if (buildingAsset == null)
        {
            clearAll();
            return;
        }
        updateSections();
        createMatrices();
        populateMatrices();
    }

    void createMatrices()
    {
        var modules = buildingAsset.getModules();
        modulesDictionary = new Dictionary<BuildingModule, BuildingModuleInstances>(modules.Length);

        foreach (var module in modules)
        {
            modulesDictionary[module] = new BuildingModuleInstances();
        }
    }

    void clearMatrices()
    {
        if (modulesDictionary != null)
        {
            foreach (var md in modulesDictionary)
            {
                md.Value.clear();
            }
        }
    }

    ProceduralBuildingRule computeRule(int floor, int maxFloor)
    {
        if (floor > 1)
        {
            return ProceduralBuildingRulesHelper.computeFallbackRule(buildingAsset, floor, maxFloor, true);
        }
        return ProceduralBuildingRulesHelper.computeDoorRule(buildingAsset, floor, maxFloor, true);
    }

    public void populateMatrices()
    {
        if (buildingAsset == null || !outlines.ContainsKey(OutlineType.General) || !outlines.ContainsKey(OutlineType.LastFloors))
        {
            return;
        }

        clearMatrices();
        clearMeshes();

        // Local space, the ground is at the transform.
        float floorH = 0f;
        Random.InitState(seed);

        RoofType effectiveRoofType;
        if (overrideRoofForDebug)
        {
            effectiveRoofType = debugRoofType;
        }
        else if (roofType == RoofType.Auto)
        {
            effectiveRoofType = Random.value < 0.5f ? RoofType.Hip : RoofType.AFrame;
        }
        else
        {
            effectiveRoofType = roofType;
        }

        for (int floor = 0; floor < floors; floor++)
        {
            var currentRule = computeRule(floor, floors);
            var wallModule = buildingAsset.getWallOf(floor, floors);
            var columnModule = buildingAsset.getColumnOf(floor, floors);
            int currentFloorID = 0;
            bool isTop = floors - floor <= 2;
            bool isTopRoof = floors - floor <= 1;

            var currentSections = outlines[OutlineType.General].sections;
            if (isTop)
            {
                currentSections = outlines[OutlineType.LastFloors].sections;
            }

            if (!isTopRoof)
            {
                for (int j = 0; j < currentSections.Count; j++)
                {
                    var section = currentSections[j];
                    float sectionLen = ProceduralBuildingUtils.lineLenght(section);
                    float sectionMinusColumn = sectionLen - columnModule.getWidth();

                    float currentS = 0f;
                    currentS = fillWithFirstColumn(columnModule, section, sectionLen, floor, currentFloorID, floorH, currentS);
                    currentFloorID += 1;

                    bool skipEndColumn = true;

                    if (sectionMinusColumn > columnModule.getWidth())
                    {
                        if (sectionMinusColumn < currentRule.lenght)
                        {
                            currentFloorID += fillWithWalls(wallModule, columnModule, section, sectionLen, floor, currentFloorID, floorH, currentS, false, skipEndColumn);
                        }
                        else
                        {
                            int fullRules = Mathf.FloorToInt(sectionMinusColumn / currentRule.lenght);
                            float usedSpace = fullRules * currentRule.lenght;
                            float remainingSpace = sectionMinusColumn - usedSpace;

                            bool shouldStretch = remainingSpace < wallModule.getWidth();
                            float stretchPer = 0f;

                            if (shouldStretch)
                            {
                                if (currentRule.stretchables > 0)
                                {
                                    stretchPer = remainingSpace / currentRule.stretchables;
                                }
                            }

                            for (int r = 0; r < fullRules; r++)
                            {
                                if (currentS + currentRule.lenght < sectionLen - columnModule.getWidth())
                                {
                                    if (newRandomPerRule)
                                    {
                                        currentRule = computeRule(floor, floors);
                                    }
                                    currentS = fillWithRule(currentRule, section, sectionLen, floor, currentFloorID, floorH, stretchPer, currentS);
                                    currentFloorID += currentRule.elements.Count;
                                }
                            }

                            remainingSpace = (sectionLen - columnModule.getWidth()) - currentS;
                            shouldStretch = remainingSpace < columnModule.getWidth();

                            if (!shouldStretch)
                            {
                                currentFloorID += fillWithWalls(wallModule, columnModule, section, sectionLen, floor, currentFloorID, floorH, currentS, false, skipEndColumn);
                            }
                        }
                    }
                }

                var currentCaps = outlines[OutlineType.General].caps;
                if (isTop)
                {
                    currentCaps = outlines[OutlineType.LastFloors].caps;
                }

                for (int j = 0; j < currentCaps.Count; j++)
                {
                    var cap = currentCaps[j];
                    currentFloorID += fillCap(wallModule, cap, floor, currentFloorID, floorH);
                }

                floorH += wallModule.getHeight();
            }
            else
            {
                createRoof(wallModule, currentSections, outlines[OutlineType.LastFloors].caps, floor, floorH, currentFloorID, effectiveRoofType);
            }
        }

        buildSerializableInstances();
    }

    Matrix4x4 createModuleMatrix(Vector3 p1, Vector3 p2, float height, float widthScale, float offset = 0, float depthScale = 1)
    {
        Vector3 t = (p1 + p2) / 2f;
        t.y = height;
        Vector3 forward = (p2 - p1).normalized;
        Vector3 outside = (Quaternion.Euler(0, -90, 0) * forward).normalized;
        t += outside * offset;
        Quaternion r = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(0, -90, 0);
        Vector3 s = new Vector3(widthScale, 1, depthScale);
        return Matrix4x4.TRS(t, r, s);
    }

    float fillWithRule(ProceduralBuildingRule rule, List<Vector3> section, float sectionLen, int floor, int startFloorID, float floorH, float stretchPer, float startS = 0)
    {
        float currentS = startS;

        for (int n = 0; n < rule.elements.Count; n++)
        {
            var module = rule.elements[n];
            float currentWidth = module.getWidth();
            if (module.canStretch)
            {
                currentWidth += stretchPer;
            }

            Vector3 p1 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, currentS);
            Vector3 p2 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, currentS + currentWidth);

            Vector3 center = (p1 + p2) / 2;
            center.y = floorH + module.getHeight() / 2;
            modulesDictionary[module].add(
                createModuleMatrix(p1, p2, floorH, currentWidth / module.getWidth()),
                new BuildingModuleInfo(center, floor, startFloorID + n)
            );

            currentS += currentWidth;
        }

        return currentS;
    }

    int fillWithWalls(BuildingModule wallModule, BuildingModule columnModule, List<Vector3> section, float sectionLen, int floor, int startFloorID, float floorH, float startS = 0, bool verbose = false, bool skipEndColumn = false)
    {
        float availableSpace = sectionLen - startS;

        int wallCount = 0;
        float probe = 0f;
        while (true)
        {
            float nextWidth = GetWallSlotWidth(floor, wallCount, wallModule);
            if (probe + nextWidth + (skipEndColumn ? 0f : columnModule.getWidth()) > availableSpace) break;
            probe += nextWidth;
            wallCount++;
            if (wallCount > 512) break;
        }
        wallCount = Mathf.Max(1, wallCount);

        float used = probe + (skipEndColumn ? 0f : columnModule.getWidth());
        float remaining = availableSpace - used;
        float stretchPer = wallCount > 0 ? remaining / wallCount : 0f;
        if (stretchPer < 0f) stretchPer = 0f;

        float currentS = startS;

        int totalIterations = skipEndColumn ? wallCount : wallCount + 1;

        for (int i = 0; i < totalIterations; i++)
        {
            bool isEndColumn = (i == wallCount && !skipEndColumn);

            BuildingModule module = isEndColumn
                ? columnModule
                : buildingAsset.getWallOf(floor, floors, /* useVariant: */ false, false, (i % 2 == 1));

            float currentWidth = isEndColumn ? module.getWidth() : module.getWidth() + stretchPer;

            Vector3 p1 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, currentS, verbose);
            Vector3 p2 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, currentS + currentWidth, verbose);
            Vector3 center = (p1 + p2) / 2;
            center.y = floorH + module.getHeight() / 2;

            modulesDictionary[module].add(
                createModuleMatrix(p1, p2, floorH, currentWidth / module.getWidth()),
                new BuildingModuleInfo(center, floor, startFloorID + i)
            );

            currentS += currentWidth;
        }

        return wallCount;
    }

    float GetWallSlotWidth(int floor, int slot, BuildingModule fallback)
    {
        var m = buildingAsset.getWallOf(floor, floors, /* useVariant: */ false, false, (slot % 2 == 1));
        return m != null ? m.getWidth() : fallback.getWidth();
    }

    void createRoof(BuildingModule poleModule, List<List<Vector3>> sections, List<List<Vector3>> caps, int floor, float floorH, int startFloorID, RoofType type)
    {
        List<Vector3> cornersList = new List<Vector3>();
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Count >= 1)
            {
                Vector3 a = s[0]; a.y = floorH;
                cornersList.Add(a);
            }
        }
        if (cornersList.Count < 3) return;

        for (int i = 0; i < caps.Count; i++)
        {
            var c = caps[i];
            for (int k = 0; k < c.Count; k++)
            {
                Vector3 p = c[k]; p.y = floorH;
                cornersList.Add(p);
            }
        }

        Vector3 minB = cornersList[0];
        Vector3 maxB = cornersList[0];
        for (int i = 1; i < cornersList.Count; i++)
        {
            minB = Vector3.Min(minB, cornersList[i]);
            maxB = Vector3.Max(maxB, cornersList[i]);
        }
        minB -= new Vector3(ROOF_OVERHANG, 0f, ROOF_OVERHANG);
        maxB += new Vector3(ROOF_OVERHANG, 0f, ROOF_OVERHANG);

        float y = floorH;
        Vector3 c0 = new Vector3(minB.x, y, minB.z);
        Vector3 c1 = new Vector3(maxB.x, y, minB.z);
        Vector3 c2 = new Vector3(maxB.x, y, maxB.z);
        Vector3 c3 = new Vector3(minB.x, y, maxB.z);

        Vector3[] wallCorners = new Vector3[] {
            new Vector3(minB.x + ROOF_OVERHANG, floorH, minB.z + ROOF_OVERHANG),
            new Vector3(maxB.x - ROOF_OVERHANG, floorH, minB.z + ROOF_OVERHANG),
            new Vector3(maxB.x - ROOF_OVERHANG, floorH, maxB.z - ROOF_OVERHANG),
            new Vector3(minB.x + ROOF_OVERHANG, floorH, maxB.z - ROOF_OVERHANG),
        };

        Vector3 centerXZ = new Vector3((minB.x + maxB.x) * 0.5f, y, (minB.z + maxB.z) * 0.5f);
        float sizeX = maxB.x - minB.x;
        float sizeZ = maxB.z - minB.z;

        // Roof height scales with the short side, so narrow houses get flatter roofs.
        float apexH = floorH + buildingAsset.topPointVerticalOffset * Mathf.Min(size.x, size.y) / 5f;

        Vector3 ridgeA, ridgeB;
        if (type == RoofType.Hip)
        {
            if (sizeX >= sizeZ)
            {
                float inset = sizeZ * 0.5f;
                ridgeA = new Vector3(minB.x + inset, apexH, centerXZ.z);
                ridgeB = new Vector3(maxB.x - inset, apexH, centerXZ.z);
            }
            else
            {
                float inset = sizeX * 0.5f;
                ridgeA = new Vector3(centerXZ.x, apexH, minB.z + inset);
                ridgeB = new Vector3(centerXZ.x, apexH, maxB.z - inset);
            }
        }
        else
        {
            if (sizeX >= sizeZ)
            {
                ridgeA = new Vector3(minB.x, apexH, centerXZ.z);
                ridgeB = new Vector3(maxB.x, apexH, centerXZ.z);
            }
            else
            {
                ridgeA = new Vector3(centerXZ.x, apexH, minB.z);
                ridgeB = new Vector3(centerXZ.x, apexH, maxB.z);
            }
        }

        Vector3[] corners = new Vector3[] { c0, c1, c2, c3 };
        Vector3[] cornerOutward = new Vector3[] {
            new Vector3(-1f, 0f, -1f).normalized,
            new Vector3( 1f, 0f, -1f).normalized,
            new Vector3( 1f, 0f,  1f).normalized,
            new Vector3(-1f, 0f,  1f).normalized,
        };

        int floorIDCount = startFloorID;

        List<Vector3> longVerts = new List<Vector3>();
        List<Vector2> longUvs = new List<Vector2>();
        List<int> longTris = new List<int>();

        List<Vector3> shortVerts = new List<Vector3>();
        List<Vector2> shortUvs = new List<Vector2>();
        List<int> shortTris = new List<int>();

        float uScale = 1f;
        float vScale = 1f;

        // Unity front faces are clockwise, their normal is cross(b - a, c - a). Every roof face points away from the
        // roof center, so the helpers below flip the winding themselves instead of relying on the corner order.
        bool FacesOutward(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            Vector3 outward = (a + b + c) / 3f - centerXZ;
            return Vector3.Dot(normal, outward) >= 0f;
        }

        void AddTrapezoid(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                  Vector3 baseA, Vector3 baseB, Vector3 topA, Vector3 topB)
        {
            if (!FacesOutward(baseA, baseB, topB))
            {
                (baseA, baseB) = (baseB, baseA);
                (topA, topB) = (topB, topA);
            }

            int startIdx = verts.Count;

            float uSlopeA = Vector3.Distance(baseA, topA) * uScale;
            float uSlopeB = Vector3.Distance(baseB, topB) * uScale;
            float uSlope = (uSlopeA + uSlopeB) * 0.5f;

            float vBase = Vector3.Distance(baseA, baseB) * vScale;

            verts.Add(baseA);
            verts.Add(baseB);
            verts.Add(topB);
            verts.Add(topA);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(0f, vBase));
            uvs.Add(new Vector2(uSlope, vBase));
            uvs.Add(new Vector2(uSlope, 0f));

            tris.Add(startIdx);
            tris.Add(startIdx + 1);
            tris.Add(startIdx + 2);

            tris.Add(startIdx);
            tris.Add(startIdx + 2);
            tris.Add(startIdx + 3);
        }

        void AddTriangle(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                  Vector3 baseA, Vector3 baseB, Vector3 top)
        {
            if (!FacesOutward(baseA, baseB, top))
            {
                (baseA, baseB) = (baseB, baseA);
            }

            int startIdx = verts.Count;
            float tileSize = 1.0f;
            float uBase = Vector3.Distance(baseA, baseB) / tileSize;

            verts.Add(baseA);
            verts.Add(baseB);
            verts.Add(top);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(uBase, 0f));
            uvs.Add(new Vector2(uBase * 0.5f, vScale));

            tris.Add(startIdx);
            tris.Add(startIdx + 1);
            tris.Add(startIdx + 2);
        }

        void AddGable(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                      Vector3 leftBase, Vector3 rightBase, Vector3 apex)
        {
            if (!FacesOutward(leftBase, rightBase, apex))
            {
                (leftBase, rightBase) = (rightBase, leftBase);
            }

            int startIdx = verts.Count;

            float width = Vector3.Distance(leftBase, rightBase);
            float height = Mathf.Abs(apex.y - leftBase.y);
            float vSpan = Mathf.Max(height, 0.01f) * vScale;

            verts.Add(leftBase);
            verts.Add(rightBase);
            verts.Add(apex);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(width * vScale, 0f));
            uvs.Add(new Vector2(width * 0.5f * vScale, vSpan));

            tris.Add(startIdx);
            tris.Add(startIdx + 1);
            tris.Add(startIdx + 2);
        }

        if (type == RoofType.Hip)
        {
            if (sizeX >= sizeZ)
            {
                AddTrapezoid(longVerts, longUvs, longTris, c0, c1, ridgeA, ridgeB);
                AddTrapezoid(longVerts, longUvs, longTris, c2, c3, ridgeB, ridgeA);
                AddTriangle(shortVerts, shortUvs, shortTris, c3, c0, ridgeA);
                AddTriangle(shortVerts, shortUvs, shortTris, c1, c2, ridgeB);
            }
            else
            {
                AddTrapezoid(longVerts, longUvs, longTris, c0, c3, ridgeA, ridgeB);
                AddTrapezoid(longVerts, longUvs, longTris, c2, c1, ridgeB, ridgeA);
                AddTriangle(shortVerts, shortUvs, shortTris, c1, c0, ridgeA);
                AddTriangle(shortVerts, shortUvs, shortTris, c2, c3, ridgeB);
            }
        }
        else
        {
            if (sizeX >= sizeZ)
            {
                AddTrapezoid(longVerts, longUvs, longTris, c0, c1, ridgeA, ridgeB);
                AddTrapezoid(longVerts, longUvs, longTris, c2, c3, ridgeB, ridgeA);

                Vector3 wGableApex = new Vector3(wallCorners[0].x, ridgeA.y, (wallCorners[0].z + wallCorners[3].z) * 0.5f);
                AddGable(shortVerts, shortUvs, shortTris, wallCorners[0], wallCorners[3], wGableApex);

                Vector3 eGableApex = new Vector3(wallCorners[1].x, ridgeB.y, (wallCorners[1].z + wallCorners[2].z) * 0.5f);
                AddGable(shortVerts, shortUvs, shortTris, wallCorners[1], wallCorners[2], eGableApex);
            }
            else
            {
                AddTrapezoid(longVerts, longUvs, longTris, c0, c3, ridgeA, ridgeB);
                AddTrapezoid(longVerts, longUvs, longTris, c2, c1, ridgeB, ridgeA);

                Vector3 sGableApex = new Vector3((wallCorners[0].x + wallCorners[1].x) * 0.5f, ridgeA.y, wallCorners[0].z);
                AddGable(shortVerts, shortUvs, shortTris, wallCorners[1], wallCorners[0], sGableApex);

                Vector3 nGableApex = new Vector3((wallCorners[3].x + wallCorners[2].x) * 0.5f, ridgeB.y, wallCorners[3].z);
                AddGable(shortVerts, shortUvs, shortTris, wallCorners[2], wallCorners[3], nGableApex);
            }
        }

        // Reused between rebuilds, every building rebuilds on each inspector change.
        roofMeshSideBottom = fillMesh(roofMeshSideBottom, longVerts, longUvs, longTris);
        roofMeshSideTop = fillMesh(roofMeshSideTop, shortVerts, shortUvs, shortTris);
        roofMeshSideTopIsGable = (type == RoofType.AFrame);

        List<Vector3> baseRing = new List<Vector3> { c0, c1, c2, c3 };
        outlines[OutlineType.LastFloorBase] = new Outline(new List<List<Vector3>> { baseRing }, new List<List<Vector3>>());
        outlines[OutlineType.LastFloorTop] = new Outline(new List<List<Vector3>> { new List<Vector3> { ridgeA, ridgeB } }, new List<List<Vector3>>());
        outlines[OutlineType.Roof] = new Outline(new List<List<Vector3>> { new List<Vector3> { ridgeA, ridgeB } }, new List<List<Vector3>>());

        var poleHorizontalModule = buildingAsset.wallRoofHorizontalPole;
        if (poleHorizontalModule == null) return;

        int[] eaveEdgeIndices;
        if (type == RoofType.Hip)
        {
            eaveEdgeIndices = new int[] { 0, 1, 2, 3 };
        }
        else
        {
            if (sizeX >= sizeZ)
            {
                eaveEdgeIndices = new int[] { 0, 2 };
            }
            else
            {
                eaveEdgeIndices = new int[] { 1, 3 };
            }
        }

        for (int k = 0; k < eaveEdgeIndices.Length; k++)
        {
            int i = eaveEdgeIndices[k];
            var current = corners[i];
            var next = corners[(i + 1) % 4];

            Vector3 h = (current - next);
            Vector3 fw = h.normalized;
            Vector3 point = (current + next) / 2.0f;
            fw = Quaternion.Euler(0, 90, 0) * fw;

            Quaternion rot = Quaternion.LookRotation(fw, Vector3.up);
            var scale = new Vector3(h.magnitude, 1, 1);
            var matrix = Matrix4x4.TRS(point, rot, scale);
            Vector3 center = point;
            center.y = floorH + poleHorizontalModule.getHeight() / 2;

            modulesDictionary[poleHorizontalModule].add(
                matrix,
                new BuildingModuleInfo(center, floor, floorIDCount)
            );
            floorIDCount += 1;
        }

        {
            Vector3 h = ridgeA - ridgeB;
            if (h.magnitude > 0.001f)
            {
                Vector3 fw = h.normalized;
                Vector3 point = (ridgeA + ridgeB) / 2.0f;
                fw = Quaternion.Euler(0, 90, 0) * fw;

                Quaternion rot = Quaternion.LookRotation(fw, Vector3.up);
                var scale = new Vector3(h.magnitude, 1, 1);
                var matrix = Matrix4x4.TRS(point, rot, scale);
                Vector3 center = point;
                center.y = point.y + poleHorizontalModule.getHeight() / 2;

                modulesDictionary[poleHorizontalModule].add(
                    matrix,
                    new BuildingModuleInfo(center, floor, floorIDCount)
                );
                floorIDCount += 1;
            }
        }

        if (type == RoofType.Hip)
        {
            float rafterOvershoot = 0f;

            for (int i = 0; i < 4; i++)
            {
                Vector3 baseCorner = corners[i];
                Vector3 wallCorner = wallCorners[i];

                Vector3 target = Vector3.Distance(baseCorner, ridgeA) < Vector3.Distance(baseCorner, ridgeB) ? ridgeA : ridgeB;

                Vector3 outwardAtCorner = cornerOutward[i];

                Vector3 nextCorner = corners[(i + 1) % 4];
                Vector3 prevCorner = corners[(i - 1 + 4) % 4];

                Vector3 edgeA = (nextCorner - baseCorner).normalized;
                Vector3 edgeB = (baseCorner - prevCorner).normalized;
                Vector3 seamOutward = (edgeA - edgeB).normalized;
                if (seamOutward.sqrMagnitude < 1e-6f) seamOutward = outwardAtCorner;

                Vector3 startPt = baseCorner + outwardAtCorner * rafterOvershoot;
                Vector3 endPt = target;

                Vector3 h = (endPt - startPt);
                if (h.magnitude < 0.001f) continue;

                Vector3 dir = h.normalized;

                Vector3 seamNormal = Vector3.Cross(dir, seamOutward).normalized;
                if (Vector3.Dot(seamNormal, Vector3.up) < 0) seamNormal = -seamNormal;

                Vector3 xAxis = dir;
                Vector3 zAxis = seamNormal;
                Vector3 yAxis = Vector3.Cross(zAxis, xAxis).normalized;
                xAxis = Vector3.Cross(yAxis, zAxis).normalized;

                Matrix4x4 rotMat = new Matrix4x4();
                rotMat.SetColumn(0, new Vector4(xAxis.x, xAxis.y, xAxis.z, 0));
                rotMat.SetColumn(1, new Vector4(yAxis.x, yAxis.y, yAxis.z, 0));
                rotMat.SetColumn(2, new Vector4(zAxis.x, zAxis.y, zAxis.z, 0));
                rotMat.SetColumn(3, new Vector4(0, 0, 0, 1));
                Quaternion rot = rotMat.rotation;

                var scale = new Vector3(h.magnitude, 1, 1);
                Vector3 point = (startPt + endPt) / 2.0f;
                var matrix = Matrix4x4.TRS(point, rot, scale);
                Vector3 center = point;

                modulesDictionary[poleHorizontalModule].add(
                    matrix,
                    new BuildingModuleInfo(center, floor, floorIDCount)
                );
                floorIDCount += 1;
            }
        }
        else
        {
            if (sizeX >= sizeZ)
            {
                AddRafter(c0, ridgeA);
                AddRafter(c3, ridgeA);
                AddRafter(c1, ridgeB);
                AddRafter(c2, ridgeB);
            }
            else
            {
                AddRafter(c1, ridgeA);
                AddRafter(c0, ridgeA);
                AddRafter(c2, ridgeB);
                AddRafter(c3, ridgeB);
            }
        }

        void AddRafter(Vector3 from, Vector3 to)
        {
            Vector3 h = (to - from);
            if (h.magnitude < 0.001f) return;

            Vector3 dir = h.normalized;
            Vector3 outward = Vector3.Cross(dir, Vector3.up).normalized;
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.forward;

            Vector3 seamNormal = Vector3.Cross(dir, outward).normalized;
            if (Vector3.Dot(seamNormal, Vector3.up) < 0) seamNormal = -seamNormal;
            outward = Vector3.Cross(seamNormal, dir).normalized;

            Vector3 xAxis = dir;
            Vector3 zAxis = seamNormal;
            Vector3 yAxis = Vector3.Cross(zAxis, xAxis).normalized;
            xAxis = Vector3.Cross(yAxis, zAxis).normalized;

            Matrix4x4 rotMat = new Matrix4x4();
            rotMat.SetColumn(0, new Vector4(xAxis.x, xAxis.y, xAxis.z, 0));
            rotMat.SetColumn(1, new Vector4(yAxis.x, yAxis.y, yAxis.z, 0));
            rotMat.SetColumn(2, new Vector4(zAxis.x, zAxis.y, zAxis.z, 0));
            rotMat.SetColumn(3, new Vector4(0, 0, 0, 1));
            Quaternion rot = rotMat.rotation;

            var scale = new Vector3(h.magnitude, 1, 1);
            Vector3 point = (from + to) / 2.0f;
            var matrix = Matrix4x4.TRS(point, rot, scale);
            Vector3 center = point;

            modulesDictionary[poleHorizontalModule].add(
                matrix,
                new BuildingModuleInfo(center, floor, floorIDCount)
            );
            floorIDCount += 1;
        }
    }

    float fillWithFirstColumn(BuildingModule columnModule, List<Vector3> section, float sectionLen, int floor, int startFloorID, float floorH, float startS = 0, bool verbose = false)
    {
        Vector3 p1 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, startS, verbose);
        Vector3 p2 = ProceduralBuildingUtils.sampleAlong(section, sectionLen, startS + columnModule.getWidth(), verbose);

        Vector3 center = (p1 + p2) / 2;
        center.y = floorH + columnModule.getHeight() / 2;

        modulesDictionary[columnModule].add(
            createModuleMatrix(p1, p2, floorH, 1),
            new BuildingModuleInfo(center, floor, startFloorID)
        );

        return startS + columnModule.getWidth();
    }

    int fillCap(BuildingModule wallModule, List<Vector3> cap, int floor, int startFloorID, float floorH)
    {
        for (int i = 0; i < cap.Count - 1; i++)
        {
            Vector3 p1 = cap[i];
            Vector3 p2 = cap[i + 1];
            float distance = (p2 - p1).magnitude;

            Vector3 center = (p1 + p2) / 2;
            center.y = floorH + wallModule.getHeight() / 2;
            modulesDictionary[wallModule].add(
                createModuleMatrix(p1, p2, floorH, distance / wallModule.getWidth()),
                new BuildingModuleInfo(center, floor, startFloorID + i)
            );
        }

        return cap.Count - 1;
    }

    void buildSerializableInstances()
    {
        instances = new List<BuildingMeshInstances>();
        if (populateInstances)
        {
            if (modulesDictionary != null)
            {
                foreach (var mat in modulesDictionary)
                {
                    if (mat.Key.render)
                    {
                        instances.Add(new BuildingMeshInstances(mat.Key.moduleMesh, mat.Key.moduleMaterial, mat.Value.matrices));
                    }
                }
            }
            if (roofMeshSideBottom != null && buildingAsset.roofTopMaterial != null)
            {
                Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
                List<Matrix4x4> mtx = new List<Matrix4x4> { m };
                instances.Add(new BuildingMeshInstances(roofMeshSideBottom, buildingAsset.roofTopMaterial, mtx));
            }
            if (roofMeshSideTop != null)
            {
                Material endMat = roofMeshSideTopIsGable && buildingAsset.aFrameGableMaterial != null
                    ? buildingAsset.aFrameGableMaterial
                    : buildingAsset.roofSidesMaterial;

                if (endMat != null)
                {
                    Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
                    List<Matrix4x4> mtx = new List<Matrix4x4> { m };
                    instances.Add(new BuildingMeshInstances(roofMeshSideTop, endMat, mtx));
                }
            }
        }
    }

    public List<BuildingMeshInstances> getInstances() => instances;

    static Mesh fillMesh(Mesh mesh, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        if (mesh == null)
        {
            mesh = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
        }
        else
        {
            mesh.Clear();
        }
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        // The thermal normal map needs tangents.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    void updateWorldMatrices(Matrix4x4 localToWorld)
    {
        worldMatrices.Clear();
        foreach (var mat in modulesDictionary)
        {
            var local = mat.Value.matrices;
            var world = new Matrix4x4[local.Count];
            for (int i = 0; i < world.Length; i++)
            {
                world[i] = localToWorld * local[i];
            }
            worldMatrices[mat.Key] = world;
        }
        worldMatricesLocalToWorld = localToWorld;
        worldMatricesDirty = false;
    }

    void renderMatrices()
    {
        if (buildingAsset == null || modulesDictionary == null)
        {
            return;
        }

        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        if (worldMatricesDirty || localToWorld != worldMatricesLocalToWorld)
        {
            updateWorldMatrices(localToWorld);
        }

        MaterialPropertyBlock properties = thermalProperties();
        int layer = gameObject.layer;

        foreach (var mat in worldMatrices)
        {
            if (mat.Key.render && mat.Value.Length > 0)
            {
                Graphics.DrawMeshInstanced(mat.Key.moduleMesh, 0, mat.Key.moduleMaterial, mat.Value, mat.Value.Length,
                    properties, ShadowCastingMode.On, true, layer);
            }
        }

        if (roofMeshSideBottom != null && buildingAsset.roofTopMaterial != null)
        {
            Graphics.DrawMesh(roofMeshSideBottom, localToWorld, buildingAsset.roofTopMaterial, layer, null, 0, properties);
        }
        if (roofMeshSideTop != null)
        {
            Material endMat = roofMeshSideTopIsGable && buildingAsset.aFrameGableMaterial != null
                ? buildingAsset.aFrameGableMaterial
                : buildingAsset.roofSidesMaterial;

            if (endMat != null)
            {
                Graphics.DrawMesh(roofMeshSideTop, localToWorld, endMat, layer, null, 0, properties);
            }
        }
    }

    /// <summary>
    /// The thermal pass replaces every material with Thermal/Base and reads the per-draw property block, so the
    /// building carries its ThermalMaterial in the block it passes to each draw. Refilled every frame so edits to the
    /// asset show up without a rebuild. Null without a thermal material.
    /// </summary>
    MaterialPropertyBlock thermalProperties()
    {
        if (thermalMaterial == null)
        {
            return null;
        }
        if (thermalBlock == null)
        {
            thermalBlock = new MaterialPropertyBlock();
        }
        thermalMaterial.ApplyTo(thermalBlock);
        return thermalBlock;
    }

    void Update()
    {
        renderMatrices();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Exports the building to an fbx in local space.
    /// </summary>
    public void createMesh()
    {
        if (modulesDictionary != null)
        {
            List<Material> materials = new List<Material>();
            List<Mesh> subMeshes = new List<Mesh>();

            foreach (var mat in modulesDictionary)
            {
                Mesh moduleMesh = mat.Key.moduleMesh;
                if (mat.Key.render)
                {
                    if (mat.Value.matrices.Count > 0)
                    {
                        List<CombineInstance> moduleInstances = new List<CombineInstance>();
                        foreach (var matrix in mat.Value.matrices)
                        {
                            CombineInstance combineInstance = new CombineInstance
                            {
                                mesh = moduleMesh,
                                transform = matrix
                            };

                            moduleInstances.Add(combineInstance);
                        }
                        Mesh moduleCombinedMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                        moduleCombinedMesh.CombineMeshes(moduleInstances.ToArray(), true, true);
                        subMeshes.Add(moduleCombinedMesh);
                        materials.Add(mat.Key.moduleMaterial);
                    }
                }
            }

            if (roofMeshSideBottom != null && buildingAsset.roofTopMaterial != null)
            {
                subMeshes.Add(roofMeshSideBottom);
                materials.Add(buildingAsset.roofTopMaterial);
            }
            if (roofMeshSideTop != null)
            {
                Material endMat = roofMeshSideTopIsGable && buildingAsset.aFrameGableMaterial != null
                    ? buildingAsset.aFrameGableMaterial
                    : buildingAsset.roofSidesMaterial;

                if (endMat != null)
                {
                    subMeshes.Add(roofMeshSideTop);
                    materials.Add(endMat);
                }
            }

            List<CombineInstance> finalCombine = new List<CombineInstance>();

            foreach (var mesh in subMeshes)
            {
                finalCombine.Add(new CombineInstance
                {
                    mesh = mesh,
                    transform = Matrix4x4.identity
                });
            }

            var combinedMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            combinedMesh.CombineMeshes(finalCombine.ToArray(), false, true);

            combinedMesh.RecalculateNormals();
            combinedMesh.RecalculateBounds();

            GameObject go = new GameObject("GeneratedMesh_TMP ");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            mf.sharedMesh = combinedMesh;
            mr.sharedMaterials = materials.ToArray();

            var options = new ExportModelOptions
            {
                ExportFormat = ExportFormat.Binary,
                ModelAnimIncludeOption = Include.Model,
                EmbedTextures = true
            };
            string exportPath = "Assets/Export/GeneratedMesh" + name + ".fbx";
            ModelExporter.ExportObject(exportPath, go, options);

            DestroyImmediate(go);
            DestroyImmediate(combinedMesh);

            AssetDatabase.Refresh();
        }
    }
#endif
}