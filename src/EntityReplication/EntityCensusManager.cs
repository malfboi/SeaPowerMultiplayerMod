using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Self-healing backbone. HOST: every 5 s broadcasts the manifest of all
    /// replicated entity ids - world units (UnitRegistry) plus every live
    /// runtime spawn it replicated (SpawnLedger: weapons, decoys, sonobuoys,
    /// aircraft). CLIENT: diffs the manifest - ids it can't resolve get a spawn
    /// replay request (rate-limited); local replicas absent from two consecutive
    /// censuses are quietly removed. Any missed event becomes a few-second blip
    /// instead of a permanent ghost.
    /// </summary>
    public static class EntityCensusManager
    {
        private const float CensusIntervalSec = 5f;
        private const int MaxRequestIds = 64;

        // ── Host ──────────────────────────────────────────────────────────────
        private static float _nextCensus;
        private static ushort _seq;
        private static readonly EntityCensusMessage _msg = new();
        private static readonly HashSet<int> _hostIds = new();

        /// <summary>Called from the host streamer loop each frame.</summary>
        public static void HostTick()
        {
            if (Time.unscaledTime < _nextCensus) return;
            _nextCensus = Time.unscaledTime + CensusIntervalSec;

            _msg.Reset();
            _msg.CensusSeq = ++_seq;
            _hostIds.Clear();

            AddUnits(UnitType.Vessel,     UnitRegistry.Vessels);
            AddUnits(UnitType.Submarine,  UnitRegistry.Submarines);
            AddUnits(UnitType.Aircraft,   UnitRegistry.AircraftList);
            AddUnits(UnitType.Helicopter, UnitRegistry.Helicopters);
            AddUnits(UnitType.LandUnit,   UnitRegistry.LandUnits);

            // Runtime spawns the host replicated (weapons/decoys/sonobuoys -
            // ledger entries are removed when their despawn goes out)
            foreach (var kv in CaptureState.SpawnLedger)
            {
                if (_hostIds.Add(kv.Key))
                    _msg.Entries.Add((kv.Key, (byte)255));
            }

            NetworkManager.Instance.BroadcastToClients(_msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Telemetry.Count("v2.censusSent");
        }

        private static void AddUnits<T>(UnitType kind, IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null) continue;
                if (_hostIds.Add(u.UniqueID))
                    _msg.Entries.Add((u.UniqueID, (byte)kind));
            }
        }

        /// <summary>Host: replay requested spawns verbatim from the ledger.</summary>
        public static void HandleDiffRequest(CensusDiffRequestMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            foreach (var id in msg.Ids)
            {
                if (CaptureState.SpawnLedger.TryGetValue(id, out var spawn))
                {
                    NetworkManager.Instance.BroadcastToClients(spawn);
                    Telemetry.Count("v2.spawnReplayed");
                    Plugin.Log.LogInfo($"[Census] Replayed {spawn.Kind} spawn id={id} " +
                        $"ammo={spawn.AmmoName} shooter={spawn.ShooterId}");
                }
            }
        }

        // ── Client ────────────────────────────────────────────────────────────
        private static readonly HashSet<int> _censusIds = new();
        private static readonly Dictionary<int, int> _missCount = new();
        private static readonly Dictionary<int, ushort> _lastRequestSeq = new();
        private static readonly List<int> _localIds = new();
        private static readonly List<int> _toRemove = new();

        public static void HandleCensus(EntityCensusMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            _censusIds.Clear();
            foreach (var e in msg.Entries)
                _censusIds.Add(e.id);

            // Missing: in the census but unresolvable here → ask for a spawn replay
            CensusDiffRequestMessage? req = null;
            foreach (var e in msg.Entries)
            {
                int id = e.id;
                if (SpawnReplicator.IsTombstoned(id)) continue;
                if (ReplicaRegistry.Find(id) != null) continue;
                if (StateSerializer.FindById(id) != null) continue;
                if (_lastRequestSeq.TryGetValue(id, out var lastSeq)
                    && (ushort)(msg.CensusSeq - lastSeq) < 2) continue; // one ask per 2 censuses

                _lastRequestSeq[id] = msg.CensusSeq;
                req ??= new CensusDiffRequestMessage();
                if (req.Ids.Count < MaxRequestIds) req.Ids.Add(id);
                Telemetry.Count("v2.censusMissing");
            }
            if (req != null)
                NetworkManager.Instance.SendToServer(req);

            // Orphans: locally registered, absent from two consecutive censuses
            ReplicaRegistry.CollectIds(_localIds);
            _toRemove.Clear();
            foreach (var id in _localIds)
            {
                if (_censusIds.Contains(id)) { _missCount.Remove(id); continue; }
                int n = (_missCount.TryGetValue(id, out var c) ? c : 0) + 1;
                if (n >= 2) { _toRemove.Add(id); _missCount.Remove(id); }
                else _missCount[id] = n;
            }

            foreach (var id in _toRemove)
            {
                var obj = ReplicaRegistry.Find(id);
                // Purge entities whose whole lifecycle is replication-owned:
                // all weapon/decoy replicas (KinematicWeapon, LiveLocal) and
                // replicated air units. Ships/subs/land stay - their endings
                // always arrive as DestroyEvents.
                var policy = ReplicaRegistry.PolicyOf(id);
                bool purgeable = policy == ReplicaPolicy.KinematicWeapon
                    || policy == ReplicaPolicy.LiveLocal
                    || (policy == ReplicaPolicy.LocalMotionUnit && (obj is Aircraft || obj is Helicopter));
                if (obj != null && purgeable)
                {
                    using (Authority.Allowed())
                        obj.destroyObject(false, false, TacView.TCEvent.Destroyed);
                    Telemetry.Count("v2.censusOrphanRemoved");
                    Plugin.Log.LogWarning($"[Census] Removed orphan replica id={id} ({obj.GetType().Name})");
                }
                WeaponReplicaDriver.Forget(id);
                AircraftReplicaDriver.Forget(id);
                ReplicaRegistry.Unregister(id);
                SpawnReplicator.Tombstone(id);
            }
        }

        public static void Reset()
        {
            _censusIds.Clear();
            _missCount.Clear();
            _lastRequestSeq.Clear();
            _nextCensus = 0f;
        }
    }
}
