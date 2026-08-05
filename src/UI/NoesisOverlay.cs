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
                // Off until the cursor is on the panel - see SetKeyboard. Set
                // together with the shadow flag so a rebuilt view cannot start
                // out disagreeing with what SetKeyboard thinks it left behind.
                _view.EnableKeyboard = false;
                _keyboard            = false;
                _view.EnableTouch    = false;
                _view.EnableActions  = false;

                // Xaml was assigned after OnEnable already ran, so the load has
                // to be forced (same as FreeContentPngExporter does).
                _view.LoadXaml(force: true);

                if (_view.Content == null)
                    throw new InvalidOperationException("NoesisView.Content was null after LoadXaml.");

                _view.Content.DataContext = _vm;
                HookPanelDrag();

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
            _dragBar = null;
            _panelOffset = null;
            _dragging = false;
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

        // ── Panel drag ───────────────────────────────────────────────────────

        private FrameworkElement? _dragBar;
        private TranslateTransform? _panelOffset;
        private bool _dragging;
        private float _grabX, _grabY;       // cursor position in root space at grab
        private float _grabOffX, _grabOffY; // panel offset at grab
        private float _barBaseX, _barBaseY; // bar's untranslated origin, for clamping

        /// <summary>Survives a view rebuild - a scene teardown recreates the whole
        /// view, and having the panel jump back to the corner every mission load
        /// would make moving it pointless.</summary>
        private float _offsetX, _offsetY;

        /// <summary>
        /// Makes the header row drag the panel.
        ///
        /// Wired here rather than in the markup because the XAML is parsed at
        /// runtime with no code-behind class, so event attributes in it would have
        /// nothing to resolve against.
        /// </summary>
        private void HookPanelDrag()
        {
            var root = _view?.Content;
            if (root == null) return;

            _dragBar     = root.FindName("DragBar") as FrameworkElement;
            _panelOffset = (root.FindName("PanelScroll") as UIElement)?.RenderTransform
                           as TranslateTransform;

            if (_dragBar == null || _panelOffset == null)
            {
                Plugin.Log.LogWarning("[UI] Panel drag handle not found - the panel stays put.");
                return;
            }

            _panelOffset.X = _offsetX;
            _panelOffset.Y = _offsetY;

            _dragBar.Cursor = Cursors.Hand;
            _dragBar.MouseLeftButtonDown += OnDragStart;
            _dragBar.MouseMove           += OnDragMove;
            _dragBar.MouseLeftButtonUp   += OnDragEnd;
            // Anything can take the capture away - a popup, a lost window focus.
            // Without this the panel would keep following the cursor afterwards.
            _dragBar.LostMouseCapture    += (_, __) => _dragging = false;
        }

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            var root = _view?.Content;
            if (root == null || _dragBar == null || _panelOffset == null) return;

            Point inRoot = e.GetPosition(root);
            Point inBar  = e.GetPosition(_dragBar);

            _grabX = inRoot.X; _grabY = inRoot.Y;
            _grabOffX = _panelOffset.X; _grabOffY = _panelOffset.Y;

            // Where the bar would sit with no offset. Derived from the two
            // readings of the same click rather than from TransformToVisual,
            // which in Noesis returns a matrix rather than a point transform.
            _barBaseX = inRoot.X - inBar.X - _panelOffset.X;
            _barBaseY = inRoot.Y - inBar.Y - _panelOffset.Y;

            _dragging = true;
            _dragBar.CaptureMouse();
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            var root = _view?.Content;
            if (!_dragging || root == null || _dragBar == null || _panelOffset == null) return;

            Point p = e.GetPosition(root);

            // Clamped so a piece of the bar always stays on screen. Dropping the
            // panel past the edge would take the only handle for retrieving it
            // over the edge too.
            const float Keep = 90f;
            float x = _grabOffX + (p.X - _grabX);
            float y = _grabOffY + (p.Y - _grabY);

            _panelOffset.X = Mathf.Clamp(x, Keep - _barBaseX - _dragBar.ActualWidth,
                                            root.ActualWidth - _barBaseX - Keep);
            _panelOffset.Y = Mathf.Clamp(y, -_barBaseY,
                                            root.ActualHeight - _barBaseY - _dragBar.ActualHeight);
            _offsetX = _panelOffset.X;
            _offsetY = _panelOffset.Y;
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            _dragBar?.ReleaseMouseCapture();
        }

        // ── Input arbitration ────────────────────────────────────────────────

        private bool _capturing;
        private bool _typing;
        private bool _keyboard;

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
            bool over = _view.Content?.IsMouseOver == true;

            // A drag follows the cursor off the panel, and the game must not
            // start panning the camera underneath it halfway through.
            SetCapturing(over || _dragging);

            // Hover, not focus alone, decides both of these. A text box keeps
            // focus after the player moves on, and keying off focus by itself
            // left typingActive stuck true with the game's hotkeys dead and
            // nothing on screen to explain it.
            SetTyping(over && _view.Content?.Keyboard?.FocusedElement is TextBox);
            SetKeyboard(over);
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
        /// The overlay answers the keyboard only while the cursor is on it.
        ///
        /// It shares a keyboard with the game and has no way to tell a keystroke
        /// meant for one from a keystroke meant for the other, so Enter pressed
        /// for the sim was being delivered to the panel and pressing whatever
        /// held focus. The cursor is the one unambiguous signal of which of the
        /// two the player is working in - and since a text box has to be clicked
        /// before it can be typed into, the cursor is always on the panel when
        /// typing actually needs to work.
        /// </summary>
        private void SetKeyboard(bool on)
        {
            if (_view == null || on == _keyboard) return;
            _keyboard = on;
            _view.EnableKeyboard = on;
        }

        /// <summary>
        /// Always reachable, so a hide-while-hovered or a crash can never leave
        /// the game's input suppressed.
        /// </summary>
        private void ReleaseInputCapture()
        {
            SetCapturing(false);
            SetTyping(false);
            SetKeyboard(false);
            _dragging = false;
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
