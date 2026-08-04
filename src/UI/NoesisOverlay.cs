using System;
using System.Text;
using Noesis;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// Hosts the multiplayer overlay as a NoesisView on its own camera.
    ///
    /// The IMGUI overlay this replaces could only be used inside a loaded
    /// mission - it said so itself, in a "load a mission first" notice. The
    /// game's UI is Noesis end to end, each view processes input in its own
    /// camera's OnGUI pass, and nothing arbitrated between that and ours.
    /// Running as a Noesis view on a camera above the game's puts the overlay in
    /// the same input pipeline as the game's own UI, so it works in the main
    /// menu - which is exactly where the Steam lobby flow is needed.
    ///
    /// Camera and view are enabled and disabled together; that is the game's own
    /// show/hide idiom for a Noesis screen (see ConsoleView.ToggleConsole).
    /// </summary>
    public class NoesisOverlay : MonoBehaviour
    {
        private const float RefreshInterval = 0.1f;

        private readonly OverlayViewModel _vm = new OverlayViewModel();

        private GameObject? _host;
        private Camera? _camera;
        private NoesisView? _view;
        private NoesisXaml? _xaml;

        /// <summary>Panel open/closed (Ctrl+F9). Popups show regardless.</summary>
        private bool _panelVisible = true;

        private float _nextRefresh;
        private string? _initError;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void Update()
        {
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.F9))
                _panelVisible = !_panelVisible;

            // The host object is DontDestroyOnLoad, but a scene teardown or a
            // failed load can still take it; rebuild rather than going dark.
            if (_initError == null && (_host == null || _view == null))
                EnsureView();

            if (_view == null) return;

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                _vm.PanelVisibility = _panelVisible ? Visibility.Visible : Visibility.Collapsed;
                _vm.Refresh(_panelVisible);
                KeepOnTop();
            }

            ApplyVisibility();
            UpdateInputCapture();
        }

        private void Awake() => useGUILayout = false;   // the fallback notice uses GUI, not GUILayout

        private void OnDisable() => ReleaseInputCapture();

        private void OnDestroy()
        {
            ReleaseInputCapture();
            DestroyView();
        }

        // ── View construction ────────────────────────────────────────────────

        private void EnsureView()
        {
            try
            {
                DestroyView();

                _host = new GameObject("SPM_OverlayCamera");
                UnityEngine.Object.DontDestroyOnLoad(_host);

                // Camera first: NoesisView.OnEnable resolves its camera with
                // TryGetComponent, and AddComponent runs OnEnable synchronously.
                _camera = _host.AddComponent<Camera>();
                _camera.clearFlags          = CameraClearFlags.Depth;
                _camera.cullingMask         = 0;
                _camera.allowHDR            = false;
                _camera.allowMSAA           = false;
                _camera.useOcclusionCulling = false;
                _camera.depth               = TopCameraDepth() + 10f;

                _xaml = ScriptableObject.CreateInstance<NoesisXaml>();
                _xaml.uri     = "memory://spm_overlay.xaml";
                _xaml.content = Encoding.UTF8.GetBytes(OverlayXaml.Markup);

                _view = _host.AddComponent<NoesisView>();
                _view.Xaml           = _xaml;
                _view.EnableMouse    = true;
                _view.EnableKeyboard = true;   // the SETTINGS rows have text fields
                _view.EnableTouch    = false;
                _view.EnableActions  = false;

                // Xaml was assigned after OnEnable already ran, so the load has
                // to be forced (same as FreeContentPngExporter does).
                _view.LoadXaml(force: true);

                if (_view.Content == null)
                    throw new InvalidOperationException("NoesisView.Content was null after LoadXaml.");

                _view.Content.DataContext = _vm;

                _camera.enabled = false;
                _view.enabled   = false;

                Plugin.Log.LogInfo($"[UI] Noesis overlay ready (camera depth {_camera.depth}).");
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
                Plugin.Log.LogError($"[UI] Noesis overlay failed to initialise - falling back to a text notice.\n{ex}");
                DestroyView();
            }
        }

        private void DestroyView()
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
            if (_xaml != null) UnityEngine.Object.Destroy(_xaml);
            _host = null;
            _camera = null;
            _view = null;
            _xaml = null;
        }

        /// <summary>
        /// Both draw order and input order follow camera depth, and the overlay
        /// outlives the scene that was loaded when it was built - a mission's UI
        /// cameras appear later and can sit above the depth chosen at the menu.
        /// </summary>
        private void KeepOnTop()
        {
            if (_camera == null) return;
            float top = TopCameraDepth();
            if (_camera.depth <= top) _camera.depth = top + 10f;
        }

        /// <summary>
        /// Highest depth among all cameras except ours. Includes disabled ones
        /// (the console camera sits disabled until toggled), and is recomputed on
        /// every rebuild because the menu and mission scenes differ.
        /// </summary>
        private float TopCameraDepth()
        {
            float top = 0f;
            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (cam == _camera) continue;
                if (cam.depth > top) top = cam.depth;
            }
            return top;
        }

        // ── Visibility ───────────────────────────────────────────────────────

        private void ApplyVisibility()
        {
            if (_camera == null || _view == null) return;

            // Popups have to reach the player with the panel closed - that is the
            // whole point of the disconnect and version-mismatch prompts.
            bool show = _panelVisible || _vm.AnyPopupActive;
            if (_camera.enabled == show) return;

            _camera.enabled = show;
            _view.enabled   = show;
            if (!show) ReleaseInputCapture();
        }

        // ── Input arbitration ────────────────────────────────────────────────

        private bool _capturing;
        private bool _typing;

        /// <summary>
        /// Nothing is done to hold other views off. A Noesis view only calls
        /// Event.Use() when it actually handled the click - that is why its input
        /// entry points return bool - so a click that misses the overlay already
        /// reaches the menu underneath on its own.
        ///
        /// An earlier version muted EnableMouse on every other view while the
        /// cursor was over the overlay. That is what made the main menu
        /// completely dead: one bad hover reading left the menu's own view muted
        /// with nothing to turn it back on. Suppression is now limited to the
        /// game's mission-side camera handling, which self-restores every tick.
        /// </summary>
        private void UpdateInputCapture()
        {
            if (_view == null || !_view.enabled) { ReleaseInputCapture(); return; }

            // Content.IsMouseOver is set for every ancestor of whatever Noesis
            // hit, so this is true exactly when the cursor is on the panel or a
            // popup - no screen/DPI maths to get wrong. The root Grid has a null
            // background and does not hit-test, so empty space reads as false.
            SetCapturing(_view.Content?.IsMouseOver == true);
            SetTyping(_view.Content?.Keyboard?.FocusedElement is TextBox);
        }

        private void SetCapturing(bool on)
        {
            if (on == _capturing) return;
            _capturing = on;

            // In a mission this stops the camera panning and the world taking
            // clicks under the panel. Menus need no equivalent: they are Noesis
            // too, so an unhandled click simply falls through to them.
            if (Singleton<MouseControlState>.InstanceExists(false))
                Singleton<MouseControlState>.Instance.setMouseIsOverUIWindow(on);
        }

        private void SetTyping(bool on)
        {
            if (on == _typing) return;
            _typing = on;

            // The game's own "a text box has focus" flag; InputHandler.OnUpdate
            // returns early on it, so typing a sync rate cannot also drive the sim.
            if (Singleton<InputHandler>.InstanceExists(false))
                Singleton<InputHandler>.Instance.typingActive = on;
        }

        /// <summary>
        /// Always reachable, so a hide-while-hovered or a crash can never leave
        /// the game's input suppressed.
        /// </summary>
        private void ReleaseInputCapture()
        {
            SetCapturing(false);
            SetTyping(false);
        }

        // ── Fallback notice ──────────────────────────────────────────────────

        /// <summary>
        /// Only ever draws when the Noesis view could not be created. Without it
        /// a broken view leaves the mod with no UI and no visible reason why.
        /// </summary>
        private void OnGUI()
        {
            if (_initError == null) return;

            UnityEngine.GUI.Label(new UnityEngine.Rect(10f, 10f, Screen.width - 20f, 40f),
                $"SeaPower MP: overlay failed to load - {_initError}. See BepInEx/LogOutput.log.");
        }
    }
}
