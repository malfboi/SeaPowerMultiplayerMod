using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Shared contact EXISTENCE, in both directions. <see cref="ContactSyncManager"/>
    /// only reconciles contacts both machines already hold - it renumbers and
    /// identifies them. A contact only one player has detected does not exist on the
    /// other machine at all, and no amount of metadata makes it appear.
    ///
    /// The gap is filled with the game's own intel-sharing call,
    /// <c>Utils.RevealContactToObject</c> - what a recon report or a comms link uses
    /// to hand one unit's picture to another. It builds a track entity and feeds it
    /// to the plotting table, and takes only ObjectBase/string/int, so it works
    /// without the mod referencing Unity.Entities (which would break Anchor Chain's
    /// load-time type resolution).
    ///
    /// FILL GAPS ONLY. A revealed track carries a zero-error fix - a perfectly
    /// located contact - so revealing indiscriminately would delete target-motion
    /// uncertainty from the game. A contact is only revealed while the local side
    /// has no track of its own for it; the moment local sensors acquire it
    /// (<c>Vehicle.DetectingSensors</c> stops reading Unknown) refreshing stops and
    /// the local, uncertain fix takes over. So the two players see the same SET of
    /// contacts, and each player's own sensors still decide how well they see them.
    ///
    /// CO-OP ONLY, like the rest of the shared picture: in PvP the two pictures are
    /// meant to differ and sharing one would hand over intel the other side has not
    /// earned.
    /// </summary>
    public static class ContactRevealManager
    {
        /// <summary>Object ids the REMOTE machine holds a contact for. The host
        /// learns these from ContactReportMessage; the client already has them as
        /// the keys of the host's override table.</summary>
        private static readonly HashSet<int> _remoteHeld = new(128);

        private static readonly ContactReportMessage _report = new();

        /// <summary>
        /// Refresh cadence. A revealed track ages out on the sensor clock -
        /// VehicleAgeOut.AirDeath is 10 s (ships and subs get 300 s) - so an air
        /// contact has to be re-revealed well inside that or it flickers.
        /// </summary>
        internal const float RefreshInterval = 3f;

        /// <summary>Nothing here is authoritative, so it is safe to drop a sweep;
        /// this is only to stop a huge first report fragmenting.</summary>
        private const int MaxIdsPerPacket = 256;

        /// <summary>The classificationOverride that reveals nothing about identity -
        /// any value the game's switch does not handle. 1, 2 and 3 all attach a
        /// DetectedSide and/or a class; see the call site.</summary>
        private const int PositionOnly = 0;

        public static void Reset()
        {
            _remoteHeld.Clear();
            _report.Reset();
        }

        // ── Inbound ───────────────────────────────────────────────────────────

        /// <summary>HOST: the client's contact list has arrived.</summary>
        public static void ApplyReceived(ContactReportMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!ContactSyncManager.CoopSessionActive) return;

            _remoteHeld.Clear();
            for (int i = 0; i < msg.UniqueIds.Count; i++)
                _remoteHeld.Add(msg.UniqueIds[i]);
        }

        /// <summary>CLIENT: the host's picture is its contact list - no extra
        /// message needed, the override table already carries exactly the ids the
        /// host holds.</summary>
        internal static void SetRemoteHeldFromOverrides(IEnumerable<int> uniqueIds)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            _remoteHeld.Clear();
            foreach (int id in uniqueIds) _remoteHeld.Add(id);
        }

        // ── Outbound (client) ─────────────────────────────────────────────────

        /// <summary>CLIENT: tell the host which foreign contacts we hold, so it can
        /// materialise the ones it is missing and number them.</summary>
        public static void ClientSendReport()
        {
            var table = Globals._playerTaskforce?.PlottingTable;
            if (table == null) return;

            _report.Reset();
            foreach (var kv in table.LocalVehicles)
            {
                var obj = kv.Key;
                if (obj == null || obj.UniqueID == 0) continue;
                if (!obj.isUnit()) continue;                        // weapons churn the table
                if (obj._taskforce == Globals._playerTaskforce) continue; // own units are on both machines already
                if (_report.UniqueIds.Count >= MaxIdsPerPacket) break;

                _report.UniqueIds.Add(obj.UniqueID);
            }

            NetworkManager.Instance.SendToServer(_report, DeliveryMethod.ReliableOrdered);
        }

        // ── The reveal sweep (both sides) ─────────────────────────────────────

        /// <summary>
        /// Materialise contacts the remote side holds and we do not, and drop the
        /// ones our own sensors have since found.
        /// </summary>
        public static void Tick()
        {
            if (!ContactSyncManager.CoopSessionActive) return;
            if (_remoteHeld.Count == 0) return;

            var table = Globals._playerTaskforce?.PlottingTable;
            if (table == null) return;

            ObjectBase? detector = null;

            foreach (int id in _remoteHeld)
            {
                var obj = StateSerializer.FindById(id);
                if (obj == null || obj.IsDestroyed || !obj.isUnit()) continue;
                if (obj._taskforce == Globals._playerTaskforce) continue;

                // Our own sensors are on it - let the local fix stand, errors and
                // all, and stop propping the track up. A contact we are only
                // holding because of an earlier reveal reads Unknown here, so it
                // keeps being refreshed until real sensors take over. Nothing else
                // is needed to decide: the vehicle's own state says whether this
                // machine can see the contact by itself.
                if (table.LocalVehicles.TryGetValue(obj, out var vehicle)
                    && vehicle != null
                    && vehicle.DetectingSensors != SensorSystem.SensorTypeSet.Unknown)
                    continue;

                detector ??= FindDetector();
                if (detector == null) return; // no live unit of ours to attribute it to

                try
                {
                    // classificationOverride MUST stay outside {1,2,3}. Those are
                    // the identifying cases and they hand over what the local side
                    // has not earned: 2 attaches DetectedSide, 3 attaches
                    // DetectedSide plus a ForceID class, and 1 - despite reading
                    // like the mildest option - routes to
                    // CheckClassificationOrIdentification with wasIdentified
                    // hardcoded true, which attaches BOTH. Using 1 made every
                    // contact the host merely HELD arrive on the client fully
                    // identified, side and type, while the host still had it as
                    // unknown.
                    //
                    // Falling off the switch attaches neither, which is the whole
                    // point here: share that the contact EXISTS and where it is,
                    // and leave identity to ContactSync, which only ever applies
                    // what the host has actually worked out.
                    Utils.RevealContactToObject(detector, obj, "None", PositionOnly);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Contacts] Reveal of {obj.name} (id={id}) failed: {ex.Message}");
                }
            }
        }

        /// <summary>Any live unit of ours can carry the report. RevealContactToObject
        /// derives bearing and range from it, but writes the contact's true position
        /// either way, so the choice does not change where the contact appears.</summary>
        private static ObjectBase? FindDetector()
        {
            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var u = all[i];
                if (u == null || u.IsDestroyed) continue;
                if (!u.isUnit()) continue;
                if (u._taskforce != Globals._playerTaskforce) continue;
                if (u.PlottingTable == null) continue;
                return u;
            }
            return null;
        }
    }
}
