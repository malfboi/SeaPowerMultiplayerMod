using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Drives inert (KinematicWeapon) replicas on the client: dead-reckons each
    /// weapon from its latest host sample along heading/pitch, aged on the HOST's
    /// clock so arrival jitter never reaches the transform, and reconciles each new
    /// sample through a decaying offset rather than a snap. Also pumps the visual side
    /// the suppressed state machine would normally run - mesh switch (canister → main
    /// mesh + afterburner), booster/sustainer flight effects, engine audio.
    /// </summary>
    public static class WeaponReplicaDriver
    {
        // Unity units per (knot · game-second) - the game's own conversion
        private const float UnityPerKnotSecond = 0.0076554087f;
        /// <summary>Dead-reckoning ceiling in REAL seconds, scaled by compression at
        /// use. Previously compared against a GAME-second accumulator, so at 10x it
        /// silently allowed ten times the intended travel - a Mach 2.5 missile could
        /// run ~750 m off track before the cap noticed.</summary>
        private const float MaxExtrapolateRealSec = 0.25f;

        /// <summary>
        /// Rate the per-sample model error is bled off, per real second.
        ///
        /// This is a velocity budget, not a position one. Removing a position offset
        /// exponentially means the rendered velocity carries an error of exactly
        /// (rate x offset), largest just after each packet and decaying to zero - a
        /// sawtooth in speed at the packet rate, which reads as surging.
        ///
        /// What is being filtered is a HOST-side artefact, not client error. A weapon's
        /// transform is stepped by physics at about half the render rate (measured: it
        /// moved on 48% of host frames), but the mission clock advances every frame, so
        /// the position the host captures is stale by 0-1 physics steps while its
        /// timestamp is current. Measured on a Harpoon flying dead straight
        /// (heading 197.70 +/- 0.07 deg) at exactly 540.0 kt, that mismatch is the WHOLE
        /// error: along-track scatter of sd 2.15 m, mean -0.004 m, with lateral error
        /// sd 0.02 m and an actual/predicted distance ratio of 0.9998.
        ///
        /// Being zero-mean is what makes filtering the right answer - successive samples
        /// cancel. From the measured figures (e_sd 2.15 m, gap 0.0567 s):
        ///     offset_sd  ~ e_sd / sqrt(2 k gap)
        ///     speed_err  ~ k x offset_sd = e_sd sqrt(k / 2 gap)   - i.e. proportional to sqrt(k)
        /// so speed error falls only as sqrt(k) while position offset grows as 1/sqrt(k).
        /// Measured, not predicted: k=15 gave 7.3%, k=4 gave 5.6%, k=1 gave 6.3%. The
        /// family bottoms out near k=4. Going lower does NOT keep helping - the standing
        /// offset grows as 1/sqrt(k) (mean magnitude 7.4 m at k=4, 11.9 m at k=1) until
        /// it collides with the saturation ceiling below, and that costs more than the
        /// slower decay saves. The host's own speed varies by 3.0%, so this leaves a
        /// residual; see ReplicaTick for the frame-ordering issue that turned out to
        /// dominate what was actually visible on screen.
        /// </summary>
        private const float OffsetDecayRate = 4f;

        /// <summary>
        /// Hard ceiling on how much of the offset may be removed in one frame, as a
        /// fraction of the distance the weapon travels in that frame. Bounds the speed
        /// error at this fraction NO MATTER how large the offset is, so a pathological
        /// case (a 300 ms packet stall leaves ~23 m) bleeds off slowly instead of
        /// lurching. Without it the decay rate alone sets the surge, and the surge
        /// scales with the outlier.
        /// </summary>
        private const float OffsetMaxSpeedFraction = 0.05f;

        /// <summary>Ceiling on the carried offset. SATURATES at this value - it does not
        /// reset to zero. Zeroing discarded the whole offset in one frame, which is a
        /// 30 m position jump and a velocity spike far worse than the error it was
        /// meant to bound; at low decay rates the offset reached this ceiling routinely
        /// (measured max 29.78 m) so the cure fired constantly. Unity units.</summary>
        private const float MaxOffsetUnits = 30f / GeoCodec.MetresPerUnityUnit;

        private class Replica
        {
            public WeaponBase Weapon = null!;
            public bool IsMissile;
            // Latest host sample
            public double LonDeg, LatDeg;
            public float HeightM, HeadingDeg, PitchDeg, SpeedKts;
            public float GameTimeSinceSample;   // accumulated GameTime.deltaTime
            public float SampleRealtime;
            public bool HasSample;

            /// <summary>Host mission clock of the newest applied sample. Drives the
            /// extrapolation age and gates out reordered stamps.</summary>
            public double LastHostSec = double.NaN;

            /// <summary>Unapplied model error, bled off over a few frames so an arriving
            /// sample never snaps the transform backwards. Unity units.</summary>
            public Vector3 PosOffset;
        }

        private static readonly Dictionary<int, Replica> _replicas = new();
        private static readonly List<int> _toRemove = new();

        /// <summary>Most recent dead-reckoning error, metres - read by MotionTrace.</summary>
        private static float _lastPredErrM = float.NaN;
        public static float LastPredictionErrorM => _lastPredErrM;

        // State samples that arrived before their spawn message (unreliable vs reliable race)
        private static readonly Dictionary<int, (EntityState entry, double hostSec, float realtime)> _pendingSamples = new();
        private const float PendingSampleTtlSec = 2f;

        // ── Reflection for Missile's private effects pump ─────────────────────
        private static readonly Action<Missile, bool>? _scheduleFlightEffects = BuildScheduleFlightEffects();
        private static readonly Action<Missile>? _updateFlightEffects = BuildUpdateFlightEffects();
        private static readonly AccessTools.FieldRef<ObjectBase, ObjectSoundHandler>? _soundHandlerRef =
            AccessTools.FieldRefAccess<ObjectBase, ObjectSoundHandler>("_soundHandler");

        private static Action<Missile, bool>? BuildScheduleFlightEffects()
        {
            var m = AccessTools.Method(typeof(Missile), "ScheduleFlightEffects");
            if (m == null) return null;
            return (Action<Missile, bool>)Delegate.CreateDelegate(typeof(Action<Missile, bool>), m);
        }

        private static Action<Missile>? BuildUpdateFlightEffects()
        {
            var m = AccessTools.Method(typeof(Missile), "UpdateFlightEffects");
            if (m == null) return null;
            return (Action<Missile>)Delegate.CreateDelegate(typeof(Action<Missile>), m);
        }

        public static int ActiveReplicas => _replicas.Count;

        // ── Registration ──────────────────────────────────────────────────────

        public static void OnReplicaSpawned(WeaponBase wb, EntitySpawnMessage msg)
        {
            _replicas[msg.EntityId] = new Replica
            {
                Weapon     = wb,
                IsMissile  = wb is Missile,
                LonDeg     = msg.LonDeg,
                LatDeg     = msg.LatDeg,
                HeightM    = msg.HeightM,
                HeadingDeg = GeoCodec.UnpackHeading(msg.HeadingQ),
                PitchDeg   = GeoCodec.UnpackAngleCdeg(msg.PitchQ),
                SpeedKts   = GeoCodec.UnpackSpeedKts(msg.SpeedQ),
                SampleRealtime = Time.realtimeSinceStartup,
                HasSample  = true,
            };

            // Seed with a fresher pre-arrived sample if one raced the spawn
            if (_pendingSamples.TryGetValue(msg.EntityId, out var pending)
                && Time.realtimeSinceStartup - pending.realtime < PendingSampleTtlSec)
            {
                OnSample(in pending.entry, pending.hostSec);
            }
            _pendingSamples.Remove(msg.EntityId);
        }

        public static void OnReplicaDemoted(WeaponBase wb)
        {
            // Save-restored weapon demoted after a session sync: seed from its
            // current transform; the host stream takes over within a tick.
            var geo = GeoCodec.ToGeo(wb.transform.position);
            _replicas[wb.UniqueID] = new Replica
            {
                Weapon     = wb,
                IsMissile  = wb is Missile,
                LonDeg     = geo._longitude,
                LatDeg     = geo._latitude,
                HeightM    = (float)geo._height,
                HeadingDeg = wb.transform.eulerAngles.y,
                PitchDeg   = Utils.WrapAngle(wb.transform.eulerAngles.x),
                SpeedKts   = wb._velocityInKnots,
                SampleRealtime = Time.realtimeSinceStartup,
                HasSample  = true,
            };
        }

        /// <summary>Called by UnitReplicaDriver for weapon-kind state entries.
        /// <paramref name="hostSec"/> is the batch's host mission stamp: it ages the
        /// extrapolation and orders the samples.</summary>
        public static void OnSample(in EntityState e, double hostSec)
        {
            if (!_replicas.TryGetValue(e.EntityId, out var r))
            {
                if (!SpawnReplicator.IsTombstoned(e.EntityId))
                {
                    _pendingSamples[e.EntityId] = (e, hostSec, Time.realtimeSinceStartup);
                    Telemetry.Count("v2.weaponSamplePending");
                }
                return;
            }

            // Reordered or duplicate stamp: DROP it. The state channel is unreliable
            // and the batch-level guard only catches gross reordering, so a stale
            // sample used to overwrite a fresher one and throw the target back down
            // the track. Units have had this guard all along; weapons did not.
            if (!double.IsNaN(r.LastHostSec) && hostSec <= r.LastHostSec)
            {
                Telemetry.Count("v2.weaponSampleReordered");
                return;
            }

            MeasureSample(r, in e, hostSec);

            // Where the OLD sample says the weapon is right now, before we replace it.
            bool hadSample = r.HasSample;
            Vector3 beforeSwap = hadSample ? TargetNow(r) : Vector3.zero;

            r.LastHostSec = hostSec;

            r.LonDeg     = e.LonDeg;
            r.LatDeg     = e.LatDeg;
            r.HeightM    = e.HeightM;
            r.HeadingDeg = GeoCodec.UnpackHeading(e.HeadingQ);
            r.PitchDeg   = GeoCodec.UnpackAngleCdeg(e.PitchQ);
            r.SpeedKts   = GeoCodec.UnpackSpeedKts(e.SpeedQ);
            r.GameTimeSinceSample = 0f;
            r.SampleRealtime = Time.realtimeSinceStartup;
            r.HasSample = true;

            // Carry the disagreement forward as a decaying offset instead of snapping.
            //
            // Constant-velocity dead reckoning cannot follow a manoeuvring weapon: on a
            // Harpoon the model was out by ~2 m per 57 ms packet (predErrM 1.99 m mean),
            // and 28% of arrivals wanted to move the target BACKWARDS. Snapping that
            // straight onto the transform is the ghosting - a missile advancing 2.2 m
            // per frame that jumps back 2 m eight frames out of nine reads as juddering
            // in place. Easing toward the target instead (the previous behaviour) hid
            // it, but bought a permanent speed-proportional lag - 5.9 m measured.
            //
            // Holding the error and bleeding it off keeps BOTH: velocity stays exactly
            // right so the weapon never stalls or reverses, and the offset converges to
            // zero so there is no standing lag.
            if (hadSample)
            {
                r.PosOffset += beforeSwap - TargetNow(r);
                // Saturate rather than reset: keep the direction and drop only the
                // excess, so a large disagreement still bleeds off smoothly.
                float mag = r.PosOffset.magnitude;
                if (mag > MaxOffsetUnits) r.PosOffset *= MaxOffsetUnits / mag;
            }
        }

        /// <summary>
        /// Grade the dead-reckoning model against ground truth, the same way
        /// UnitReplicaDriver.RecordPredictionError does: project the PREVIOUS sample
        /// forward to the new sample's host time and compare. Unlike drift (the
        /// controller residual, which the per-frame lerp drives to near-zero whatever
        /// the stream rate), this is the error the smoothing is there to hide - it is
        /// the figure that responds to stream rate. Called only for in-order samples.
        /// </summary>
        private static void MeasureSample(Replica r, in EntityState e, double hostSec)
        {
            _lastPredErrM = float.NaN;
            if (double.IsNaN(r.LastHostSec)) return;

            double gap = hostSec - r.LastHostSec;
            if (gap > 5d) return; // long gap - the old sample says nothing about now

            Vector3 predicted = DeadReckon(r, (float)gap);
            Vector3 actual    = GeoCodec.ToUnity(e.LatDeg, e.LonDeg, e.HeightM);

            // Horizontal only, so it stays a real distance (y is metres, x/z are ~67 m units)
            float dx = (actual.x - predicted.x) * GeoCodec.MetresPerUnityUnit;
            float dz = (actual.z - predicted.z) * GeoCodec.MetresPerUnityUnit;
            _lastPredErrM = Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static void Forget(int entityId)
        {
            _replicas.Remove(entityId);
            _pendingSamples.Remove(entityId);
        }

        public static void Reset()
        {
            _replicas.Clear();
            _pendingSamples.Clear();
        }

        /// <summary>Unit vector the weapon is travelling along.</summary>
        private static Vector3 Direction(Replica r)
            => Quaternion.Euler(r.PitchDeg, r.HeadingDeg, 0f) * Vector3.forward;

        /// <summary>
        /// How far past its sample to extrapolate, in game-seconds.
        ///
        /// Aged on the HOST's clock, not from when the packet happened to arrive -
        /// counting from arrival folds the link's arrival jitter (measured sd 0.022 s,
        /// which at 277 m/s is 6.1 m) straight into the target. Between spawn and the
        /// first streamed sample there is no host stamp, so it falls back to
        /// time-since-arrival rather than pinning the weapon at its launch point for a
        /// packet interval.
        /// </summary>
        private static float ExtrapolationAge(Replica r)
        {
            float age = UnitReplicaDriver.HostClockLocked && !double.IsNaN(r.LastHostSec)
                ? (float)(UnitReplicaDriver.HostClockNow() - r.LastHostSec)
                : r.GameTimeSinceSample;
            if (float.IsNaN(age) || age < 0f) age = 0f;
            return Mathf.Min(age, MaxExtrapolateRealSec * Mathf.Max(1f, GameTime.TimeCompression));
        }

        /// <summary>Dead-reckoned position <paramref name="seconds"/> past the sample.</summary>
        private static Vector3 DeadReckon(Replica r, float seconds)
            => GeoCodec.ToUnity(r.LatDeg, r.LonDeg, r.HeightM)
               + Direction(r) * (r.SpeedKts * UnityPerKnotSecond * seconds);

        /// <summary>Dead-reckoned position for the CURRENT render instant.</summary>
        private static Vector3 TargetNow(Replica r) => DeadReckon(r, ExtrapolationAge(r));

        // ── Per-frame drive (called from Plugin.Update on the client) ─────────

        public static void Tick()
        {
            if (_replicas.Count == 0) return;
            if (Plugin.Instance.CfgIsHost.Value) return;

            float dt = GameTime.deltaTime; // 0 while paused; scales with compression

            foreach (var kv in _replicas)
            {
                var r = kv.Value;
                var wb = r.Weapon;
                if (wb == null || wb.IsDestroyed) { _toRemove.Add(kv.Key); continue; }
                if (!r.HasSample) continue;

                r.GameTimeSinceSample += dt;   // still drives _dt / effect timing below

                // basePos and dir are needed again below (velocity vector, trace), so
                // derive the target here rather than letting TargetNow recompute both.
                Vector3 basePos = GeoCodec.ToUnity(r.LatDeg, r.LonDeg, r.HeightM);
                Vector3 dir     = Direction(r);
                Vector3 target  = basePos + dir * (r.SpeedKts * UnityPerKnotSecond * ExtrapolationAge(r));

                // Bleed off the accumulated model error, rate-limited against this
                // frame's forward travel so the speed error it induces stays bounded.
                // Frame-rate independent: the exponential runs on real time, the
                // ceiling on the distance actually covered this frame.
                Vector3 bleed = r.PosOffset * (1f - Mathf.Exp(-OffsetDecayRate * Time.unscaledDeltaTime));
                float maxBleed = OffsetMaxSpeedFraction * r.SpeedKts * UnityPerKnotSecond * dt;
                float bleedMag = bleed.magnitude;
                if (bleedMag > maxBleed) bleed *= maxBleed / bleedMag;
                r.PosOffset -= bleed;

                var tr = wb.transform;
                bool trace = MotionTrace.IsTracing(kv.Key);
                Vector3 prePos = trace ? tr.position : default;

                // Assign directly - no easing toward the target. Missile/Torpedo/Bomb
                // OnFixedUpdate and OnUpdateEveryFrame are suppressed on the client, so
                // nothing else moves a weapon transform: there is no local sim to blend
                // with. Continuity comes from the decaying offset, not from smoothing
                // the target, so velocity stays exactly right and no lag accumulates.
                tr.position = target + r.PosOffset;
                tr.eulerAngles = new Vector3(r.PitchDeg, r.HeadingDeg, 0f);

                wb._velocityInKnots = r.SpeedKts;
                wb._velocityInUnity = r.SpeedKts * UnityPerKnotSecond; // suppressed native update maintains this
                wb._velocityVecInUnity = dir * wb._velocityInUnity;   // map course leader reads this vector
                // The suppressed native update would maintain these: geo position
                // feeds the map/sensor/threat maths, and the reactive properties
                // feed the map UI (course/speed/altitude readouts - without them
                // the map shows the launch heading forever).
                wb._geoPosition = GeoCodec.ToGeo(tr.position);
                wb.Heading.Value  = wb.getHeading();
                wb.Velocity.Value = wb.getVelocityInKnots();
                wb.Altitude.Value = wb.getHeightInFeet();
                wb._dt += dt; // generic time-since-launch - drives mesh-switch/effects timing

                // easeK carries the offset magnitude in metres: it is the quantity that
                // has to converge to zero, and watching it is how you tell ordinary
                // model error from a real discontinuity.
                if (trace)
                    MotionTrace.ClientWeapon(wb, prePos, target, basePos,
                        r.PosOffset.magnitude * GeoCodec.MetresPerUnityUnit,
                        r.GameTimeSinceSample, r.SpeedKts, r.HeadingDeg, r.PitchDeg);

                // Kill the game's debug weapon trail if one exists (root-level
                // TrailRenderer, solid red line - created during the save-load
                // relaunch while DM._showWeaponTrails was on, before demotion).
                var dbgTrail = tr.GetComponent<TrailRenderer>();
                if (dbgTrail != null && dbgTrail.enabled) dbgTrail.enabled = false;

                if (r.IsMissile)
                    PumpMissileVisuals((Missile)wb);

                // Audio follows the weapon
                if (_soundHandlerRef != null)
                {
                    var sh = _soundHandlerRef(wb);
                    sh?.OnUpdate();
                }
            }

            if (_toRemove.Count > 0)
            {
                for (int i = 0; i < _toRemove.Count; i++) _replicas.Remove(_toRemove[i]);
                _toRemove.Clear();
            }

            // Prune stale pending samples
            if (_pendingSamples.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                _toRemove.Clear();
                foreach (var kv in _pendingSamples)
                    if (now - kv.Value.realtime > PendingSampleTtlSec) _toRemove.Add(kv.Key);
                for (int i = 0; i < _toRemove.Count; i++) _pendingSamples.Remove(_toRemove[i]);
                _toRemove.Clear();
            }
        }

        /// <summary>
        /// The visual progression Missile.Launch.takeAction would run: canister →
        /// main mesh (+ AFTERBURNER submodels) at the resource-defined switch time,
        /// booster/sustainer effect scheduling, per-frame effect/audio updates.
        /// </summary>
        private static void PumpMissileVisuals(Missile m)
        {
            var wi = m._weaponInstance;
            if (wi != null && !m._launchObjectSwitched && m._dt > wi._resourcesMeshSwitchTime)
            {
                m._launchObjectSwitched = true;
                if (wi._mainMesh != null) wi._mainMesh.SetActive(true);
                if (wi._mainMeshForLaunch != null) wi._mainMeshForLaunch.SetActive(false);
                if (wi._mainMeshCanister != null) wi._mainMeshCanister.SetActive(false);
                if (wi._subModels != null)
                {
                    foreach (var sub in wi._subModels)
                    {
                        if (sub._type == WeaponSubModel.Type.AFTERBURNER && sub._subModel != null)
                            sub._subModel.gameObject.SetActive(true);
                    }
                }
            }

            if (!m._isBoosterEffectStarted)
                _scheduleFlightEffects?.Invoke(m, false);

            _updateFlightEffects?.Invoke(m);

            // Booster burnout: vanilla cuts the booster in the flight-stage logic
            // we suppress, and UpdateFlightEffects never ends it (endDelay = -1) -
            // without this the booster flame/trail burns for the whole flight.
            // IsMotorBurning runs the real thrust curve, so the cut lands on
            // vanilla timing.
            if (m._isBoosterEffectStarted && m._boosterEffect != null && !m.IsMotorBurning)
            {
                // Components can sit on children of the effect instance - stop them all.
                foreach (var ps in m._boosterEffect.GetComponentsInChildren<ParticleSystem>())
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                foreach (var trail in m._boosterEffect.GetComponentsInChildren<TrailRenderer>())
                    trail.emitting = false;
                m._weaponInstance?._boosterAudioSource?.Stop();
                m._boosterEffect = null;
            }
        }
    }
}
