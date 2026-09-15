using System;
using System.Collections.Generic;
using UnityEngine;

namespace Logger
{
    [Serializable]
    public class LoggerData
    {
        public Color color;
        public bool enabled;
        public LoggerData(Color color, bool enabled)
        {
            this.color = color;
            this.enabled = enabled;
        }
        public static LoggerData GetDefault()
        {
            return new LoggerData(Color.dodgerBlue, true);
        }
    }
    [CreateAssetMenu(fileName = "LoggerSettingsData", menuName = "Editor/LoggerSettingsData")]
    public class LoggerSettingsData : ScriptableObject
    {
        [SerializeField]
        public Dictionary<string, LoggerData> channels;
    }
}