using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SeapowerMultiplayer
{
    internal enum LogKind : byte
    {
        Log       = 0,
        Exception = 1,
        Metric    = 2,
        Session   = 3,
    }

    /// <summary>One captured line. A struct so the ring is a single allocation.</summary>
    internal struct LogRecord
    {
        public long    UtcMs;
        public LogKind Kind;
        public byte    Level;     // BepInEx LogLevel bits; 0 for non-log kinds
        public string  Source;    // "SPMP", "Unity", or another plugin's source name
        public string  Message;   // already redacted
        public string? Stack;     // exceptions only, already redacted
        public int     Repeat;    // occurrences collapsed by the dedupe set
    }

    /// <summary>
    /// Captures the log stream into a bounded ring.
    ///
    /// Registered into <c>BepInEx.Logging.Logger.Listeners</c>, which is the only
    /// way to see all 300-odd <c>Plugin.Log</c> call sites without editing any of
    /// them. It also gets what the disk log does not: BepInEx filters per
    /// listener, and the disk sink's LogLevels drops Debug, so this sees strictly
    /// more than LogOutput.log.
    ///
    /// THREADING: BepInEx raises LogEvent on whatever thread called Log.*, and
    /// network code logs from LiteNetLib/Steam background threads. Unlike
    /// <see cref="Telemetry"/> this cannot be main-thread-only, so the ring is
    /// locked. Redaction deliberately happens BEFORE the lock is taken - it is
    /// the expensive part and holding the lock across it would let a chatty
    /// background thread stall the main thread.
    /// </summary>
    internal sealed class LogRingSink : ILogListener
    {
        private const int Capacity        = 2048;
        private const int MaxDedupeKeys   = 200;

        internal static LogRingSink? Active { get; private set; }

        private readonly LogRecord[] _ring = new LogRecord[Capacity];
        private readonly object _gate = new();
        private int _head;      // next write slot
        private int _count;     // valid entries, <= Capacity
        private int _dropped;   // overwritten before being drained

        // Unity exceptions repeat every frame; collapse them or the ring is
        // nothing but one stack trace. Same trick the base game's Sentry hook uses.
        private readonly Dictionary<string, int> _exceptionSeen = new();

        internal static void Attach()
        {
            if (Active != null) return;
            var sink = new LogRingSink();
            Active = sink;
            BepInEx.Logging.Logger.Listeners.Add(sink);
            Application.logMessageReceived += OnUnityLog;
        }

        internal static void Detach()
        {
            var sink = Active;
            if (sink == null) return;
            Active = null;
            Application.logMessageReceived -= OnUnityLog;
            try { BepInEx.Logging.Logger.Listeners.Remove(sink); } catch { }
        }

        // ── Capture ──────────────────────────────────────────────────────────

        public void LogEvent(object sender, LogEventArgs args)
        {
            if (args?.Data == null) return;

            string source = args.Source?.SourceName ?? "?";

            // Unity's Error/Exception lines are owned by OnUnityLog, which is the
            // only path that carries a stack trace. Taking them here too would
            // double-record every exception.
            bool unityError = source == "Unity"
                && (args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            if (unityError) return;

            Push(LogKind.Log, (byte)args.Level,
                 source == PluginInfo.PLUGIN_NAME ? "SPMP" : source,
                 Redactor.Scrub(args.Data.ToString()), null);

            if ((args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                Analytics.NoteError("log.error");
            else if ((args.Level & LogLevel.Warning) != 0)
                Analytics.NoteWarning();
        }

        private static void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;

            var sink = Active;
            if (sink == null) return;

            string key = condition + "|" + FirstFrame(stack);
            lock (sink._gate)
            {
                if (sink._exceptionSeen.TryGetValue(key, out int n))
                {
                    // Already recorded once. Count it and stop - no regex, no
                    // ring write, so an every-frame exception stays cheap.
                    sink._exceptionSeen[key] = n + 1;
                    return;
                }
                if (sink._exceptionSeen.Count < MaxDedupeKeys)
                    sink._exceptionSeen[key] = 1;
            }

            sink.Push(LogKind.Exception, (byte)LogLevel.Error, "Unity",
                      Redactor.Scrub(condition), Redactor.Scrub(stack));

            Analytics.NoteError("unity.exception");
        }

        /// <summary>Metric and session lines enter the same ring so ordering is
        /// preserved end to end. Message is pre-serialised JSON.</summary>
        internal void PushStructured(LogKind kind, string json)
            => Push(kind, 0, "SPMP", json, null);

        private void Push(LogKind kind, byte level, string source, string message, string? stack)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_gate)
            {
                if (_count == Capacity) _dropped++;   // about to overwrite an undrained entry

                _ring[_head] = new LogRecord
                {
                    UtcMs   = now,
                    Kind    = kind,
                    Level   = level,
                    Source  = source,
                    Message = message,
                    Stack   = stack,
                    Repeat  = 1,
                };
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        // ── Drain ────────────────────────────────────────────────────────────

        /// <summary>Moves everything out of the ring, oldest first. Called from
        /// the uploader thread.</summary>
        internal List<LogRecord> Drain(out int dropped)
        {
            var outp = new List<LogRecord>(Capacity);
            lock (_gate)
            {
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                    outp.Add(_ring[(start + i) % Capacity]);

                dropped = _dropped;
                _dropped = 0;
                _count = 0;
                _head = 0;
                _exceptionSeen.Clear();
            }
            return outp;
        }

        internal int Count { get { lock (_gate) return _count; } }

        internal void Clear()
        {
            lock (_gate)
            {
                _count = 0;
                _head = 0;
                _dropped = 0;
                _exceptionSeen.Clear();
                Array.Clear(_ring, 0, _ring.Length);
            }
        }

        public void Dispose() { }

        private static string FirstFrame(string? stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            int nl = stack!.IndexOf('\n');
            return nl < 0 ? stack : stack.Substring(0, nl);
        }
    }
}
