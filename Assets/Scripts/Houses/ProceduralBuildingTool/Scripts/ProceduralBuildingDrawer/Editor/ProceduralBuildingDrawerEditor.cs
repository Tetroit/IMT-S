using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public enum LineDrawerMode { None, Drawing, EditPerimeter }


[CustomEditor(typeof(ProceduralBuildingDrawer))]
public class ProceduralBuildingDrawerEditor : Editor
{

    ProceduralBuildingDrawer building;
    LineDrawerMode currentMode = LineDrawerMode.None;

    void OnEnable()
    {
        building = (ProceduralBuildingDrawer)target;
        Tools.hidden = true;
    }

    void OnDisable()
    {
        Tools.hidden = false;
    }

    void clearAll()
    {
        building.clearAll();
    }

    public override void OnInspectorGUI()
    {

        bool isDrawState = currentMode == LineDrawerMode.Drawing;
        bool isDrawButtonToggle = GUILayout.Toggle(isDrawState, "Draw", "Button");

        if (isDrawButtonToggle != isDrawState)
        {
            if (isDrawButtonToggle)
            {
                clearAll();
                currentMode = LineDrawerMode.Drawing;
            }
            else
            {
                currentMode = LineDrawerMode.None;
            }
        }

        bool isEditPerimeterState = currentMode == LineDrawerMode.EditPerimeter;
        bool isEditPerimeterButtonToggle = GUILayout.Toggle(isEditPerimeterState, "Edit Perimeter", "Button");

        if (isEditPerimeterButtonToggle != isEditPerimeterState)
        {
            if (isEditPerimeterButtonToggle)
            {
                currentMode = LineDrawerMode.EditPerimeter;
            }
            else
            {
                currentMode = LineDrawerMode.None;
            }
        }

        if (GUILayout.Button("Clear"))
        {
            Undo.RecordObject(building, "Clear Points");
            clearAll();
            currentMode = LineDrawerMode.None;
        }

        if (GUILayout.Button("GenerateMesh"))
        {
            currentMode = LineDrawerMode.None;
            createMesh();
        }

        EditorGUILayout.Space();

        DrawDefaultInspector();
    }

    Vector3 editControlPoint(Vector3 cp)
    {
        Vector3 newPos = Handles.Slider2D(
            cp,
            Vector3.up,
            Vector3.right,
            Vector3.forward,
            HandleUtility.GetHandleSize(cp) * 0.1f,
            Handles.SphereHandleCap,
            0f,
            true
        );
        return newPos;
    }

    void createMesh()
    {
        building.createMesh();
    }

    void OnSceneGUI()
    {
        Event e = Event.current;

        if (currentMode == LineDrawerMode.Drawing)
        {
            Vector3 planePoint = new Vector3(0, building.transform.position.y, 0);

            if (e.type == EventType.MouseDrag && e.button == 0 && !e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Plane plane = new Plane(-Vector3.up, planePoint);

                if (plane.Raycast(ray, out float distance))
                {
                    Vector3 worldPos = ray.GetPoint(distance);
                    var d = building.rawPoints.Count > 0 ? Vector3.Distance(building.rawPoints[^1], worldPos) : 0.0f;
                    if (building.rawPoints.Count == 0 || d > ProceduralBuildingDrawer.DRAW_POINT_DISTANCE)
                    {
                        Undo.RecordObject(building, "Add Point");
                        building.rawPoints.Add(worldPos);
                    }
                }

                e.Use();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Plane plane = new Plane(-Vector3.up, planePoint);
                building.rawPoints.Clear();
                if (plane.Raycast(ray, out float distance))
                {
                    Vector3 worldPos = ray.GetPoint(distance);

                    Undo.RecordObject(building, "Add Point");
                    building.rawPoints.Add(worldPos);
                }

                e.Use();
            }

            if (e.type == EventType.MouseDrag && e.button == 0 && e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Plane plane = new Plane(-Vector3.up, planePoint);
                if (plane.Raycast(ray, out float distance))
                {
                    Vector3 worldPos = ray.GetPoint(distance);

                    Undo.RecordObject(building, "Add Point");
                    if (building.rawPoints.Count < 2)
                    {
                        building.rawPoints.Add(worldPos);
                    }
                    else
                    {
                        building.rawPoints[1] = (worldPos);
                    }
                }

                e.Use();
            }


            if (e.button == 0 && building.left.Count < 1 && building.right.Count < 1)
            {
                Handles.color = Color.yellow;
                for (int i = 0; i < building.rawPoints.Count - 1; i++)
                {
                    Handles.DrawSolidDisc(building.rawPoints[i], Vector3.up, .05f);
                    Handles.DrawLine(building.rawPoints[i], building.rawPoints[i + 1], 3);
                }
            }

            if (e.type == EventType.MouseUp && e.button == 0)
            {
                building.initCurve();
                e.Use();
            }
        }

        if (currentMode == LineDrawerMode.EditPerimeter)
        {

            if (e.type == EventType.MouseUp)
            {
                building.updateAll();
            }

            for (int i = 0; i < building.left.Count; i++)
            {
                if (i == 0 || i == building.left.Count - 1)
                {
                    EditorGUI.BeginChangeCheck();
                    var np = editControlPoint(building.left[i]);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(building, "Move Perimeter Point");
                        ProceduralBuildingUtils.moveLine(building.left, i, np);
                    }
                }
            }

            for (int i = 0; i < building.right.Count; i++)
            {
                if (i == 0 || i == building.right.Count - 1)
                {
                    EditorGUI.BeginChangeCheck();
                    var np = editControlPoint(building.right[i]);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(building, "Move Perimeter Point");
                        ProceduralBuildingUtils.moveLine(building.right, i, np);
                    }
                }
            }
        }



        if (building.showOutlines)
        {
            if (e.alt)
            {
                Handles.color = Color.blue;
                for (int i = 0; i < building.left.Count - 1; i++)
                {
                    Handles.DrawSolidDisc(building.left[i], Vector3.up, .05f);
                    Handles.DrawLine(building.left[i], building.left[i + 1], 3);
                }

                Handles.color = Color.red;
                for (int i = 0; i < building.right.Count - 1; i++)
                {
                    Handles.DrawSolidDisc(building.right[i], Vector3.up, .05f);
                    Handles.DrawLine(building.right[i], building.right[i + 1], 3);
                }
            }
            else
            {

                if (building.outlines.ContainsKey(OutlineType.General))
                {
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
        }
    }

}