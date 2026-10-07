using System;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    /// <summary>
    /// A way of placing the LOD focus in the Flight Preview window: settings, minimap editing and minimap drawing.
    /// </summary>
    [Serializable]
    public abstract class FlightPreviewModeBase
    {
        public abstract FlightPreviewMode mode { get; }

        /// <summary>Called when the mode becomes active or the config changes.</summary>
        public virtual void OnActivated(FlightPreviewWindow window) { }

        /// <summary>Extra settings, drawn inside the window's change check on <paramref name="serializedConfig"/>.</summary>
        public abstract void DrawSettings(FlightPreviewWindow window, SerializedObject serializedConfig);

        /// <summary>Minimap input, mouse positions are in minimap space. Returns true when the event was used.</summary>
        /// <param name="canStart">False when the mouse is over minimap UI, no new interaction may start.</param>
        public abstract bool HandleInput(FlightPreviewWindow window, FlightPreviewMinimap minimap, Event e, bool canStart);

        /// <summary>Draws on top of the tiles, repaint only.</summary>
        public abstract void DrawOverlay(FlightPreviewWindow window, FlightPreviewMinimap minimap);

        protected static readonly Color FocusColor = new Color(1f, 0.9f, 0.3f);
        protected static readonly Color RadiusFill = new Color(1f, 0.9f, 0.3f, 0.12f);
        protected static readonly Color ViewDistanceColor = new Color(1f, 1f, 1f, 0.35f);
    }

    /// <summary>LOD from the distance to a single point, the center (origin by default).</summary>
    [Serializable]
    public class RadiusMode : FlightPreviewModeBase
    {
        private const float HandleRadius = 7;

        [NonSerialized] private bool _dragging;
        [NonSerialized] private bool _hover;
        [NonSerialized] private int _undoGroup;

        public override FlightPreviewMode mode => FlightPreviewMode.Radius;

        public override void OnActivated(FlightPreviewWindow window)
        {
            var config = window.config;
            if (config != null && window.geometry != null && DoubleVector2.ApproximatelyEquals(config.center, DoubleVector2.zero))
            {
                Undo.RecordObject(config, "Center Flight Preview On Origin");
                config.center = window.geometry.origin;
                EditorUtility.SetDirty(config);
                window.OnFocusChanged(false);
            }
        }

        public override void DrawSettings(FlightPreviewWindow window, SerializedObject serializedConfig)
        {
            EditorGUILayout.LabelField("Radius Mode", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedConfig.FindProperty("center"), new GUIContent("Center (mercator)"), true);

            DoubleVector2 offset = window.config.center - window.origin;
            double mercatorPerMeter = WebMercator.MercatorPerMeter(window.origin.y);
            EditorGUILayout.LabelField("Offset from origin", $"{offset.x / mercatorPerMeter:0.#} m, {offset.y / mercatorPerMeter:0.#} m");

            using (new EditorGUI.DisabledScope(window.geometry == null))
            {
                if (GUILayout.Button("Center On Origin"))
                {
                    Undo.RecordObject(window.config, "Center Flight Preview On Origin");
                    window.config.center = window.geometry.origin;
                    EditorUtility.SetDirty(window.config);
                    window.OnFocusChanged(false);
                }
            }
            EditorGUILayout.HelpBox("Drag the yellow center handle on the minimap to move it.", MessageType.None);
        }

        public override bool HandleInput(FlightPreviewWindow window, FlightPreviewMinimap minimap, Event e, bool canStart)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var config = window.config;
            Vector2 handle = minimap.ToScreen(config.center);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseMove:
                    bool hover = Vector2.Distance(e.mousePosition, handle) <= HandleRadius;
                    if (hover != _hover)
                    {
                        _hover = hover;
                        window.Repaint();
                    }
                    break;

                case EventType.MouseDown:
                    if (canStart && e.button == 0 && Vector2.Distance(e.mousePosition, handle) <= HandleRadius)
                    {
                        GUIUtility.hotControl = id;
                        _dragging = true;
                        _undoGroup = Undo.GetCurrentGroup();
                        e.Use();
                        return true;
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && _dragging)
                    {
                        Undo.RecordObject(config, "Move Flight Preview Center");
                        config.center = minimap.ToMercator(e.mousePosition);
                        EditorUtility.SetDirty(config);
                        window.OnFocusChanged(true);
                        e.Use();
                        return true;
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && _dragging)
                    {
                        GUIUtility.hotControl = 0;
                        _dragging = false;
                        window.OnFocusChanged(false);
                        Undo.CollapseUndoOperations(_undoGroup);
                        e.Use();
                        return true;
                    }
                    break;
            }

            if (_hover || _dragging)
                EditorGUIUtility.AddCursorRect(minimap.rect, MouseCursor.MoveArrow);
            return false;
        }

        public override void DrawOverlay(FlightPreviewWindow window, FlightPreviewMinimap minimap)
        {
            var config = window.config;
            Vector3 center = minimap.ToScreen(config.center);
            float radius = minimap.MetersToPixels(config.lodSettings.radius);
            float viewDistance = minimap.MetersToPixels(config.lodSettings.ViewDistance());

            Handles.color = ViewDistanceColor;
            Handles.DrawWireDisc(center, Vector3.forward, viewDistance);
            Handles.color = RadiusFill;
            Handles.DrawSolidDisc(center, Vector3.forward, radius);
            Handles.color = FocusColor;
            Handles.DrawWireDisc(center, Vector3.forward, radius);

            float size = _hover || _dragging ? HandleRadius + 2 : HandleRadius;
            Handles.color = Color.black;
            Handles.DrawSolidDisc(center, Vector3.forward, size + 1.5f);
            Handles.color = FocusColor;
            Handles.DrawSolidDisc(center, Vector3.forward, size);
        }
    }

    /// <summary>LOD from the distance to a flight path drawn on the minimap.</summary>
    [Serializable]
    public class PathMode : FlightPreviewModeBase
    {
        private const float NodeRadius = 6;
        private const float SegmentPickDistance = 6;

        public bool drawing;

        [NonSerialized] private int _dragNode = -1;
        [NonSerialized] private int _hoverNode = -1;
        [NonSerialized] private int _undoGroup;
        [NonSerialized] private Vector2 _mousePosition;
        [NonSerialized] private bool _mouseInside;

        public override FlightPreviewMode mode => FlightPreviewMode.Path;

        public override void DrawSettings(FlightPreviewWindow window, SerializedObject serializedConfig)
        {
            var config = window.config;
            EditorGUILayout.LabelField("Path Mode", EditorStyles.boldLabel);

            drawing = GUILayout.Toggle(drawing, new GUIContent("Draw Path", "Left click on the minimap appends nodes. Esc to stop."), "Button");

            EditorGUILayout.LabelField("Nodes", config.path.Count.ToString());
            EditorGUILayout.LabelField("Length", $"{PathLengthMeters(config) / 1000:0.###} km");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(config.path.Count < 2))
                {
                    if (GUILayout.Button("Reverse"))
                    {
                        Undo.RecordObject(config, "Reverse Flight Path");
                        config.path.Reverse();
                        EditorUtility.SetDirty(config);
                        window.OnFocusChanged(false);
                    }
                }
                using (new EditorGUI.DisabledScope(config.path.Count == 0))
                {
                    if (GUILayout.Button("Clear"))
                    {
                        Undo.RecordObject(config, "Clear Flight Path");
                        config.path.Clear();
                        EditorUtility.SetDirty(config);
                        window.OnFocusChanged(false);
                    }
                    if (GUILayout.Button("Frame"))
                        window.FrameFocus();
                }
            }

            EditorGUILayout.HelpBox(
                "Draw Path: left click appends a node.\n" +
                "Drag a node to move it.\n" +
                "Ctrl + click on a segment inserts a node.\n" +
                "Right click a node to delete it.",
                MessageType.None);
        }

        private static double PathLengthMeters(TerrainDataConfig config)
        {
            double length = 0;
            for (int i = 0; i < config.path.Count - 1; i++)
            {
                DoubleVector2 a = config.path[i], b = config.path[i + 1];
                double dx = b.x - a.x, dy = b.y - a.y;
                length += Math.Sqrt(dx * dx + dy * dy) / WebMercator.MercatorPerMeter((a.y + b.y) / 2);
            }
            return length;
        }

        private int HitNode(FlightPreviewMinimap minimap, TerrainDataConfig config, Vector2 position)
        {
            // Last node first, it is drawn on top.
            for (int i = config.path.Count - 1; i >= 0; i--)
                if (Vector2.Distance(minimap.ToScreen(config.path[i]), position) <= NodeRadius + 2)
                    return i;
            return -1;
        }

        private int HitSegment(FlightPreviewMinimap minimap, TerrainDataConfig config, Vector2 position)
        {
            int best = -1;
            float bestDistance = SegmentPickDistance;
            for (int i = 0; i < config.path.Count - 1; i++)
            {
                float d = HandleUtility.DistancePointToLineSegment(position, minimap.ToScreen(config.path[i]), minimap.ToScreen(config.path[i + 1]));
                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }

        private void BeginNodeDrag(int id, int node)
        {
            GUIUtility.hotControl = id;
            _dragNode = node;
        }

        public override bool HandleInput(FlightPreviewWindow window, FlightPreviewMinimap minimap, Event e, bool canStart)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var config = window.config;
            _mousePosition = e.mousePosition;
            _mouseInside = minimap.rect.Contains(e.mousePosition);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseMove:
                {
                    int hover = HitNode(minimap, config, e.mousePosition);
                    if (hover != _hoverNode)
                        _hoverNode = hover;
                    // Drawing preview line follows the mouse.
                    window.Repaint();
                    break;
                }

                case EventType.MouseDown:
                {
                    if (!canStart)
                        break;
                    int hit = HitNode(minimap, config, e.mousePosition);
                    if (e.button == 1 && hit >= 0)
                    {
                        Undo.RecordObject(config, "Delete Flight Path Node");
                        config.path.RemoveAt(hit);
                        EditorUtility.SetDirty(config);
                        _hoverNode = -1;
                        window.OnFocusChanged(false);
                        e.Use();
                        return true;
                    }
                    if (e.button != 0)
                        break;

                    _undoGroup = Undo.GetCurrentGroup();
                    if (hit >= 0)
                    {
                        BeginNodeDrag(id, hit);
                        e.Use();
                        return true;
                    }
                    int segment = EditorGUI.actionKey ? HitSegment(minimap, config, e.mousePosition) : -1;
                    if (segment >= 0)
                    {
                        Undo.RecordObject(config, "Insert Flight Path Node");
                        config.path.Insert(segment + 1, minimap.ToMercator(e.mousePosition));
                        EditorUtility.SetDirty(config);
                        BeginNodeDrag(id, segment + 1);
                        window.OnFocusChanged(true);
                        e.Use();
                        return true;
                    }
                    if (drawing)
                    {
                        Undo.RecordObject(config, "Add Flight Path Node");
                        config.path.Add(minimap.ToMercator(e.mousePosition));
                        EditorUtility.SetDirty(config);
                        BeginNodeDrag(id, config.path.Count - 1);
                        window.OnFocusChanged(true);
                        e.Use();
                        return true;
                    }
                    break;
                }

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && _dragNode >= 0 && _dragNode < config.path.Count)
                    {
                        Undo.RecordObject(config, "Move Flight Path Node");
                        config.path[_dragNode] = minimap.ToMercator(e.mousePosition);
                        EditorUtility.SetDirty(config);
                        window.OnFocusChanged(true);
                        e.Use();
                        return true;
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && _dragNode >= 0)
                    {
                        GUIUtility.hotControl = 0;
                        _dragNode = -1;
                        window.OnFocusChanged(false);
                        Undo.CollapseUndoOperations(_undoGroup);
                        e.Use();
                        return true;
                    }
                    break;

                case EventType.KeyDown:
                    if (drawing && e.keyCode == KeyCode.Escape)
                    {
                        drawing = false;
                        window.Repaint();
                        e.Use();
                        return true;
                    }
                    break;
            }

            if (_hoverNode >= 0 || _dragNode >= 0)
                EditorGUIUtility.AddCursorRect(minimap.rect, MouseCursor.MoveArrow);
            else if (drawing)
                EditorGUIUtility.AddCursorRect(minimap.rect, MouseCursor.ArrowPlus);
            return false;
        }

        public override void DrawOverlay(FlightPreviewWindow window, FlightPreviewMinimap minimap)
        {
            var config = window.config;
            int count = config.path.Count;
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
                points[i] = minimap.ToScreen(config.path[i]);

            // Max zoom corridor: a thick line plus discs for round joints and caps.
            float radius = minimap.MetersToPixels(config.lodSettings.radius);
            if (radius >= 1)
            {
                Handles.color = RadiusFill;
                if (count > 1)
                    Handles.DrawAAPolyLine(radius * 2, points);
                foreach (var p in points)
                    Handles.DrawSolidDisc(p, Vector3.forward, radius);
            }

            Handles.color = Color.black;
            if (count > 1)
                Handles.DrawAAPolyLine(5, points);
            Handles.color = FocusColor;
            if (count > 1)
                Handles.DrawAAPolyLine(3, points);

            if (drawing && _mouseInside && count > 0 && _dragNode < 0)
            {
                Handles.color = FocusColor;
                Handles.DrawDottedLine(points[count - 1], _mousePosition, 4);
            }

            for (int i = 0; i < count; i++)
            {
                bool active = i == _hoverNode || i == _dragNode;
                float size = active ? NodeRadius + 2 : NodeRadius;
                Handles.color = Color.black;
                Handles.DrawSolidDisc(points[i], Vector3.forward, size + 1.5f);
                Handles.color = i == 0 ? new Color(0.3f, 1f, 0.4f) : i == count - 1 ? new Color(1f, 0.35f, 0.3f) : FocusColor;
                Handles.DrawSolidDisc(points[i], Vector3.forward, size);
            }
        }
    }
}
