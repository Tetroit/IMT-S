using System;
using System.Collections.Generic;using System.Security.Claims;
using UnityEngine;

public class BuildingModuleInstances{
    public List<Matrix4x4> matrices;
    public List<BuildingModuleInfo> infos;

    public BuildingModuleInstances() {
        matrices = new List<Matrix4x4>();
        infos = new List<BuildingModuleInfo>();
    }
    
    public int count => matrices.Count;
    
    public void add(Matrix4x4 matrix, BuildingModuleInfo info) {
        matrices.Add(matrix);
        infos.Add(info);
    }
    
    public void clear() {
        matrices.Clear();
        infos.Clear();
    }
    
    public void removeAt(int id) {
        if (id < 0 || id >= matrices.Count)
            throw new ArgumentOutOfRangeException(nameof(id));
        
        matrices.RemoveAt(id);
        infos.RemoveAt(id);
    }

    public bool remove(Matrix4x4 matrix, BuildingModuleInfo info) {
        int idx = matrices.IndexOf(matrix);
        if (idx >= 0 && infos[idx].Equals(info)) {
            removeAt(idx);
            return true;
        }
        return false;
    }
    
    public (Matrix4x4 matrix, BuildingModuleInfo info) this[int index] {
        get {
            if (index < 0 || index >= matrices.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return (matrices[index], infos[index]);
        }
        set {
            if (index < 0 || index >= matrices.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            matrices[index] = value.matrix;
            infos[index] = value.info;
        }
    }
}


public class BuildingModuleInfo {
    public Vector3 center;
    public int floor;
    public int floorID;

    public BuildingModuleInfo(Vector3 center, int floor, int floorID) {
        this.center = center;
        this.floor = floor;
        this.floorID = floorID;
    }
}

public struct SelectedModuleEntry {
    public BuildingModule module;
    public int index;
    public BuildingModuleInfo info;

    public SelectedModuleEntry(BuildingModule module, int index, BuildingModuleInfo info) {
        this.module = module;
        this.index = index;
        this.info = info;
    }
}

[System.Serializable]
public class Outline {
    public List<List<Vector3>> sections;
    public List<List<Vector3>> caps;

    public Outline() {
        sections = new List<List<Vector3>>();
        caps = new List<List<Vector3>>();
    }
    
    public Outline(List<List<Vector3>> sections, List<List<Vector3>> caps) {
        this.sections = new List<List<Vector3>>(sections);
        this.caps = new List<List<Vector3>>(caps);
    }
    
    public Outline((List<List<Vector3>> sections, List<List<Vector3>> caps) outline) {
        this.sections = new List<List<Vector3>>(outline.sections);
        this.caps = new List<List<Vector3>>(outline.caps);
    }
}

public enum OutlineType { General, LastFloors, LastFloorBase, LastFloorTop, Roof, Terrace }

[System.Serializable]
public class BuildingMeshInstances {
    public Mesh mesh;
    public Material material;
    public List<Matrix4x4> matrices;

    public BuildingMeshInstances(Mesh mesh, Material material, List<Matrix4x4> matrices) {
        this.mesh = mesh;
        this.material = material;
        this.matrices = new List<Matrix4x4>(matrices);
    }
}