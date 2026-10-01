using UnityEngine;

[CreateAssetMenu(fileName = "BuildingModule", menuName = "TNTC/BuildingModule", order = 0)]
public class BuildingModule : ScriptableObject {
    public Mesh moduleMesh;
    public Material moduleMaterial;
    public bool canStretch = true;
    public float customWidth = 0;
    public bool render = true;
    public bool canBeAltered;
    public bool canHaveBalcony;

    public Vector3 getSize() => moduleMesh.bounds.size;
    public float getWidth() => customWidth > 0 ? customWidth : moduleMesh.bounds.size.x;
    public float getHeight() => moduleMesh.bounds.size.y;
}
