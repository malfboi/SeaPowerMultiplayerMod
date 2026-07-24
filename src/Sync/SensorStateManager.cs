using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Replicates which sensors are emitting, host → client.
    ///
    /// The client simulates its own sensor DETECTION, but it does not decide when
    /// anything switches on: AI is host-only (Patch_V2_AI_OnFixedUpdate_Suppress),
    /// so every AI-driven radar toggle happens on the host and the client's copy of
    /// that unit keeps whatever state the join save had. A SAM site that went active
    /// to engage stayed dark client-side, produced no ESM contact, and could not be
    /// engaged with anti-radiation weapons.
    ///
    /// This runs in BOTH co-op and PvP, unlike the contact picture. Emission is
    /// physical world state, not intelligence: the radar really is radiating, and
    /// the client still has to detect it with its own ESM at its own range and
    /// bearing. Withholding it does not hide anything from the player - it just
    /// makes their sensors wrong.
    /// </summary>
    public static class SensorStateManager
    {
        /// <summary>Sensors are addressed by bit position, so this is the ceiling on
        /// how many we can describe per unit. Real units carry a handful; the guard
        /// exists so an outlier silently loses its tail rather than corrupting the
        /// mask by shifting past 63.</summary>
        private const int MaxSensorsPerUnit = 64;

        private static bool _warnedTooManySensors;

        // ── Host capture ──────────────────────────────────────────────────────

        private static readonly Dictionary<int, (ulong mask, ulong emitMask, ulong guideMask)> _lastSent = new(256);
        private static readonly SensorStateMessage _msg = new();
        private static readonly HashSet<int> _seen = new(256);
        private static float _nextFullSweep;

        private const float FullSweepInterval = 10f;
        private const int   MaxEntriesPerPacket = 64;   // 12 B each

        /// <summary>True when this sensor is actually radiating. For a radar that is
        /// <c>Radar._isActive</c> (what gates CalculateRawSensorContacts) or
        /// <c>_isGuiding</c> - RadarCalculator's anti-radiation check accepts either.
        /// Anything that is not a radar has no separate emission concept, so the
        /// switch is the emission.</summary>
        private static bool IsEmitting(SensorSystem s)
        {
            // Radars only. A passive receiver - ESM, sonar in listening mode, visual -
            // radiates nothing, so folding its IsOn in here would claim emissions that
            // do not exist. That is not just cosmetic: it made the detection report
            // read "36 radiating" for a scene where three radars were up, and filled
            // the untracked list with airliners and launchers that were never
            // detectable in the first place.
            if (s is SensorSystemRadar radar)
                return radar._isGuiding || (radar.getRadar()?._isActive ?? false);
            return false;
        }

        /// <summary>Builds all three masks for one unit, or false if it has no
        /// sensors. Emit and guide stay separate bits: RadarCalculator's
        /// Targeting-type branch requires <c>_isGuiding</c> SPECIFICALLY, so a
        /// merged "emitting" bit cannot be applied back to the right field.</summary>
        private static bool TryBuildMask(ObjectBase unit, out ulong mask, out ulong emitMask, out ulong guideMask)
        {
            mask = 0UL;
            emitMask = 0UL;
            guideMask = 0UL;
            var sensors = unit._obp?._sensorSystems;
            if (sensors == null || sensors.Count == 0) return false;

            int count = sensors.Count;
            if (count > MaxSensorsPerUnit)
            {
                count = MaxSensorsPerUnit;
                if (!_warnedTooManySensors)
                {
                    _warnedTooManySensors = true;
                    Plugin.Log.LogWarning($"[Sensors] {unit.name} has {sensors.Count} sensors - " +
                        $"only the first {MaxSensorsPerUnit} replicate.");
                }
            }

            for (int i = 0; i < count; i++)
            {
                var s = sensors[i];
                if (s == null) continue;
                if (s.IsOn.Value) mask |= 1UL << i;
                if (s is SensorSystemRadar radar)
                {
                    if (radar.getRadar()?._isActive ?? false) emitMask  |= 1UL << i;
                    if (radar._isGuiding)                     guideMask |= 1UL << i;
                }
            }
            return true;
        }

        /// <summary>Host: sweep every unit and send the masks that changed.</summary>
        public static void HostBroadcast()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            bool full = Time.unscaledTime >= _nextFullSweep;
            if (full) _nextFullSweep = Time.unscaledTime + FullSweepInterval;

            _msg.Reset();
            _msg.IsFull = full;
            _sweepPacketsSent = 0;
            _emittingCount = 0;
            _emitting.Clear();
            _seen.Clear();

            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var unit = all[i];
                if (unit == null || unit.UniqueID == 0) continue;
                // Exclude weapons (their seekers are not emitters we replicate) rather
                // than whitelisting unit types: isUnit() is false for anything
                // Base-derived, which would silently skip a static site that has a
                // radar - exactly the case this exists for.
                if (unit is WeaponBase) continue;
                if (!TryBuildMask(unit, out ulong mask, out ulong emitMask, out ulong guideMask)) continue;

                int id = unit.UniqueID;
                _seen.Add(id);
                if ((emitMask | guideMask) != 0UL)
                {
                    _emittingCount++;
                    if (full && _emitting.Count < 16) _emitting.Add($"{unit.name}[{Describe(emitMask | guideMask)}]");
                }

                var current = (mask, emitMask, guideMask);
                bool known = _lastSent.TryGetValue(id, out var previous);
                if (!full && known && previous == current)
                    continue;

                // Emitter changes are rare and are exactly what goes wrong, so each
                // one is logged rather than hidden behind a debug flag.
                if (!full)
                    Plugin.Log.LogInfo($"[Sensors] {unit.name} (id={id}) " +
                        $"on {Describe(previous.mask)} → {Describe(mask)}, " +
                        $"radiating {Describe(previous.emitMask)} → {Describe(emitMask)}, " +
                        $"guiding {Describe(previous.guideMask)} → {Describe(guideMask)}");

                _lastSent[id] = current;
                _msg.Entries.Add(new SensorStateMessage.Entry
                {
                    UniqueId = id, OnMask = mask, EmitMask = emitMask, GuideMask = guideMask,
                });

                if (_msg.Entries.Count >= MaxEntriesPerPacket) Flush();
            }

            if (full) PruneLastSent();
            if (_msg.Entries.Count > 0 || (full && _sweepPacketsSent == 0)) Flush();

            if (full)
                Plugin.Log.LogInfo($"[Sensors] Host sweep: {_seen.Count} units with sensors, " +
                    $"{_emittingCount} emitting, sent {_sweepPacketsSent} packet(s). " +
                    $"Emitting: {(_emitting.Count == 0 ? "(none)" : string.Join(", ", _emitting))}");
        }

        private static int _emittingCount;

        /// <summary>Names of units emitting this sweep. The point of the log is to
        /// answer "is the SAM site in here at all", which a count cannot.</summary>
        private static readonly List<string> _emitting = new(16);

        /// <summary>Bit indices that are on, for logs - "none" or "0,3,7".</summary>
        private static string Describe(ulong mask)
        {
            if (mask == 0UL) return "none";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < MaxSensorsPerUnit; i++)
            {
                if (((mask >> i) & 1UL) == 0UL) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(i);
            }
            return sb.ToString();
        }

        private static int _sweepPacketsSent;

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
            _sweepPacketsSent++;
            _msg.Reset(); // clears IsFull - continuation packets must not re-assert it
        }

        // ── Client apply ──────────────────────────────────────────────────────

        /// <summary>The host's emitter picture, held so it can be re-asserted. The
        /// host only sends on change, so without this any local code that flipped a
        /// sensor back would win until the next full sweep - up to ten seconds of a
        /// radar being wrong in exactly the situation this exists to fix.</summary>
        private static readonly Dictionary<int, (ulong mask, ulong emitMask, ulong guideMask)> _desired = new(256);

        /// <summary>Host-decided radiating + guiding state, per radar, for the
        /// OnUpdate postfix to re-impose each frame. Keyed by the SensorSystem
        /// itself so the hot path is one dictionary lookup with no index search.</summary>
        internal static readonly Dictionary<SensorSystem, (bool emit, bool guide)> DesiredEmit = new(256);

        public static void ApplyReceived(SensorStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            // Cleared together: DesiredEmit is keyed by sensor instance, so a scene
            // reload would otherwise leave dead keys accumulating. Both are
            // repopulated by the ApplyToUnit calls immediately below.
            if (msg.IsFull) { _desired.Clear(); DesiredEmit.Clear(); }

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                _desired[e.UniqueId] = (e.OnMask, e.EmitMask, e.GuideMask);
                ApplyToUnit(e.UniqueId, e.OnMask, e.EmitMask, e.GuideMask, fromNetwork: true);
            }

            if (msg.IsFull)
            {
                Plugin.Log.LogInfo($"[Sensors] Client full sweep received: {msg.Entries.Count} unit(s)");
                ReportDetectionOfEmitters(msg);
            }
        }

        /// <summary>
        /// Diagnostic: for every unit the host says is RADIATING, report what the
        /// client's own sensors have made of it.
        ///
        /// Emitter replication is now confirmed working, yet the emitter still does
        /// not become a contact - so the question has moved downstream, to whether
        /// the client's detection pipeline turns a live emission into a track at
        /// all. Contacts exist only as ECS track entities built by the local sensor
        /// sim, so this reads the outcome rather than the input: does a Vehicle
        /// exist for the emitter, and which of our sensors are holding it.
        /// </summary>
        private static void ReportDetectionOfEmitters(SensorStateMessage msg)
        {
            var table = Globals._playerTaskforce?.PlottingTable;
            if (table == null)
            {
                Plugin.Log.LogWarning("[Sensors] No player plotting table - cannot report detection");
                return;
            }

            int radiating = 0, tracked = 0, missing = 0, notRadiatingLocally = 0;
            var detail = new List<string>(16);

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                ulong hostEmit = e.EmitMask | e.GuideMask;
                if (hostEmit == 0UL) continue;
                radiating++;

                var unit = StateSerializer.FindById(e.UniqueId);
                if (unit == null) { missing++; continue; }

                // Read the LOCAL radars back rather than trusting that our write
                // held. Anything the host says is radiating but that is dark here is
                // the failure this whole thing is chasing, so it is stated outright.
                string local = "?";
                var sensors = unit._obp?._sensorSystems;
                if (sensors != null)
                {
                    ulong localEmit = 0UL;
                    int count = Mathf.Min(sensors.Count, MaxSensorsPerUnit);
                    for (int b = 0; b < count; b++)
                        if (sensors[b] != null && IsEmitting(sensors[b])) localEmit |= 1UL << b;
                    local = Describe(localEmit);
                    if (localEmit != hostEmit) notRadiatingLocally++;
                }

                var vehicle = table.VehicleForObject(unit);
                if (vehicle != null) tracked++;

                if (detail.Count < 16)
                    detail.Add($"{unit.name} host[{Describe(hostEmit)}] local[{local}] " +
                        $"{(vehicle == null ? "NO-TRACK" : $"track{vehicle.Id}/{vehicle.DetectingSensors}")}");
            }

            Plugin.Log.LogInfo($"[Sensors] Client vs host emitters: {radiating} radiating on host, " +
                $"{notRadiatingLocally} mismatched locally, {tracked} held as contacts, " +
                $"{missing} not found. {(detail.Count == 0 ? "" : string.Join("  |  ", detail))}");
        }

        /// <summary>Client: re-impose the held picture. Cheap - every sensor already
        /// in the right state is a bool compare and nothing else.</summary>
        public static void ClientReassert()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            foreach (var kv in _desired)
                ApplyToUnit(kv.Key, kv.Value.mask, kv.Value.emitMask, kv.Value.guideMask, fromNetwork: false);
        }

        private static void ApplyToUnit(int uniqueId, ulong mask, ulong emitMask, ulong guideMask, bool fromNetwork)
        {
            var unit = StateSerializer.FindById(uniqueId);
            if (unit == null)
            {
                if (fromNetwork)
                    Plugin.Log.LogWarning($"[Sensors] id={uniqueId} not found locally - emitter state dropped");
                return;
            }

            var sensors = unit._obp?._sensorSystems;
            if (sensors == null) return;

            int count = Mathf.Min(sensors.Count, MaxSensorsPerUnit);
            for (int b = 0; b < count; b++)
            {
                var s = sensors[b];
                if (s == null) continue;

                // Radiating state first, and independently of the switch. A
                // fire-control radar is switched on but only radiates while it holds
                // a target, and that target assignment is host-side AI the client
                // never runs - so nothing local will ever set this, and nothing
                // local fights us over it either.
                if (s is SensorSystemRadar radar)
                {
                    bool wantEmit  = ((emitMask  >> b) & 1UL) != 0UL;
                    bool wantGuide = ((guideMask >> b) & 1UL) != 0UL;

                    // Recorded per sensor so the OnUpdate postfix can re-impose both
                    // every frame. Setting them here alone is not enough: a search
                    // radar's OnUpdate drives _isActive straight off the local IsOn,
                    // undoing a one-shot write within a frame.
                    DesiredEmit[radar] = (wantEmit, wantGuide);

                    // _isGuiding is what RadarCalculator's Targeting-type branch
                    // demands - an illuminator "emits" by guiding, and _isActive
                    // alone never satisfies that check. Direct field write; the
                    // only local reader that acts on it client-side is
                    // AlignToTarget, which no-ops on a null target.
                    if (radar._isGuiding != wantGuide)
                    {
                        radar._isGuiding = wantGuide;
                        Plugin.Log.LogInfo($"[Sensors] {unit.name} sensor {b} " +
                            $"({radar.GetType().Name}) guiding → {(wantGuide ? "ON" : "OFF")}");
                    }

                    var r = radar.getRadar();
                    if (r != null && r._isActive != wantEmit)
                    {
                        r.setActive(wantEmit);
                        Plugin.Log.LogInfo($"[Sensors] {unit.name} sensor {b} " +
                            $"({radar.GetType().Name}) radiating → {(wantEmit ? "ON" : "OFF")}");
                    }
                }

                bool want = ((mask >> b) & 1UL) != 0UL;
                if (s.IsOn.Value == want) continue;

                // ApplyingFromNetwork keeps the SensorSystem.Enable/Disable patches
                // from treating this as a local player toggle and echoing it back
                // upstream.
                OrderHandler.ApplyingFromNetwork = true;
                try
                {
                    if (want) s.Enable(false);
                    else      s.Disable(false);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Sensors] {unit.name} sensor {b} " +
                        $"{(want ? "enable" : "disable")} threw: {ex.Message}");
                    continue;
                }
                finally { OrderHandler.ApplyingFromNetwork = false; }

                // Enable() silently returns when the sensor is destroyed, inoperable
                // or _allowTurnOn is false, and warm-up means IsOn may lag by a tick.
                // Report the refusal once per sensor so a radar that will NEVER come
                // up client-side is visible instead of looking like a lost packet.
                if (s.IsOn.Value != want)
                    WarnRefusedOnce(unit, b, want, s);
                else
                    Plugin.Log.LogInfo($"[Sensors] {unit.name} sensor {b} " +
                        $"({s.GetType().Name}) → {(want ? "ON" : "OFF")}");
            }
        }

        private static readonly HashSet<(int, int, bool)> _warnedRefusals = new();

        private static void WarnRefusedOnce(ObjectBase unit, int index, bool want, SensorSystem s)
        {
            var key = (unit.UniqueID, index, want);
            if (!_warnedRefusals.Add(key)) return;
            // "Switch", not emission: Enable/Disable warm up over several ticks, so
            // this fires routinely and does NOT mean the radar is dark. Radiating
            // state is held separately and forced every frame by
            // Patch_SensorSystemRadar_OnUpdate_ClientEmit.
            Plugin.Log.LogWarning($"[Sensors] {unit.name} sensor {index} ({s.GetType().Name}) " +
                $"switch did not go {(want ? "ON" : "OFF")} yet - inoperable={s.Inoperable.Value} " +
                $"allowTurnOn={s._allowTurnOn}. Will keep retrying (emission is unaffected).");
        }

        public static void Reset()
        {
            _lastSent.Clear();
            _seen.Clear();
            _desired.Clear();
            DesiredEmit.Clear();
            _warnedRefusals.Clear();
            _nextFullSweep = 0f;
            _sweepPacketsSent = 0;
        }
    }
}
