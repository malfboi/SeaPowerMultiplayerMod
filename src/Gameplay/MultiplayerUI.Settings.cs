using System.Collections.Generic;
using BepInEx.Configuration;
using SeapowerMultiplayer.Transport;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// SETTINGS section of the F9 overlay. Writes straight to the BepInEx
    /// ConfigEntry objects, which persist to
    /// BepInEx/config/com.seapowermultiplayer.plugin.cfg on assignment - so there is no
    /// separate save step and the file stays interchangeable with the launcher.
    /// </summary>
    public partial class MultiplayerUI
    {
        /// <summary>True while one of our text fields owns keyboard focus.
        /// Patch_InputHandler_OnUpdate reads this to mute the game's hotkeys
        /// so typing an IP doesn't also drive the sim.</summary>
        internal static bool TextInputFocused;

        private bool _foldSettings;
        private bool _foldAdvanced;

        // Draft text per field, keyed by control name. Committed on Enter or
        // focus loss so a half-typed port never reaches the network layer.
        private readonly Dictionary<string, string> _drafts = new();

        private GUIStyle? _segOnStyle;
        private GUIStyle? _segOffStyle;
        private GUIStyle? _textFieldStyle;
        private GUIStyle? _checkStyle;
        private bool _settingsStylesInit;

        private static void ReleaseTextFocus()
        {
            GUIUtility.keyboardControl = 0;
            TextInputFocused = false;
        }

        private void InitSettingsStyles()
        {
            if (_settingsStylesInit) return;

            _segOnStyle = new GUIStyle(_buttonStyle!)
            {
                fixedHeight = 20,
                normal = { background = _btnActiveTex, textColor = Color.white },
                hover  = { background = _btnActiveTex, textColor = Color.white },
            };

            _segOffStyle = new GUIStyle(_buttonStyle!)
            {
                fixedHeight = 20,
                fontStyle   = FontStyle.Normal,
                normal      = { background = _btnNormalTex, textColor = new Color(0.50f, 0.57f, 0.66f) },
            };

            _textFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize    = 11,
                fixedHeight = 20,
            };

            _checkStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize    = 14,
                fixedWidth  = 20,
                fixedHeight = 18,
                alignment   = TextAnchor.MiddleCenter,
                normal      = { textColor = new Color(0.50f, 0.80f, 0.98f) },
            };

            _settingsStylesInit = true;
        }

        // ── Row helpers ──────────────────────────────────────────────────────

        /// <summary>Assigns only on a real change: OnGUI runs several times a frame
        /// and every assignment fires BepInEx's save-on-set.</summary>
        private static void Set<T>(ConfigEntry<T> entry, T value)
        {
            if (!Equals(entry.Value, value)) entry.Value = value;
        }

        /// <summary>Two-button segmented choice. Returns the (possibly changed) value.</summary>
        private bool SegmentedRow(string label, bool value, string onText, string offText)
        {
            bool result = value;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(64));
            if (GUILayout.Button(onText,  value ? _segOnStyle : _segOffStyle))  result = true;
            if (GUILayout.Button(offText, value ? _segOffStyle : _segOnStyle)) result = false;
            GUILayout.EndHorizontal();
            return result;
        }

        private bool ToggleRow(string label, bool value, string? note = null)
        {
            bool result = value;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(value ? "☑" : "☐", _checkStyle, GUILayout.Width(20)))
                result = !value;
            GUILayout.Label(label, _labelStyle);
            if (note != null)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(note, _dimLabelStyle);
            }
            GUILayout.EndHorizontal();
            return result;
        }

        /// <summary>
        /// Text row backed by a draft buffer. Returns true (with <paramref name="text"/>
        /// set) only on commit - Enter, or focus moving away from the field.
        /// </summary>
        private bool TextRow(string label, string name, string current, out string text)
        {
            bool focusedBefore = GUI.GetNameOfFocusedControl() == name;

            // While not being edited the draft mirrors the live value, so changes
            // made elsewhere (env overrides, Steam lobby join) show up here.
            if (!focusedBefore) _drafts[name] = current;
            else if (!_drafts.ContainsKey(name)) _drafts[name] = current;

            // Read the key event before TextField consumes it.
            bool enter = focusedBefore
                && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(64));
            GUI.SetNextControlName(name);
            _drafts[name] = GUILayout.TextField(_drafts[name], 24, _textFieldStyle);
            GUILayout.EndHorizontal();

            text = _drafts[name];

            if (enter)
            {
                ReleaseTextFocus();
                return true;
            }
            // Focus left the field this pass
            return focusedBefore && GUI.GetNameOfFocusedControl() != name;
        }

        private void IntRow(string label, string name, ConfigEntry<int> entry, int min, int max)
        {
            if (TextRow(label, name, entry.Value.ToString(), out string typed)
                && int.TryParse(typed, out int parsed))
                Set(entry, Mathf.Clamp(parsed, min, max));
        }

        private void FloatRow(string label, string name, ConfigEntry<float> entry, float min, float max)
        {
            if (TextRow(label, name, entry.Value.ToString("0.##"), out string typed)
                && float.TryParse(typed, out float parsed))
                Set(entry, Mathf.Clamp(parsed, min, max));
        }

        // ── Section ──────────────────────────────────────────────────────────

        private void DrawSettings()
        {
            InitSettingsStyles();

            GUILayout.Space(2);
            string arrow = _foldSettings ? "▼" : "▶";
            if (GUILayout.Button($"⚙  SETTINGS      {arrow}", _sectionTitleStyle!, GUILayout.ExpandWidth(true)))
            {
                _foldSettings = !_foldSettings;
                if (!_foldSettings) ReleaseTextFocus();
            }
            GUILayout.Box("", _separatorStyle!, GUILayout.ExpandWidth(true));

            if (!_foldSettings) return;

            var p = Plugin.Instance;

            // Role and transport are not settings here: the transport is always
            // Steam (forced in Plugin.Awake) and host/client is decided by who
            // creates the lobby, which SteamLobbyManager writes to CfgIsHost.
            //
            // PvP is baked into the v2 handshake and the lobby metadata, so it can
            // only change while nothing is running.
            bool locked = NetworkManager.Instance.IsConnected
                       || NetworkManager.Instance.IsHostRunning
                       || SteamLobbyManager.InLobby;

            bool prevEnabled = GUI.enabled;
            GUI.enabled = !locked;

            Set(p.CfgPvP, SegmentedRow("Mode", p.CfgPvP.Value, "PvP", "Co-op"));

            GUI.enabled = prevEnabled;

            if (locked)
                GUILayout.Label("  Leave the lobby to change mode", _dimLabelStyle);

            Set(p.CfgTimeVote, ToggleRow("Time vote (host)", p.CfgTimeVote.Value));

            // ── Sync state stream ────────────────────────────────────────────
            // All three are re-read every send, so edits apply live mid-session.
            GUILayout.Space(2);
            GUILayout.Label("  Sync state (applies live)", _dimLabelStyle);
            IntRow("Unit Hz", "spmp.unithz", p.CfgUnitStateHz, 1, 60);
            IntRow("Missile Hz", "spmp.mslhz", p.CfgMissileStateHz, 1, 60);
            FloatRow("Damage s", "spmp.dmgint", p.CfgDamageSyncInterval, 0.25f, 30f);

            // ── Shared tactical picture ──────────────────────────────────────
            // Sensors run locally on both machines, so without these the same
            // contact carries a different track number on each screen and can be
            // identified on one and unknown on the other.
            //
            // Co-op only - in PvP the players are opponents whose pictures are
            // meant to differ. Shown disabled rather than hidden so the toggles
            // stay discoverable and the reason they are off is on screen.
            //
            // Both take effect on the next tick, no restart. Switching contacts off
            // stops anything further being imposed and releases the client, but a
            // track number already written onto a Vehicle stays until that contact
            // drops and is re-detected - Vehicle.Id is assigned once at creation and
            // UpdateFromECS never rewrites it, so there is nothing to revert to.
            // Hence "applies live" is not claimed here.
            GUILayout.Space(2);
            GUILayout.Label("  Shared picture (co-op)", _dimLabelStyle);

            GUI.enabled = !p.CfgPvP.Value;
            Set(p.CfgContactSync, ToggleRow("Contacts & track numbers", p.CfgContactSync.Value));
            Set(p.CfgDrawingSync, ToggleRow("Map markers", p.CfgDrawingSync.Value));
            GUI.enabled = prevEnabled;

            if (p.CfgPvP.Value)
                GUILayout.Label("  Co-op only — sharing these would give away intel", _dimLabelStyle);

            GUILayout.Space(2);
            string advArrow = _foldAdvanced ? "▼" : "▶";
            if (GUILayout.Button($" {advArrow}  Advanced", _sectionHeaderStyle!, GUILayout.ExpandWidth(true)))
            {
                _foldAdvanced = !_foldAdvanced;
                if (!_foldAdvanced) ReleaseTextFocus();
            }

            if (_foldAdvanced)
            {
                Set(p.CfgVerboseDebug, ToggleRow("Verbose logging", p.CfgVerboseDebug.Value));

                GUILayout.Space(2);
                GUI.enabled = !locked;
                if (GUILayout.Button("Reset to defaults", _buttonStyle))
                {
                    ResetToDefaults();
                    ReleaseTextFocus();
                }
                GUI.enabled = prevEnabled;
            }

            // Assert focus state for the hotkey mute (cleared each OnGUI pass).
            if (GUIUtility.keyboardControl != 0)
                TextInputFocused = true;
        }

        private void ResetToDefaults()
        {
            var p = Plugin.Instance;
            // Only what this panel exposes. CfgTransport and CfgIsHost are driven
            // by the Steam lobby flow, not by the user, so resetting them here
            // would fight it.
            ConfigEntryBase[] all =
            {
                p.CfgPvP, p.CfgTimeVote, p.CfgVerboseDebug,
                p.CfgDamageSyncInterval, p.CfgMissileStateHz, p.CfgUnitStateHz,
                p.CfgContactSync, p.CfgDrawingSync,
            };
            foreach (var e in all)
                e.BoxedValue = e.DefaultValue;

            _drafts.Clear();
            Plugin.Log.LogInfo("[Settings] Reset to defaults");
        }
    }
}
