using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    /// <summary>
    /// Generates the LOD tile list of a <see cref="TerrainConfig"/> around a point (Radius Mode)
    /// or along a flight path (Path Mode), and previews it on a minimap.
    /// </summary>
    public class FlightPreviewWindow : EditorWindow
    {
        private const float SettingsWidth = 320;
        // Rebuild interval while dragging, a rebuild can take a few ms with many tiles.
        private const double DragRebuildInterval = 0.05;

        private static readonly Color BackgroundColor = new Color(0.32f, 0.32f, 0.32f);
        private static readonly string[] ModeNames = { "Radius Mode", "Path Mode" };
        private static readonly string[] DownloadModeNames = { "Single", "Area" };
        private static readonly int[] ResolutionValues = { 256, 512, 1024, 2048, 4096, 8192 };
        private static readonly GUIContent[] ResolutionNames = Array.ConvertAll(ResolutionValues, r => new GUIContent($"{r} x {r}"));

        [SerializeField] private TerrainConfig _config;
        [SerializeField] private FlightPreviewMinimap _minimap = new FlightPreviewMinimap();
        [SerializeField] private RadiusMode _radiusMode = new RadiusMode();
        [SerializeField] private PathMode _pathMode = new PathMode();
        [SerializeField] private bool _framedOnce;

        [NonSerialized] private SerializedObject _serializedConfig;
        [NonSerialized] private List<TileBounds> _preview = new List<TileBounds>();
        [NonSerialized] private readonly SortedDictionary<int, int> _zoomCounts = new SortedDictionary<int, int>();
        [NonSerialized] private bool _truncated;
        [NonSerialized] private bool _previewDirty;
        [NonSerialized] private double _lastBuildTime;
        [NonSerialized] private bool _needsFrame = true;
        [NonSerialized] private bool _panning;
        [NonSerialized] private Vector2 _settingsScroll;
        [NonSerialized] private Rect _toolbarRect;
        [NonSerialized] private Rect _legendRect;
        [NonSerialized] private readonly List<TileDownloadBatch> _downloads = new List<TileDownloadBatch>();

        public TerrainConfig config => _config;
        /// <summary>Mercator origin of the minimap, the terrain config origin.</summary>
        public DoubleVector2 origin => _config != null ? _config.origin : DoubleVector2.zero;

        private FlightPreviewModeBase activeMode =>
            _config != null && _config.mode == FlightPreviewMode.Path ? _pathMode : _radiusMode;

        [MenuItem("Tools/Procedural Generation/Flight Preview")]
        public static void Open()
        {
            var window = GetWindow<FlightPreviewWindow>();
            window.titleContent = new GUIContent("Flight Preview");
            window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(700, 400);
            wantsMouseMove = true;
            Undo.undoRedoPerformed += OnUndoRedo;
            AutoAssignConfigs();
            OnConfigChanged();
            // Keep the serialized view across domain reloads.
            _needsFrame = !_framedOnce;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void AutoAssignConfigs()
        {
            if (_config != null)
                return;
            var coordinator = FindAnyObjectByType<TerrainCoordinator>();
            _config = coordinator != null && coordinator.terrainConfig != null ? coordinator.terrainConfig : FindAsset<TerrainConfig>();
        }

        private static T FindAsset<T>() where T : UnityEngine.Object
        {
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            return guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
        }

        private void OnConfigChanged()
        {
            _serializedConfig = _config != null ? new SerializedObject(_config) : null;
            LoadPreviewFromConfig();
            _needsFrame = true;
            if (_config == null)
                return;
            activeMode.OnActivated(this);
            // A fresh config has nothing generated yet.
            if (_config.tiles.Count == 0)
                OnFocusChanged(false);
        }

        private void OnUndoRedo()
        {
            // Undo restores tiles together with the settings, no need to rebuild.
            _serializedConfig?.Update();
            LoadPreviewFromConfig();
            Repaint();
        }

        private void LoadPreviewFromConfig()
        {
            _preview = _config != null ? new List<TileBounds>(_config.tiles) : new List<TileBounds>();
            _truncated = false;
            _previewDirty = false;
            CountZooms();
        }

        /// <summary>
        /// The focus or LOD settings changed. While <paramref name="dragging"/> only the preview is rebuilt (throttled),
        /// otherwise the tiles are written to the config right away, in the same undo group as the change.
        /// </summary>
        public void OnFocusChanged(bool dragging)
        {
            if (_config == null)
                return;
            if (dragging)
            {
                _previewDirty = true;
            }
            else
            {
                RebuildPreview();
                CommitPreview();
            }
            Repaint();
        }

        private void RebuildPreview()
        {
            var result = _config.Generate();
            _preview = result.tiles;
            _truncated = result.truncated;
            _previewDirty = false;
            _lastBuildTime = EditorApplication.timeSinceStartup;
            CountZooms();
        }

        private void CommitPreview()
        {
            Undo.RecordObject(_config, "Generate LOD Tiles");
            _config.tiles = new List<TileBounds>(_preview);
            EditorUtility.SetDirty(_config);
        }

        private void CountZooms()
        {
            _zoomCounts.Clear();
            foreach (var tile in _preview)
                _zoomCounts[tile.zoom] = _zoomCounts.TryGetValue(tile.zoom, out int count) ? count + 1 : 1;
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(SettingsWidth)))
                {
                    _settingsScroll = EditorGUILayout.BeginScrollView(_settingsScroll);
                    DrawSettings();
                    EditorGUILayout.EndScrollView();
                }

                Rect mapRect = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                DrawMinimap(mapRect);
            }

            if (_config != null && _previewDirty)
            {
                if (EditorApplication.timeSinceStartup - _lastBuildTime >= DragRebuildInterval)
                    RebuildPreview();
                else
                    Repaint();
            }
        }

        #region Settings

        private void DrawSettings()
        {
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            var config = (TerrainConfig)EditorGUILayout.ObjectField("Terrain Config", _config, typeof(TerrainConfig), false);
            if (EditorGUI.EndChangeCheck())
            {
                _config = config;
                OnConfigChanged();
            }

            if (_config == null)
            {
                EditorGUILayout.HelpBox("Select or create a Terrain Config to store the tiles in.", MessageType.Info);
                if (GUILayout.Button("Create Terrain Config"))
                    CreateConfig();
                return;
            }

            if (_serializedConfig == null || _serializedConfig.targetObject != _config)
                _serializedConfig = new SerializedObject(_config);
            _serializedConfig.Update();

            EditorGUILayout.Space();
            var modeProperty = _serializedConfig.FindProperty("mode");
            EditorGUI.BeginChangeCheck();
            modeProperty.enumValueIndex = GUILayout.Toolbar(modeProperty.enumValueIndex, ModeNames);
            bool modeChanged = EditorGUI.EndChangeCheck();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Level Of Detail", EditorStyles.boldLabel);
            var lod = _serializedConfig.FindProperty("lodSettings");
            var minZoom = lod.FindPropertyRelative("minZoom");
            var maxZoom = lod.FindPropertyRelative("maxZoom");
            EditorGUILayout.PropertyField(minZoom, new GUIContent("Min Zoom Level", minZoom.tooltip));
            EditorGUILayout.PropertyField(maxZoom, new GUIContent("Max Zoom Level", maxZoom.tooltip));
            maxZoom.intValue = Mathf.Max(maxZoom.intValue, minZoom.intValue);
            var radius = lod.FindPropertyRelative("radius");
            EditorGUILayout.PropertyField(radius, new GUIContent(_config.mode == FlightPreviewMode.Path ? "Radius (distance to path, m)" : "Radius (m)", radius.tooltip));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Visibility", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(lod.FindPropertyRelative("altitude"), new GUIContent("Altitude (m)"));
            EditorGUILayout.PropertyField(lod.FindPropertyRelative("detailBias"));
            EditorGUILayout.PropertyField(lod.FindPropertyRelative("maxViewDistance"), new GUIContent("Max View Distance (m)"));
            EditorGUILayout.PropertyField(lod.FindPropertyRelative("balance"), new GUIContent("Balance Neighbours"));
            EditorGUILayout.LabelField("View distance", FormatMeters(_config.lodSettings.ViewDistance()));

            EditorGUILayout.Space();
            activeMode.DrawSettings(this, _serializedConfig);

            EditorGUILayout.Space();
            DrawSummary();

            bool settingsChanged = EditorGUI.EndChangeCheck();
            if (modeChanged || settingsChanged)
            {
                _serializedConfig.ApplyModifiedProperties();
                if (modeChanged)
                    activeMode.OnActivated(this);
                OnFocusChanged(false);
            }

            // Outside the change check, download settings do not change the tiles.
            EditorGUILayout.Space();
            DrawDownloadOptions();
            _serializedConfig.ApplyModifiedProperties();
            DrawDownloads();
        }

        private void DrawDownloadOptions()
        {
            EditorGUILayout.LabelField("Load Tiles", EditorStyles.boldLabel);
            var downloadModeProperty = _serializedConfig.FindProperty("downloadMode");
            downloadModeProperty.enumValueIndex = GUILayout.Toolbar(downloadModeProperty.enumValueIndex, DownloadModeNames);
            var downloadMode = (DownloadMode)downloadModeProperty.enumValueIndex;

            var folderProperty = _serializedConfig.FindProperty("downloadDstFolder");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(folderProperty, new GUIContent("Download To (folder)", folderProperty.tooltip));
                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select Folder", folderProperty.stringValue, "");
                    // Inside Assets the folder is kept project relative, so it works on every machine.
                    if (!string.IsNullOrEmpty(selected))
                        folderProperty.stringValue = TileDownloadBatch.ToAssetPath(selected) ?? selected;
                }
            }
            string folder = folderProperty.stringValue;
            bool hasFolder = !string.IsNullOrWhiteSpace(folder);
            if (hasFolder && TileDownloadBatch.ToAssetPath(folder) == null)
                EditorGUILayout.HelpBox("The folder is outside Assets, no SatContext assets are created.", MessageType.Warning);

            bool canDownload = hasFolder;
            if (downloadMode == DownloadMode.Single)
            {
                EditorGUILayout.PropertyField(_serializedConfig.FindProperty("tileToDownload"), new GUIContent("Tile To Download"), true);
                EditorGUILayout.PropertyField(_serializedConfig.FindProperty("colorZoom"), new GUIContent("Color Zoom"));
                EditorGUILayout.PropertyField(_serializedConfig.FindProperty("heightZoom"), new GUIContent("Height Zoom"));
            }
            else
            {
                var colorResolution = _serializedConfig.FindProperty("colorResolution");
                var heightResolution = _serializedConfig.FindProperty("heightResolution");
                EditorGUILayout.IntPopup(colorResolution, ResolutionNames, ResolutionValues, new GUIContent("Color Resolution", colorResolution.tooltip));
                EditorGUILayout.IntPopup(heightResolution, ResolutionNames, ResolutionValues, new GUIContent("Height Resolution", heightResolution.tooltip));
                EditorGUILayout.LabelField("Zoom per tile",
                    $"color z+{TileDownloadBatch.ZoomOffset(colorResolution.intValue)}, height z+{TileDownloadBatch.ZoomOffset(heightResolution.intValue)}");
                EditorGUILayout.PropertyField(_serializedConfig.FindProperty("skipExisting"));
                EditorGUILayout.LabelField("Tiles", $"{_config.tiles.Count}, one z-x-y folder each");
                canDownload &= _config.tiles.Count > 0;
            }

            using (new EditorGUI.DisabledScope(!canDownload))
            {
                string label = downloadMode == DownloadMode.Single ? "Download Tile" : $"Download {_config.tiles.Count} Tiles";
                if (GUILayout.Button(label))
                {
                    // Take the values that were just edited too.
                    _serializedConfig.ApplyModifiedProperties();
                    StartDownload(downloadMode == DownloadMode.Single ? CreateSingleDownload() : CreateAreaDownload());
                }
            }
        }

        private TileDownloadBatch CreateSingleDownload()
        {
            var job = new TileDownloadJob
            {
                tile = _config.tileToDownload,
                colorZoom = _config.colorZoom,
                heightZoom = _config.heightZoom,
                outputDir = Path.GetFullPath(_config.downloadDstFolder),
            };
            return new TileDownloadBatch($"Tile {job.name}", new List<TileDownloadJob> { job }, loadData: true, skipExisting: false, clampZooms: false);
        }

        private TileDownloadBatch CreateAreaDownload()
        {
            string root = Path.GetFullPath(_config.downloadDstFolder);
            int colorOffset = TileDownloadBatch.ZoomOffset(_config.colorResolution);
            int heightOffset = TileDownloadBatch.ZoomOffset(_config.heightResolution);
            var jobs = new List<TileDownloadJob>(_config.tiles.Count);
            foreach (var tile in _config.tiles)
            {
                jobs.Add(new TileDownloadJob
                {
                    tile = tile,
                    colorZoom = tile.zoom + colorOffset,
                    heightZoom = tile.zoom + heightOffset,
                    outputDir = Path.Combine(root, TileDownloadBatch.FolderName(tile)),
                });
            }
            // Only the metadata is loaded, the images of a large area would not fit in memory.
            return new TileDownloadBatch($"Area, {jobs.Count} tiles", jobs, loadData: false, skipExisting: _config.skipExisting, clampZooms: true);
        }

        private void StartDownload(TileDownloadBatch batch)
        {
            batch.changed += () =>
            {
                // The window may be closed while downloading.
                if (this != null)
                    Repaint();
            };
            _downloads.Add(batch);
            batch.Start();
        }

        private void DrawDownloads()
        {
            if (_downloads.Count == 0)
                return;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Downloads", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!_downloads.Exists(b => b.isFinished)))
                {
                    if (GUILayout.Button("Clear Finished", GUILayout.Width(100)))
                        _downloads.RemoveAll(b => b.isFinished);
                }
            }

            foreach (var batch in _downloads)
                DrawDownload(batch);
        }

        private static void DrawDownload(TileDownloadBatch batch)
        {
            EditorGUILayout.LabelField(batch.label, EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string status = batch.statusText;
                Rect bar = EditorGUILayout.GetControlRect();
                EditorGUI.ProgressBar(bar, batch.progress, status);
                // Tooltip, the status is often longer than the bar.
                GUI.Label(bar, new GUIContent("", status));
                using (new EditorGUI.DisabledScope(batch.isFinished || batch.isCancelled))
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(60)))
                        batch.Cancel();
                }
            }

            if (batch.jobs.Count == 1)
            {
                var context = batch.jobs[0].satContext;
                if (context != null && GUILayout.Button("Select SatContext"))
                {
                    Selection.activeObject = context;
                    EditorGUIUtility.PingObject(context);
                }
            }

            int failed = batch.Count(TileDownloadState.Failed);
            if (failed > 0 && batch.jobs.Count > 1)
            {
                batch.showFailed = EditorGUILayout.Foldout(batch.showFailed, $"Failed tiles ({failed})", true);
                if (batch.showFailed)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        foreach (var job in batch.jobs)
                        {
                            if (job.state == TileDownloadState.Failed)
                                EditorGUILayout.LabelField(new GUIContent(job.name), new GUIContent(job.message, job.message));
                        }
                    }
                }
            }
        }

        private void DrawSummary()
        {
            EditorGUILayout.LabelField("Tiles", EditorStyles.boldLabel);
            var lod = _config.lodSettings;
            foreach (var (zoom, count) in _zoomCounts)
            {
                Rect row = EditorGUILayout.GetControlRect();
                EditorGUI.DrawRect(new Rect(row.x, row.y + 3, 12, row.height - 6), TerrainConfig.GetZoomColor(zoom, lod.minZoom, lod.maxZoom));
                row.xMin += 18;
                EditorGUI.LabelField(row, $"Zoom {zoom}", $"{count} tiles  ({FormatMeters(WebMercator.TileSize(zoom) / WebMercator.MercatorPerMeter(origin.y))})");
            }
            EditorGUILayout.LabelField("Total", $"{_preview.Count} tiles");
            if (_truncated)
                EditorGUILayout.HelpBox($"Stopped at {TileLodBuilder.MaxTiles} tiles, lower the max zoom, radius or view distance.", MessageType.Warning);
            if (_config.mode == FlightPreviewMode.Path && _config.path.Count == 0)
                EditorGUILayout.HelpBox("The path is empty, enable Draw Path and click on the minimap.", MessageType.Info);

            if (GUILayout.Button("Regenerate"))
                OnFocusChanged(false);
        }

        private void CreateConfig()
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Terrain Config", "TerrainConfig", "asset", "Where to save the Terrain Config.");
            if (string.IsNullOrEmpty(path))
                return;
            var config = CreateInstance<TerrainConfig>();
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();
            _config = config;
            OnConfigChanged();
        }

        private static string FormatMeters(double meters) => meters >= 1000 ? $"{meters / 1000:0.##} km" : $"{meters:0.#} m";

        #endregion

        #region Minimap

        private void DrawMinimap(Rect mapRect)
        {
            GUI.BeginClip(mapRect);
            Rect local = new Rect(0, 0, mapRect.width, mapRect.height);
            _minimap.rect = local;
            _minimap.origin = origin;

            if (_needsFrame && Event.current.type == EventType.Repaint && local.width > 1)
            {
                _needsFrame = false;
                _framedOnce = true;
                if (!FrameTiles())
                    FrameFocus();
            }

            Event e = Event.current;
            _toolbarRect = new Rect(6, 6, 270, 20);
            _legendRect = new Rect(local.width - 126, 6, 120, 8 + 16 * Mathf.Max(_zoomCounts.Count, 1));
            bool overUi = _toolbarRect.Contains(e.mousePosition) || _legendRect.Contains(e.mousePosition);

            // Pan id is taken before the mode's so ids stay stable when the mode handles an event.
            int panId = GUIUtility.GetControlID(FocusType.Passive);
            bool used = _config != null && activeMode.HandleInput(this, _minimap, e, !overUi && local.Contains(e.mousePosition));
            if (!used)
                HandlePanZoom(panId, e, local, overUi);

            if (e.type == EventType.Repaint)
                DrawMinimapContent(local);

            DrawMinimapUI(local);
            GUI.EndClip();
        }

        private void HandlePanZoom(int id, Event e, Rect local, bool overUi)
        {
            switch (e.GetTypeForControl(id))
            {
                case EventType.ScrollWheel:
                    if (local.Contains(e.mousePosition) && !overUi)
                    {
                        _minimap.ZoomAt(e.mousePosition, Math.Pow(1.15, -e.delta.y / 3.0));
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseDown:
                    if (local.Contains(e.mousePosition) && !overUi)
                    {
                        GUIUtility.hotControl = id;
                        _panning = true;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && _panning)
                    {
                        _minimap.Pan(e.delta);
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && _panning)
                    {
                        GUIUtility.hotControl = 0;
                        _panning = false;
                        e.Use();
                    }
                    break;

                case EventType.MouseMove:
                    if (local.Contains(e.mousePosition))
                        Repaint();
                    break;
            }

            if (_panning)
                EditorGUIUtility.AddCursorRect(local, MouseCursor.Pan);
        }

        private void DrawMinimapContent(Rect local)
        {
            EditorGUI.DrawRect(local, BackgroundColor);
            if (_config == null)
                return;

            var lod = _config.lodSettings;
            foreach (var tile in _preview)
            {
                if (!_minimap.GetTileRect(tile, out Rect r))
                    continue;
                Color color = TerrainConfig.GetZoomColor(tile.zoom, lod.minZoom, lod.maxZoom);
                Color fill = color;
                fill.a = 0.3f;
                Color outline = color;
                outline.a = r.width > 6 ? 0.9f : 0.4f;
                Handles.DrawSolidRectangleWithOutline(r, fill, outline);
            }

            activeMode.DrawOverlay(this, _minimap);

            DrawOriginMarker(_minimap.ToScreen(origin));
            _minimap.DrawScaleBar(new Vector2(12, local.height - 12));
        }

        private void DrawOriginMarker(Vector3 p)
        {
            const float size = 10;
            Handles.color = Color.black;
            Handles.DrawAAPolyLine(5, p + Vector3.left * size, p + Vector3.right * size);
            Handles.DrawAAPolyLine(5, p + Vector3.up * size, p + Vector3.down * size);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(2.5f, p + Vector3.left * size, p + Vector3.right * size);
            Handles.DrawAAPolyLine(2.5f, p + Vector3.up * size, p + Vector3.down * size);
            Handles.DrawWireDisc(p, Vector3.forward, size * 0.6f);
            GUI.Label(new Rect(p.x + size, p.y - size - 6, 60, 16), "Origin", EditorStyles.whiteMiniLabel);
        }

        private void DrawMinimapUI(Rect local)
        {
            GUILayout.BeginArea(_toolbarRect);
            using (new EditorGUILayout.HorizontalScope())
            {
                Vector2 center = local.center;
                if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(24)))
                    _minimap.ZoomAt(center, 1.5);
                if (GUILayout.Button("-", EditorStyles.miniButtonMid, GUILayout.Width(24)))
                    _minimap.ZoomAt(center, 1 / 1.5);
                if (GUILayout.Button("Frame Tiles", EditorStyles.miniButtonMid))
                    FrameTiles();
                if (GUILayout.Button("Frame Focus", EditorStyles.miniButtonMid))
                    FrameFocus();
                if (GUILayout.Button("Origin", EditorStyles.miniButtonRight))
                    _minimap.viewCenter = DoubleVector2.zero;
            }
            GUILayout.EndArea();

            if (Event.current.type != EventType.Repaint || _config == null)
                return;

            // Legend
            var lod = _config.lodSettings;
            EditorGUI.DrawRect(_legendRect, new Color(0, 0, 0, 0.45f));
            float y = _legendRect.y + 4;
            if (_zoomCounts.Count == 0)
                GUI.Label(new Rect(_legendRect.x + 6, y, 110, 16), "No tiles", EditorStyles.whiteMiniLabel);
            foreach (var (zoom, count) in _zoomCounts)
            {
                EditorGUI.DrawRect(new Rect(_legendRect.x + 6, y + 3, 10, 10), TerrainConfig.GetZoomColor(zoom, lod.minZoom, lod.maxZoom));
                GUI.Label(new Rect(_legendRect.x + 22, y, 100, 16), $"z{zoom}  {count}", EditorStyles.whiteMiniLabel);
                y += 16;
            }

            // Cursor readout
            Vector2 mouse = Event.current.mousePosition;
            if (local.Contains(mouse))
            {
                DoubleVector2 mercator = _minimap.ToMercator(mouse);
                DoubleVector2 offset = (mercator - origin) / _minimap.MercatorPerMeter;
                string text = $"{offset.x:0} m, {offset.y:0} m from origin";
                for (int i = _preview.Count - 1; i >= 0; i--)
                {
                    var tile = _preview[i];
                    if (mercator.x >= tile.minX && mercator.x <= tile.maxX && mercator.y >= tile.minY && mercator.y <= tile.maxY)
                    {
                        text += $"   |   tile z{tile.zoom}  x {tile.x}  y {tile.y}";
                        break;
                    }
                }
                var size = EditorStyles.whiteMiniLabel.CalcSize(new GUIContent(text));
                var readout = new Rect(local.width - size.x - 12, local.height - size.y - 8, size.x + 8, size.y + 2);
                EditorGUI.DrawRect(readout, new Color(0, 0, 0, 0.45f));
                GUI.Label(new Rect(readout.x + 4, readout.y, size.x, size.y), text, EditorStyles.whiteMiniLabel);
            }
        }

        private bool FrameTiles()
        {
            if (_preview.Count == 0)
                return false;
            var min = new DoubleVector2(double.MaxValue, double.MaxValue);
            var max = new DoubleVector2(double.MinValue, double.MinValue);
            foreach (var tile in _preview)
            {
                min = new DoubleVector2(Math.Min(min.x, tile.minX), Math.Min(min.y, tile.minY));
                max = new DoubleVector2(Math.Max(max.x, tile.maxX), Math.Max(max.y, tile.maxY));
            }
            _minimap.Frame(min, max);
            Repaint();
            return true;
        }

        /// <summary>Frames the radius around the focus (point or path).</summary>
        public void FrameFocus()
        {
            if (_config == null)
                return;
            var focus = _config.CreateFocus();
            if (!focus.isValid)
            {
                _minimap.viewCenter = DoubleVector2.zero;
                return;
            }
            focus.GetBounds(out DoubleVector2 min, out DoubleVector2 max);
            double margin = Math.Max(_config.lodSettings.radius, 50) * 2 * _minimap.MercatorPerMeter;
            _minimap.Frame(min - new DoubleVector2(margin, margin), max + new DoubleVector2(margin, margin));
            Repaint();
        }

        #endregion
    }
}
