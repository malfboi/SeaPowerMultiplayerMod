using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Holds the game's own F10 debug/cheat panel shut.
    ///
    /// No Harmony patch: InputHandler.Update already gates the toggle on
    /// <c>DM._allowDMHotkey</c> (<c>if (getKeyDown(HideDebugPanel) &amp;&amp;
    /// DM._allowDMHotkey)</c>), so clearing that flag is the engine's own way of
    /// saying "this hotkey does nothing". The toggle lives inline in a very long
    /// method, so patching it would take a transpiler to reach - and would only
    /// re-implement what the flag already does.
    ///
    /// Re-asserted every frame rather than set once, because it is not ours alone:
    /// OptionsManager turns it back on whenever it re-reads the options ini, and
    /// MissionBriefViewModel re-opens the panel (<c>DM._showDM = true</c>) after a
    /// briefing if it had been open. A single write at connect time would lose to
    /// both. <c>_showDM</c> is cleared too, so the rule also closes a panel that
    /// was already on screen instead of merely refusing to open a new one.
    ///
    /// HOST DECIDES. The host's setting rides in the Welcome message, so it is in
    /// force on the client from the handshake - before the session save is sent,
    /// which is the point: the client never gets a window where the mission is
    /// loaded and the cheat panel still opens.
    ///
    /// The host's rule is a floor, not an exact value: it can only ever take the
    /// panel away. A client that has its own setting on keeps the panel shut even
    /// under a host that allows it - restricting yourself further harms nobody, and
    /// the alternative would have a host's "off" quietly switch the panel back on
    /// for a player who had deliberately disabled it.
    /// </summary>
    internal static class DebugMenuLock
    {
        private static bool _locked;
        /// <summary>Whether the hotkey was allowed before we took it away, so
        /// releasing the lock puts it back. Latched from observation rather than
        /// captured once: OptionsManager only sets the flag when it reads the
        /// options ini, which can happen after we have already locked.</summary>
        private static bool _hotkeyWasAllowed;

        /// <summary>This machine's own setting, plus the host's rule when we are the
        /// client of an established session.</summary>
        internal static bool RuleActive =>
            Plugin.Instance.CfgDisableF10Menu.Value || HostRule;

        /// <summary>The host's rule as it reached us in Welcome. False on the host
        /// itself and whenever no session is established.</summary>
        internal static bool HostRule
        {
            get
            {
                var nm = NetworkManager.Instance;
                return nm.IsEstablished
                    && !Plugin.Instance.CfgIsHost.Value
                    && (nm.SessionParams?.DisableF10Menu ?? false);
            }
        }

        internal static void Tick()
        {
            if (RuleActive)
            {
                if (!_locked)
                {
                    _locked = true;
                    _hotkeyWasAllowed = false;
                    Plugin.Log.LogInfo("[DebugMenu] F10 debug menu disabled"
                        + (HostRule ? " (host's rule)" : ""));
                }

                if (DM._allowDMHotkey)
                {
                    _hotkeyWasAllowed = true;   // remember, then take it away
                    DM._allowDMHotkey = false;
                }
                if (DM._showDM) DM._showDM = false;
            }
            else if (_locked)
            {
                _locked = false;
                DM._allowDMHotkey = _hotkeyWasAllowed;
                Plugin.Log.LogInfo("[DebugMenu] F10 debug menu re-enabled");
            }
        }
    }
}
