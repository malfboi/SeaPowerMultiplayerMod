using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Keeps Standing Orders (the game's <c>TacticalDoctrine</c>, Shift+O) in step
    /// across a team.
    ///
    /// The doctrine is a hierarchy - taskforce root → formation → unit, each lower
    /// level either inheriting or overriding - and the SIM reads the resolved value
    /// per unit (AI.GetPossibleTargetsList for auto-attack-surface, CheckWinchester
    /// for RTB, AI salvo sizing, alignment, engage-task timeout...). Only the host
    /// runs that sim, so a guest's edit does nothing unless it reaches the host's
    /// copy. And the root is seeded from each machine's OWN usersettings.ini - in a
    /// PvP seat the host's copy of the opposing root even starts from the AI
    /// defaults (auto-attack-surface ON, winchester ON) - so the root also has to be
    /// agreed once at session start.
    ///
    /// STATE BY POLLING, like DrawingSyncManager: every <see cref="Interval"/> the
    /// local team's doctrine is read and diffed against the last value sent OR
    /// received for each setting, and only the differences travel. That catches every
    /// edit path (panel, hotkey, reset-to-defaults, the per-unit context menus)
    /// without patching the generated MVVM setters, and applying a received value
    /// updates the same table, so nothing echoes.
    ///
    /// Units and formations seen for the first time are baselined silently: both
    /// machines load them from the same save (formation and unit overrides are saved
    /// with the mission), and a new leader id after a swap is the same formation.
    ///
    /// ROOT AGREEMENT. The host's team is established from the start (the host's
    /// root IS the sim's). A guest's first poll sends an empty InitialRoot request.
    /// If that team's root is already established the host answers with it and the
    /// guest adopts it; otherwise (the first Red guest in a PvP seat - they own that
    /// fleet, and the host's copy is still on AI defaults) the host marks it
    /// established and echoes the request back, and the guest replies with its whole
    /// root as ordinary updates.
    /// </summary>
    internal static class DoctrineSyncManager
    {
        private const float Interval = 1f;
        private const string ObjectPropertyPrefix = "@";

        private static float _nextPoll;
        private static bool _primed;
        private static bool _warned;

        // "scope|id|key" → (isSet, value) for everything of OUR team last sent or
        // received. An empty key marks "this owner has been baselined".
        private static readonly Dictionary<string, (bool isSet, int value)> _known = new();
        // Host only: teams whose root has been agreed this session.
        private static readonly HashSet<byte> _establishedRoots = new();

        private static readonly FieldInfo? _settingsField =
            AccessTools.Field(typeof(TacticalDoctrine), "_settings");
        private static readonly Dictionary<Type, PropertyInfo?> _valueProps = new();
        private static readonly Dictionary<Type, PropertyInfo?> _isSetProps = new();

        // Per-unit doctrine flags that live on ObjectBase rather than in the
        // TacticalDoctrine hierarchy (set from the unit context menus).
        private static List<PropertyInfo>? _objectDoctrineProps;

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + Interval;

            if (!Live())
            {
                // A new mission re-baselines from the newly loaded save.
                if (SessionManager.SceneLoading) _primed = false;
                return;
            }

            try { Poll(); }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.Log.LogWarning($"[Doctrine] Poll failed, standing orders will not sync: {ex}");
            }
        }

        private static bool Live()
            => NetworkManager.Instance.IsEstablished
               && SimSyncManager.CurrentState == SimState.Synchronized
               && !SessionManager.SceneLoading
               && Globals._playerTaskforce != null
               && _settingsField != null;

        internal static void Reset()
        {
            _primed = false;
            _known.Clear();
            _establishedRoots.Clear();
        }

        // ── Poll / send ───────────────────────────────────────────────────────

        private static void Poll()
        {
            var tf = Globals._playerTaskforce;
            byte team = (byte)PlayerRegistry.LocalTeam;

            if (!_primed)
            {
                _primed = true;
                _known.Clear();
                var all = new List<DoctrineSyncMessage.Entry>();
                CollectDoctrine(tf.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Root, 0, all, null);
                CollectNew(tf, all);
                Remember(all);

                if (!Plugin.Instance.CfgIsHost.Value)
                    Send(team, new List<DoctrineSyncMessage.Entry>(), initialRoot: true);  // see class summary

                Plugin.Log.LogInfo($"[Doctrine] Baselined {all.Count} standing-order value(s)");
                return;
            }

            var changed = new List<DoctrineSyncMessage.Entry>();
            CollectDoctrine(tf.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Root, 0, changed, _known);
            for (int i = 0; i < tf.Formations.Count; i++)
            {
                var f = tf.Formations[i];
                var leader = f?.LeaderStation?.UnitObject;
                if (f?.TacticalDoctrine == null || leader == null) continue;
                CollectDoctrine(f.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Formation, leader.UniqueID, changed, _known);
            }
            var units = tf._taskforceUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.IsDestroyed) continue;
                CollectDoctrine(u.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Unit, u.UniqueID, changed, _known);
                CollectObjectDoctrine(u, changed, _known);
            }

            // First sightings (a launched aircraft, a new leader id) join the table
            // silently; both machines got them from the same save or spawn.
            var fresh = new List<DoctrineSyncMessage.Entry>();
            CollectNew(tf, fresh);
            Remember(fresh);

            if (changed.Count == 0) return;
            Remember(changed);
            Send(team, changed, initialRoot: false);
            Plugin.Log.LogInfo($"[Doctrine] Sent {changed.Count} standing-order change(s)");
        }

        /// <summary>Every value, plus a marker, of each formation and unit not yet in
        /// the table.</summary>
        private static void CollectNew(Taskforce tf, List<DoctrineSyncMessage.Entry> into)
        {
            for (int i = 0; i < tf.Formations.Count; i++)
            {
                var f = tf.Formations[i];
                var leader = f?.LeaderStation?.UnitObject;
                if (f?.TacticalDoctrine == null || leader == null) continue;
                if (_known.ContainsKey(Key(DoctrineSyncMessage.ScopeKind.Formation, leader.UniqueID, ""))) continue;
                CollectDoctrine(f.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Formation, leader.UniqueID, into, null);
                into.Add(Marker(DoctrineSyncMessage.ScopeKind.Formation, leader.UniqueID));
            }

            var units = tf._taskforceUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.IsDestroyed) continue;
                if (_known.ContainsKey(Key(DoctrineSyncMessage.ScopeKind.Unit, u.UniqueID, ""))) continue;
                CollectDoctrine(u.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Unit, u.UniqueID, into, null);
                CollectObjectDoctrine(u, into, null);
                into.Add(Marker(DoctrineSyncMessage.ScopeKind.Unit, u.UniqueID));
            }
        }

        private static DoctrineSyncMessage.Entry Marker(DoctrineSyncMessage.ScopeKind scope, int id)
            => new DoctrineSyncMessage.Entry { Scope = scope, Id = id, Key = "" };

        /// <summary>The doctrine's values. With <paramref name="known"/> given, only
        /// those that differ from it, and only for an owner already baselined.</summary>
        private static void CollectDoctrine(TacticalDoctrine? doctrine, DoctrineSyncMessage.ScopeKind scope,
                                            int id, List<DoctrineSyncMessage.Entry> into,
                                            Dictionary<string, (bool isSet, int value)>? known)
        {
            if (doctrine == null) return;
            if (known != null && scope != DoctrineSyncMessage.ScopeKind.Root
                && !known.ContainsKey(Key(scope, id, ""))) return;

            if (!(_settingsField!.GetValue(doctrine) is Dictionary<string, ITacticalDoctrineItem> settings)) return;

            foreach (var kv in settings)
            {
                var item = kv.Value;
                if (item == null || !TryRead(item, out int value)) continue;

                // Root settings are never "overrides"; below the root an inactive
                // override's Value merely mirrors the parent and is not worth sending.
                bool isSet = scope == DoctrineSyncMessage.ScopeKind.Root || item.IsSet;
                if (!isSet) value = 0;

                var e = new DoctrineSyncMessage.Entry { Scope = scope, Id = id, Key = kv.Key, IsSet = isSet, Value = value };
                if (known != null && known.TryGetValue(KeyOf(e), out var prev) && prev == (isSet, value)) continue;
                into.Add(e);
            }
        }

        private static void CollectObjectDoctrine(ObjectBase unit, List<DoctrineSyncMessage.Entry> into,
                                                  Dictionary<string, (bool isSet, int value)>? known)
        {
            if (known != null && !known.ContainsKey(Key(DoctrineSyncMessage.ScopeKind.Unit, unit.UniqueID, ""))) return;

            foreach (var prop in ObjectDoctrineProps())
            {
                var rp = prop.GetValue(unit);
                if (rp == null || !TryRead(rp, out int value)) continue;

                var e = new DoctrineSyncMessage.Entry
                {
                    Scope = DoctrineSyncMessage.ScopeKind.Unit, Id = unit.UniqueID,
                    Key = ObjectPropertyPrefix + prop.Name, IsSet = true, Value = value,
                };
                if (known != null && known.TryGetValue(KeyOf(e), out var prev) && prev == (true, value)) continue;
                into.Add(e);
            }
        }

        /// <summary>ObjectBase's own <c>ReactiveProperty&lt;bool|enum&gt; ...Doctrine</c>
        /// flags (sprint-and-drift, baffle clearing, fighters-ignore-ASMs, bomb attack
        /// profile). Found by shape so a renamed or added one is picked up.</summary>
        private static List<PropertyInfo> ObjectDoctrineProps()
        {
            if (_objectDoctrineProps != null) return _objectDoctrineProps;
            _objectDoctrineProps = new List<PropertyInfo>();
            foreach (var p in typeof(ObjectBase).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!p.Name.EndsWith("Doctrine", StringComparison.Ordinal) || !p.CanRead) continue;
                var t = p.PropertyType;
                if (!t.IsGenericType || t.GetGenericTypeDefinition().Name != "ReactiveProperty`1") continue;
                var arg = t.GetGenericArguments()[0];
                if (arg == typeof(bool) || arg.IsEnum) _objectDoctrineProps.Add(p);
            }
            return _objectDoctrineProps;
        }

        /// <summary>Chunks <paramref name="entries"/> into messages (an InitialRoot
        /// request carries none). Guests always go to the host; the host addresses
        /// its own team, or one slot.</summary>
        private static void Send(byte team, List<DoctrineSyncMessage.Entry> entries, bool initialRoot,
                                 byte toSlot = PlayerRegistry.NoSender)
        {
            int i = 0;
            do
            {
                var msg = new DoctrineSyncMessage { Team = team, InitialRoot = initialRoot };
                for (; i < entries.Count && msg.Entries.Count < DoctrineSyncMessage.MaxEntries; i++)
                    if (!string.IsNullOrEmpty(entries[i].Key))     // baseline markers stay local
                        msg.Entries.Add(entries[i]);
                if (msg.Entries.Count == 0 && !initialRoot) return;

                if (toSlot != PlayerRegistry.NoSender) NetworkManager.Instance.SendToSlot(toSlot, msg);
                else NetworkManager.Instance.SendToMyTeam(msg);
            } while (i < entries.Count);
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        /// <summary>Main thread. <paramref name="fromSlot"/> is the sender as the host
        /// saw it; guests receive everything from the host.</summary>
        internal static void ApplyReceived(DoctrineSyncMessage msg, byte fromSlot)
        {
            if (_settingsField == null) return;
            bool isHost = Plugin.Instance.CfgIsHost.Value;

            if (isHost)
            {
                // A guest may only speak for its own team's taskforce.
                if (!PlayerRegistry.TryGet(fromSlot, out var sender) || (byte)sender.Team != msg.Team)
                {
                    Plugin.Log.LogWarning($"[Doctrine] Dropped standing orders from slot {fromSlot} for another team");
                    return;
                }

                if (msg.InitialRoot)
                {
                    var tfNow = Teams.TaskforceFor((Team)msg.Team);
                    if (tfNow?.TacticalDoctrine == null) return;
                    // The host's own team is settled even before our first poll has run.
                    bool hostTeam = msg.Team == (byte)PlayerRegistry.LocalTeam;
                    if (!hostTeam && _establishedRoots.Add(msg.Team))
                    {
                        // Nobody has set this team's orders yet - the guest's own are the
                        // ones to use. Echo the request: "send me yours".
                        Send(msg.Team, new List<DoctrineSyncMessage.Entry>(), initialRoot: true, toSlot: fromSlot);
                        Plugin.Log.LogInfo($"[Doctrine] Slot {fromSlot} sets the {Teams.Name((Team)msg.Team)} standing orders");
                    }
                    else
                    {
                        // Already agreed - the guest adopts them.
                        var root = new List<DoctrineSyncMessage.Entry>();
                        CollectDoctrine(tfNow.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Root, 0, root, null);
                        Send(msg.Team, root, initialRoot: false, toSlot: fromSlot);
                        Plugin.Log.LogInfo($"[Doctrine] Sent current {Teams.Name((Team)msg.Team)} standing orders to slot {fromSlot}");
                    }
                    return;
                }
            }
            else if (msg.InitialRoot)
            {
                // The host has no orders for our team yet: ours become the team's.
                var own = Globals._playerTaskforce;
                if (own?.TacticalDoctrine == null) return;
                var root = new List<DoctrineSyncMessage.Entry>();
                CollectDoctrine(own.TacticalDoctrine, DoctrineSyncMessage.ScopeKind.Root, 0, root, null);
                Send(msg.Team, root, initialRoot: false);
                Plugin.Log.LogInfo($"[Doctrine] Sent our standing orders to the host ({root.Count} value(s))");
                return;
            }

            var tf = Teams.TaskforceFor((Team)msg.Team);
            if (tf == null) return;

            int applied = 0;
            for (int i = 0; i < msg.Entries.Count; i++)
                if (ApplyEntry(tf, msg.Entries[i])) applied++;

            // The sender's teammates hear about it too.
            if (isHost) NetworkManager.Instance.RelayToTeam(fromSlot, msg);

            if (applied > 0)
                Plugin.Log.LogInfo($"[Doctrine] Applied {applied} standing-order value(s) for {Teams.Name((Team)msg.Team)}");
        }

        private static bool ApplyEntry(Taskforce tf, DoctrineSyncMessage.Entry e)
        {
            if (string.IsNullOrEmpty(e.Key)) return false;
            // Only our own team's table is polled; another team's orders (the host
            // holding a PvP opponent's) are never re-sent, so they are not recorded -
            // their root keys would otherwise collide with our own root's.
            bool local = tf == Globals._playerTaskforce;

            if (e.Scope == DoctrineSyncMessage.ScopeKind.Root)
                return ApplyToDoctrine(tf.TacticalDoctrine, e, local);

            var unit = StateSerializer.FindById(e.Id);
            if (unit == null || unit._taskforce != tf) return false;

            if (e.Scope == DoctrineSyncMessage.ScopeKind.Formation)
                return ApplyToDoctrine(unit.Formation?.TacticalDoctrine, e, local);

            if (e.Key.StartsWith(ObjectPropertyPrefix, StringComparison.Ordinal))
            {
                string name = e.Key.Substring(ObjectPropertyPrefix.Length);
                var prop = ObjectDoctrineProps().Find(p => p.Name == name);
                var rp = prop?.GetValue(unit);
                if (rp == null || !TryWrite(rp, e.Value)) return false;
                if (local) Remember(e);
                return true;
            }
            return ApplyToDoctrine(unit.TacticalDoctrine, e, local);
        }

        private static bool ApplyToDoctrine(TacticalDoctrine? doctrine, DoctrineSyncMessage.Entry e, bool local)
        {
            if (doctrine == null) return false;
            if (!(_settingsField!.GetValue(doctrine) is Dictionary<string, ITacticalDoctrineItem> settings)) return false;
            if (!settings.TryGetValue(e.Key, out var item) || item == null) return false;

            if (e.Scope != DoctrineSyncMessage.ScopeKind.Root && item.IsOverrideable)
            {
                if (!e.IsSet)
                {
                    item.ClearOverride();
                    if (local) Remember(e);
                    return true;
                }
                // Activate first, as LoadOverrideFromFile does, so the inherited-value
                // mirroring in Refresh cannot overwrite the value being applied.
                Prop(_isSetProps, item.GetType(), "IsSet")?.SetValue(item, true);
            }

            if (!TryWrite(item, e.Value)) return false;
            if (local) Remember(e);
            return true;
        }

        private static void Remember(DoctrineSyncMessage.Entry e)
            => _known[KeyOf(e)] = (e.IsSet, e.IsSet ? e.Value : 0);

        private static void Remember(List<DoctrineSyncMessage.Entry> entries)
        {
            for (int i = 0; i < entries.Count; i++) Remember(entries[i]);
        }

        // ── Value access ──────────────────────────────────────────────────────

        private static bool TryRead(object holder, out int value)
        {
            value = 0;
            var prop = Prop(_valueProps, holder.GetType(), "Value");
            if (prop == null) return false;
            switch (prop.GetValue(holder))
            {
                case bool b:  value = b ? 1 : 0;            return true;
                case int n:   value = n;                    return true;
                case Enum en: value = Convert.ToInt32(en);  return true;
                default:      return false;
            }
        }

        private static bool TryWrite(object holder, int value)
        {
            var prop = Prop(_valueProps, holder.GetType(), "Value");
            if (prop == null || !prop.CanWrite) return false;
            var t = prop.PropertyType;
            object boxed;
            if (t == typeof(bool)) boxed = value != 0;
            else if (t == typeof(int)) boxed = value;
            else if (t.IsEnum) boxed = Enum.ToObject(t, value);
            else return false;
            prop.SetValue(holder, boxed);
            return true;
        }

        private static PropertyInfo? Prop(Dictionary<Type, PropertyInfo?> cache, Type type, string name)
        {
            if (cache.TryGetValue(type, out var p)) return p;
            p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            cache[type] = p;
            return p;
        }

        private static string Key(DoctrineSyncMessage.ScopeKind scope, int id, string key)
            => $"{(byte)scope}|{id}|{key}";

        private static string KeyOf(DoctrineSyncMessage.Entry e) => Key(e.Scope, e.Id, e.Key);
    }
}
