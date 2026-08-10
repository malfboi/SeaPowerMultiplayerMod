using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side carrier flight-ops mimicry. Aircraft in the host's flight-deck
    /// pipeline (launch taxi/catapult, landing rollout) are "deck puppets" here:
    /// parented to the local carrier root, flight sim off (_isInFlight=false,
    /// _hasControl=false), colliders off (matching the host - FlightDeck spawns
    /// them collider-less), and driven by the host's carrier-relative DeckState
    /// stream. Elevator rides, taxi paths and the cat stroke reproduce because
    /// the host's real deck path is replayed in carrier-local space.
    ///
    /// Phase flips:
    ///  - deck → airborne: the host re-sends the EntitySpawn at giveControl
    ///    (wheels-up); FlipToAirborne unparents and hands the unit to the normal
    ///    stream chase. A world-space stream sample for a puppet is treated the
    ///    same way after a grace window (missed-flip self-heal).
    ///  - airborne → deck: a DeckState sample for a flying replica means the host
    ///    parented it to a carrier (touchdown) - EnterDeckMode mirrors that.
    /// </summary>
    public static class DeckPuppetDriver
    {
        private class Puppet
        {
            public ObjectBase Unit = null!;
            public ObjectBase Carrier = null!;
            public Vector3 TargetLocal;
            public float   TargetYawDeg;
            public bool    HasSample;
            public float   EnterRealtime;

            // Launch-phase spool-up (see ParkAirframe / TickSpool). Only a puppet
            // that arrived as a deck SPAWN is parked and spooled; one that entered
            // deck mode by touching down is already running and stays that way.
            public bool    Parked;
            public Vector3 ParkedAtLocal;
            public bool    Spooling;
            public float   SpoolStartTime;
            public bool    RotorsEngaged;
        }

        private const float LerpFactor = 0.25f;
        // World samples sent before a touchdown re-parent can arrive (unreliable,
        // reordered) after the deck flip - ignore them briefly instead of flapping.
        private const float WorldSampleGraceSec = 1.5f;

        private static readonly Dictionary<int, Puppet> _puppets = new();
        private static readonly List<int> _toRemove = new();

        public static int ActivePuppets => _puppets.Count;

        public static bool IsDeckPuppet(int unitId) => _puppets.ContainsKey(unitId);

        // How far the host's deck sim has to move a puppet from where it first
        // appeared before we call it "being taxied out" and start the spool. Deck
        // samples are quantised and a parked airframe does not wander, so a couple of
        // metres is comfortably above the noise and well below an elevator ride.
        private const float TaxiTriggerMetres = 2f;

        /// <summary>Deck-phase spawn from SpawnReplicator (EntitySpawn with the deck flag).</summary>
        public static void RegisterDeckSpawn(ObjectBase unit, ObjectBase carrier)
        {
            EnterDeckMode(unit, carrier);
            ParkAirframe(unit);
        }

        /// <summary>Put a freshly replicated deck launch into the state the HOST's copy
        /// is in at the same moment, which is parked and cold.
        ///
        /// The client builds every replica through ObjectsManager.createHelicopter with
        /// parent null, and that creator's `homeBase != null &amp;&amp; parent != null`
        /// test therefore fails even for a deck launch - so it takes the airborne else
        /// branch and calls setImmediateFlightConditions + setAnimsForFlight +
        /// GiveControl(0). setImmediateFlightConditions writes full rotor RPM straight
        /// into _rotorCurrentRPM, so the helicopter was at flight RPM from the frame it
        /// replicated, including while still sitting in the hangar - playtest 40's
        /// "helicopters came out of the hangar with rotors already spinning".
        ///
        /// The host's own path does the opposite two lines after creating it:
        /// FlightDeck.getObjectToLaunch calls setAnimsForTakeoff() and clears
        /// _isInFlight. Mirroring those exact calls is the fix - not a replayed
        /// animation, the same call on the same object at the same point in its life.
        ///
        /// Helicopters only. A fixed-wing replica comes out of the same else branch
        /// with setAnimsForFlight, which is the state it wants for its whole visible
        /// deck run anyway; its below-decks Wings_Extend happens before the replica
        /// exists and there is nothing on this side to undo.</summary>
        private static void ParkAirframe(ObjectBase unit)
        {
            if (!(unit is Helicopter h)) return;

            h._helicopterAnimation?.setAnimsForTakeoff();
            // engageRotors(false), not stopRotors(): the latter starts a spin-DOWN,
            // which is a fair description of a helo shutting down and a poor one for
            // a machine that was never running.
            h._hfcs?.engageRotors(false);

            // The RPM NUMBER, not just the switch. engageRotors(false) only stops it
            // CLIMBING; setImmediateFlightConditions already stamped _rotorCurrentRPM
            // to full at creation and the stock decay is 2.5% of max per second, so it
            // still reads as spinning for the better part of a minute. Two things
            // sample it, and they disagreed: the blade animation cuts out below 1%
            // (HelicopterFlightControlSystem.cs:167), which is why the rotors LOOKED
            // stopped, while _hoverWavesOverlay recomputes the downwash water disc from
            // GetRelativeRPM every frame and only switches off below 0.1
            // (Helicopter.cs:345). Hence a parked helo with still blades churning the
            // sea under itself.
            if (_rotorCurrentRpmField?.GetValue(h._hfcs) is float[] rpm)
                for (int i = 0; i < rpm.Length; i++) rpm[i] = 0f;

            // Audio does not read RPM at all: GiveControl latches _rotorsFlightStarted
            // (Helicopter.cs:1263) and the creator called it on the way past, so the
            // flight loop was playing on a cold airframe. Stopping the sources alone
            // will not do - the latch is still up and OnUpdate re-plays the loop on the
            // next frame - so it has to be cleared first.
            _rotorsFlightStartedField?.SetValue(h, false);
            StopRotorAudio(h);

            if (_puppets.TryGetValue(unit.UniqueID, out var p)) p.Parked = true;
        }

        // Both private on their own class, and both are state the CREATOR set that has
        // no public undo. Resolved once and allowed to degrade: on a build that renames
        // either, the launch still replicates and only the cosmetic park is lost - the
        // same trade FlightDeckStreamer makes for CrewSkill.
        private static readonly System.Reflection.FieldInfo? _rotorCurrentRpmField =
            HarmonyLib.AccessTools.Field(typeof(HelicopterFlightControlSystem), "_rotorCurrentRPM");
        private static readonly System.Reflection.FieldInfo? _rotorsFlightStartedField =
            HarmonyLib.AccessTools.Field(typeof(Helicopter), "_rotorsFlightStarted");

        /// <summary>Silence a parked airframe. Unity's null operator throughout - these
        /// are scene objects, and `?.` would happily call Stop on a destroyed one.</summary>
        private static void StopRotorAudio(Helicopter h)
        {
            var hp = h._hp;
            if (hp == null) return;

            if (hp._enginePowerUpAudioSource   != null) hp._enginePowerUpAudioSource.Stop();
            if (hp._engineLoopAudioSource      != null) hp._engineLoopAudioSource.Stop();
            if (hp._rotorsIdleLoopAudioSource  != null) hp._rotorsIdleLoopAudioSource.Stop();
            if (hp._rotorsFlightLoopAudioSource != null) hp._rotorsFlightLoopAudioSource.Stop();
        }

        /// <summary>Stock spool, replayed on the puppet: HelicopterTakeOff.onEnter runs
        /// powerUpEngines, and its onUpdate engages the rotors once _engineWarmUpTime
        /// has passed. FlipToAirborne's setImmediateFlightConditions still lands exactly
        /// as before - this only fills in the middle the client never had.
        ///
        /// Triggered by the host MOVING the aircraft, not by deck-mode entry. A puppet
        /// enters deck mode the moment it replicates anywhere on the ship, hangar
        /// included, so an entry-based timer spins up idle rotors on a helo that is
        /// still parked below decks (playtest 40b caught exactly that).</summary>
        private static void TickSpool(Puppet p)
        {
            if (!p.Parked || !(p.Unit is Helicopter h)) return;

            if (!p.Spooling)
            {
                if ((p.TargetLocal - p.ParkedAtLocal).sqrMagnitude
                    < TaxiTriggerMetres * TaxiTriggerMetres) return;

                p.Spooling = true;
                p.SpoolStartTime = GameTime.time;
                h.powerUpEngines();
                return;
            }

            if (p.RotorsEngaged) return;
            if (GameTime.time - p.SpoolStartTime < (h._hp?._engineWarmUpTime ?? 30f)) return;

            p.RotorsEngaged = true;
            h.engageRotors();
        }

        private static void EnterDeckMode(ObjectBase unit, ObjectBase carrier)
        {
            SetInFlight(unit, false);
            unit._hasControl = false;
            SetCollidersActive(unit, false);
            unit.transform.SetParent(carrier.transform, worldPositionStays: true);
            AircraftReplicaDriver.Forget(unit.UniqueID);
            _puppets[unit.UniqueID] = new Puppet
            {
                Unit          = unit,
                Carrier       = carrier,
                EnterRealtime = Time.realtimeSinceStartup,
            };
        }

        public static void OnDeckState(DeckStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            if (!_puppets.TryGetValue(msg.AircraftId, out var p))
            {
                // Flying replica got a deck sample → host parented it to a carrier
                // (landing rollout). Mirror the flip.
                var unit = ReplicaRegistry.Find(msg.AircraftId) ?? StateSerializer.FindById(msg.AircraftId);
                if (unit == null || unit is WeaponBase) return;
                if (!(unit is Aircraft) && !(unit is Helicopter)) return;
                var carrier = StateSerializer.FindById(msg.CarrierId);
                if (carrier == null) return;

                EnterDeckMode(unit, carrier);
                p = _puppets[msg.AircraftId];
                Telemetry.Count("v2.deckEnter");
            }

            p.TargetLocal  = new Vector3(msg.LocalX, msg.LocalY, msg.LocalZ);
            p.TargetYawDeg = GeoCodec.UnpackHeading(msg.LocalYawQ);
            if (!p.HasSample)
            {
                p.HasSample = true;
                // First sample: snap into place (spawn placement was approximate)
                var tr = p.Unit.transform;
                tr.localPosition = p.TargetLocal;
                var e = tr.localEulerAngles;
                tr.localEulerAngles = new Vector3(e.x, p.TargetYawDeg, e.z);
                // Where "parked" is, for the taxi test - the spawn's own position is
                // approximate, so the first real sample is the reference.
                p.ParkedAtLocal = p.TargetLocal;
            }
        }

        /// <summary>UnitReplicaDriver hook: a WORLD-space sample arrived for this
        /// unit. For a deck puppet that means the host flew it off (and the client
        /// missed the wheels-up spawn re-send) - flip after a grace window that
        /// absorbs reordered pre-touchdown packets. Returns true when the sample
        /// was consumed (caller skips normal world application).</summary>
        public static bool HandleWorldSample(ObjectBase unit, in EntityState e)
        {
            if (!_puppets.TryGetValue(e.EntityId, out var p)) return false;

            if (Time.realtimeSinceStartup - p.EnterRealtime < WorldSampleGraceSec)
                return true; // swallow - likely a stale pre-flip packet

            var geo = new GeoPosition(e.LatDeg, e.LonDeg, e.HeightM);
            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
            FlipToAirborne(unit,
                new Vector3(local.x, e.HeightM, local.y),
                GeoCodec.UnpackHeading(e.HeadingQ),
                GeoCodec.UnpackSpeedKts(e.SpeedQ));
            Telemetry.Count("v2.deckFlipFromSample");
            return true;
        }

        /// <summary>Wheels-up: re-sent EntitySpawn for an existing puppet.</summary>
        public static void FlipToAirborne(ObjectBase unit, EntitySpawnMessage msg)
        {
            var geo = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
            FlipToAirborne(unit,
                new Vector3(local.x, msg.HeightM, local.y),
                GeoCodec.UnpackHeading(msg.HeadingQ),
                GeoCodec.UnpackSpeedKts(msg.SpeedQ));
        }

        public static void FlipToAirborne(ObjectBase unit, Vector3 posUnity, float headingDeg, float speedKts)
        {
            _puppets.Remove(unit.UniqueID);

            // giveControl unparents; position at the host's wheels-up point
            if (unit is Aircraft a)
            {
                a.AircraftAnimation.setAnimsForFlight();
                a.giveControl(speedKts);
            }
            else if (unit is Helicopter h)
            {
                h.GiveControl(speedKts);
                h.setImmediateFlightConditions();
            }
            SetInFlight(unit, true);
            unit.transform.position = posUnity;
            unit.transform.eulerAngles = new Vector3(0f, headingDeg, 0f);
            unit._geoPosition = Utils.worldPositionFromUnityToLongLat(posUnity, Globals._currentCenterTile);
            // Colliders stay off - host deck-launched aircraft fly collider-less too
            Telemetry.Count("v2.deckAirborne");
        }

        /// <summary>Per-frame drive (Plugin.Update, client).</summary>
        public static void Tick()
        {
            if (_puppets.Count == 0) return;
            if (Plugin.Instance.CfgIsHost.Value) return;

            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                if (p.Unit == null || p.Unit.IsDestroyed || p.Carrier == null)
                {
                    _toRemove.Add(kv.Key);
                    continue;
                }
                if (!p.HasSample) continue;

                TickSpool(p);

                var tr = p.Unit.transform;
                Vector3 preLocal = tr.localPosition;
                tr.localPosition = Vector3.Lerp(tr.localPosition, p.TargetLocal, LerpFactor);
                var e = tr.localEulerAngles;
                tr.localEulerAngles = new Vector3(
                    e.x, Mathf.LerpAngle(e.y, p.TargetYawDeg, LerpFactor), e.z);

                if (MotionTrace.IsTracing(kv.Key))
                    MotionTrace.DeckPuppet(p.Unit, p.Carrier, preLocal, p.TargetLocal,
                        p.TargetYawDeg, LerpFactor);

                // Map/sensor maths read the geo position
                p.Unit._geoPosition = Utils.worldPositionFromUnityToLongLat(
                    tr.position, Globals._currentCenterTile);
            }

            if (_toRemove.Count > 0)
            {
                for (int i = 0; i < _toRemove.Count; i++) _puppets.Remove(_toRemove[i]);
                _toRemove.Clear();
            }
        }

        public static void Forget(int unitId) => _puppets.Remove(unitId);

        public static void Reset() => _puppets.Clear();

        private static void SetInFlight(ObjectBase unit, bool inFlight)
        {
            if (unit is Aircraft a) a._isInFlight = inFlight;
            else if (unit is Helicopter h) h._isInFlight = inFlight;
        }

        internal static void SetCollidersActive(ObjectBase unit, bool active)
        {
            var obp = unit._obp;
            if (obp == null) return;
            if (obp._meshCollidersParent != null) obp._meshCollidersParent.SetActive(active);
            if (obp._hitboxes == null) return;
            foreach (var hitbox in obp._hitboxes)
            {
                if (hitbox?._go != null) hitbox._go.SetActive(active);
            }
        }
    }
}
