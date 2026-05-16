using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer
{
    internal enum MpLogLevel
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3,
        Trace = 4,
    }

    internal static class MpLog
    {
        private static readonly Dictionary<string, float> LastLogTimes = new();
        private static readonly HashSet<string> OnceKeys = new();

        public static MpLogLevel CurrentLevel
        {
            get
            {
                string raw = Plugin.Instance?.CfgLogLevel?.Value ?? "Info";
                if (Enum.TryParse(raw, ignoreCase: true, out MpLogLevel level))
                    return level;
                return MpLogLevel.Info;
            }
        }

        public static bool IsDebugEnabled =>
            CurrentLevel >= MpLogLevel.Debug || (Plugin.Instance?.CfgVerboseDebug?.Value ?? false);

        public static bool IsTraceEnabled => CurrentLevel >= MpLogLevel.Trace;

        public static void Error(string category, string message) =>
            Plugin.Log.LogError(Format(category, message));

        public static void Warn(string category, string message)
        {
            if (CurrentLevel >= MpLogLevel.Warning)
                Plugin.Log.LogWarning(Format(category, message));
        }

        public static void Info(string category, string message)
        {
            if (CurrentLevel >= MpLogLevel.Info)
                Plugin.Log.LogInfo(Format(category, message));
        }

        public static void Debug(string category, string message)
        {
            if (IsDebugEnabled)
                Plugin.Log.LogDebug(Format(category, message));
        }

        public static void Trace(string category, string message)
        {
            if (IsTraceEnabled)
                Plugin.Log.LogDebug(Format(category, message));
        }

        public static void WarnOnce(string key, string category, string message)
        {
            if (!OnceKeys.Add(key)) return;
            Warn(category, message);
        }

        public static void WarnThrottle(string key, string category, string message, float intervalSeconds)
        {
            if (!ShouldLog(key, intervalSeconds)) return;
            Warn(category, message);
        }

        public static void InfoThrottle(string key, string category, string message, float intervalSeconds)
        {
            if (!ShouldLog(key, intervalSeconds)) return;
            Info(category, message);
        }

        private static bool ShouldLog(string key, float intervalSeconds)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (LastLogTimes.TryGetValue(key, out float last) && now - last < intervalSeconds)
                return false;

            LastLogTimes[key] = now;
            return true;
        }

        private static string Format(string category, string message) =>
            $"[{category}] {message}";
    }
}
