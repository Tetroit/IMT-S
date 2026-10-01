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
            // Instance matrices are in the drawer's local space.
            Matrix4x4 localToWorld = proceduralBuildingDrawer.transform.localToWorldMatrix;
            foreach (var instance in instances) {
                var matrices = new Matrix4x4[instance.matrices.Count];
                for (int i = 0; i < matrices.Length; i++) {
                    matrices[i] = localToWorld * instance.matrices[i];
                }
                Graphics.DrawMeshInstanced(instance.mesh, 0, instance.material, matrices, matrices.Length);
            }
        }
    }
}
