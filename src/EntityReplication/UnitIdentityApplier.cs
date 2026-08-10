using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT: gives an aircraft/helicopter replica the callsign and formation
    /// membership its host original has.
    ///
    /// Neither survives replication on its own. The client builds replicas by
    /// calling <c>ObjectsManager.createAircraft</c> directly, never through
    /// <c>FlightDeck.getObjectToLaunch</c> / <c>launchVehicle</c> - and those two
    /// are where the game assigns both:
    ///
    ///   getObjectToLaunch -> createAircraft (our spawn capture fires HERE)
    ///                     -> _obp._objectName = "&lt;Callsign&gt; &lt;n&gt;"
    ///   launchVehicle     -> new UnitFormation(leader) / formation.AddUnit(...)
    ///
    /// So a client-side group launch produced N unrelated aircraft carrying their
    /// ini default names instead of one Vic of "Diamond-1..4". Both values are
    /// settled by the time the host hands the aircraft control, which is where the
    /// wheels-up spawn re-send is captured, so they ride in on that message.
    ///
    /// Formation membership is rebuilt with the game's own API, so
    /// <c>CalculateAirUnitNames</c> derives the "-1/-2/-3" suffixes locally exactly
    /// as it does host-side. The wingman station-keeper it switches on is already
    /// handled - <see cref="Patch_FormationFlightPhysics_OnFixedUpdate"/> skips it
    /// while the host stream is driving the replica.
    /// </summary>
    internal static class UnitIdentityApplier
    {
        /// <summary>Wingmen whose leader had not arrived yet, retried once per frame.
        /// EntitySpawn is ReliableOrdered and the leader launches first, so this is a
        /// safety net for a wingman recovered by census before its leader.</summary>
        private static readonly Dictionary<int, int> _pendingFormation = new();
        private static readonly List<int> _resolved = new();

        internal static void Reset() => _pendingFormation.Clear();

        internal static void Apply(ObjectBase unit, EntitySpawnMessage msg)
        {
            if (unit == null) return;
            ApplyName(unit, msg.UnitName);

            if (msg.FormationLeaderId == 0) return;

            // Joining a formation re-stations the unit (relative waypoints, control
            // mode, motion-controller swap). Those go through the same game calls the
            // order patches intercept, so without this the client would forward its
            // own replica bookkeeping upstream as player orders.
            bool prev = OrderHandler.ApplyingFromNetwork;
            OrderHandler.ApplyingFromNetwork = true;
            try
            {
                if (!JoinFormation(unit, msg.FormationLeaderId))
                    _pendingFormation[msg.EntityId] = msg.FormationLeaderId;
            }
            finally { OrderHandler.ApplyingFromNetwork = prev; }
        }

        /// <summary>The callsign block only runs host-side for the PLAYER taskforce -
        /// an opposing-side launch keeps its class name. Mirror that rather than
        /// pushing the host's name onto units the local player is not supposed to
        /// have identified.</summary>
        private static void ApplyName(ObjectBase unit, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (unit._obp == null) return;
            if (unit._taskforce != Globals._playerTaskforce) return;
            if (unit._obp._objectName == name) return;

            unit._obp._objectName = name;
            unit._obp._shortName  = name;
            unit.Name.Value       = name;
        }

        /// <summary>Returns false when the leader replica is not present yet.</summary>
        private static bool JoinFormation(ObjectBase unit, int leaderId)
        {
            if (unit.Formation != null) return true;   // already placed

            if (leaderId == unit.UniqueID)
            {
                CreateFormationForLeader(unit);
                return true;
            }

            var leader = StateSerializer.FindById(leaderId);
            if (leader == null) return false;

            // The leader's own spawn carries FormationLeaderId == its own id, so its
            // formation normally exists by now; build it on demand if the two
            // messages arrived out of order.
            if (leader.Formation == null)
            {
                if (!leader.IsAirUnit) return false;
                CreateFormationForLeader(leader);
                if (leader.Formation == null) return false;
            }

            // Returning false parks this join in _pendingFormation, which DrainPending
            // retries once a frame - so a formation that is momentarily unfit (a corpse
            // just unseated from the leader's station) costs a frame, not the join.
            if (!SpawnReplicator.PrepareForJoin(leader.Formation)) return false;

            leader.Formation.AddUnit(unit);
            return true;
        }

        /// <summary>Same construction FlightDeck.launchVehicle uses for a multiple
        /// launch: an aircraft Vic named off the leader's callsign.</summary>
        private static void CreateFormationForLeader(ObjectBase leader)
        {
            if (leader._taskforce == null) return;

            string name = Singleton<LanguageResourceHandler>.Instance
                .getText("Formation", "FormationName")
                .Replace("${Callsign}", leader.DisplayNameShort);

            var p = new UnitFormationParameters(leader._type == ObjectBase.ObjectType.Aircraft)
            {
                _name         = name,
                _leaderObject = leader,
                _pattern      = UnitFormation.FormationPattern.Vic,
            };
            // The constructor registers itself with the taskforce and assigns
            // leader.Formation through AddUnit(isLeader: true).
            _ = new UnitFormation(p);
        }

        /// <summary>Drained once per frame from <see cref="ReplicaTick"/>.</summary>
        internal static void DrainPending()
        {
            if (_pendingFormation.Count == 0) return;

            bool prev = OrderHandler.ApplyingFromNetwork;
            OrderHandler.ApplyingFromNetwork = true;
            try
            {
                foreach (var kv in _pendingFormation)
                {
                    var unit = ReplicaRegistry.Find(kv.Key);
                    if (unit == null || unit.IsDestroyed) { _resolved.Add(kv.Key); continue; }
                    if (JoinFormation(unit, kv.Value)) _resolved.Add(kv.Key);
                }
            }
            finally { OrderHandler.ApplyingFromNetwork = prev; }

            for (int i = 0; i < _resolved.Count; i++) _pendingFormation.Remove(_resolved[i]);
            _resolved.Clear();
        }
    }
}
