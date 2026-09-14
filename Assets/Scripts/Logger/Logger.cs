using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Logger
{
    public static class Logger
    {
        private static LoggerSettingsData _data;
        /// <summary>
        /// [WARNING] this is a global setting, proceed with caution
        /// </summary>
        public static LoggerSettingsData data { 
            get{
                if (_data == null) 
                    Load();
                return _data;
            }
        }
        [InitializeOnLoadMethod]
        private static void Load()
        {
            var guids = AssetDatabase.FindAssets("t:LoggerSettingsData");

            if (guids.Length == 0)
            {
                Debug.LogWarning("LoggerSettingsData.asset could not be found.");
                return;
            }

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            
            if (guids.Length > 1) Debug.LogWarning($"Multiple LoggerSettingsData assets found. Using {path}." );
            if (guids.Length == 1) Debug.Log($"LoggerSettingsData.asset found. Using {path}.");
            _data = AssetDatabase.LoadAssetAtPath<LoggerSettingsData>(path);
        }
        private static string GetColor(string channel)
        {
            var color = data.channels.GetValueOrDefault(channel, LoggerData.GetDefault()).color;
            return ColorUtility.ToHtmlStringRGB(color);
        }
        private static bool IsChannelEnabled(string channel)
        {
            return data.channels.GetValueOrDefault(channel, LoggerData.GetDefault()).enabled;
        }
        /// <summary>
        /// Logs normal message to console
        /// </summary>
        /// <param name="message">Text being logged</param>
        /// <param name="channel">Channel you want to log to ("Default" by default)</param>
        /// <param name="sender">Sender object from which Log is called, leave null or empty if doesnt matter</param>
        [Conditional("ENABLE_LOG")]
        public static void Log(string message, string channel = "Default", Object sender = null)
        {
            string color = GetColor(channel);
            message =  $"<color=#{color}>{message}</color>";
            if (IsChannelEnabled(channel))
            {
                Debug.Log(message, sender);
            }
        }
        /// <summary>
        /// Logs warning message to console
        /// </summary>
        /// <param name="message">Text being logged</param>
        /// <param name="channel">Channel you want to log to ("Default" by default)</param>
        /// <param name="sender">Sender object from which Log is called, leave null or empty if doesnt matter</param>
        [Conditional("ENABLE_LOG")]
        public static void LogWarning(string message, string channel = "Default", Object sender = null)
        {
            string color = GetColor(channel);
            message =  $"<color=yellow>[WARN]</color> <color=#{color}>{message}</color>";
            if (IsChannelEnabled(channel))
            {
                Debug.LogWarning(message, sender);
            }
        }
        /// <summary>
        /// Logs error message to console
        /// </summary>
        /// <param name="message">Text being logged</param>
        /// <param name="channel">Channel you want to log to ("Default" by default)</param>
        /// <param name="sender">Sender object from which Log is called, leave null or empty if doesnt matter</param>
        [Conditional("ENABLE_LOG")]
        public static void LogError(string message, string channel = "Default", Object sender = null)
        {
            string color = GetColor(channel);
            message =  $"<color=red>[ERROR]</color> <color=#{color}>{message}</color>";
            if (IsChannelEnabled(channel))
            {
                Debug.LogError(message, sender);
            }
        }
#if UNITY_EDITOR
        public static void Test()
        {
            foreach (string channelEnum in _data.channels.Keys)
            {
                Log($"Testing {channelEnum.ToString()} channel", channelEnum, _data);
            }
        }
#endif
    }
}