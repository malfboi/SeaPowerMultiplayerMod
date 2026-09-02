using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tells the player why an order they just gave did nothing.
    ///
    /// This is all that survives of the old transient "ally lock". That lock claimed a
    /// unit for whoever had it selected and released it on deselect, which made sense
    /// when the only two states were "co-op, share everything" and "PvP, share nothing".
    /// Neither survives: with the unit lock ON, <see cref="FormationOwnership"/> decides
    /// who commands what, persistently and per formation; with it OFF the session is a
    /// free-for-all and there is nothing to arbitrate. A selection-following claim on top
    /// of either would only fight them.
    ///
    /// The FEEDBACK half was always the valuable part and is kept verbatim. The gate is
    /// enforced by silently dropping the order in OrderSyncHelper. On a client the game
    /// also renders the unit as uncontrollable, so the refusal at least looks deliberate.
    /// On the HOST that override must not be applied - it would make the game drop queued
    /// fires mid-tick - so the unit still looks controllable, the click is accepted, and
    /// nothing happens. That is how queued waypoints on someone else's P-3 read as a lost
    /// order rather than a refusal.
    /// </summary>
    public static class OrderRefusalNotice
    {
        /// <summary>Name of the unit whose order was last refused.</summary>
        public static string LastRefusedUnitName { get; private set; } = "";

        /// <summary>Name of the player who holds it, for the notice text.</summary>
        public static string LastRefusedOwnerName { get; private set; } = "";

        /// <summary>Unscaled time the message should stop being shown.</summary>
        public static float NoticeUntil { get; private set; }

        private const float NoticeSeconds = 3f;
        private static float _nextRefusalLog;

        // ── Player-input recency ─────────────────────────────────────────────
        //
        // The order patches cannot tell a player's click from the unit's own local sim:
        // state machines tick on every machine, and for an aircraft somebody else is
        // flying they keep re-issuing internal waypoint/speed maintenance calls, each of
        // which is (correctly) refused. Each refusal refreshed the notice, so "someone
        // else is commanding it" sat on screen permanently while the other player merely
        // flew the aircraft. A refusal is only worth telling the player about when THEY
        // just did something - approximated as a mouse press moments ago, which every map
        // or panel-issued order involves.

        private const float InputRecencySec = 0.3f;
        private static float _lastPointerInputTime = -1f;

        /// <summary>Called once per frame from Plugin.Update.</summary>
        public static void SampleInput()
        {
            if (UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1)
                || UnityEngine.Input.GetMouseButtonUp(0) || UnityEngine.Input.GetMouseButtonUp(1))
                _lastPointerInputTime = UnityEngine.Time.unscaledTime;
        }

        /// <summary>Called when the ownership gate refuses an order. Safe to call every
        /// frame - a waypoint drag fires continuously, so the notice just keeps refreshing
        /// its own expiry and the log line is throttled. Refusals with no recent player
        /// input are internal sim calls and are refused silently.</summary>
        public static void Note(ObjectBase? unit)
        {
            if (_lastPointerInputTime < 0f
                || UnityEngine.Time.unscaledTime - _lastPointerInputTime > InputRecencySec)
                return;

            LastRefusedUnitName  = unit == null ? "" : unit.Name?.Value ?? unit.name;
            LastRefusedOwnerName = FormationOwnership.OwnerName(unit);
            NoticeUntil          = UnityEngine.Time.unscaledTime + NoticeSeconds;

            if (UnityEngine.Time.unscaledTime < _nextRefusalLog) return;
            _nextRefusalLog = UnityEngine.Time.unscaledTime + NoticeSeconds;
            Plugin.Log.LogInfo($"[Ownership] Order refused for {LastRefusedUnitName} — " +
                               $"{(LastRefusedOwnerName.Length > 0 ? LastRefusedOwnerName + " is commanding it." : "you do not command it.")}");
        }

        public static void Reset()
        {
            LastRefusedUnitName  = "";
            LastRefusedOwnerName = "";
            NoticeUntil          = 0f;
        }
    }
}
