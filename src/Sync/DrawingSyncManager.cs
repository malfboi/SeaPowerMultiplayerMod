using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Shares the map drawing layer - markers, relative markers, rulers, circles,
    /// polygons and text - between the two players. These are annotations, not
    /// simulation state: they live only in
    /// <c>Globals._mainGameViewModel.Map.DrawingLayer.Graphics</c> and were
    /// previously transferred only inside the join save, so anything either player
    /// drew afterwards stayed on their own screen.
    ///
    /// Serialization reuses the game's own SaveLoadManager.WriteDrawingData /
    /// LoadDrawingData, which already handle every drawing type and resolve
    /// unit-anchored drawings by UniqueID (stable across machines). Both are
    /// private statics, so they are reached by reflection rather than reimplemented.
    ///
    /// Changes are detected by polling the serialized form: the drawing layer has
    /// no change event, and a handful of drawings costs nothing to re-serialize
    /// once a second.
    /// </summary>
    public static class DrawingSyncManager
    {
        private const string Section = "DrawingLayer";

        /// <summary>Serialized layer at the last send/receive. A poll that produces
        /// something different is a local edit worth broadcasting.</summary>
        private static string _baseline = "";
        private static bool _suppressPoll;

        /// <summary>The first poll of a session only records the baseline. Both
        /// players start from the same drawings (they came in the join save), so
        /// sending them straight back at each other buys nothing.</summary>
        private static bool _primed;

        // ── Game-code reflection ──────────────────────────────────────────────

        private static MethodInfo? _writeDrawingData;
        private static MethodInfo? _loadDrawingData;
        private static bool _resolved;
        private static bool _warned;

        private static bool Resolve()
        {
            if (_resolved) return _writeDrawingData != null && _loadDrawingData != null;
            _resolved = true;

            _writeDrawingData = AccessTools.Method(typeof(SaveLoadManager), "WriteDrawingData");
            _loadDrawingData  = AccessTools.Method(typeof(SaveLoadManager), "LoadDrawingData");

            if (_writeDrawingData == null || _loadDrawingData == null)
            {
                Plugin.Log.LogWarning("[Drawings] SaveLoadManager.WriteDrawingData/LoadDrawingData not found - " +
                    "map markers will not be shared between players");
                return false;
            }
            return true;
        }

        private static IniHandler? MissionIni
            => Singleton<SceneCreator>.InstanceExists(false) ? Singleton<SceneCreator>.Instance.MissionIni : null;

        // ── Capture ───────────────────────────────────────────────────────────

        /// <summary>IniHandler keys its storage off a file name - the parameterless
        /// constructor leaves that empty and every accessor then throws - so the
        /// scratch handler is named. Re-constructing under the same name resets that
        /// one cache slot, so polling does not accumulate anything.</summary>
        private const string ScratchIni = "SPMP_DrawingScratch.ini";

        /// <summary>Runs the game's writer into a scratch ini and lifts the
        /// [DrawingLayer] section out of it. Null when the layer is unavailable.</summary>
        private static Dictionary<string, string>? Capture()
        {
            if (!Resolve()) return null;
            if (Globals._mainGameViewModel?.Map?.DrawingLayer?.Graphics == null) return null;

            var scratch = new IniHandler(ScratchIni);
            _writeDrawingData!.Invoke(null, new object[] { scratch });

            return scratch.doesSectionExist(Section)
                ? new Dictionary<string, string>(scratch.GetSectionKeyValues(Section))
                : new Dictionary<string, string>();
        }

        private static string Fingerprint(Dictionary<string, string> data)
        {
            // Ordinal-sorted so dictionary iteration order can never masquerade as
            // an edit and start a send loop.
            var keys = new List<string>(data.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
                sb.Append(keys[i]).Append('=').Append(data[keys[i]]).Append(';');
            return sb.ToString();
        }

        // ── Poll / send ───────────────────────────────────────────────────────

        /// <summary>Called on a timer from ContactSyncStreamer. Sends the whole
        /// layer when it differs from what was last sent or received.</summary>
        public static void PollAndSend()
        {
            if (_suppressPoll) return;
            if (!ContactSyncManager.CoopSessionActive) return; // never hand an opponent your plot

            Dictionary<string, string>? data;
            try { data = Capture(); }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning($"[Drawings] Capture failed, marker sharing disabled: {ex.Message}");
                }
                return;
            }
            if (data == null) return;

            string fingerprint = Fingerprint(data);
            if (!_primed)
            {
                _primed = true;
                _baseline = fingerprint;
                return;
            }
            if (fingerprint == _baseline) return;
            _baseline = fingerprint;

            var msg = new DrawingSyncMessage();
            foreach (var kv in data)
                msg.Entries.Add((kv.Key, kv.Value));

            NetworkManager.Instance.SendToOther(msg);
            Plugin.Log.LogInfo($"[Drawings] Sent {msg.Entries.Count} map drawing(s)");
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        /// <summary>Replaces the local drawing layer with the sender's. The game's
        /// loader only ADDS, so the existing graphics are cleared first.</summary>
        public static void ApplyReceived(DrawingSyncMessage msg)
        {
            if (Plugin.Instance.CfgPvP.Value) return; // co-op only
            if (!Resolve()) return;

            var layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer?.Graphics == null) return;

            var ini = MissionIni;
            if (ini == null) return;

            var data = new Dictionary<string, string>(msg.Entries.Count);
            for (int i = 0; i < msg.Entries.Count; i++)
                data[msg.Entries[i].key] = msg.Entries[i].value;

            // The poll must not see our own rebuild as a local edit and echo it back.
            _suppressPoll = true;
            try
            {
                layer.Graphics.Clear();

                // LoadDrawingData reads the live mission ini. Swapping the section
                // is safe: it is only consulted at load time, and a save writes the
                // section fresh from the live graphics.
                ini.RemoveSection(Section);
                if (data.Count > 0)
                {
                    ini.AddSection(Section, data);
                    _loadDrawingData!.Invoke(null, Array.Empty<object>());
                }

                // Baseline off what we would now PRODUCE, not off what arrived.
                // The two need not be byte-identical (the ini format round-trips
                // doubles through ToString/TryParse), and any difference would read
                // as a local edit on the next poll and bounce the layer back and
                // forth between the players forever.
                var rebuilt = Capture();
                _baseline = rebuilt != null ? Fingerprint(rebuilt) : Fingerprint(data);
                Plugin.Log.LogInfo($"[Drawings] Applied {data.Count} map drawing(s) from the other player");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Drawings] Apply failed: {ex.Message}");
            }
            finally { _suppressPoll = false; }
        }

        public static void Reset()
        {
            _baseline = "";
            _suppressPoll = false;
            _primed = false;
        }
    }
}
