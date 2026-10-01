using UnityEditor;
using UnityEngine;


[CustomEditor(typeof(ProceduralBuildingDrawer))]
public class ProceduralBuildingDrawerEditor : Editor
{

    ProceduralBuildingDrawer building;

    void OnEnable()
    {
        building = (ProceduralBuildingDrawer)target;
    }

    public override void OnInspectorGUI()
    {
        if (GUILayout.Button("Rebuild"))
        {
            building.updateAll();
        }

        if (GUILayout.Button("GenerateMesh"))
        {
            building.createMesh();
        }

        EditorGUILayout.Space();

        DrawDefaultInspector();
    }

    void OnSceneGUI()
    {
        // Outlines and handles are in the building's local space.
        using (new Handles.DrawingScope(Color.yellow, building.transform.localToWorldMatrix))
        {
            editRectangle();

            if (building.showOutlines)
            {
                drawOutlines();
            }
        }
    }

    /// <summary>
    /// One handle per edge, dragging an edge resizes the rectangle and keeps the opposite edge in place.
    /// </summary>
    void editRectangle()
    {
        Vector2 half = building.size / 2f;
        Handles.DrawPolyLine(
            new Vector3(-half.x, 0f, -half.y),
            new Vector3(half.x, 0f, -half.y),
            new Vector3(half.x, 0f, half.y),
            new Vector3(-half.x, 0f, half.y),
            new Vector3(-half.x, 0f, -half.y));

        for (int axis = 0; axis < 2; axis++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 outward = (axis == 0 ? Vector3.right : Vector3.forward) * sign;
                Vector3 handlePos = outward * half[axis];

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.Slider(handlePos, outward, HandleUtility.GetHandleSize(handlePos) * 0.1f, Handles.CubeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector2 size = building.size;
                    float newLength = Mathf.Max(ProceduralBuildingDrawer.MIN_SIZE, size[axis] + Vector3.Dot(moved - handlePos, outward));
                    float grow = newLength - size[axis];
                    size[axis] = newLength;

                    Undo.RecordObjects(new Object[] { building, building.transform }, "Resize Building");
                    // The center moves by half of the growth, towards the dragged edge.
                    building.transform.position += building.transform.TransformVector(outward * (grow / 2f));
                    building.setRectangle(size);
                }
            }
        }
    }

    void drawOutlines()
    {
        if (!building.outlines.ContainsKey(OutlineType.General))
        {
            return;
        }

        Handles.color = Color.green;
        int id = 0;
        for (int j = 0; j < building.outlines[OutlineType.General].sections.Count; j++)
        {
            var section = building.outlines[OutlineType.General].sections[j];
            for (int i = 0; i < section.Count; i++)
            {
                Handles.DrawSolidDisc(section[i], Vector3.up, .05f);
                Handles.Label(section[i] + Vector3.up * .1f, id.ToString());
                id++;
                if (i < section.Count - 1)
                {
                    Handles.DrawLine(section[i], section[i + 1], 3);
                }
            }
        }

        Handles.color = Color.red;
        foreach (var cap in building.outlines[OutlineType.General].caps)
        {
            for (int i = 0; i < cap.Count; i++)
            {
                Handles.DrawSolidDisc(cap[i], Vector3.up, .05f);
                if (i < cap.Count - 1)
                {
                    Handles.DrawLine(cap[i], cap[i + 1], 3);
                }
            }
        }
    }

}
