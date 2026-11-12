using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// The replacement for the old session-wide <c>CfgPvP</c> flag.
    ///
    /// THE IDEA THAT MAKES N PLAYERS WORK. <c>Globals._playerTaskforce</c> already means
    /// "the taskforce THIS MACHINE's local player commands" - that is precisely what the
    /// guest-side save reseat establishes. So every existing
    /// <c>tf == Globals._playerTaskforce</c> test was already correct for any number of
    /// players, and team membership reduces to two things: whether this machine's save
    /// was reseated, and who to route a message to. Two guests on the host's side both
    /// load unchanged and 2v1 falls out with no extra machinery.
    ///
    /// <c>CfgPvP</c> was three different questions wearing one name, which is why it could
    /// not simply be generalised:
    ///   1. "Is MY save reseated?"         → <see cref="LocalSaveSwapped"/>
    ///   2. "Is THIS taskforce human-run?" → <see cref="IsHumanControlled"/>
    ///   3. "Have I anyone to share with / hide from?" → <see cref="HasTeammates"/> /
    ///      <see cref="ContestedSession"/>
    /// </summary>
    public static class Teams
    {
        public static Team LocalTeam => PlayerRegistry.LocalTeam;

        /// <summary>
        /// Team of a taskforce - the same answer on every machine, because it is looked
        /// up by the side's stable mission name in <see cref="SideTable"/>. Civilian and
        /// unknown taskforces are nobody's team and return null.
        ///
        /// Before any mission has published a table there are only the two default
        /// sides, resolved through this machine's own reseat: my taskforce is my team,
        /// and the other principal side is the other one.
        /// </summary>
        public static Team? TeamOf(Taskforce? tf)
        {
            if (tf == null) return null;
            if (SideTable.HasEntries) return SideTable.IndexOf(tf);
            if (tf == Globals._playerTaskforce) return LocalTeam;
            if (tf == Globals._enemyTaskforce) return LocalTeam == Team.Host ? Team.Enemy : Team.Host;
            return null;
        }

        public static Team? TeamOf(ObjectBase? unit) => TeamOf(unit?._taskforce);

        /// <summary>The local taskforce object for a team.</summary>
        public static Taskforce? TaskforceFor(Team team)
        {
            if (SideTable.HasEntries) return SideTable.FindTaskforce(team);
            if (team == LocalTeam) return Globals._playerTaskforce;
            return team == Team.Host || team == Team.Enemy ? Globals._enemyTaskforce : null;
        }

        /// <summary>
        /// At least one connected, established human commands this taskforce.
        ///
        /// Replaces <c>CfgPvP &amp;&amp; tf == Globals._enemyTaskforce</c> everywhere the
        /// question was really "must the host stop running AI for this fleet, because a
        /// person is flying it". Correct in every configuration: a co-op trio leaves the
        /// other sides as AI; 1v1 and 2v1 both make the seated side human.
        /// </summary>
        public static bool IsHumanControlled(Taskforce? tf)
        {
            var team = TeamOf(tf);
            return team.HasValue && PlayerRegistry.AnyOnTeam(team.Value);
        }

        public static bool IsHumanControlled(ObjectBase? unit) => IsHumanControlled(unit?._taskforce);

        /// <summary>This taskforce is on MY team, so co-op semantics apply to it -
        /// shared picture, shared drawings, ally handling.</summary>
        public static bool IsFriendly(Taskforce? tf)
        {
            var team = TeamOf(tf);
            return team.HasValue && team.Value == LocalTeam;
        }

        public static bool IsFriendly(ObjectBase? unit) => IsFriendly(unit?._taskforce);

        /// <summary>There is at least one OTHER human on my team - i.e. anyone to share a
        /// contact picture or a map drawing with. Replaces the <c>!CfgPvP</c> gates,
        /// which asked this question badly: co-op meant "there is a teammate", and with
        /// per-player teams that is now literally checkable.</summary>
        public static bool HasTeammates
            => PlayerRegistry.TeammateCount(PlayerRegistry.LocalSlot) > 0;

        /// <summary>Humans are seated on at least two different sides. The
        /// intel-separation question and the closest surviving relative of the old PvP
        /// flag.</summary>
        public static bool ContestedSession
        {
            get
            {
                int sidesWithHumans = 0;
                foreach (var team in SideTable.All)
                    if (PlayerRegistry.AnyOnTeam(team) && ++sidesWithHumans > 1) return true;
                return false;
            }
        }

        /// <summary>
        /// This machine loaded a save with PlayerTaskforce rewritten to another side.
        ///
        /// Only a guest seated off the host's side reseats: the host runs the
        /// authoritative sim on the mission's own save, which is exactly why the host is
        /// always <see cref="Team.Host"/>. Note this is NOT the same question as
        /// <see cref="ContestedSession"/> - such a player is reseated whether or not
        /// anyone is sitting on the host's side with them.
        ///
        /// Safe to read while applying a received save: BlockedPreHandshake drops
        /// everything except Hello/Welcome until Established, so Welcome - which sets the
        /// team - always lands before any SessionSync can.
        /// </summary>
        public static bool LocalSaveSwapped
            => !Plugin.Instance.CfgIsHost.Value && PlayerRegistry.LocalTeam != Team.Host;

        public static string Name(Team t) => SideTable.DisplayName(t);

        /// <summary>A team as the lobby and config carry it: a side index, or the legacy
        /// "Blue"/"Red" names for the two default sides.</summary>
        public static Team Parse(string? value)
        {
            if (string.IsNullOrEmpty(value)) return Team.Host;
            if (value!.Equals("Red", System.StringComparison.OrdinalIgnoreCase)) return Team.Enemy;
            return byte.TryParse(value, out var index) ? (Team)index : Team.Host;
        }
    }
}
