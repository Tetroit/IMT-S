using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Random = UnityEngine.Random;


[CreateAssetMenu(fileName = "BuildingAsset", menuName = "TNTC/BuildingAsset")]
public class BuildingAsset : ScriptableObject
{
    [Header("Ground Floor")]
    public BuildingModule wallGroundFloor;
    public BuildingModule wallGroundFloorL;
    public BuildingModule wallGroundFloorR;
    public BuildingModule doorGroundFloor;
    [Header("Columns")]
    public BuildingModule columnGroundFloor;
    [Header("Roof")]
    public BuildingModule wallRoofHorizontalPole;
    [Header("Windowed Walls")]
    public BuildingModule wallGroundFloorWindow;
    [Header("Materials")]
    public Material roofSidesMaterial;
    public Material roofTopMaterial;
    public Material aFrameGableMaterial;
    [Header("Roof offsets")]
    public float topPointVerticalOffset = 1.43f;

    public BuildingModule[] getModules()
    {
        var modules = new List<BuildingModule>();
        var fields = typeof(BuildingAsset).GetFields(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var field in fields)
        {
            if (field.FieldType == typeof(BuildingModule))
            {
                var val = field.GetValue(this) as BuildingModule;
                if (val != null)
                    modules.Add(val);
            }
            else if (typeof(IEnumerable<BuildingModule>).IsAssignableFrom(field.FieldType))
            {
                if (field.GetValue(this) is IEnumerable<BuildingModule> enumerable)
                    modules.AddRange(enumerable.Where(m => m != null));
            }
        }

        return modules.ToArray();
    }

    public BuildingModule getWallOf(int floor, int maxFloor, bool useVariant = false, bool useEmpty = false, bool useWindow = false)
    {
        BuildingModule wall = useWindow && wallGroundFloorWindow != null ? wallGroundFloorWindow : wallGroundFloor;
        return wall != null ? wall : wallGroundFloor;
    }

    public BuildingModule getOffCenteredWallOf(int floor, int maxFloor, bool isLeft, int variant = 0, bool useWindow = false)
    {
        BuildingModule wall = useWindow && wallGroundFloorWindow != null
            ? wallGroundFloorWindow
            : (isLeft ? wallGroundFloorL : wallGroundFloorR);

        return wall != null ? wall : wallGroundFloor;
    }

    public BuildingModule getDoorOf(int floor)
    {
        return doorGroundFloor;
    }

    public BuildingModule getColumnOf(int floor, int maxFloor)
    {
        return columnGroundFloor;
    }
}