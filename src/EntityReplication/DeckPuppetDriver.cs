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
        }

        private const float LerpFactor = 0.25f;
        // World samples sent before a touchdown re-parent can arrive (unreliable,
        // reordered) after the deck flip - ignore them briefly instead of flapping.
        private const float WorldSampleGraceSec = 1.5f;

        private static readonly Dictionary<int, Puppet> _puppets = new();
        private static readonly List<int> _toRemove = new();

        public static int ActivePuppets => _puppets.Count;

        public static bool IsDeckPuppet(int unitId) => _puppets.ContainsKey(unitId);

        /// <summary>Deck-phase spawn from SpawnReplicator (EntitySpawn with the deck flag).</summary>
        public static void RegisterDeckSpawn(ObjectBase unit, ObjectBase carrier)
            => EnterDeckMode(unit, carrier);

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
