using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Shared tactical picture. Sensors are simulated locally on both machines -
    /// the host is NOT authoritative for detection - so each side allocates its
    /// own track numbers (in detection order) and narrows classification at its
    /// own pace. The result players see is two different pictures of one task
    /// force: mismatched track numbers, and contacts identified on one screen and
    /// unknown on the other.
    ///
    /// The host captures its picture here and the client overlays it onto its own
    /// (<see cref="Patch_Vehicle_UpdateFromECS_ContactSync"/>). The overlay is
    /// ADDITIVE, never subtractive: the host's track number always wins (that is
    /// the whole point - one shared number), but side and class are only applied
    /// when the host actually knows them. A contact the client has identified and
    /// the host has not therefore stays identified rather than visibly regressing
    /// to unknown, and the client ends up seeing the union of both pictures.
    ///
    /// Limitation: a contact the host has detected and the client has not still
    /// shows nothing on the client. Contacts only exist as ECS track entities
    /// created by the sensor pipeline, and the mod does not reference
    /// Unity.Entities - there is no way to synthesize one. Both machines run the
    /// same sensors over the same units, so this is a timing gap, not a
    /// permanent hole; it closes as soon as the client's own sensors see it.
    /// </summary>
    public static class ContactSyncManager
    {
        /// <summary>What the host knows about one contact, resolved for direct
        /// assignment. BoxedClass is a pre-built <c>SourcedProperty&lt;string&gt;</c>
        /// so the per-frame apply is a field write, not an allocation.</summary>
        internal readonly struct Override
        {
            public readonly int     TrackId;
            public readonly bool    Classified; // host knows whose it is
            public readonly object? BoxedClass; // null = host has not identified it
            public readonly byte    Compliance; // host's AI.Compliance roll, 0 = none yet

            public Override(int trackId, bool classified, object? boxedClass, byte compliance)
            {
                TrackId    = trackId;
                Classified = classified;
                BoxedClass = boxedClass;
                Compliance = compliance;
            }
        }

        // ── Client state ──────────────────────────────────────────────────────

        private static readonly Dictionary<int, Override> _overrides = new(128);

        internal static bool HasOverrides => _overrides.Count > 0;

        internal static bool TryGet(int uniqueId, out Override ov) => _overrides.TryGetValue(uniqueId, out ov);

        // ── Client track-number collision guard ───────────────────────────────

        /// <summary>Where the client parks track numbers for foreign contacts the
        /// host has not numbered yet. Comfortably above the game's own foreign
        /// range (PlottingTable._maxForeignTrackId starts at 7001), so a number the
        /// client invents can never equal one the host issues.</summary>
        private const int PrivateTrackBase = 20001;

        private static readonly Dictionary<int, int> _privateTrackIds = new(32);
        private static int _nextPrivateTrackId = PrivateTrackBase;

        /// <summary>
        /// CLIENT: keep a contact the host has not reported out of the host's
        /// numbering range.
        ///
        /// Both plotting tables allocate in detection order from the same bases, so
        /// the client's own counter hands out numbers the host is also handing out -
        /// to different contacts. The overlay then stamps the host's number onto the
        /// contacts it covers and the two collide, which is how one number ends up
        /// on two tracks. Only foreign contacts are moved: own units enter both
        /// tables from the same save in the same order, so their numbers already
        /// agree, and renumbering them would churn the player's own unit list.
        ///
        /// The private number is stable per contact and transient in practice - it
        /// lasts until the host reports the contact, at which point the override
        /// takes over.
        /// </summary>
        internal static void EnsurePrivateTrackId(Vehicle vehicle, int uniqueId)
        {
            if (vehicle.Id >= PrivateTrackBase) return; // already parked

            if (!_privateTrackIds.TryGetValue(uniqueId, out int id))
            {
                id = _nextPrivateTrackId++;
                _privateTrackIds[uniqueId] = id;
            }
            vehicle.Id = id;
        }

        // Reflection for Vehicle.Class: its type is SourcedProperty<string>, whose
        // Source field is a Track containing a Unity.Entities.Entity. Naming the
        // type in source would drag a Unity.Entities reference into the assembly,
        // and every reference has to resolve before Anchor Chain will load the mod
        // at all - not worth it for one field write.
        private static FieldInfo? _classField;
        private static FieldInfo? _sourcedValueField;
        private static Type?      _sourcedPropertyType;
        private static bool       _classReflectionResolved;

        private static bool ResolveClassReflection()
        {
            if (_classReflectionResolved) return _classField != null;
            _classReflectionResolved = true;

            try
            {
                _classField = typeof(Vehicle).GetField("Class", BindingFlags.Instance | BindingFlags.Public);
                if (_classField == null) return false;

                _sourcedPropertyType = Nullable.GetUnderlyingType(_classField.FieldType) ?? _classField.FieldType;
                _sourcedValueField   = _sourcedPropertyType.GetField("Value", BindingFlags.Instance | BindingFlags.Public);
                if (_sourcedValueField == null) { _classField = null; return false; }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Contacts] Vehicle.Class not reflectable ({ex.GetType().Name}) - " +
                    "contact identification will not be shared, track numbers still will");
                _classField = null;
                return false;
            }
            return true;
        }

        /// <summary>Builds the boxed SourcedProperty&lt;string&gt; the applier assigns.</summary>
        private static object? BoxClass(string className)
        {
            if (string.IsNullOrEmpty(className) || !ResolveClassReflection()) return null;
            object boxed = Activator.CreateInstance(_sourcedPropertyType!);
            _sourcedValueField!.SetValue(boxed, className);
            return boxed;
        }

        /// <summary>Writes a pre-boxed class onto a Vehicle. Returns false if
        /// reflection is unavailable, so the caller leaves Identified alone.</summary>
        internal static bool ApplyClass(Vehicle vehicle, object boxedClass)
        {
            if (!ResolveClassReflection()) return false;
            _classField!.SetValue(vehicle, boxedClass);
            return true;
        }

        // Vehicle.Side is SourcedProperty<Taskforce>? - same Unity.Entities problem
        // as Class, same solution.
        private static FieldInfo? _sideField;
        private static FieldInfo? _sourcedSideValueField;
        private static Type?      _sourcedSideType;
        private static bool       _sideReflectionResolved;

        /// <summary>One boxed SourcedProperty per taskforce. There are two, and
        /// SetValue copies the struct in, so the boxes are safe to share.</summary>
        private static readonly Dictionary<Taskforce, object> _boxedSides = new(2);

        private static bool ResolveSideReflection()
        {
            if (_sideReflectionResolved) return _sideField != null;
            _sideReflectionResolved = true;

            try
            {
                _sideField = typeof(Vehicle).GetField("Side", BindingFlags.Instance | BindingFlags.Public);
                if (_sideField == null) return false;

                _sourcedSideType       = Nullable.GetUnderlyingType(_sideField.FieldType) ?? _sideField.FieldType;
                _sourcedSideValueField = _sourcedSideType.GetField("Value", BindingFlags.Instance | BindingFlags.Public);
                if (_sourcedSideValueField == null) { _sideField = null; return false; }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Contacts] Vehicle.Side not reflectable ({ex.GetType().Name}) - " +
                    "the camera will not attach to host-identified contacts on the client");
                _sideField = null;
                return false;
            }
            return true;
        }

        /// <summary>Client: give a contact a Side when the host has classified it
        /// and the client's own sensors have not. Additive like the rest of the
        /// overlay - a Side the client resolved itself is never overwritten.</summary>
        internal static bool ApplySideIfMissing(Vehicle vehicle, Taskforce side)
        {
            if (side == null || !ResolveSideReflection()) return false;
            if (_sideField!.GetValue(vehicle) != null) return false; // client already knows

            if (!_boxedSides.TryGetValue(side, out var boxed))
            {
                boxed = Activator.CreateInstance(_sourcedSideType!);
                _sourcedSideValueField!.SetValue(boxed, side);
                _boxedSides[side] = boxed;
            }
            _sideField.SetValue(vehicle, boxed);
            return true;
        }

        /// <summary>Host side of the same problem: reading Vehicle.Class in source
        /// would name SourcedProperty&lt;string&gt; and pull the reference in just
        /// the same way. Returns "" when the contact is not identified.</summary>
        private static string ReadClassName(Vehicle vehicle)
        {
            if (!ResolveClassReflection()) return "";
            object? boxed = _classField!.GetValue(vehicle);   // null when the Nullable is empty
            if (boxed == null) return "";
            return _sourcedValueField!.GetValue(boxed) as string ?? "";
        }

        /// <summary>Client: fold a host packet into the override table.</summary>
        public static void ApplyReceived(ContactSyncMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (Plugin.Instance.CfgPvP.Value) return; // co-op only - see CoopSessionActive

            if (msg.IsFull) _overrides.Clear();

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                _overrides[e.UniqueId] = new Override(e.TrackId, e.Classified, BoxClass(e.ClassName), e.Compliance);
            }

            // The override keys ARE the host's contact list, so the reveal sweep
            // needs no message of its own in this direction.
            ContactRevealManager.SetRemoteHeldFromOverrides(_overrides.Keys);
        }

        public static void Reset()
        {
            _overrides.Clear();
            _lastSent.Clear();
            _boxedSides.Clear(); // taskforce objects do not survive a scene change
            _privateTrackIds.Clear();
            _nextPrivateTrackId = PrivateTrackBase;
            _nextFullSweep = 0f;
        }

        // ── Host capture ──────────────────────────────────────────────────────

        /// <summary>CLIENT: the host's compliance roll for a contact, or Unknown when
        /// the host has not reported one. Read by the AI.CurrentCompliance patch so a
        /// merchant gives both players the same answer.</summary>
        internal static AI.Compliance ComplianceFor(int uniqueId) =>
            _overrides.TryGetValue(uniqueId, out var ov)
                ? (AI.Compliance)ov.Compliance
                : AI.Compliance.Unknown;

        private static readonly Dictionary<int, (int trackId, bool classified, string cls, byte compliance)> _lastSent = new(128);
        private static readonly ContactSyncMessage _msg = new();
        private static readonly HashSet<int> _seen = new(128);
        private static float _nextFullSweep;

        /// <summary>Entries per packet. These go out ReliableOrdered, so LiteNetLib
        /// fragments anything oversized rather than throwing the way it does for
        /// unreliable payloads - this is just to keep a big table's first sweep from
        /// becoming one large fragmented send.</summary>
        private const int MaxEntriesPerPacket = 24;

        /// <summary>CO-OP ONLY. Both players command one task force and are meant to
        /// share its picture, so making it consistent costs nothing.
        ///
        /// In PvP the two players are opponents with deliberately separate pictures.
        /// Sharing them would hand over identifications the other side has not
        /// earned - the client would learn a contact is a Kirov the moment the host
        /// worked it out - which is the same intel leak
        /// SessionManager.ClearDetectionData exists to prevent at join. Nothing here
        /// runs in PvP; every caller checks first, and this is the backstop.</summary>
        internal static bool CoopSessionActive
            => !Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsEstablished;

        /// <summary>Host: release the client's picture back to its own sensors.
        /// An empty full sweep clears the override table; without this, switching
        /// the setting off mid-session would leave the client pinned to whatever
        /// the host last sent, forever.</summary>
        public static void HostBroadcastClear()
        {
            if (!NetworkManager.Instance.IsEstablished) return;
            _lastSent.Clear();
            _nextFullSweep = 0f;
            _msg.Reset();
            _msg.IsFull = true;
            NetworkManager.Instance.BroadcastToClients(_msg, DeliveryMethod.ReliableOrdered);
            _msg.Reset();
            Plugin.Log.LogInfo("[Contacts] Sync disabled - released the client's contact picture");
        }

        /// <summary>Host: sweep the plotting table and send what changed.</summary>
        public static void HostBroadcast()
        {
            if (!CoopSessionActive) return;

            var table = Globals._playerTaskforce?.PlottingTable;
            if (table == null) return;

            bool full = Time.unscaledTime >= _nextFullSweep;
            if (full) _nextFullSweep = Time.unscaledTime + FullSweepInterval;

            _msg.Reset();
            // Only the first packet of a full sweep clears the client's table; the
            // continuation packets carry the rest of the same sweep and must not.
            _msg.IsFull = full;
            _sweepPacketsSent = 0;
            _seen.Clear();

            foreach (var kv in table.LocalVehicles)
            {
                var obj = kv.Key;
                var vehicle = kv.Value;
                if (obj == null || vehicle == null) continue;
                if (!obj.isUnit()) continue;          // weapons churn the table; their track numbers are throwaway
                if (obj.UniqueID == 0) continue;

                int id = obj.UniqueID;
                _seen.Add(id);

                bool classified = vehicle.UnitTaskforce.Value != null;
                string cls = ReadClassName(vehicle);

                // Only neutrals answer an identification request; for anyone else the
                // getter hard-codes Ignore. Reading it here is what forces the host's
                // roll, and it must be the ONLY roll in the session - so restrict it
                // to the units the request can actually be made of.
                byte compliance = (obj._ai != null && obj._taskforce == Globals._neutralTaskforce)
                    ? (byte)obj._ai.CurrentCompliance
                    : (byte)AI.Compliance.Unknown;

                var current = (vehicle.Id, classified, cls, compliance);
                if (!full && _lastSent.TryGetValue(id, out var previous) && previous == current)
                    continue;

                _lastSent[id] = current;
                _msg.Entries.Add(new ContactSyncMessage.Entry
                {
                    UniqueId   = id,
                    TrackId    = vehicle.Id,
                    Classified = classified,
                    ClassName  = cls,
                    Compliance = compliance,
                });

                if (_msg.Entries.Count >= MaxEntriesPerPacket)
                    Flush();
            }

            if (full) PruneLastSent();
            // An empty full sweep still has to go out - that is what tells the
            // client every contact is gone. A later empty packet of the same
            // sweep does not: it would clear what the earlier ones just set.
            if (_msg.Entries.Count > 0 || (full && _sweepPacketsSent == 0))
                Flush();
        }

        /// <summary>Drop change-tracking for contacts the host no longer holds, so a
        /// re-detected object is re-sent rather than suppressed as "unchanged".</summary>
        private static void PruneLastSent()
        {
            if (_lastSent.Count == _seen.Count) return;
            var stale = new List<int>();
            foreach (var id in _lastSent.Keys)
                if (!_seen.Contains(id)) stale.Add(id);
            for (int i = 0; i < stale.Count; i++) _lastSent.Remove(stale[i]);
        }

        private static void Flush()
        {
            NetworkManager.Instance.BroadcastToClients(_msg, DeliveryMethod.ReliableOrdered);
            _sweepPacketsSent++;
            _msg.Reset(); // clears IsFull - continuation packets never re-clear
        }

        private static int _sweepPacketsSent;

        internal const float FullSweepInterval = 10f;
    }
}
