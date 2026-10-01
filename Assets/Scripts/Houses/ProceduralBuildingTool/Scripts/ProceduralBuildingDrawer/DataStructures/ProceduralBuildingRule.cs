using System.Collections.Generic;
using UnityEngine;

public class ProceduralBuildingRule {
    public List<BuildingModule> elements;
    public float lenght;
    public int stretchables;
    
    public ProceduralBuildingRule(List<BuildingModule> elements, float lenght, int stretchables) {
        this.elements = elements;
        this.lenght = lenght;
        this.stretchables = stretchables;
    }
}

public static class ProceduralBuildingRulesHelper {
    
    static float getWidth(List<BuildingModule> modules) {
        float l = 0;
        foreach (var m in modules) {
            l += m.getWidth();
        }
        return l;
    }
    
    static int getStretchables(List<BuildingModule> modules) {
        int c = 0;
        foreach (var m in modules) {
            if (m.canStretch) {
                c++;
            }
        }
        return c;
    }
    
    public static ProceduralBuildingRule computeDoorRule(BuildingAsset buildingAsset, int floor, int maxFloor, bool useVariant) {
       
        var wallModuleL = buildingAsset.getOffCenteredWallOf(floor, maxFloor, true, 0);
        var wallModuleR = buildingAsset.getOffCenteredWallOf(floor, maxFloor, false, 0);
        var doorModule = buildingAsset.getDoorOf(floor);
        var column = buildingAsset.getColumnOf(floor, maxFloor);
        
        var ruleModules = new List<BuildingModule> {
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            wallModuleR,
            doorModule,
            wallModuleL,
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            column
        };
        
        return  new ProceduralBuildingRule( 
            ruleModules,
            getWidth(ruleModules), 
            getStretchables(ruleModules) 
        );
    }
    
    public static ProceduralBuildingRule computeFallbackRule(BuildingAsset buildingAsset, int floor, int maxFloor,  bool useVariant) {
        var column = buildingAsset.getColumnOf(floor, maxFloor);
        var isTop = floor >= 2 && maxFloor - floor <= 2;
        
        var ruleModules = new List<BuildingModule> {
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            buildingAsset.getWallOf(floor, maxFloor, useVariant),
            // isTop? buildingAsset.getWallOf(floor, maxFloor, useVariant) : column
            column
        };
        
        return  new ProceduralBuildingRule( 
            ruleModules, 
            getWidth(ruleModules), 
            getStretchables(ruleModules)
        );
    }
    
}
