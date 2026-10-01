using System;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.SatContext.Editor
{
    [CustomEditor(typeof(SatContext))]
    public class SatContextEditor : UnityEditor.Editor
    {
        private GUIStyle _falseStyle;
        private GUIStyle _trueStyle;
        public void OnEnable()
        {
            _falseStyle = new GUIStyle();
            _falseStyle.normal.textColor = Color.red;
            _trueStyle = new GUIStyle();
            _trueStyle.normal.textColor = Color.green;
        }
        public GUIStyle BoolStyle(bool b) => b ? _trueStyle : _falseStyle;
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            SatContext satContext = (SatContext)target;
            if (GUILayout.Button("Check Paths"))
            {
                satContext.CheckPaths();
            }
            if (GUILayout.Button("Load Data"))
            {
                serializedObject.Update();
                satContext.LoadData();
                EditorUtility.SetDirty(satContext);
            }
            GUILayout.Space(10);
            // Header
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Name", EditorStyles.boldLabel);
                GUILayout.Label("Status", EditorStyles.boldLabel, GUILayout.Width(70));
                GUILayout.Label("Size", EditorStyles.boldLabel, GUILayout.Width(70));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool imgLoaded = satContext.segmentationImage.loaded;
                GUILayout.Label("Segmentation Image");
                GUILayout.Label(imgLoaded ? "loaded" : "not loaded", BoolStyle(imgLoaded), GUILayout.Width(70));
                GUILayout.Label(imgLoaded ? $"{satContext.segmentationImage.width}x{satContext.segmentationImage.height}" : "-", GUILayout.Width(70));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool imgLoaded = satContext.segmentationConfidenceImage != null;
                GUILayout.Label("Segmentation Confidence Image");
                GUILayout.Label(imgLoaded ? "loaded" : "not loaded", BoolStyle(imgLoaded), GUILayout.Width(70));
                GUILayout.Label(imgLoaded ? $"{satContext.segmentationConfidenceImage.width}x{satContext.segmentationConfidenceImage.height}" : "-", GUILayout.Width(70));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool imgLoaded = satContext.colorImage != null;
                GUILayout.Label("Color Image");
                GUILayout.Label(imgLoaded ? "loaded" : "not loaded", BoolStyle(imgLoaded), GUILayout.Width(70));
                GUILayout.Label(imgLoaded ? $"{satContext.colorImage.width}x{satContext.colorImage.height}" : "-", GUILayout.Width(70));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool imgLoaded = satContext.heightmapImage.loaded;
                GUILayout.Label("Heightmap Image");
                GUILayout.Label(imgLoaded ? "loaded" : "not loaded", BoolStyle(imgLoaded), GUILayout.Width(70));
                GUILayout.Label(imgLoaded ? $"{satContext.heightmapImage.shape[1]}x{satContext.heightmapImage.shape[0]}" : "-", GUILayout.Width(70));
            }
        }
    }
}