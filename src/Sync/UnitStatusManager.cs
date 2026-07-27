using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Replicates the bottom-row status of a unit, host → client: its order/status
    /// line and the engagement state of each weapon mount.
    ///
    /// Neither is decided locally. <c>CurrentOrderText</c> is written by the
    /// aircraft/submarine state machines, UnitFormation, Morale and RefuelTask;
    /// per-mount engagement comes out of the host's engage pipeline
    /// (HandleEngageTasks → WeaponSystem). All of it is host-only under unified
    /// authority, so a client-side ship the player had ordered to engage showed a
    /// blank status line and "Ready" on every mount while the host showed
    /// "Engaging (Firing)".
    ///
    /// Only the flags GetStatus() reads are carried, not the engagement itself:
    /// the client is being told what to display, not being handed a firing
    /// decision. Reload timers, Offline and Empty are deliberately untouched -
    /// those come from the local reload clock and the damage/ammo channels.
    /// </summary>
    public static class UnitStatusManager
    {
        /// <summary>Mounts are addressed by index in a byte-counted list, so this is
        /// the ceiling per unit. Real units carry a few dozen at most; the guard
        /// exists so an outlier loses its tail rather than overflowing the count.</summary>
        private const int MaxMountsPerUnit = 255;

        private const float FullSweepInterval = 10f;
        private const int   MaxEntriesPerPacket = 32;

        private static bool _warnedTooManyMounts;

        // ── Host capture ──────────────────────────────────────────────────────

        private static readonly Dictionary<int, (string text, byte[] mounts)> _lastSent = new(256);
        private static readonly UnitStatusMessage _msg = new();
        private static readonly HashSet<int> _seen = new(256);
        private static readonly List<UnitStatusMessage.Mount> _scratch = new(64);
        private static readonly List<byte> _packed = new(128);
        private static float _nextFullSweep;

        /// <summary>PvP: only the remote player's own taskforce. A status line that
        /// names the track a ship is engaging is intelligence, and so is seeing an
        /// opponent's mounts go to Engaging. Co-op: the whole friendly side, which
        /// both players command and already share a contact picture for.</summary>
        private static bool IsClientVisible(ObjectBase unit, bool pvp)
        {
            var tf = unit._taskforce;
            if (tf == null) return false;
            if (pvp) return tf == Globals._enemyTaskforce;
            return tf.Side == Taskforce.TfType.Player || tf.Side == Taskforce.TfType.Ally;
        }

        /// <summary>Host: sweep the units the client can inspect and send what changed.</summary>
        public static void HostBroadcast()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            bool full = Time.unscaledTime >= _nextFullSweep;
            if (full) _nextFullSweep = Time.unscaledTime + FullSweepInterval;

            _msg.Reset();
            _msg.IsFull = full;
            _seen.Clear();

            bool pvp = Plugin.Instance.CfgPvP.Value;
            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var unit = all[i];
                if (unit == null || unit.UniqueID == 0) continue;
                if (unit is WeaponBase) continue;
                if (unit.IsDestroyed) continue;
                if (!IsClientVisible(unit, pvp)) continue;

                string text = unit.CurrentOrderText?.Value ?? "";
                BuildMounts(unit);

                int id = unit.UniqueID;
                _seen.Add(id);

                bool known = _lastSent.TryGetValue(id, out var previous);
                if (!full && known && previous.text == text && SamePacked(previous.mounts))
                    continue;

                _lastSent[id] = (text, _packed.ToArray());
                _msg.Entries.Add(new UnitStatusMessage.Entry
                {
                    UniqueId  = id,
                    OrderText = text,
                    Mounts    = new List<UnitStatusMessage.Mount>(_scratch),
                });

                if (_msg.Entries.Count >= MaxEntriesPerPacket) Flush();
            }

            if (full) PruneLastSent();
            if (_msg.Entries.Count > 0) Flush();
        }

        /// <summary>Fills _scratch (wire form) and _packed (change-detection form)
        /// with this unit's per-mount engagement state.</summary>
        private static void BuildMounts(ObjectBase unit)
        {
            _scratch.Clear();
            _packed.Clear();

            var systems = unit._obp?._weaponSystems;
            if (systems == null) return;

            int count = systems.Count;
            if (count > MaxMountsPerUnit)
            {
                count = MaxMountsPerUnit;
                if (!_warnedTooManyMounts)
                {
                    _warnedTooManyMounts = true;
                    Plugin.Log.LogWarning($"[UnitStatus] {unit.name} has {systems.Count} weapon systems - " +
                        $"only the first {MaxMountsPerUnit} replicate.");
                }
            }

            for (int i = 0; i < count; i++)
            {
                var ws = systems[i];
                // The index IS the address, so a null slot still takes its place.
                bool exec = ws != null && ws._executingEngageTask;
                bool auto = ws != null && ws._isAutoEngaging;
                byte state = (byte)(ws != null ? (int)ws._engageState : 0);

                _scratch.Add(new UnitStatusMessage.Mount
                {
                    ExecutingEngageTask = exec,
                    AutoEngaging        = auto,
                    EngageState         = state,
                });
                _packed.Add((byte)((exec ? 1 : 0) | (auto ? 2 : 0)));
                _packed.Add(state);
            }
        }

        private static bool SamePacked(byte[]? previous)
        {
            if (previous == null) return _packed.Count == 0;
            if (previous.Length != _packed.Count) return false;
            for (int i = 0; i < previous.Length; i++)
                if (previous[i] != _packed[i]) return false;
            return true;
        }

        /// <summary>Forget units that are gone, so a re-used id is not mistaken for
        /// unchanged and skipped.</summary>
        private static void PruneLastSent()
        {
            if (_lastSent.Count == _seen.Count) return;
            var stale = new List<int>();
            foreach (var id in _lastSent.Keys)
                if (!_seen.Contains(id)) stale.Add(id);
            for (int i = 0; i < stale.Count; i++) _lastSent.Remove(stale[i]);
        }

        private static void Flush()
        {
            NetworkManager.Instance.BroadcastToClients(_msg, DeliveryMethod.ReliableOrdered);
            Telemetry.Count("v2.unitStatusSent");
            _msg.Reset(); // clears IsFull - continuation packets must not re-assert it
        }

        // ── Client apply ──────────────────────────────────────────────────────

        /// <summary>The host's picture, held so it can be re-imposed. The host only
        /// sends on change, so anything local that overwrites a status line (an
        /// aircraft's own flight state still runs client-side) would otherwise win
        /// until the next full sweep.</summary>
        private static readonly Dictionary<int, UnitStatusMessage.Entry> _desired = new(256);

        /// <summary>Host-decided engagement per weapon system, for the GetStatus
        /// postfix to render. Keyed by the WeaponSystem itself so the hot path is one
        /// dictionary lookup. Deliberately NOT written into the system's own
        /// _executingEngageTask / _engageState fields: those are simulation inputs,
        /// and WeaponSystemLauncher.OnUpdate reads _executingEngageTask to drive the
        /// whole engage pipeline - abort checks, container choice, alignment, hatches.
        /// Setting it on a client mount that never ran launch() sent OnUpdate into
        /// that pipeline with no _ammoForEngage or target, throwing an NRE every
        /// frame; because the systems loop runs inside ObjectBase.OnLazyUpdate, the
        /// throw also aborted everything after it on that unit, which is why a
        /// firing submarine's propeller and hatch animations froze.</summary>
        internal static readonly Dictionary<WeaponSystem, (bool exec, bool auto, byte state)> DesiredEngage = new(256);

        public static void ApplyReceived(UnitStatusMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            // Cleared together: DesiredEngage is keyed by system instance, so a scene
            // reload would otherwise leave dead keys accumulating. Both are
            // repopulated by the ApplyToUnit calls below.
            if (msg.IsFull) { _desired.Clear(); DesiredEngage.Clear(); }

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                _desired[e.UniqueId] = e;
                ApplyToUnit(e);
            }
        }

        /// <summary>Client: re-impose the held picture. Cheap - a unit already
        /// correct is a string compare and a few bool writes.</summary>
        public static void ClientReassert()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            foreach (var kv in _desired)
                ApplyToUnit(kv.Value);
        }

        private static void ApplyToUnit(UnitStatusMessage.Entry e)
        {
            var unit = StateSerializer.FindById(e.UniqueId);
            // No warning on a miss: a replica may not be built yet, and a unit that
            // died here still sits in _desired until the host's next full sweep.
            if (unit == null || unit.IsDestroyed) return;

            var line = unit.CurrentOrderText;
            if (line != null && line.Value != e.OrderText)
                line.Value = e.OrderText;

            var systems = unit._obp?._weaponSystems;
            if (systems == null || e.Mounts == null) return;

            int count = Mathf.Min(systems.Count, e.Mounts.Count);
            for (int i = 0; i < count; i++)
            {
                var ws = systems[i];
                if (ws == null) continue;
                var m = e.Mounts[i];

                // Held for the GetStatus postfix to render - never written into the
                // system's own engage fields. See DesiredEngage.
                DesiredEngage[ws] = (m.ExecutingEngageTask, m.AutoEngaging, m.EngageState);
            }
        }

        public static void Reset()
        {
            _lastSent.Clear();
            _seen.Clear();
            _desired.Clear();
            DesiredEngage.Clear();
            _scratch.Clear();
            _packed.Clear();
            _nextFullSweep = 0f;
        }
    }
}
