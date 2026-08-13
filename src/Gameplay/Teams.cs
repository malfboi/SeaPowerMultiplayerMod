using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// The replacement for the old session-wide <c>CfgPvP</c> flag.
    ///
    /// THE IDEA THAT MAKES N PLAYERS WORK. <c>Globals._playerTaskforce</c> already means
    /// "the taskforce THIS MACHINE's local player commands" - that is precisely what the
    /// guest-side save swap establishes. So every existing
    /// <c>tf == Globals._playerTaskforce</c> test was already correct for any number of
    /// players, and team membership reduces to two things: whether this machine's save
    /// was swapped, and who to route a message to. Two Blue guests both load unswapped
    /// and 2v1 falls out with no extra machinery.
    ///
    /// <c>CfgPvP</c> was three different questions wearing one name, which is why it could
    /// not simply be generalised:
    ///   1. "Is MY save swapped?"          → <see cref="LocalSaveSwapped"/>
    ///   2. "Is THIS taskforce human-run?" → <see cref="IsHumanControlled"/>
    ///   3. "Have I anyone to share with / hide from?" → <see cref="HasTeammates"/> /
    ///      <see cref="ContestedSession"/>
    /// </summary>
    public static class Teams
    {
        public static Team LocalTeam => PlayerRegistry.LocalTeam;

        /// <summary>
        /// Team of a taskforce, IN THIS MACHINE'S FRAME OF REFERENCE.
        ///
        /// Because the save swap makes my own side read as <c>_playerTaskforce</c> on my
        /// machine regardless of which team I am on, "my taskforce" maps to my team and
        /// the other principal side maps to the other team. Neutral/ally taskforces are
        /// nobody's team and return null.
        /// </summary>
        public static Team? TeamOf(Taskforce? tf)
        {
            if (tf == null) return null;
            if (tf == Globals._playerTaskforce) return LocalTeam;
            if (tf == Globals._enemyTaskforce) return Other(LocalTeam);
            return null;
        }

        public static Team? TeamOf(ObjectBase? unit) => TeamOf(unit?._taskforce);

        public static Team Other(Team t) => t == Team.Blue ? Team.Red : Team.Blue;

        /// <summary>The local taskforce object for a team, resolved through this
        /// machine's own swap.</summary>
        public static Taskforce? TaskforceFor(Team team)
            => team == LocalTeam ? Globals._playerTaskforce : Globals._enemyTaskforce;

        /// <summary>
        /// At least one connected, established human commands this taskforce.
        ///
        /// Replaces <c>CfgPvP &amp;&amp; tf == Globals._enemyTaskforce</c> everywhere the
        /// question was really "must the host stop running AI for this fleet, because a
        /// person is flying it". Correct in every configuration: a co-op trio leaves Red
        /// as AI; 1v1 and 2v1 both make Red human.
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

        /// <summary>Both teams have a human in them. The intel-separation question and the
        /// closest surviving relative of the old PvP flag.</summary>
        public static bool ContestedSession
            => PlayerRegistry.AnyOnTeam(Team.Blue) && PlayerRegistry.AnyOnTeam(Team.Red);

        /// <summary>
        /// This machine loaded a save with PlayerTaskforce/EnemyTaskforce swapped.
        ///
        /// Only a Red GUEST swaps: the host runs the authoritative sim on the mission's
        /// own unswapped save, which is exactly why the host is always Blue. Note this is
        /// NOT the same question as <see cref="ContestedSession"/> - a Red player is
        /// swapped whether or not anyone is sitting on Blue.
        ///
        /// Safe to read while applying a received save: BlockedPreHandshake drops
        /// everything except Hello/Welcome until Established, so Welcome - which sets the
        /// team - always lands before any SessionSync can.
        /// </summary>
        public static bool LocalSaveSwapped
            => !Plugin.Instance.CfgIsHost.Value && PlayerRegistry.LocalTeam == Team.Red;

        public static string Name(Team t) => t == Team.Blue ? "Blue" : "Red";
    }
}
