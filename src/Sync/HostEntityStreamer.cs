using System.Collections;
using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// v2 host-side unit streamer: captures ALL units (both taskforces - unified
    /// host authority) and broadcasts quantized EntityStateBatch packets.
    /// Missiles and everything else (units, torpedoes) stream on independent
    /// timers - rates are configurable: [Sync] MissileStateHz (default 20) and
    /// UnitStateHz (default 10).
    ///
    /// Staggering (explicit requirement - no single-tick spikes):
    ///  - changed units send immediately (quantized-domain change detection),
    ///  - idle units heartbeat at 1.5 s with a per-entity phase offset spread
    ///    across 15 buckets, so heartbeats stay distributed across ticks,
    ///  - missile-only ticks interleave between unit ticks, spreading send load,
    ///  - batches self-split at ~1100 B (LiteNetTransport silently upgrades
    ///    larger unreliable payloads to reliable - must never trip that).
    /// </summary>
    public class HostEntityStreamer : MonoBehaviour
    {
        private const int   EntriesPerPacket =
            (ProtocolInfo.MaxStatePacketBytes - EntityStateBatchMessage.HeaderWireSize)
            / EntityStateBatchMessage.EntryWireSize;

        // Idle heartbeat (mirrors v1 ChangeTracker semantics, quantized domain).
        // Internal because the client's sample-staleness window is derived from it -
        // an unchanged unit appears no more often than this, so anything shorter on
        // the client silently stops correcting slow-moving units.
        internal const float HeartbeatInterval = 1.5f;
        private const int   StaggerBuckets    = 15;

        // Change thresholds in the QUANTIZED domain
        private const int LatLonE7Threshold = 90;   // ~1 m
        private const float HeightThreshold = 0.5f; // m
        private const int HeadingQThreshold = 91;   // ~0.5°
        private const int AngleQThreshold   = 200;  // 2° (pitch/roll, centideg)
        private const int SpeedQThreshold   = 2;    // 0.2 kt
        private const int CmdSpeedQThreshold = 5;   // 0.5 kt
        private const int RudderQThreshold  = 2;    // 1°
        private const float DesiredAltThreshold = 5f;
        private const int IntegrityThreshold = 2;   // 1%

        private uint _serverTick;
        private float _nextMissileSend;
        private float _nextUnitSend;
        private float _nextNearSend;

        // ── Client viewport (interest management) ────────────────────────────
        // Only a handful of units are ever on screen, and they are the only ones
        // whose smoothness the player can judge. Streaming those faster costs very
        // little. Until a hint arrives every unit is "far", which is exactly the
        // flat-rate behaviour this had before.
        private static Vector3 _clientFocus;
        private static float _clientFocusRadiusSq;
        private static bool _hasClientFocus;

        public static void OnViewportHint(Messages.ViewportHintMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            _clientFocus = msg.ToLocalUnity();
            _clientFocusRadiusSq = msg.RadiusUnity * msg.RadiusUnity;
            _hasClientFocus = true;
        }

        public static void ClearViewportHint() => _hasClientFocus = false;

        /// <summary>Horizontal distance only: y is metres while x/z are ~67 m Unity
        /// units, so a 3D compare would measure altitude against a horizontal radius
        /// and never call an airborne unit near.</summary>
        private static bool IsNearClient(Vector3 worldPos)
        {
            if (!_hasClientFocus) return false;
            float dx = worldPos.x - _clientFocus.x;
            float dz = worldPos.z - _clientFocus.z;
            return dx * dx + dz * dz <= _clientFocusRadiusSq;
        }

        private readonly EntityStateBatchMessage _msg = new();
        private readonly List<EntityState> _captured = new(256);

        private readonly Dictionary<int, EntityState> _lastSent     = new();
        private readonly Dictionary<int, float>       _nextHeartbeat = new();

        // Per-entity heartbeat phase, derived from the id (sequential ids spread
        // uniformly across buckets) - no per-entity storage needed.
        private static float PhaseOf(int id)
            => HeartbeatInterval * (((id % StaggerBuckets) + StaggerBuckets) % StaggerBuckets) / StaggerBuckets;

        private void Start() => StartCoroutine(StreamLoop());

        private IEnumerator StreamLoop()
        {
            while (true)
            {
                yield return null;

                float now = Time.unscaledTime;
                bool missileTick = now >= _nextMissileSend;
                bool unitTick    = now >= _nextUnitSend;
                bool nearTick    = now >= _nextNearSend;
                if (!missileTick && !unitTick && !nearTick) continue;
                if (missileTick)
                    _nextMissileSend = now + 1f / Mathf.Clamp(Plugin.Instance.CfgMissileStateHz.Value, 1, 60);
                if (unitTick)
                    _nextUnitSend = now + 1f / Mathf.Clamp(Plugin.Instance.CfgUnitStateHz.Value, 1, 60);
                if (nearTick)
                    _nextNearSend = now + 1f / Mathf.Clamp(Plugin.Instance.CfgUnitStateHzNear.Value, 1, 60);

                if (!Plugin.Instance.CfgIsHost.Value) continue;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try
                {
                    CaptureAndSend(missileTick, unitTick, nearTick);
                    EntityCensusManager.HostTick();
                    if (unitTick) FlightDeckStreamer.HostTick();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[EntityStreamer] Exception: {ex}");
                }
            }
        }

        public void ClearTracking()
        {
            _lastSent.Clear();
            _nextHeartbeat.Clear();
        }

        private void CaptureAndSend(bool missileTick, bool unitTick, bool nearTick)
        {
            _serverTick++;

            _captured.Clear();
            if (unitTick || nearTick)
            {
                // On a near-only tick, skip everything outside the client's view
                // before doing any per-unit work - the geo conversion is the
                // expensive part and far units are not due to send yet.
                bool nearOnly = !unitTick;
                CaptureUnits(UnitType.Vessel,     UnitRegistry.Vessels,     nearOnly);
                CaptureUnits(UnitType.Submarine,  UnitRegistry.Submarines,  nearOnly);
                CaptureUnits(UnitType.Aircraft,   UnitRegistry.AircraftList, nearOnly);
                CaptureUnits(UnitType.Helicopter, UnitRegistry.Helicopters, nearOnly);
                CaptureUnits(UnitType.LandUnit,   UnitRegistry.LandUnits,   nearOnly);
                CaptureUnits(UnitType.Torpedo,    UnitRegistry.Torpedoes,   nearOnly);
            }
            // Missiles/bombs: always-dirty while flying - bypass the heartbeat filter
            if (missileTick)
            {
                CaptureUnits(UnitType.Missile,    UnitRegistry.Missiles, false);
                CaptureUnits(UnitType.Bomb,       UnitRegistry.Bombs,    false);
            }

            // Deck-phase aircraft ride a carrier-relative channel instead of the
            // world-space stream (a moving deck can't be represented world-space)
            if (unitTick)
            {
                SendDeckStates(UnitRegistry.AircraftList);
                SendDeckStates(UnitRegistry.Helicopters);
            }

            if (_captured.Count == 0) return;

            float now = Time.unscaledTime;
            float gameSeconds = Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                              + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                              + Singleton<SeaPower.Environment>.Instance.Seconds;

            _msg.Reset();
            _msg.ServerTick  = _serverTick;
            _msg.GameSeconds = gameSeconds;

            for (int i = 0; i < _captured.Count; i++)
            {
                var current = _captured[i];
                int id = current.EntityId;

                // Weapons stream at full rate (they're always moving); units use
                // change-detection + phase-staggered heartbeats.
                bool isWeapon = current.Kind == UnitType.Missile || current.Kind == UnitType.Torpedo
                             || current.Kind == UnitType.Bomb;

                bool include;
                if (isWeapon)
                {
                    include = true;
                }
                else if (!_lastSent.TryGetValue(id, out var previous))
                {
                    include = true; // new entity - send now
                }
                else if (HasChanged(in current, in previous))
                {
                    include = true;
                }
                else
                {
                    include = now >= (_nextHeartbeat.TryGetValue(id, out var hb) ? hb : 0f);
                }

                if (!include) continue;

                if (!isWeapon)
                {
                    _lastSent[id] = current;
                    // Next heartbeat aligned to this entity's id-derived phase so idle
                    // units stay distributed across ticks instead of clumping.
                    float ph = PhaseOf(id);
                    _nextHeartbeat[id] = now - ((now - ph) % HeartbeatInterval) + HeartbeatInterval;
                }

                _msg.Entries.Add(current);
                if (_msg.Entries.Count >= EntriesPerPacket)
                {
                    NetworkManager.Instance.BroadcastToClients(_msg, LiteNetLib.DeliveryMethod.Unreliable);
                    _msg.Entries.Clear();
                }
            }

            if (_msg.Entries.Count > 0)
                NetworkManager.Instance.BroadcastToClients(_msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

        private readonly DeckStateMessage _deckMsg = new();

        /// <summary>Carrier-relative transforms for aircraft in the flight-deck
        /// pipeline (launch taxi/catapult, landing rollout) - anything whose root
        /// is parented under another unit. The client parents its puppet to the
        /// carrier and lerps the local transform.</summary>
        private void SendDeckStates<T>(IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                var parentTr = unit.transform.parent;
                if (parentTr == null) continue;
                var carrier = parentTr.GetComponentInParent<ObjectBase>();
                if (carrier == null || carrier == unit) continue;

                Vector3 local = carrier.transform.InverseTransformPoint(unit.transform.position);
                float relYaw  = unit.transform.eulerAngles.y - carrier.transform.eulerAngles.y;

                _deckMsg.AircraftId = unit.UniqueID;
                _deckMsg.CarrierId  = carrier.UniqueID;
                _deckMsg.LocalX     = local.x;
                _deckMsg.LocalY     = local.y;
                _deckMsg.LocalZ     = local.z;
                _deckMsg.LocalYawQ  = GeoCodec.PackHeading(relYaw);
                NetworkManager.Instance.BroadcastToClients(_deckMsg, LiteNetLib.DeliveryMethod.Unreliable);
            }
        }

        private void CaptureUnits<T>(UnitType kind, IReadOnlyList<T> units, bool nearOnly) where T : ObjectBase
        {
            bool weaponKind = kind == UnitType.Missile || kind == UnitType.Torpedo || kind == UnitType.Bomb;
            bool airKind    = kind == UnitType.Aircraft || kind == UnitType.Helicopter;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;
                if (nearOnly && !IsNearClient(unit.transform.position)) continue;
                // Destroyed weapons leave the stream (Impact/Despawn events own the ending).
                // Un-launched (mounted/racked) weapons are transform children of their
                // platform - streaming them world-space rips them off the wing.
                if (weaponKind && (unit.IsDestroyed || !((WeaponBase)(ObjectBase)unit).isLaunched())) continue;
                // Sonobuoys are LiveLocal on the client (own drift/sensing sim) -
                // streaming them would fight it
                if (kind == UnitType.Bomb
                    && ((WeaponBase)(ObjectBase)unit)._ap?._subType == Ammunition.Type.Sonobuoy) continue;
                // Deck-phase aircraft (parented under a carrier) ride the
                // DeckState channel, not the world-space stream
                if (airKind && unit.transform.parent != null) continue;

                var geo = Utils.worldPositionFromUnityToLongLat(
                    unit.transform.position, Globals._currentCenterTile);

                float desiredAlt = 0f;
                if (unit is Aircraft || unit is Helicopter || unit is Submarine)
                    desiredAlt = (float)unit.DesiredAltitude.Value;

                float rudder = unit is Vessel v ? StateSerializer.GetRudderAngle(v) : 0f;

                // A slider/typed speed does not move getTelegraph(), so it needs its
                // own field - otherwise the client's local sim keeps chasing the
                // stale preset and fights the position corrections.
                float cmdKts = StateSerializer.CustomCommandKnots(unit);
                bool customSpeed = !float.IsNaN(cmdKts);

                byte flags = 0;
                if (customSpeed) flags |= EntityState.FlagCustomSpeed;
                if (unit.IsDestroyed) flags |= EntityState.FlagDestroyed;
                var comps = unit.Compartments;
                if (comps != null && comps._isSinking) flags |= EntityState.FlagSinking;

                float integrity = comps?.IntegrityPercentage ?? 100f;

                _captured.Add(new EntityState
                {
                    Kind       = kind,
                    EntityId   = unit.UniqueID,
                    LonDeg     = geo._longitude,
                    LatDeg     = geo._latitude,
                    HeightM    = (float)geo._height,
                    HeadingQ   = GeoCodec.PackHeading(unit.transform.eulerAngles.y),
                    PitchQ     = GeoCodec.PackAngleCdeg(unit.transform.eulerAngles.x),
                    RollQ      = GeoCodec.PackAngleCdeg(unit.transform.eulerAngles.z),
                    SpeedQ     = GeoCodec.PackSpeedKts(unit._velocityInKnots),
                    Telegraph  = (sbyte)Mathf.Clamp(unit.getTelegraph(), sbyte.MinValue, sbyte.MaxValue),
                    RudderQ    = (sbyte)Mathf.Clamp(Mathf.RoundToInt(rudder * 2f), sbyte.MinValue, sbyte.MaxValue),
                    DesiredAlt = desiredAlt,
                    CmdSpeedQ  = customSpeed
                        ? (short)Mathf.Clamp(Mathf.RoundToInt(cmdKts * 10f), short.MinValue, short.MaxValue)
                        : (short)0,
                    Flags      = flags,
                    Integrity  = (byte)Mathf.Clamp(Mathf.RoundToInt(integrity * 2f), 0, 255),
                });
            }
        }

        private static bool HasChanged(in EntityState a, in EntityState b)
        {
            // Critical transitions - always immediate
            if (a.Flags != b.Flags) return true;
            if (a.Telegraph != b.Telegraph) return true;

            if (System.Math.Abs(a.LonDeg - b.LonDeg) * 1e7 > LatLonE7Threshold) return true;
            if (System.Math.Abs(a.LatDeg - b.LatDeg) * 1e7 > LatLonE7Threshold) return true;
            if (Mathf.Abs(a.HeightM - b.HeightM) > HeightThreshold) return true;

            int hdgDiff = Mathf.Abs(a.HeadingQ - b.HeadingQ);
            if (hdgDiff > 32768) hdgDiff = 65536 - hdgDiff; // circular
            if (hdgDiff > HeadingQThreshold) return true;

            if (Mathf.Abs(a.SpeedQ - b.SpeedQ) > SpeedQThreshold) return true;
            // Aircraft hold a mach number, so their commanded knots drift with height -
            // a wider band than SpeedQ keeps that from re-sending every tick.
            if (Mathf.Abs(a.CmdSpeedQ - b.CmdSpeedQ) > CmdSpeedQThreshold) return true;
            if (Mathf.Abs(a.RudderQ - b.RudderQ) > RudderQThreshold) return true;
            if (Mathf.Abs(a.Integrity - b.Integrity) > IntegrityThreshold) return true;
            if (Mathf.Abs(a.DesiredAlt - b.DesiredAlt) > DesiredAltThreshold) return true;
            if (Mathf.Abs(a.PitchQ - b.PitchQ) > AngleQThreshold) return true;
            if (Mathf.Abs(a.RollQ - b.RollQ) > AngleQThreshold) return true;
            return false;
        }
    }
}
