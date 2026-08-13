using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public enum RelayAudience
    {
        /// <summary>Host-handled, or meaningless to anyone but the host. Goes nowhere.</summary>
        None,
        /// <summary>The sender's teammates. Anything that is one side's business.</summary>
        Team,
        /// <summary>Every other player. Reserved for genuinely global state.</summary>
        All,
    }

    /// <summary>
    /// Who else should hear a guest-originated <see cref="GameEventMessage"/>.
    ///
    /// Kept as a table rather than scattered `if (isHost) Broadcast(...)` calls at each
    /// event's handler, because the failure mode of forgetting one is silent: the event
    /// applies on the host, the sender sees its own effect locally, and only the third
    /// player is quietly out of step.
    /// </summary>
    internal static class GameEventRelay
    {
        internal static RelayAudience AudienceFor(GameEventType type) => type switch
        {
            // Time is global - everyone has to agree on it, opponents included.
            GameEventType.TimeProposal         => RelayAudience.All,
            GameEventType.TimeProposalResponse => RelayAudience.All,

            // The ally lock is a within-team concept: it exists so teammates do not
            // fight over a shared fleet, and an opponent has no use for it.
            GameEventType.UnitSelected   => RelayAudience.Team,
            GameEventType.UnitDeselected => RelayAudience.Team,

            // Host decides and announces the result itself; relaying the REQUEST would
            // have everyone act on something that has not been granted yet.
            GameEventType.HardSyncRequest => RelayAudience.None,

            // Host-originated in the first place - a guest never sends these, and
            // reflecting one back would be a loop waiting to happen.
            GameEventType.TimeChanged       => RelayAudience.None,
            GameEventType.TaskforceAssigned => RelayAudience.None,
            GameEventType.MissionEnd        => RelayAudience.None,
            GameEventType.TimeVoteMode      => RelayAudience.None,

            _ => RelayAudience.None,
        };
    }
}
