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

        private static readonly Dictionary<int, (string text, byte[] mounts, float range)> _lastSent = new(256);
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
                float range = FuelRangeOf(unit);

                int id = unit.UniqueID;
                _seen.Add(id);

                bool known = _lastSent.TryGetValue(id, out var previous);
                if (!full && known && previous.text == text && SamePacked(previous.mounts)
                    && Mathf.Abs(previous.range - range) < FuelDeltaKm)
                    continue;

                _lastSent[id] = (text, _packed.ToArray(), range);
                _msg.Entries.Add(new UnitStatusMessage.Entry
                {
                    UniqueId  = id,
                    OrderText = text,
                    RangeKm   = range,
                    Mounts    = new List<UnitStatusMessage.Mount>(_scratch),
                });

                if (_msg.Entries.Count >= MaxEntriesPerPacket) Flush();
            }

            if (full) PruneLastSent();
            if (_msg.Entries.Count > 0) Flush();
        }

        /// <summary>How far RangeInKm has to move before it is worth a packet.
        ///
        /// Fuel changes every physics tick, so folding it into the change signature
        /// raw would make every airborne unit emit continuously and turn a
        /// change-detected stream into a per-tick one. Two kilometres is a few seconds
        /// of cruise for a fast jet and a small fraction of any airframe's tank, and
        /// the 10 s full sweep bounds the staleness regardless. This is a re-anchor,
        /// not a feed: the client keeps burning locally in between, which is what
        /// keeps the gauge moving smoothly rather than stepping.</summary>
        private const float FuelDeltaKm = 2f;

        /// <summary>RangeInKm for an air unit, 0 for anything else - ships and subs
        /// have the property too but nothing burns it, so it would be a constant on
        /// the wire and a permanent no-op at the far end.</summary>
        private static float FuelRangeOf(ObjectBase unit)
        {
            if (!(unit is Aircraft) && !(unit is Helicopter)) return 0f;
            return unit.RangeInKm?.Value ?? 0f;
        }

        /// <summary>Client: re-anchor an air unit's tank to the host's, then let the
        /// game recompute everything that hangs off it.
        ///
        /// UpdateFuelConsumption is the game's own re-derivation - Aircraft's load path
        /// calls exactly this pair, RangeInKm followed by UpdateFuelConsumption(0f)
        /// (Aircraft.cs:1801-1802) - so ActualRangeInKm, RangeOnMap, EnduranceInSec and
        /// the endurance ring all fall out of it rather than being set by hand here.
        /// The two overloads differ only in signature.</summary>
        private static void ApplyFuel(ObjectBase unit, float rangeKm)
        {
            if (rangeKm <= 0f) return;   // not an air unit on the host, or not known yet

            var range = unit.RangeInKm;
            if (range == null) return;
            range.Value = rangeKm;

            if (unit is Aircraft a)        a.UpdateFuelConsumption(0f);
            else if (unit is Helicopter h) h.UpdateFuelConsumption();
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

                var target = ws?._targetObject;
                int targetId = (target != null && !target.IsDestroyed) ? target.UniqueID : 0;

                _scratch.Add(new UnitStatusMessage.Mount
                {
                    ExecutingEngageTask = exec,
                    AutoEngaging        = auto,
                    EngageState         = state,
                    TargetId            = targetId,
                });
                _packed.Add((byte)((exec ? 1 : 0) | (auto ? 2 : 0)));
                _packed.Add(state);
                // Into the change signature, so a mount switching targets re-sends even
                // when its engage state has not moved. Four bytes rather than a hash:
                // this list is compared, not stored per unit beyond the last packet.
                _packed.Add((byte)targetId);
                _packed.Add((byte)(targetId >> 8));
                _packed.Add((byte)(targetId >> 16));
                _packed.Add((byte)(targetId >> 24));
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
                ApplyToUnit(e, fromNetwork: true);
            }
        }

        /// <summary>Client: re-impose the held picture. Cheap - a unit already
        /// correct is a string compare and a few bool writes.</summary>
        public static void ClientReassert()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            foreach (var kv in _desired)
                ApplyToUnit(kv.Value, fromNetwork: false);
        }

        /// <summary>
        /// CLIENT: impose the host's status line, once per frame, from LateUpdate.
        ///
        /// ONE WRITER, ONE POINT. CurrentOrderText is a ReactiveProperty owned by
        /// whoever wrote last, and on a guest two things were writing it. This manager
        /// shipped the host's finished line for every visible unit and re-sent all of
        /// them on the 10 s full sweep, while the guest's OWN state machines wrote the
        /// local line every frame - AircraftStates.MovingInFormation.onUpdate is three
        /// lines and all three are a write, and the vessel and helicopter twins are the
        /// same. The log signature was pairs 0.02 s apart with 10.2 s between pairs
        /// ("Engage Air Contact 7020 with AIM-54A…" / "Joining Formation", over and
        /// over), and the player read it as flicker on every non-leader aircraft and
        /// ship.
        ///
        /// LateUpdate is the fix rather than a faster reassert: Unity guarantees it
        /// runs after every MonoBehaviour Update - so after every state machine has had
        /// its say - and before the frame is drawn. The host's line is therefore the
        /// last write of the frame, every frame, and nothing is ever rendered
        /// half-argued. Racing the state machines on a 0.5 s timer could only ever swap
        /// which of them won, which is precisely what the flicker was.
        ///
        /// AND IT SETTLES THE OTHER HALF. The 0.5 s reassert used to rewrite this line
        /// too, comparing against the unit's CURRENT value - so it also undid anything
        /// that corrected a line locally, twice a second, for reasons it could not see.
        /// Moving the write out of that loop leaves the reassert to the mounts and
        /// gives the line exactly one producer. A local correction is no longer
        /// something to be fought or protected: it belongs in this method, where the
        /// host's text is turned into the text this machine shows.
        /// </summary>
        public static void ClientLateAssertText()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (_desired.Count == 0) return;

            foreach (var kv in _desired)
            {
                var e = kv.Value;
                var unit = StateSerializer.FindById(e.UniqueId);
                if (unit == null || unit.IsDestroyed) continue;

                var line = unit.CurrentOrderText;
                if (line == null) continue;

                // Reference-equal on the common path: the value written is the same
                // string instance held in _desired, so a line nothing has touched
                // since the last frame costs one pointer compare.
                if (line.Value == e.OrderText) continue;

                line.Value = e.OrderText;
            }
        }

        /// <summary>
        /// CLIENT: train each engaging mount on the target the host says it is engaging.
        ///
        /// The slew is WeaponSystem.alignToTarget → _mount.rotate, driven by the
        /// launcher's own engagement - which a client never has, because the shot is
        /// relayed and the round returns as a replica. So mounts sat where they were and
        /// missiles simply appeared as the ship fired. CIWS were the exception and gave
        /// the game away: CosmeticEventHandler hands them a target when the CiwsStart
        /// burst arrives, so they DID slew, but only from the moment they opened fire -
        /// the reported "shooting off to the side and turning in".
        ///
        /// Calling the stock alignToTarget rather than steering the mount ourselves is
        /// the whole point. It is virtual, so a launcher gets
        /// WeaponSystemLauncher's override - which routes to RotateToFixedAngles when
        /// _vwp._useLaunchAngle, or picks the nearest preferred arc - and a gun gets the
        /// base implementation. Those are the same overrides the HOST calls, so whatever
        /// the mount does there it now does here. A mount that genuinely rotates to a
        /// constant hull-relative angle before launch is not a bug to be corrected; it
        /// is the behaviour, and reproducing it is the job.
        ///
        /// SAFE TO DRIVE FROM OUTSIDE: alignToTarget clears _isInRestPosition, stamps
        /// _lastUsageTime and rotates. It does not fire, does not touch the engage task,
        /// and cannot start one - the client's own gun path is redirected upstream by
        /// Patch_V2_GunFire_Upstream regardless. The _lastUsageTime stamp is wanted:
        /// it keeps the stock rest-return from dragging the mount back while it aims.
        ///
        /// The flags come from the host and only ever describe a mount already engaging,
        /// so this cannot aim a mount the host has at rest.
        /// </summary>
        public static void ClientLateAimMounts()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (_desired.Count == 0) return;

            foreach (var kv in _desired)
            {
                var e = kv.Value;
                if (e.Mounts == null || e.Mounts.Count == 0) continue;

                var unit = StateSerializer.FindById(e.UniqueId);
                if (unit == null || unit.IsDestroyed) continue;

                var systems = unit._obp?._weaponSystems;
                if (systems == null) continue;

                int count = Mathf.Min(systems.Count, e.Mounts.Count);
                for (int i = 0; i < count; i++)
                {
                    var m = e.Mounts[i];
                    if (m.TargetId == 0) continue;
                    if (!m.ExecutingEngageTask && !m.AutoEngaging) continue;

                    var ws = systems[i];
                    if (ws == null || ws.Inoperable.Value) continue;

                    var target = ReplicaRegistry.Find(m.TargetId) ?? StateSerializer.FindById(m.TargetId);
                    if (target == null || target.IsDestroyed) continue;

                    // Mirrors the host's own call sites: guns and CIWS pass false
                    // (WeaponSystemGun.cs:330, WeaponSystemCIWS.cs:675), launchers pass
                    // their fixed vertical launch angle (WeaponSystemLauncher.cs:606).
                    bool fixedAngle = ws is WeaponSystemLauncher
                                   && ws._vwp != null && ws._vwp._fixVerticalLaunchAngleForLauncher;

                    try { ws.alignToTarget(target.getUnityPosition(), fixedAngle, 0); }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[UnitStatus] {unit.name} mount {i} alignToTarget threw: {ex.Message}");
                    }
                }
            }
        }

        private static void ApplyToUnit(UnitStatusMessage.Entry e, bool fromNetwork)
        {
            var unit = StateSerializer.FindById(e.UniqueId);
            // No warning on a miss: a replica may not be built yet, and a unit that
            // died here still sits in _desired until the host's next full sweep.
            if (unit == null || unit.IsDestroyed) return;

            // The status LINE is not written here - see ClientLateAssertText. It is the
            // one field on this entry a local writer also competes for, so it is imposed
            // once a frame from LateUpdate instead of from two cadences at once.

            // Fuel on a REAL update only. The reassert replays this same entry twice a
            // second off _desired, and re-imposing a fuel figure the client has since
            // burned past would drag the gauge backwards on every pump - a freeze, and
            // a visibly jumping one, instead of the periodic re-anchor this is meant
            // to be. Between anchors the client's own UpdateFuelConsumption keeps the
            // number moving, which is what makes it smooth.
            if (fromNetwork) ApplyFuel(unit, e.RangeKm);

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
