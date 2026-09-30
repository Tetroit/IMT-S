using System.Collections.Generic;
using UnityEngine;

public class ProceduralBuildingRenderer : MonoBehaviour {
    public ProceduralBuildingDrawer proceduralBuildingDrawer;
    List<BuildingMeshInstances> instances = new List<BuildingMeshInstances>();
    
    void Start() {
        if (proceduralBuildingDrawer != null) {
            instances = proceduralBuildingDrawer.getInstances();
        }
    }

    void Update() {
        renderInstances();
    }
    
    void renderInstances() {
        if (instances != null) {
            foreach (var instance in instances) {
                Graphics.DrawMeshInstanced(instance.mesh, 0, instance.material, instance.matrices.ToArray(), instance.matrices.Count);
            }
        }
    }
}
