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
        private const float AirPuppetSharpness   = 6f;

        // Hard resync tier (horizontal, Unity units - ~67 m each). Aircraft have
        // their own tiers inline in DriveAircraft.
        private const float ShipSnapThreshold = 75f;

        // Aircraft position tolerance tiers, sized in metres and converted to
        // Unity units (~67.2 m each). Horizontal used to be a bare 50/500 UNITS -
        // a 3.4 km accept band and a 34 km snap - which meant a host aircraft
        // could fly an entire evasive engagement (sub-km jinks) without the
        // client ever correcting: the replica just cruised straight through it.
        // 150 m still leaves the native physics unfought in steady flight (chase
        // steering holds the error well under that), while a manoeuvring host now
        // pulls the replica along its actual path.
        private const float AirAcceptXZ = 150f  / GeoCodec.MetresPerUnityUnit;
        private const float AirSnapXZ   = 2000f / GeoCodec.MetresPerUnityUnit;
        private const float AirAcceptY  = 30f  / GeoCodec.MetresPerUnityUnit;   // ~100 ft
        private const float AirSnapY    = 600f / GeoCodec.MetresPerUnityUnit;   // ~2000 ft

        // Unity units per (knot · game-second) - the game's own conversion
        private const float UnityPerKnotSecond = 0.0076554087f;

        // Past this age the sample is guesswork: the local sim (running on the
        // host's mirrored telegraph + rudder) tracks better than a stale target,
        // so stop correcting rather than drag the unit back to where it was.
        //
        // This is bounded by the host's idle heartbeat, not by anything local: a
        // unit whose quantized state is unchanged (a ship at anchor or holding a
        // slow steady course) only appears in the stream that often. Sized to
        // survive one LOST heartbeat plus transit and jitter - at merely
        // heartbeat + epsilon, a single dropped packet on a high-ping link drops
        // every slow ship out of correction entirely, and because the drift stats
        // are then measured over nothing the overlay reports a reassuring 0.0.
        private static float MaxSampleAgeRealSec =>
            HostEntityStreamer.HeartbeatInterval * 2f + 0.5f;

        // Ceiling on dead reckoning, in REAL seconds - converted to game-seconds
        // against the current compression. A stalled stream, a clock disagreement
        // or a paused host must never fling a unit across the map.
        private const float MaxExtrapolationRealSec = 0.6f;

        // Turn rates above this are measurement noise, not a ship manoeuvring.
        private const float MaxTurnRateDegPerGameSec = 15f;

        // Mission clock wraps at midnight; a jump more negative than this is a
        // day rollover rather than the client running ahead of the host.
        private const float DayGameSeconds = 86400f;

        // Render-delay bounds, in REAL seconds (scaled by compression at use).
        // The floor keeps a LAN from paying for jitter it does not have; the
        // ceiling stops a pathological link from parking units visibly in the past.
        private const float MinRenderDelayRealSec = 0.05f;
        private const float MaxRenderDelayRealSec = 0.5f;

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
        private struct Snapshot
        {
            public double LonDeg, LatDeg;
            public float HeightM, Heading, Pitch, Roll, Speed;
            public float HostMissionSec;   // host's mission clock at capture
        }

        /// <summary>State the driver resolved for one unit at the render time.</summary>
        private struct Pose
        {
            public Vector3 Position;
            public float Heading, Pitch, Roll, Speed;
        }

        private sealed class Sample
        {
            public const int Capacity = 4;

            public ObjectBase Unit = null!;
            public UnitType Kind;
            public float RecordRealTime;   // Time.unscaledTime at arrival (staleness only)
            public float TurnRateDegSec;   // derived from consecutive snapshots
            public bool  HasPrev;
            public bool  WarnedFarDrift;

            // Puppet control-surface feed (FeedPuppetControlState)
            public float PrevBank, PrevPitch;
            public bool  AttitudeValid;

            public readonly Snapshot[] Buf = new Snapshot[Capacity];
            public int Count;
            public int Head = Capacity - 1;  // first Push lands on 0

            /// <summary>0 = newest, 1 = the one before it, ...</summary>
            public Snapshot At(int back)
                => Buf[((Head - back) % Capacity + Capacity) % Capacity];

            public void Push(in Snapshot snap)
            {
                Head = (Head + 1) % Capacity;
                Buf[Head] = snap;
                if (Count < Capacity) Count++;
            }
        }

        private static readonly Dictionary<int, Sample> _samples = new();
        private static readonly List<int> _toRemove = new();

        // ── Link cadence, measured in game-seconds ───────────────────────────
        // Interval is the host's send cadence (steady). Transit deviation is the
        // link's arrival jitter - the quantity the render delay has to hide.
        private static float _emaIntervalGameSec;
        private static float _emaTransitGameSec;
        private static float _emaTransitDevGameSec;
        private static float _lastBatchHostSec = -1f;

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

            // The host stamps every batch with its own mission clock. Both sides run
            // the same clock, so (localNow - hostStamp) already contains the packet's
            // flight time - no RTT term is needed, and the link's jitter therefore
            // never reaches the extrapolation distance.
            float hostSec = msg.GameSeconds;
            TrackCadence(hostSec);

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

                // Turn rate from consecutive snapshots. Heading is quantized to
                // 0.0055 deg, so the derivative is clean at any real ship's turn
                // rate and costs nothing on the wire.
                if (s.HasPrev)
                {
                    var prev = s.At(0);
                    float dtSec = hostSec - prev.HostMissionSec;

                    // Reordered or duplicate stamp: drop it, the buffer must stay ordered.
                    if (dtSec <= 0f) continue;

                    // Grade the motion model against ground truth BEFORE anything from
                    // this sample is folded in - in particular before the turn rate is
                    // updated, or the new heading would leak into its own prediction.
                    RecordPredictionError(s, hostSec, in e);

                    // Over a long gap the previous rate says nothing about now, and
                    // carrying it forward would swing the unit off its track.
                    s.TurnRateDegSec = dtSec < 5f
                        ? Mathf.Clamp(Mathf.DeltaAngle(prev.Heading, heading) / dtSec,
                                      -MaxTurnRateDegPerGameSec, MaxTurnRateDegPerGameSec)
                        : 0f;
                }
                s.HasPrev = true;

                s.Push(new Snapshot
                {
                    LonDeg         = e.LonDeg,
                    LatDeg         = e.LatDeg,
                    HeightM        = e.HeightM,
                    Heading        = heading,
                    Pitch          = GeoCodec.UnpackAngleCdeg(e.PitchQ),
                    Roll           = GeoCodec.UnpackAngleCdeg(e.RollQ),
                    Speed          = speed,
                    HostMissionSec = hostSec,
                });

                s.Unit           = unit;
                s.Kind           = e.Kind;
                s.RecordRealTime = Time.unscaledTime;
                s.WarnedFarDrift = false;
            }
        }

        // ── Prediction error ─────────────────────────────────────────────────
        // How wrong the motion model was, graded against ground truth each time a
        // fresh host sample lands. This is deliberately NOT the drift figure: drift
        // is the controller residual (distance to the target the driver just
        // computed) and the per-frame correction drives it to near-zero whatever the
        // state rate, so it says nothing about replication accuracy. This does - it
        // is the error the correction is there to hide, and it is what climbs when
        // the stream rate is lowered.
        //
        // Horizontal only, so it stays a real distance: y is metres while x/z are
        // ~67 m units, and mixing them (as the air drift figure does) yields a
        // number that is not a length at all.
        private const float PredictErrWindowSec = 5f;

        private static float _predErrShipSum, _predErrShipMax;
        private static float _predErrAirSum,  _predErrAirMax;
        private static int   _predErrShipN,   _predErrAirN;
        private static float _predErrWindowEnd;

        private static void RecordPredictionError(Sample s, float hostSec, in EntityState e)
        {
            Pose predicted = ResolvePose(s, hostSec);

            Vector2 actual = Utils.longLatToLocal(
                new GeoPosition(e.LatDeg, e.LonDeg, e.HeightM), Globals._currentCenterTile);
            float dx = actual.x - predicted.Position.x;
            float dz = actual.y - predicted.Position.z;
            float err = Mathf.Sqrt(dx * dx + dz * dz);

            if (e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
            {
                _predErrAirSum += err;
                if (err > _predErrAirMax) _predErrAirMax = err;
                _predErrAirN++;
            }
            else
            {
                _predErrShipSum += err;
                if (err > _predErrShipMax) _predErrShipMax = err;
                _predErrShipN++;
            }

            // Publish per window, so avg and max describe the same interval and the
            // max reflects recent behaviour rather than the worst spike since the
            // session began (usually the first sample after a join, which is
            // meaningless). n is the sample count over that window, not a unit count.
            float now = Time.unscaledTime;
            if (now < _predErrWindowEnd) return;

            _predErrWindowEnd = now + PredictErrWindowSec;
            StateApplier.ReportPredictionError(
                _predErrShipN > 0 ? _predErrShipSum / _predErrShipN : 0f, _predErrShipMax, _predErrShipN,
                _predErrAirN  > 0 ? _predErrAirSum  / _predErrAirN  : 0f, _predErrAirMax,  _predErrAirN);

            _predErrShipSum = _predErrShipMax = 0f;
            _predErrAirSum  = _predErrAirMax  = 0f;
            _predErrShipN   = _predErrAirN    = 0;
        }

        /// <summary>
        /// Track the link's cadence on the host's own clock. Send interval is steady;
        /// the spread of transit times is the jitter the render delay has to cover.
        /// </summary>
        private static void TrackCadence(float hostSec)
        {
            const float Alpha = 0.1f;

            // A tick that splits into several packets repeats one stamp - those carry
            // no cadence information.
            if (_lastBatchHostSec >= 0f)
            {
                float interval = hostSec - _lastBatchHostSec;
                if (interval > 0f && interval < 5f)
                    _emaIntervalGameSec = _emaIntervalGameSec <= 0f
                        ? interval
                        : Mathf.Lerp(_emaIntervalGameSec, interval, Alpha);
            }
            if (hostSec > _lastBatchHostSec) _lastBatchHostSec = hostSec;

            float transit = LocalMissionSeconds() - hostSec;
            if (transit < 0f || transit > 10f) return; // clock disagreement - ignore

            if (_emaTransitGameSec <= 0f)
            {
                _emaTransitGameSec = transit;
                return;
            }
            _emaTransitDevGameSec = Mathf.Lerp(
                _emaTransitDevGameSec, Mathf.Abs(transit - _emaTransitGameSec), Alpha);
            _emaTransitGameSec = Mathf.Lerp(_emaTransitGameSec, transit, Alpha);
        }

        /// <summary>
        /// How far behind the local clock to render remote units, in game-seconds.
        /// Sized to span two send intervals plus the measured jitter, so a bracketing
        /// pair of snapshots is normally already in hand and the driver interpolates
        /// instead of extrapolating. Zero disables interpolation entirely.
        /// </summary>
        private static float RenderDelayGameSec()
        {
            if (!Plugin.Instance.CfgReplicaInterpolation.Value) return 0f;

            float tc = Mathf.Max(1f, GameTime.TimeCompression);
            float delay = 2f * _emaIntervalGameSec + 2f * _emaTransitDevGameSec;
            return Mathf.Clamp(delay, MinRenderDelayRealSec * tc, MaxRenderDelayRealSec * tc);
        }

        /// <summary>
        /// The local mission clock, in the same domain the host stamps batches with
        /// (<see cref="HostEntityStreamer"/> builds it identically). Continuous and
        /// compression-scaled, and it stops dead while paused - which is exactly
        /// what dead reckoning needs.
        /// </summary>
        private static float LocalMissionSeconds()
        {
            var env = Singleton<SeaPower.Environment>.Instance;
            return env.Hour * 3600f + env.Minutes * 60f + env.Seconds;
        }

        private static Vector3 ToUnity(in Snapshot snap)
        {
            Vector2 flat = Utils.longLatToLocal(
                new GeoPosition(snap.LatDeg, snap.LonDeg, snap.HeightM), Globals._currentCenterTile);
            return new Vector3(flat.x, snap.HeightM, flat.y);
        }

        /// <summary>
        /// Resolve where the host says this unit was at <paramref name="renderMissionSec"/>.
        ///
        /// Between two snapshots this interpolates, which is the whole point: an
        /// interpolated target cannot carry arrival-time noise, because both ends are
        /// states the host actually reported. Past the newest snapshot - a slow-updating
        /// unit, or a loss burst - it falls back to arc extrapolation, clamped.
        /// </summary>
        private static Pose ResolvePose(Sample s, float renderMissionSec)
        {
            var newest = s.At(0);

            if (s.Count >= 2 && renderMissionSec < newest.HostMissionSec)
            {
                for (int back = 0; back < s.Count - 1; back++)
                {
                    var newer = s.At(back);
                    var older = s.At(back + 1);
                    float span = newer.HostMissionSec - older.HostMissionSec;
                    if (span <= 0f) continue;
                    if (renderMissionSec < older.HostMissionSec) continue;

                    float t = Mathf.Clamp01((renderMissionSec - older.HostMissionSec) / span);
                    return new Pose
                    {
                        Position = Vector3.Lerp(ToUnity(older), ToUnity(newer), t),
                        Heading  = Mathf.LerpAngle(older.Heading, newer.Heading, t),
                        Pitch    = Mathf.LerpAngle(older.Pitch,   newer.Pitch,   t),
                        Roll     = Mathf.LerpAngle(older.Roll,    newer.Roll,    t),
                        Speed    = Mathf.Lerp(older.Speed, newer.Speed, t),
                    };
                }

                // Older than everything buffered - hold the oldest rather than
                // extrapolate backwards into a position the host never reported.
                var oldest = s.At(s.Count - 1);
                return new Pose
                {
                    Position = ToUnity(oldest),
                    Heading  = oldest.Heading,
                    Pitch    = oldest.Pitch,
                    Roll     = oldest.Roll,
                    Speed    = oldest.Speed,
                };
            }

            // Extrapolate forward from the newest snapshot.
            float age = renderMissionSec - newest.HostMissionSec;
            if (age < -DayGameSeconds * 0.5f) age += DayGameSeconds; // midnight rollover
            if (age < 0f) age = 0f;
            age = Mathf.Min(age, MaxExtrapolationRealSec * Mathf.Max(1f, GameTime.TimeCompression));

            Vector3 pos = ToUnity(newest);

            // Follow the arc the unit is actually on: a ship mid-turn travels a curve,
            // and projecting it down the sampled heading throws the target sideways by
            // more the longer the extrapolation runs. Heading at the midpoint of the
            // interval is the chord direction.
            float distance = newest.Speed * UnityPerKnotSecond * age;
            if (distance != 0f)
            {
                float rad = (newest.Heading + s.TurnRateDegSec * age * 0.5f) * Mathf.Deg2Rad;
                pos.x += Mathf.Sin(rad) * distance;
                pos.z += Mathf.Cos(rad) * distance;
            }

            return new Pose
            {
                Position = pos,
                Heading  = newest.Heading + s.TurnRateDegSec * age,
                Pitch    = newest.Pitch,
                Roll     = newest.Roll,
                Speed    = newest.Speed,
            };
        }

        // ── Per-frame drive (called from Plugin.Update on the client) ─────────

        public static void Tick()
        {
            if (_samples.Count == 0) return;
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            // Error accrues per game-second but the ease runs per real-second, so at
            // high compression a real-time-only rate falls hopelessly behind. Scale
            // by compression, capped: past ~10x the pull is already effectively
            // immediate within a frame and more gain only amplifies residual noise.
            float easeDt = dt * Mathf.Clamp(GameTime.TimeCompression, 1f, 10f);

            float realNow = Time.unscaledTime;

            // Render remote units slightly in the host's past so a bracketing pair of
            // snapshots is already in hand. This is what converts the link's arrival
            // jitter from target noise into a fixed, invisible offset.
            float nowMissionSec    = LocalMissionSeconds();
            float renderMissionSec = nowMissionSec - RenderDelayGameSec();

            float shipDriftSum = 0f, shipDriftMax = 0f; int shipCount = 0;
            float airDriftSum  = 0f, airDriftMax  = 0f; int airCount  = 0;

            foreach (var kv in _samples)
            {
                var s = kv.Value;
                var unit = s.Unit;
                if (unit == null || unit.IsDestroyed) { _toRemove.Add(kv.Key); continue; }
                if (s.Count == 0) continue;
                if (realNow - s.RecordRealTime > MaxSampleAgeRealSec) continue;

                var tr = unit.transform;
                bool isAir  = s.Kind == UnitType.Aircraft || s.Kind == UnitType.Helicopter;
                bool puppet = isAir && AircraftReplicaDriver.IsFormationPuppet(unit);

                // Chase-driven aircraft measure against the track extrapolated to
                // NOW: their native physics flies at a point ahead of the newest
                // sample, so a render-delayed target sits speed x delay BEHIND the
                // aircraft - at time compression that systematic offset alone
                // exceeded the accept band and the corrector dragged the plane
                // backwards against its own flight model every frame (the
                // normal-flight ghosting). Puppets have no local motion to
                // disagree with, so they keep the smoother interpolated target.
                Pose pose = ResolvePose(s, isAir && !puppet ? nowMissionSec : renderMissionSec);

                if (isAir) DriveAircraft(unit, tr, s, in pose, easeDt, puppet, ref airDriftSum, ref airDriftMax, ref airCount);
                else       DriveSurface(unit, tr, s, in pose, easeDt, ref shipDriftSum, ref shipDriftMax, ref shipCount);
            }

            if (_toRemove.Count > 0)
            {
                for (int i = 0; i < _toRemove.Count; i++) _samples.Remove(_toRemove[i]);
                _toRemove.Clear();
            }

            StateApplier.ReportDrift(
                shipCount > 0 ? shipDriftSum / shipCount : 0f, shipDriftMax, shipCount,
                airCount  > 0 ? airDriftSum  / airCount  : 0f, airDriftMax,  airCount);
        }

        /// <summary>
        /// Vessels, submarines and land units. Vertical is left entirely to the
        /// local sim: surface ships have their own wave motion and submarines
        /// their own depth physics (chasing the streamed DesiredAltitude), and
        /// pulling y toward the host's instantaneous value fights both.
        /// </summary>
        private static void DriveSurface(ObjectBase unit, Transform tr, Sample s, in Pose pose,
            float dt, ref float driftSum, ref float driftMax, ref int count)
        {
            Vector3 pos = tr.position;
            Vector3 err = pose.Position - pos;
            err.y = 0f;

            float drift = err.magnitude;
            driftSum += drift;
            if (drift > driftMax) driftMax = drift;
            count++;

            bool syncAttitude = s.Kind == UnitType.Submarine; // host-authoritative dive angle
            var eul = tr.eulerAngles;

            if (drift > ShipSnapThreshold)
            {
                tr.position = new Vector3(pose.Position.x, pos.y, pose.Position.z);
                tr.eulerAngles = new Vector3(
                    syncAttitude ? pose.Pitch : eul.x, pose.Heading, syncAttitude ? pose.Roll : eul.z);
                unit._velocityInKnots = pose.Speed;
                return;
            }

            tr.position = pos + err * Ease(ShipPosSharpness, dt);

            float kAng = Ease(ShipHeadingSharpness, dt);
            tr.eulerAngles = new Vector3(
                syncAttitude ? Mathf.LerpAngle(eul.x, pose.Pitch, kAng) : eul.x,
                Mathf.LerpAngle(eul.y, pose.Heading, kAng),
                syncAttitude ? Mathf.LerpAngle(eul.z, pose.Roll, kAng) : eul.z);

            unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, pose.Speed, Ease(ShipSpeedSharpness, dt));
        }

        /// <summary>
        /// Aircraft and helicopters. Position keeps the existing tolerance tiers -
        /// inside the accept band the AFCS steers toward the streamed target on
        /// its own and the transform is left alone; only attitude and speed are
        /// corrected, which is where the airborne jitter came from.
        /// </summary>
        private static void DriveAircraft(ObjectBase unit, Transform tr, Sample s, in Pose pose,
            float dt, bool puppet, ref float driftSum, ref float driftMax, ref int count)
        {
            Vector3 pos = tr.position;
            Vector3 target = pose.Position;   // y carries the streamed height directly
            bool isOnDeck = target.y < 2.0f;

            float kXZ, kY;
            if (isOnDeck)
            {
                kXZ = kY = Ease(AirNearSharpness, dt);
            }
            else if (puppet)
            {
                // Wingman puppet: its FormationFlightPhysics is suppressed while
                // the stream is fresh (the station-keeper writes the transform
                // directly off the LOCAL leader every physics tick, fighting these
                // corrections - the wingman jitter), so nothing else moves this
                // aircraft. Correct every frame with no dead band; every wingman
                // lags the stream equally, so the formation shape survives.
                kXZ = kY = Ease(AirPuppetSharpness, dt);
            }
            else
            {
                float yDrift  = Mathf.Abs(pos.y - target.y);
                float xzDrift = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(target.x, target.z));

                // Horizontal keeps its tuned band. Vertical gets its own, and is
                // corrected independently: altitude is commanded precisely and moves
                // slowly, so it needs a real tolerance - but tightening it must not
                // drag the horizontal axes into constant correction with it.
                kXZ = xzDrift < AirAcceptXZ ? 0f
                    : xzDrift < AirSnapXZ   ? Ease(AirFarSharpness, dt)
                    : 1f;
                kY  = yDrift < AirAcceptY ? 0f
                    : yDrift < AirSnapY   ? Ease(AirFarSharpness, dt)
                    : 1f;

                if ((kXZ >= 1f || kY >= 1f) && !s.WarnedFarDrift)
                {
                    s.WarnedFarDrift = true;
                    Plugin.Log.LogWarning($"[UnitReplica] Aircraft {unit.name} drift " +
                        $"Y={yDrift * GeoCodec.MetresPerUnityUnit:F0} m " +
                        $"XZ={xzDrift * GeoCodec.MetresPerUnityUnit:F0} m, force-snapped");
                }
            }

            float drift = Vector3.Distance(pos, target);
            driftSum += drift;
            if (drift > driftMax) driftMax = drift;
            count++;

            if (kXZ > 0f || kY > 0f)
                tr.position = new Vector3(
                    Mathf.Lerp(pos.x, target.x, kXZ),
                    Mathf.Lerp(pos.y, target.y, kY),
                    Mathf.Lerp(pos.z, target.z, kXZ));

            float kAng = Ease(AirAttitudeSharpness, dt);
            var eul = tr.eulerAngles;
            tr.eulerAngles = new Vector3(
                Mathf.LerpAngle(eul.x, pose.Pitch, kAng),
                Mathf.LerpAngle(eul.y, pose.Heading, kAng),
                Mathf.LerpAngle(eul.z, pose.Roll, kAng));

            unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, pose.Speed, Ease(AirSpeedSharpness, dt));

            if (puppet) FeedPuppetControlState(unit, tr, s, in pose);
        }

        /// <summary>
        /// Puppets get no control-surface animation for free: the suppressed
        /// FormationFlightPhysics.OnFixedUpdate is what used to refresh
        /// BankAngle/BankRate/PitchRate/GLoad, and its still-running OnUpdate
        /// computes the Normed*ControlDemand values (which
        /// AircraftFlightControlSystem turns into aileron/elevator/rudder
        /// deflection) FROM those fields - frozen inputs, frozen surfaces. Feed
        /// the observed kinematics of the pose we just imposed back into the
        /// controller and the game's own demand math animates the surfaces to
        /// match the manoeuvre, with its own gains and sign conventions.
        /// </summary>
        private static void FeedPuppetControlState(ObjectBase unit, Transform tr, Sample s, in Pose pose)
        {
            var mc = (unit as Aircraft)?.Motioncontroller;
            if (mc == null) return;

            // Same formulas FormationFlightPhysics.OnFixedUpdate used.
            float bank  = Utils.AngleOffAroundAxis(tr.up, Vector3.up, tr.forward);
            float pitch = Utils.AngleOffAroundAxis(tr.forward,
                Vector3.ProjectOnPlane(tr.forward, Vector3.up),
                Vector3.ProjectOnPlane(tr.right, Vector3.up), clockwise: true);

            float gdt = GameTime.deltaTime;
            if (s.AttitudeValid && gdt > 0.0001f)
            {
                mc.BankRate  = Mathf.DeltaAngle(s.PrevBank,  bank)  / gdt;
                mc.PitchRate = Mathf.DeltaAngle(s.PrevPitch, pitch) / gdt;
            }
            s.PrevBank      = bank;
            s.PrevPitch     = pitch;
            s.AttitudeValid = true;

            mc.BankAngle  = bank;
            mc.PitchAngle = pitch;
            mc.YawAngle   = tr.eulerAngles.y;
            mc.Velocity   = pose.Speed * 0.514444f; // knots → m/s
            // Level-turn approximation - only the pitch demand's G term reads it.
            mc.GLoad = Mathf.Clamp(1f / Mathf.Max(0.2f, Mathf.Cos(bank * Mathf.Deg2Rad)), 1f, 9f);
        }

        /// <summary>Frame-rate-independent smoothing fraction for a rate of k per second.</summary>
        private static float Ease(float sharpness, float dt) => 1f - Mathf.Exp(-sharpness * dt);

        /// <summary>Mirror the host's command-state so local sim targets it between corrections.</summary>
        private static void ApplyCommandState(ObjectBase unit, in EntityState e)
        {
            // Speed command - only when changed; suppress patch re-send.
            // A slider/typed speed is not a telegraph, so it comes down its own
            // field and must not be reduced back to the (stale) preset.
            if ((e.Flags & EntityState.FlagCustomSpeed) != 0)
            {
                float kts = e.CmdSpeedQ / 10f;
                float cur = StateSerializer.CustomCommandKnots(unit);
                if (float.IsNaN(cur) || Mathf.Abs(cur - kts) > 0.5f)
                {
                    bool prev = OrderHandler.ApplyingFromNetwork;
                    OrderHandler.ApplyingFromNetwork = true;
                    try { StateSerializer.ApplyCustomSpeed(unit, kts); }
                    finally { OrderHandler.ApplyingFromNetwork = prev; }
                }
            }
            else if (e.Kind == UnitType.Vessel || e.Kind == UnitType.Submarine)
            {
                // Re-assert the telegraph when the value differs OR when we are still
                // holding a custom command the host has since dropped - _telegraph does
                // not move while a custom speed is set, so it alone can read "in sync".
                if (unit.getTelegraph() != e.Telegraph
                    || !float.IsNaN(StateSerializer.CustomCommandKnots(unit)))
                {
                    bool prev = OrderHandler.ApplyingFromNetwork;
                    OrderHandler.ApplyingFromNetwork = true;
                    try { unit.setTelegraph(e.Telegraph); }
                    finally { OrderHandler.ApplyingFromNetwork = prev; }
                }
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
            _emaIntervalGameSec = 0f;
            _emaTransitGameSec = 0f;
            _emaTransitDevGameSec = 0f;
            _lastBatchHostSec = -1f;
            _predErrShipSum = _predErrShipMax = 0f;
            _predErrAirSum  = _predErrAirMax  = 0f;
            _predErrShipN   = _predErrAirN    = 0;
            _predErrWindowEnd = 0f;
        }
    }
}
