using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SelectionEntry {
    public BuildingModule module;
    public List<int> indices;

    public SelectionEntry(BuildingModule module, List<int> indices) {
        this.module = module;
        this.indices = new List<int>(indices);
    }
}

[Serializable]
public class PersistentSelection{
    [SerializeField]
    List<SelectionEntry> entries;

    public Dictionary<BuildingModule, List<int>> matricesIds = new Dictionary<BuildingModule, List<int>>();

    public PersistentSelection(Dictionary<BuildingModule, List<int>> other) {
        if (other != null) {
            matricesIds = new Dictionary<BuildingModule, List<int>>();
            foreach (var e in other) {
                matricesIds[e.Key] = new List<int>(e.Value);
            }

            entries = new List<SelectionEntry>();
            foreach (var kvp in matricesIds) {
                entries.Add(new SelectionEntry(kvp.Key, kvp.Value));
            }
        }
    }

    public void clear() {
        matricesIds = new();
        entries = new List<SelectionEntry>();
    }
    
    public void log(string header = "", bool logVoidElement = false) {
        Debug.Log("<color=orange>" + header + "</color>");
        var str = "matricesIds: ";
        if (matricesIds != null) {
            str += matricesIds.Count + " ";
            foreach (var module in matricesIds) {
                if (!logVoidElement && module.Value.Count <= 0) {
                    continue;
                }
                str += module.Key.name + module.Value.Count + "  " + (module.Value.Count > 0 ? "( <color=red>" : "");
                foreach (var moduleV in module.Value) {
                    str += (moduleV) + " " ;
                }
                str += module.Value.Count > 0 ? "</color> )" : "" + ", "; 
            }
        }
        else {
            str += ("matricesIds is null");
        }
        str += "\n"; 
        str += "entries: ";
        if (entries != null) {
            str += entries.Count + " ";
            // foreach (var e in entries) {
            //     if (!logVoidElement && entries.Count <= 0) {
            //         continue;
            //     }
            //     str += e.module.ToString() + e.indices.Count + "  " + (e.indices.Count > 0 ? "( <color=red>" : "");
            //     foreach (var moduleV in e.indices) {
            //         str += (moduleV) + " " ;
            //     }
            //     str += e.indices.Count > 0 ? "</color> )" : "" + ", "; 
            // }
        }
        else {
            str += ("entries is null");
        }
        
        Debug.Log(str);

    }
}
