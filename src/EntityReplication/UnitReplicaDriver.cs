using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// v2 client-side unit state application (replaces StateApplier's unit path).
    /// The host streams ALL units (unified host authority); the client applies
    /// every entry - including its own taskforce - using the v1-proven hybrid
    /// model: local propulsion/flight physics integrate between snapshots, the
    /// stream feeds command-state (telegraph/rudder/desired altitude) so local
    /// sim targets what the host commanded, and position/heading/speed
    /// corrections pull the transform toward the host's track.
    ///
    /// Corrections are applied EVERY FRAME (<see cref="Tick"/>), not on packet
    /// arrival. Arrival only records the sample. Two properties of that split
    /// are what remove the jitter:
    ///  - the sample is dead-reckoned forward by its own age (latency + time
    ///    since arrival) before it is used as a target, so a moving unit is no
    ///    longer yanked backwards onto a stale position once per packet, and
    ///  - the residual is eased in with a frame-rate-independent exponential
    ///    instead of a fixed fraction landing in one frame, so it reads as
    ///    continuous motion rather than a 10 Hz sawtooth.
    /// Both matter more the higher the time compression, because the sim covers
    /// more ground between two real-time packets.
    /// </summary>
    public static class UnitReplicaDriver
    {
        // ── Correction rates (continuous, per real second) ───────────────────
        // Applied as `1 - exp(-k * dt)`: k is 1/timeConstant, so k=6 converges
        // ~95% in half a second. The old per-packet fractions (0.7 pos / 0.8
        // heading at 10 Hz) are equivalent to k=12 / k=16 - deliberately gentler
        // here, since extrapolation now removes the systematic error these were
        // fighting and a softer pull rejects packet-to-packet noise.
        private const float ShipPosSharpness     = 6f;
        private const float ShipHeadingSharpness = 8f;
        private const float ShipSpeedSharpness   = 4f;
        private const float AirAttitudeSharpness = 10f;
        private const float AirSpeedSharpness    = 4f;
        private const float AirNearSharpness     = 4f;
        private const float AirFarSharpness      = 2f;

        // Hard resync tier (horizontal, Unity units - ~67 m each). Aircraft have
        // their own tiers inline in DriveAircraft.
        private const float ShipSnapThreshold = 75f;

        // Unity units per (knot · game-second) - the game's own conversion
        private const float UnityPerKnotSecond = 0.0076554087f;

        // Past this age the sample is guesswork: the local sim (running on the
        // host's mirrored telegraph + rudder) tracks better than a stale target,
        // so stop correcting rather than drag the unit back to where it was.
        private const float MaxSampleAgeRealSec = 0.75f;

        // Compiled setter for Vessel._setRudderAngle (autopilot steering target).
        // SetRudderToHeading writes this field directly, so a method patch can't
        // feed it - we mirror the host's value so local propulsion turns the
        // same way between corrections.
        private static readonly Action<Vessel, float>? _setRudderAngle;

        static UnitReplicaDriver()
        {
            var field = AccessTools.Field(typeof(Vessel), "_setRudderAngle");
            if (field != null)
            {
                var vParam = Expression.Parameter(typeof(Vessel));
                var fParam = Expression.Parameter(typeof(float));
                var assign = Expression.Assign(Expression.Field(vParam, field), fParam);
                _setRudderAngle = Expression.Lambda<Action<Vessel, float>>(assign, vParam, fParam).Compile();
            }
        }

        /// <summary>Latest host sample for one unit, held until the next packet.</summary>
        private sealed class Sample
        {
            public ObjectBase Unit = null!;
            public UnitType Kind;
            public double LonDeg, LatDeg;
            public float HeightM, Heading, Pitch, Roll, Speed;
            public float RecordGameTime;   // GameTime.time at arrival (pauses with the sim)
            public float RecordRealTime;   // Time.unscaledTime at arrival
            public float LatencyGameSec;   // one-way flight time, in game-seconds
            public bool  WarnedFarDrift;
        }

        private static readonly Dictionary<int, Sample> _samples = new();
        private static readonly List<int> _toRemove = new();

        // ── Alignment (first batch after scene load re-keys local IDs) ───────
        private static bool _pendingAlignment;
        public static void SetPendingAlignment() => _pendingAlignment = true;

        // Last applied server tick (drop stale/reordered unreliable packets per entity batch)
        private static uint _lastServerTick;

        public static void Apply(EntityStateBatchMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            // Unreliable channel: tolerate reorder within a small window, drop very stale
            if (msg.ServerTick + 10 < _lastServerTick) { Telemetry.Count("v2.staleBatchDropped"); return; }
            if (msg.ServerTick > _lastServerTick) _lastServerTick = msg.ServerTick;

            if (_pendingAlignment && msg.Entries.Count > 0)
            {
                _pendingAlignment = false;
                RunAlignment(msg);
                return; // apply starts next batch, on aligned IDs
            }

            // Keep the client's auto-defence switch asserted (cheap re-check)
            Suppression.EnforceDefenseFlag();

            float latencyGame = LatencyGameSeconds();

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];

                // Weapon kinds route to the kinematic replica driver
                if (e.Kind == UnitType.Missile || e.Kind == UnitType.Torpedo || e.Kind == UnitType.Bomb)
                {
                    WeaponReplicaDriver.OnSample(in e);
                    continue;
                }

                var unit = ReplicaRegistry.Find(e.EntityId);
                if (unit == null)
                {
                    unit = StateSerializer.FindById(e.EntityId);
                    if (unit != null)
                        ReplicaRegistry.Register(e.EntityId, unit, ReplicaPolicy.LocalMotionUnit);
                }
                if (unit == null)
                {
                    Telemetry.Count("v2.unknownUnitId"); // census self-heals missed spawns
                    continue;
                }
                if (unit is WeaponBase) continue; // ID collision safety

                // Deck puppets: a world-space sample means the host flew this unit
                // off (or it's a stale pre-touchdown packet) - the driver decides
                // and either flips it airborne or swallows the sample.
                if ((e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
                    && DeckPuppetDriver.HandleWorldSample(unit, in e))
                {
                    _samples.Remove(e.EntityId); // the puppet owns this transform now
                    continue;
                }

                // ── Destruction / sinking (host-decided) ──────────────────────
                var comps = unit.Compartments;
                bool sinking   = (e.Flags & EntityState.FlagSinking)   != 0;
                bool destroyed = (e.Flags & EntityState.FlagDestroyed) != 0;

                if (sinking && comps != null && !comps._isSinking)
                {
                    _samples.Remove(e.EntityId);
                    comps.Sink(Compartments.SinkFocus.All, false);
                    continue;
                }
                if (comps != null && comps._isSinking) { _samples.Remove(e.EntityId); continue; }
                if (destroyed && !unit.IsDestroyed)
                {
                    _samples.Remove(e.EntityId);
                    CombatEventHandler.DestroyFromNetwork(unit);
                    continue;
                }

                // ── Decode (only for entries that survived the filters above) ─
                float heading = GeoCodec.UnpackHeading(e.HeadingQ);
                float speed   = GeoCodec.UnpackSpeedKts(e.SpeedQ);

                // ── Command-state feed (local sim targets host's commands) ───
                ApplyCommandState(unit, in e);

                if (e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
                {
                    var geo = new GeoPosition(e.LatDeg, e.LonDeg, e.HeightM);
                    Vector2 flat = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                    AircraftReplicaDriver.Report(
                        unit, new Vector3(flat.x, e.HeightM, flat.y), speed, heading);
                }

                if (!_samples.TryGetValue(e.EntityId, out var s))
                {
                    s = new Sample();
                    _samples[e.EntityId] = s;
                }
                s.Unit           = unit;
                s.Kind           = e.Kind;
                s.LonDeg         = e.LonDeg;
                s.LatDeg         = e.LatDeg;
                s.HeightM        = e.HeightM;
                s.Heading        = heading;
                s.Pitch          = GeoCodec.UnpackAngleCdeg(e.PitchQ);
                s.Roll           = GeoCodec.UnpackAngleCdeg(e.RollQ);
                s.Speed          = speed;
                s.RecordGameTime = GameTime.time;
                s.RecordRealTime = Time.unscaledTime;
                s.LatencyGameSec = latencyGame;
                s.WarnedFarDrift = false;
            }
        }

        /// <summary>
        /// One-way network latency expressed in game-seconds - how far the sim
        /// advanced while the packet was in flight. Scales with time compression
        /// (at 30x a 60 ms RTT is nearly a game-second of travel) and collapses
        /// to zero while paused.
        /// </summary>
        private static float LatencyGameSeconds()
        {
            if (GameTime.IsPaused()) return 0f;
            float oneWaySec = NetworkManager.Instance.LastRttMs / 2000f;
            return oneWaySec * GameTime.TimeCompression;
        }

        // ── Per-frame drive (called from Plugin.Update on the client) ─────────

        public static void Tick()
        {
            if (_samples.Count == 0) return;
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            float realNow = Time.unscaledTime;
            float gameNow = GameTime.time;

            float shipDriftSum = 0f, shipDriftMax = 0f; int shipCount = 0;
            float airDriftSum  = 0f, airDriftMax  = 0f; int airCount  = 0;

            foreach (var kv in _samples)
            {
                var s = kv.Value;
                var unit = s.Unit;
                if (unit == null || unit.IsDestroyed) { _toRemove.Add(kv.Key); continue; }
                if (realNow - s.RecordRealTime > MaxSampleAgeRealSec) continue;

                var tr = unit.transform;
                bool isAir = s.Kind == UnitType.Aircraft || s.Kind == UnitType.Helicopter;

                // Dead-reckon the sample to "now" along the host's own heading.
                // Without this every moving unit is pulled back onto a position
                // that is (latency + time since the packet) old, once per packet.
                float ageGame = (gameNow - s.RecordGameTime) + s.LatencyGameSec;
                Vector2 flat = Utils.longLatToLocal(
                    new GeoPosition(s.LatDeg, s.LonDeg, s.HeightM), Globals._currentCenterTile);
                Vector3 target = new Vector3(flat.x, s.HeightM, flat.y);
                float rad = s.Heading * Mathf.Deg2Rad;
                target.x += Mathf.Sin(rad) * (s.Speed * UnityPerKnotSecond * ageGame);
                target.z += Mathf.Cos(rad) * (s.Speed * UnityPerKnotSecond * ageGame);

                if (isAir) DriveAircraft(unit, tr, s, target, dt, ref airDriftSum, ref airDriftMax, ref airCount);
                else       DriveSurface(unit, tr, s, target, dt, ref shipDriftSum, ref shipDriftMax, ref shipCount);
            }

            if (_toRemove.Count > 0)
            {
                for (int i = 0; i < _toRemove.Count; i++) _samples.Remove(_toRemove[i]);
                _toRemove.Clear();
            }

            StateApplier.ReportDrift(
                shipCount > 0 ? shipDriftSum / shipCount : 0f, shipDriftMax,
                airCount  > 0 ? airDriftSum  / airCount  : 0f, airDriftMax);
        }

        /// <summary>
        /// Vessels, submarines and land units. Vertical is left entirely to the
        /// local sim: surface ships have their own wave motion and submarines
        /// their own depth physics (chasing the streamed DesiredAltitude), and
        /// pulling y toward the host's instantaneous value fights both.
        /// </summary>
        private static void DriveSurface(ObjectBase unit, Transform tr, Sample s, Vector3 target,
            float dt, ref float driftSum, ref float driftMax, ref int count)
        {
            Vector3 pos = tr.position;
            Vector3 err = target - pos;
            err.y = 0f;

            float drift = err.magnitude;
            driftSum += drift;
            if (drift > driftMax) driftMax = drift;
            count++;

            bool syncAttitude = s.Kind == UnitType.Submarine; // host-authoritative dive angle
            var eul = tr.eulerAngles;

            if (drift > ShipSnapThreshold)
            {
                tr.position = new Vector3(target.x, pos.y, target.z);
                tr.eulerAngles = new Vector3(
                    syncAttitude ? s.Pitch : eul.x, s.Heading, syncAttitude ? s.Roll : eul.z);
                unit._velocityInKnots = s.Speed;
                return;
            }

            tr.position = pos + err * Ease(ShipPosSharpness, dt);

            float kAng = Ease(ShipHeadingSharpness, dt);
            tr.eulerAngles = new Vector3(
                syncAttitude ? Mathf.LerpAngle(eul.x, s.Pitch, kAng) : eul.x,
                Mathf.LerpAngle(eul.y, s.Heading, kAng),
                syncAttitude ? Mathf.LerpAngle(eul.z, s.Roll, kAng) : eul.z);

            unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, s.Speed, Ease(ShipSpeedSharpness, dt));
        }

        /// <summary>
        /// Aircraft and helicopters. Position keeps the existing tolerance tiers -
        /// inside the accept band the AFCS steers toward the streamed target on
        /// its own and the transform is left alone; only attitude and speed are
        /// corrected, which is where the airborne jitter came from.
        /// </summary>
        private static void DriveAircraft(ObjectBase unit, Transform tr, Sample s, Vector3 target,
            float dt, ref float driftSum, ref float driftMax, ref int count)
        {
            Vector3 pos = tr.position;
            bool isOnDeck = s.HeightM < 2.0f;

            float kPos;
            if (isOnDeck)
            {
                kPos = Ease(AirNearSharpness, dt);
            }
            else
            {
                float yDrift  = Mathf.Abs(pos.y - target.y);
                float xzDrift = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(target.x, target.z));

                if (yDrift < 50f && xzDrift < 50f)
                {
                    kPos = 0f; // accept zone - AFCS chases
                }
                else if (yDrift < 500f && xzDrift < 500f)
                {
                    kPos = Ease(AirFarSharpness, dt);
                }
                else
                {
                    kPos = 1f;
                    if (!s.WarnedFarDrift)
                    {
                        s.WarnedFarDrift = true;
                        Plugin.Log.LogWarning($"[UnitReplica] Aircraft {unit.name} drift " +
                            $"Y={yDrift:F0} XZ={xzDrift:F0} exceeded 500, force-snapped");
                    }
                }
            }

            float drift = Vector3.Distance(pos, target);
            driftSum += drift;
            if (drift > driftMax) driftMax = drift;
            count++;

            if (kPos > 0f) tr.position = Vector3.Lerp(pos, target, kPos);

            float kAng = Ease(AirAttitudeSharpness, dt);
            var eul = tr.eulerAngles;
            tr.eulerAngles = new Vector3(
                Mathf.LerpAngle(eul.x, s.Pitch, kAng),
                Mathf.LerpAngle(eul.y, s.Heading, kAng),
                Mathf.LerpAngle(eul.z, s.Roll, kAng));

            unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, s.Speed, Ease(AirSpeedSharpness, dt));
        }

        /// <summary>Frame-rate-independent smoothing fraction for a rate of k per second.</summary>
        private static float Ease(float sharpness, float dt) => 1f - Mathf.Exp(-sharpness * dt);

        /// <summary>Mirror the host's command-state so local sim targets it between corrections.</summary>
        private static void ApplyCommandState(ObjectBase unit, in EntityState e)
        {
            // Telegraph (vessels + subs) - only when changed; suppress patch re-send
            if ((e.Kind == UnitType.Vessel || e.Kind == UnitType.Submarine)
                && unit.getTelegraph() != e.Telegraph)
            {
                bool prev = OrderHandler.ApplyingFromNetwork;
                OrderHandler.ApplyingFromNetwork = true;
                try { unit.setTelegraph(e.Telegraph); }
                finally { OrderHandler.ApplyingFromNetwork = prev; }
            }

            // Rudder steering target (vessels) - direct field write, no patches fire
            if (unit is Vessel vessel && _setRudderAngle != null)
                _setRudderAngle(vessel, e.RudderQ / 2f);

            // Desired altitude / depth (aircraft, helicopters, submarines)
            if (e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
            {
                if (e.DesiredAlt > 0f)
                    unit.DesiredAltitude.Value = e.DesiredAlt;
            }
            else if (e.Kind == UnitType.Submarine)
            {
                if (e.DesiredAlt != 0f)
                    unit.DesiredAltitude.Value = e.DesiredAlt;
            }
        }

        private static void RunAlignment(EntityStateBatchMessage msg)
        {
            // Reuse the v1 alignment core (position-match → SetUniqueId two-pass)
            var units = new List<UnitState>(msg.Entries.Count);
            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                units.Add(new UnitState
                {
                    EntityId = e.EntityId,
                    Kind     = e.Kind,
                    X        = (float)e.LonDeg,
                    Y        = e.HeightM,
                    Z        = (float)e.LatDeg,
                });
            }
            StateApplier.RunAlignmentFromUnitStates(units);
            ReplicaRegistry.Clear(); // re-resolve under the aligned IDs
            _samples.Clear();
        }

        public static void Reset()
        {
            _pendingAlignment = false;
            _lastServerTick = 0;
            _samples.Clear();
            _toRemove.Clear();
        }
    }
}
