using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// The sides a player can be seated on, and what the mission calls them.
    ///
    /// A <see cref="Team"/> is an index into this table. Index 0 is always the HOST's
    /// side (the mission's PlayerTaskforce - the host runs the authoritative sim on
    /// the save exactly as the mission wrote it), index 1 is the mission's
    /// EnemyTaskforce, and every other non-civilian side follows in mission order.
    /// Civilian sides are nobody's to command and are left out.
    ///
    /// Entries are matched to taskforces by the side's STABLE name
    /// (<c>Taskforce._nameInMissionFile</c>), never by role or position: a guest's save
    /// has PlayerTaskforce rewritten to its own side, which changes every role on its
    /// machine, and a legacy mission's taskforce order follows PlayerTaskforce - but the
    /// names are the same everywhere.
    ///
    /// Host-authoritative. The host builds it from the live mission and replicates it
    /// with <see cref="SideTableMessage"/> alongside every roster broadcast; before any
    /// mission is loaded it is empty and the two default sides go by Blue and Red.
    /// </summary>
    public static class SideTable
    {
        public sealed class Entry
        {
            public string Name = "";         // stable mission name, the cross-machine key
            public string DisplayName = "";  // what the mission's localisation calls it
        }

        /// <summary>Bounded so the table always fits one sub-MTU reliable packet - see
        /// FlightDeckStreamer.ChunkBudgetBytes for why that matters.</summary>
        public const int MaxSides = 16;
        private const int MaxDisplayNameChars = 32;

        private static readonly List<Entry> _entries = new();

        /// <summary>Bumped whenever the table changes, so the overlay can rebuild the
        /// rows that carry a button per side.</summary>
        public static int Version
        {
            get { HostEnsureFresh(); return _version; }
        }
        private static int _version;

        /// <summary>The player/enemy taskforces the host's table was built for, and how
        /// many taskforces existed then. A change in any means a different (or
        /// still-loading) mission, and the table is rebuilt on next use.</summary>
        private static Taskforce? _builtForPlayer;
        private static Taskforce? _builtForEnemy;
        private static int _builtForCount = -1;

        /// <summary>Number of seatable sides. Never below two, so Blue and Red are always
        /// on offer even before a mission is loaded.</summary>
        public static int Count
        {
            get
            {
                HostEnsureFresh();
                return _entries.Count < 2 ? 2 : _entries.Count;
            }
        }

        public static IEnumerable<Team> All
        {
            get
            {
                int n = Count;
                for (int i = 0; i < n; i++) yield return (Team)i;
            }
        }

        public static bool IsValid(Team team) => (int)team < Count;

        public static string DisplayName(Team team)
        {
            HostEnsureFresh();
            int i = (int)team;
            if (i < _entries.Count)
            {
                var e = _entries[i];
                return string.IsNullOrEmpty(e.DisplayName) ? e.Name : e.DisplayName;
            }
            return i == 0 ? "Blue" : i == 1 ? "Red" : $"Side {i + 1}";
        }

        /// <summary>The side's stable mission name, or null when no mission has
        /// published one for this index.</summary>
        public static string? StableName(Team team)
        {
            HostEnsureFresh();
            int i = (int)team;
            return i < _entries.Count ? _entries[i].Name : null;
        }

        /// <summary>Which seat a taskforce is, or null for a civilian or unknown side.
        /// Null too when no table exists yet - see Teams.TeamOf for the fallback.</summary>
        public static Team? IndexOf(Taskforce? tf)
        {
            if (tf == null) return null;
            HostEnsureFresh();
            string name = tf._nameInMissionFile;
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Name == name) return (Team)i;
            return null;
        }

        public static bool HasEntries
        {
            get { HostEnsureFresh(); return _entries.Count > 0; }
        }

        /// <summary>This machine's live taskforce for a side, by stable name. Searched
        /// newest-first so a taskforce left over from a previous mission in the same
        /// lobby can never win over the current one.</summary>
        public static Taskforce? FindTaskforce(Team team)
        {
            string? name = StableName(team);
            if (name == null || !Singleton<TaskforceManager>.InstanceExists(false)) return null;
            var all = Singleton<TaskforceManager>.Instance._taskForces;
            for (int i = all.Count - 1; i >= 0; i--)
                if (all[i] != null && all[i]._nameInMissionFile == name) return all[i];
            return null;
        }

        // ── Host ──────────────────────────────────────────────────────────────

        /// <summary>Host: rebuild from the live mission if it has changed since the last
        /// build. Cheap when nothing moved - two reference/int compares - so every query
        /// above calls it, which is what keeps load-time spawn stamps correct before any
        /// explicit refresh has run.</summary>
        private static void HostEnsureFresh()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            var player = Globals._playerTaskforce;
            int count = Singleton<TaskforceManager>.InstanceExists(false)
                ? Singleton<TaskforceManager>.Instance._taskForces.Count : 0;
            var enemy = Globals._enemyTaskforce;
            if (player == _builtForPlayer && enemy == _builtForEnemy && count == _builtForCount) return;
            _builtForPlayer = player;
            _builtForEnemy = enemy;
            _builtForCount = count;

            var built = new List<Entry>();
            if (player != null && count > 0)
            {
                Add(built, player);
                Add(built, enemy);
                foreach (var tf in Singleton<TaskforceManager>.Instance._taskForces)
                    Add(built, tf);
            }

            if (SameAs(built)) return;
            _entries.Clear();
            _entries.AddRange(built);
            _version++;
            Plugin.Log.LogInfo($"[Sides] {Describe()}");
        }

        private static void Add(List<Entry> list, Taskforce? tf)
        {
            if (tf == null || tf.IsCivilian || list.Count >= MaxSides) return;
            string name = tf._nameInMissionFile ?? "";
            foreach (var e in list) if (e.Name == name) return;
            string display = tf.DisplayName ?? name;
            if (display.Length > MaxDisplayNameChars) display = display.Substring(0, MaxDisplayNameChars);
            list.Add(new Entry { Name = name, DisplayName = display });
        }

        /// <summary>Host: publish the table and pull anyone seated on a side this mission
        /// does not have back onto a side it does. Called with every roster broadcast,
        /// and before every session send so the table a guest reseats its save by is
        /// already in its hands - both ride the same reliable ordered stream.</summary>
        public static void HostPublish()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            HostEnsureFresh();
            PlayerRegistry.HostClampTeams(Count);

            var msg = new SideTableMessage();
            foreach (var e in _entries)
                msg.Entries.Add(new SideTableMessage.Entry { Name = e.Name, DisplayName = e.DisplayName });
            NetworkManager.Instance.BroadcastToClients(msg);
        }

        // ── Guest ─────────────────────────────────────────────────────────────

        public static void ApplyReceived(SideTableMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            var built = new List<Entry>();
            foreach (var e in msg.Entries)
                built.Add(new Entry { Name = e.Name ?? "", DisplayName = e.DisplayName ?? "" });
            if (SameAs(built)) return;
            _entries.Clear();
            _entries.AddRange(built);
            _version++;
            Plugin.Log.LogInfo($"[Sides] From host: {Describe()}");
        }

        public static void Reset()
        {
            _entries.Clear();
            _builtForPlayer = null;
            _builtForEnemy = null;
            _builtForCount = -1;
            _version++;
        }

        private static bool SameAs(List<Entry> other)
        {
            if (other.Count != _entries.Count) return false;
            for (int i = 0; i < other.Count; i++)
                if (other[i].Name != _entries[i].Name || other[i].DisplayName != _entries[i].DisplayName)
                    return false;
            return true;
        }

        private static string Describe()
        {
            if (_entries.Count == 0) return "no mission sides";
            var parts = new List<string>();
            for (int i = 0; i < _entries.Count; i++)
                parts.Add($"{i}={_entries[i].Name} ('{_entries[i].DisplayName}')");
            return string.Join(", ", parts);
        }
    }
}
