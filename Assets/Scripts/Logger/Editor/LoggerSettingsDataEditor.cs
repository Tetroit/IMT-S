using System;
using UnityEditor;
using UnityEngine;

namespace Logger.Editor
{
    [CustomEditor(typeof(LoggerSettingsData))]
    public class LoggerSettingsDataEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            LoggerSettingsData loggerSettingsData = (LoggerSettingsData)target;
            base.OnInspectorGUI();
            if (GUILayout.Button("Test"))
            {
                Logger.Test();
            }
        }
    }
}