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
            /// <summary>Host's mission clock at capture. Double: seconds-since-midnight
            /// in float32 quantizes to ~4 ms, which distorts the span between two
            /// snapshots by up to 8% and moves the interpolation fraction with it.</summary>
            public double HostMissionSec;
        }

        /// <summary>State the driver resolved for one unit at the render time.</summary>
        private struct Pose
        {
            public Vector3 Position;
            public float Heading, Pitch, Roll, Speed;
        }

        private sealed class Sample
        {
            // Deep enough that a burst of near-simultaneous samples cannot collapse the
            // ring's TIME coverage. The host runs three independent send timers (unit,
            // near, missile) and a unit inside the client's view is captured by two of
            // them, so consecutive stamps as close as 8 ms are routine. At the old
            // capacity of 4 such a burst left the ring spanning a few tens of ms, the
            // render point fell off the back of it, and ResolvePose returned HOLD -
            // freezing the target until the next sample. That was 18% of frames on a
            // steady straight-line ship, and it is what the stutter looked like.
            public const int Capacity = 16;

            public ObjectBase Unit = null!;
            public UnitType Kind;
            public float RecordRealTime;   // Time.unscaledTime at arrival (staleness only)
            public float TurnRateDegSec;   // derived from consecutive snapshots
            public bool  HasPrev;
            public bool  WarnedFarDrift;

            /// <summary>EMA of THIS entity's own sample interval, in game-seconds.
            /// Change detection gates each unit independently, so a unit's effective
            /// rate is a function of its speed and the host's thresholds - not of the
            /// timer it rides. The global batch-arrival interval measures the three
            /// interleaved streams together and is far shorter than any one entity's.</summary>
            public float EmaIntervalGameSec;

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
        private static bool  _hasTransitEma;
        private static double _lastBatchHostSec = -1d;

        /// <summary>Arrival jitter, in game-seconds. Truer link-quality signal than
        /// transport ping - it is what the render delay is actually sized against.</summary>
        internal static float ArrivalJitterSec => _emaTransitDevGameSec;

        /// <summary>Host send cadence as the client actually observes it, which at
        /// low host framerate is slower than the configured Hz.</summary>
        internal static float HostCadenceSec => _emaIntervalGameSec;

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
            double hostSec = msg.GameSeconds;
            TrackCadence(hostSec);

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];

                // Weapon kinds route to the kinematic replica driver
                if (e.Kind == UnitType.Missile || e.Kind == UnitType.Torpedo || e.Kind == UnitType.Bomb)
                {
                    // OnSample first: it grades the dead-reckoning model against this
                    // sample, and the trace row reports that figure.
                    WeaponReplicaDriver.OnSample(in e, hostSec);
                    if (MotionTrace.IsTracing(e.EntityId))
                        MotionTrace.ClientReceive(in e, msg.ServerTick, msg.Entries.Count, hostSec,
                            LocalMissionSeconds(), float.NaN,
                            WeaponReplicaDriver.LastPredictionErrorM, 0, float.NaN,
                            _emaIntervalGameSec, _emaTransitDevGameSec, float.NaN);
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
                    using (Authority.Allowed())
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
                    AircraftReplicaDriver.Report(
                        unit, GeoCodec.ToUnity(e.LatDeg, e.LonDeg, e.HeightM), speed, heading);
                }

                if (!_samples.TryGetValue(e.EntityId, out var s))
                {
                    s = new Sample();
                    _samples[e.EntityId] = s;
                }

                // Turn rate from consecutive snapshots. Heading is quantized to
                // 0.0055 deg, so the derivative is clean at any real ship's turn
                // rate and costs nothing on the wire.
                float gapSec = float.NaN, predErr = float.NaN;
                if (s.HasPrev)
                {
                    var prev = s.At(0);
                    double dtSec = hostSec - prev.HostMissionSec;

                    // Reordered or duplicate stamp: drop it, the buffer must stay ordered.
                    if (dtSec <= 0d)
                    {
                        Telemetry.Count("v2.unitSampleReordered");
                        if (MotionTrace.IsTracing(e.EntityId))
                            MotionTrace.SampleDropped(e.EntityId, msg.ServerTick, hostSec,
                                prev.HostMissionSec);
                        continue;
                    }
                    gapSec = (float)dtSec;

                    // This entity's own send cadence - what its render delay is sized from.
                    if (dtSec < 5f)
                        s.EmaIntervalGameSec = s.EmaIntervalGameSec <= 0f
                            ? (float)dtSec
                            : Mathf.Lerp(s.EmaIntervalGameSec, (float)dtSec, 0.1f);

                    // Grade the motion model against ground truth BEFORE anything from
                    // this sample is folded in - in particular before the turn rate is
                    // updated, or the new heading would leak into its own prediction.
                    predErr = RecordPredictionError(s, hostSec, in e);

                    // Over a long gap the previous rate says nothing about now, and
                    // carrying it forward would swing the unit off its track.
                    s.TurnRateDegSec = dtSec < 5d
                        ? Mathf.Clamp(Mathf.DeltaAngle(prev.Heading, heading) / (float)dtSec,
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

                if (MotionTrace.IsTracing(e.EntityId))
                    MotionTrace.ClientReceive(in e, msg.ServerTick, msg.Entries.Count, hostSec,
                        LocalMissionSeconds(), gapSec,
                        predErr * GeoCodec.MetresPerUnityUnit, s.Count, s.TurnRateDegSec,
                        _emaIntervalGameSec, _emaTransitDevGameSec, s.EmaIntervalGameSec);
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

        /// <summary>Returns the horizontal prediction error in Unity units.</summary>
        private static float RecordPredictionError(Sample s, double hostSec, in EntityState e)
        {
            Pose predicted = ResolvePose(s, hostSec);

            Vector3 actual = GeoCodec.ToUnity(e.LatDeg, e.LonDeg, e.HeightM);
            float dx = actual.x - predicted.Position.x;
            float dz = actual.z - predicted.Position.z;
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
            if (now < _predErrWindowEnd) return err;

            _predErrWindowEnd = now + PredictErrWindowSec;
            StateApplier.ReportPredictionError(
                _predErrShipN > 0 ? _predErrShipSum / _predErrShipN : 0f, _predErrShipMax, _predErrShipN,
                _predErrAirN  > 0 ? _predErrAirSum  / _predErrAirN  : 0f, _predErrAirMax,  _predErrAirN);

            _predErrShipSum = _predErrShipMax = 0f;
            _predErrAirSum  = _predErrAirMax  = 0f;
            _predErrShipN   = _predErrAirN    = 0;
            return err;
        }

        /// <summary>
        /// Track the link's cadence and the offset between the two mission clocks.
        ///
        /// (localClock - hostStamp) is NOT flight time: it is clock skew PLUS flight
        /// time. The two clocks are only ever forced into agreement on a
        /// time-compression change, so between those the skew is arbitrary and
        /// routinely NEGATIVE - a client running behind the host produces a negative
        /// figure on every packet. The old guard rejected exactly that case, so on
        /// such a link this method returned early every single time: the deviation EMA
        /// stayed pinned at zero, the render delay carried no jitter term at all, and
        /// the skew was silently added to the render delay as unintended latency.
        /// (Measured on a real session: 470/470 packets rejected, skew -151 ms.)
        ///
        /// Both quantities are kept instead. The offset converts local clock into the
        /// host's domain; the deviation AROUND that offset is the arrival jitter the
        /// render delay has to hide.
        /// </summary>
        private static void TrackCadence(double hostSec)
        {
            const float Alpha = 0.1f;

            // A tick that splits into several packets repeats one stamp - those carry
            // no cadence information.
            if (_lastBatchHostSec >= 0d)
            {
                double interval = hostSec - _lastBatchHostSec;
                if (interval > 0f && interval < 5f)
                    _emaIntervalGameSec = _emaIntervalGameSec <= 0f
                        ? (float)interval
                        : Mathf.Lerp(_emaIntervalGameSec, (float)interval, Alpha);
            }
            if (hostSec > _lastBatchHostSec) _lastBatchHostSec = hostSec;

            double transit = LocalMissionSeconds() - hostSec;
            // Sign carries no information about validity - only magnitude does. A
            // genuine clock disagreement (scene load, a missed snap) is seconds wide.
            if (System.Math.Abs(transit) > 10d) return;

            if (!_hasTransitEma)
            {
                _emaTransitGameSec = (float)transit;
                _hasTransitEma = true;
                return;
            }
            _emaTransitDevGameSec = Mathf.Lerp(
                _emaTransitDevGameSec, (float)System.Math.Abs(transit - _emaTransitGameSec), Alpha);
            _emaTransitGameSec = Mathf.Lerp(_emaTransitGameSec, (float)transit, Alpha);
        }

        // ── Render clock ─────────────────────────────────────────────────────
        //
        // The mission clock CANNOT be sampled per frame as a render time. It is a
        // float32 holding seconds-since-midnight, so by mid-morning (36,000 s) it has
        // only ~4 ms of resolution, and the game accumulates into it in steps of its
        // own: measured live, it advanced in three discrete sizes (0, ~9.4 ms, ~19 ms)
        // and DID NOT MOVE AT ALL on 29% of frames, against a 6.9 ms frame time.
        //
        // Scheduling interpolation on that makes the target lurch instead of slide, in
        // exact multiples of one clock quantum. The lurch is mostly along-track, where
        // 13 m/s of forward travel hides it - but the host's own track wanders ~0.3 m
        // laterally, so the chord between two snapshots is not parallel to the hull and
        // every lurch throws a slice of itself sideways. That is the side-to-side jitter.
        //
        // So the render time is its own clock: advanced smoothly at RENDER rate in
        // double precision, and slewed gently onto the host's estimated clock. It stays
        // locked without ever stepping, which is the whole point.
        private static double _renderClock;
        private static bool   _renderClockValid;

        /// <summary>Seconds to close the offset between the render clock and the
        /// measured host clock. Slow enough to be invisible, fast enough that a real
        /// drift never accumulates.</summary>
        private const float RenderClockLockRate = 0.5f;

        /// <summary>Past this the clocks genuinely disagree (scene load, missed snap) -
        /// jump rather than crawl.</summary>
        private const double RenderClockResyncSec = 1.0;

        /// <summary>
        /// Drive the render clock. Called from Plugin.Update BEFORE the network pump
        /// and the replica drivers, so every consumer in a frame - including
        /// WeaponReplicaDriver and the sample-arrival path - reads the same value.
        /// </summary>
        internal static void TickRenderClock()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized)
            {
                _renderClockValid = false;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) AdvanceRenderClock(dt);
        }

        private static void AdvanceRenderClock(float dtReal)
        {
            // Game-seconds elapsed this frame. Deliberately derived from real frame
            // time and compression rather than read off the mission clock, because the
            // mission clock is the coarse thing being smoothed.
            float tc = GameTime.IsPaused() ? 0f : Mathf.Max(0f, GameTime.TimeCompression);
            _renderClock += (double)dtReal * tc;

            if (!_hasTransitEma) return;

            double target = (double)LocalMissionSeconds() - _emaTransitGameSec;
            if (!_renderClockValid)
            {
                _renderClock = target;
                _renderClockValid = true;
                return;
            }

            double err = target - _renderClock;
            if (System.Math.Abs(err) > RenderClockResyncSec)
            {
                _renderClock = target;
                return;
            }
            _renderClock += err * (1.0 - System.Math.Exp(-RenderClockLockRate * dtReal));
        }

        /// <summary>
        /// Best estimate of the HOST's mission clock right now, in the domain its batch
        /// stamps are written in - smooth and monotonic at render rate.
        /// </summary>
        internal static double HostClockNow()
            => _renderClockValid ? _renderClock : LocalMissionSeconds();

        /// <summary>True once the render clock has locked onto the host's - until then
        /// there is no common time base and age-from-stamp is meaningless.</summary>
        internal static bool HostClockLocked => _renderClockValid;

        /// <summary>
        /// The mission clock was snapped to the host's. Every buffered stamp, and the
        /// clock-offset EMA itself, is now expressed against a clock that no longer
        /// exists - and the offset feeds the render time, so a stale one would actively
        /// throw units off their track rather than merely degrade smoothing. Drop it all.
        /// </summary>
        public static void OnMissionClockSnapped()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            _samples.Clear();
            _hasTransitEma = false;
            _renderClockValid = false;   // re-lock rather than crawl onto the new clock
            _emaTransitGameSec = 0f;
            _emaTransitDevGameSec = 0f;
            _lastBatchHostSec = -1d;
            Plugin.Log.LogInfo("[UnitReplica] Mission clock snapped - snapshot rings and clock offset flushed.");
        }

        /// <summary>
        /// How far behind the host's clock to render this entity, in game-seconds.
        /// Sized to span two of ITS OWN send intervals plus the link's measured jitter,
        /// so a bracketing pair of snapshots is normally already in hand and the driver
        /// interpolates instead of extrapolating. Zero disables interpolation entirely.
        ///
        /// The interval has to be per-entity. Change detection gates every unit
        /// independently against a ~1 m position threshold, so a unit's effective rate
        /// is a function of its speed and those thresholds, not of the timer it rides -
        /// and the global figure measures the unit, near and missile streams
        /// interleaved, which is far shorter than any single entity's gap. Measured on
        /// a real session: global 31 ms against this ship's actual 91 ms, giving a
        /// delay less than a third of what it needed.
        ///
        /// An idle unit that only heartbeats lands on the ceiling clamp, which is
        /// harmless: a ship holding station has nothing to interpolate.
        /// </summary>
        private static float RenderDelayFor(Sample s)
        {
            if (!Plugin.Instance.CfgReplicaInterpolation.Value) return 0f;

            float interval = s.EmaIntervalGameSec > 0f ? s.EmaIntervalGameSec : _emaIntervalGameSec;
            float tc = Mathf.Max(1f, GameTime.TimeCompression);
            float delay = 2f * interval + 2f * _emaTransitDevGameSec;
            return Mathf.Clamp(delay, MinRenderDelayRealSec * tc, MaxRenderDelayRealSec * tc);
        }

        /// <summary>
        /// The local mission clock, in the same domain the host stamps batches with
        /// (<see cref="HostEntityStreamer"/> builds it identically). Continuous and
        /// compression-scaled, and it stops dead while paused - which is exactly
        /// what dead reckoning needs.
        /// </summary>
        private static double LocalMissionSeconds() => TimeSyncManager.MissionSeconds();

        /// <summary>Precise conversion, not the game's: Utils.longLatToLocal does the
        /// arithmetic in float32 through a value near 180, snapping east-west position
        /// to a ~1.1 m staircase. This runs every frame on the interpolation target, so
        /// that staircase became the replica's visible lateral jitter.</summary>
        private static Vector3 ToUnity(in Snapshot snap)
            => GeoCodec.ToUnity(snap.LatDeg, snap.LonDeg, snap.HeightM);

        /// <summary>
        /// Resolve where the host says this unit was at <paramref name="renderMissionSec"/>.
        ///
        /// Between two snapshots this interpolates, which is the whole point: an
        /// interpolated target cannot carry arrival-time noise, because both ends are
        /// states the host actually reported. Past the newest snapshot - a slow-updating
        /// unit, or a loss burst - it falls back to arc extrapolation, clamped.
        /// </summary>
        // Diagnostics for MotionTrace: which branch produced the last pose, its
        // interpolation fraction / extrapolation age, and (for INTERP) how far back in
        // the ring the bracketing pair was found. Read immediately after the call.
        // _poseMode holds literals only - this runs for every unit every frame, so it
        // must not allocate; the bracket index is formatted at trace time instead.
        private static string _poseMode = "";
        private static float  _poseT, _poseAge;
        private static int    _poseBack = -1;

        private static Pose ResolvePose(Sample s, double renderMissionSec)
        {
            var newest = s.At(0);
            _poseT = float.NaN;
            _poseBack = -1;
            _poseAge = (float)(renderMissionSec - newest.HostMissionSec);

            if (s.Count >= 2 && renderMissionSec < newest.HostMissionSec)
            {
                for (int back = 0; back < s.Count - 1; back++)
                {
                    var newer = s.At(back);
                    var older = s.At(back + 1);
                    double span = newer.HostMissionSec - older.HostMissionSec;
                    if (span <= 0d) continue;
                    if (renderMissionSec < older.HostMissionSec) continue;

                    float t = Mathf.Clamp01((float)((renderMissionSec - older.HostMissionSec) / span));
                    _poseMode = "INTERP";
                    _poseBack = back;
                    _poseT = t;
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
                _poseMode = "HOLD";
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
            float age = (float)(renderMissionSec - newest.HostMissionSec);
            if (age < -DayGameSeconds * 0.5f) age += DayGameSeconds; // midnight rollover
            if (age < 0f) age = 0f;
            float rawAge = age;
            age = Mathf.Min(age, MaxExtrapolationRealSec * Mathf.Max(1f, GameTime.TimeCompression));
            _poseMode = age < rawAge ? "EXTRAP_CAP" : "EXTRAP";
            _poseAge = age;

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

            // Render remote units slightly in the HOST's past so a bracketing pair of
            // snapshots is already in hand. This is what converts the link's arrival
            // jitter from target noise into a fixed, invisible offset. The delay is
            // per-entity, so it is computed inside the loop.
            double nowMissionSec = HostClockNow();

            float shipDriftSum = 0f, shipDriftMax = 0f; int shipCount = 0;
            float airDriftSum  = 0f, airDriftMax  = 0f; int airCount  = 0;

            foreach (var kv in _samples)
            {
                var s = kv.Value;
                var unit = s.Unit;
                if (unit == null || unit.IsDestroyed) { _toRemove.Add(kv.Key); continue; }
                if (s.Count == 0) continue;

                bool trace = MotionTrace.IsTracing(kv.Key);
                float  renderDelay      = RenderDelayFor(s);
                double renderMissionSec = nowMissionSec - renderDelay;

                float sampleAge = realNow - s.RecordRealTime;
                if (sampleAge > MaxSampleAgeRealSec)
                {
                    // Not corrected this frame - the local sim is on its own. Worth a
                    // row: a run of these next to visible jitter says the stream, not
                    // the corrector, is the problem.
                    if (trace)
                        MotionTrace.ClientCorrection(unit, unit.transform.position,
                            unit.transform.eulerAngles.y, unit.transform.position, float.NaN, float.NaN,
                            "STALE", float.NaN, float.NaN, nowMissionSec, renderMissionSec,
                            renderDelay, sampleAge, float.NaN, s.TurnRateDegSec, s.Count,
                            s.At(0).HostMissionSec, "sample too old, no correction");
                    continue;
                }

                var tr = unit.transform;
                bool isAir  = s.Kind == UnitType.Aircraft || s.Kind == UnitType.Helicopter;
                bool puppet = isAir && AircraftReplicaDriver.IsFormationPuppet(unit);

                // Transform before we touch it - everything between our last write and
                // now is the local sim's own doing (MotionTrace separates the two).
                Vector3 prePos = trace ? tr.position : default;
                float   preHdg = trace ? tr.eulerAngles.y : 0f;

                // Chase-driven aircraft measure against the track extrapolated to
                // NOW: their native physics flies at a point ahead of the newest
                // sample, so a render-delayed target sits speed x delay BEHIND the
                // aircraft - at time compression that systematic offset alone
                // exceeded the accept band and the corrector dragged the plane
                // backwards against its own flight model every frame (the
                // normal-flight ghosting). Puppets have no local motion to
                // disagree with, so they keep the smoother interpolated target.
                Pose pose = ResolvePose(s, isAir && !puppet ? nowMissionSec : renderMissionSec);
                string poseMode = trace && _poseBack > 0 ? _poseMode + _poseBack : _poseMode;
                float  poseT = _poseT, poseAge = _poseAge;

                if (isAir) DriveAircraft(unit, tr, s, in pose, easeDt, puppet, ref airDriftSum, ref airDriftMax, ref airCount);
                else       DriveSurface(unit, tr, s, in pose, easeDt, ref shipDriftSum, ref shipDriftMax, ref shipCount);

                if (trace)
                    MotionTrace.ClientCorrection(unit, prePos, preHdg,
                        pose.Position, pose.Heading, pose.Speed,
                        poseMode, poseT, poseAge,
                        nowMissionSec,
                        // the clock the pose was actually resolved at - chase-driven
                        // aircraft target NOW, everything else the delayed render time
                        isAir && !puppet ? nowMissionSec : renderMissionSec,
                        renderDelay, sampleAge, _lastEaseK,
                        s.TurnRateDegSec, s.Count, s.At(0).HostMissionSec,
                        puppet ? "puppet" : isAir ? "air" : "surface");
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
        /// <summary>Position-correction fraction the last drive actually applied
        /// (1 = snap, 0 = inside the accept band) - reported by MotionTrace.</summary>
        private static float _lastEaseK;

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

            // Host-authoritative attitude for everything that floats: a submarine's
            // dive angle, and a surface ship's heel and pitch. Leaving roll to the
            // local sim does not work on a replica - roll is a rigidbody result
            // (Compartments buoyancy torque + the turn-induced tilt applyTiltAndPitchForces
            // derives from CurrentTurnRate), and on a replica both inputs are dead:
            // the transform is written from the stream every frame, so the rudder
            // loop never turns the ship and CurrentTurnRate sits at ~0, and the
            // rigidbody's integrated rotation is overwritten before it can show.
            // The ship ends up holding whatever attitude it loaded with. The host
            // already streams PitchQ/RollQ, so take them - same mechanism heading
            // has always used.
            bool syncAttitude = s.Kind == UnitType.Submarine || s.Kind == UnitType.Vessel;
            var eul = tr.eulerAngles;

            if (drift > ShipSnapThreshold)
            {
                _lastEaseK = 1f;
                tr.position = new Vector3(pose.Position.x, pos.y, pose.Position.z);
                tr.eulerAngles = new Vector3(
                    syncAttitude ? pose.Pitch : eul.x, pose.Heading, syncAttitude ? pose.Roll : eul.z);
                unit._velocityInKnots = pose.Speed;
                return;
            }

            _lastEaseK = Ease(ShipPosSharpness, dt);
            tr.position = pos + err * _lastEaseK;

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

            _lastEaseK = kXZ;

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
                //
                // The third case is a unit with NO speed command at all. A save-based
                // session sync restores _telegraph but not SpeedCommand, which the game
                // only ever builds inside setTelegraph/SetSpeedCommand - so a ship whose
                // restored telegraph happens to match the host's was never going to be
                // called here, and its command stayed null for the rest of the mission.
                // The null-guards downstream (Patch_Compartments_UpdateWantedVelocityInKnots
                // and friends) then hold it at zero thrust and it is dragged along by
                // position corrections instead of sailing - 29 ships in one playtest.
                //
                // Not covered by the custom-speed test above it: CustomCommandKnots is NaN
                // both for a null command and for a healthy TelegraphSpeed, so it cannot
                // tell the two apart. setTelegraph rebuilds the command unconditionally
                // (no early return on a matching value), so one forced call repairs it and
                // the condition stops holding.
                if (unit.getTelegraph() != e.Telegraph
                    || !float.IsNaN(StateSerializer.CustomCommandKnots(unit))
                    || unit.SpeedCommand?.Value == null)
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
            _hasTransitEma = false;
            _lastBatchHostSec = -1d;
            _predErrShipSum = _predErrShipMax = 0f;
            _predErrAirSum  = _predErrAirMax  = 0f;
            _predErrShipN   = _predErrAirN    = 0;
            _predErrWindowEnd = 0f;
        }
    }
}
