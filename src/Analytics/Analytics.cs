using System;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Opt-in diagnostics. The only type the rest of the mod touches.
    ///
    /// Nothing is captured, retained or sent until the player has explicitly
    /// consented. When consent is off the listener is never attached and the
    /// uploader thread is never started - this is "collect nothing", not "collect
    /// but don't send".
    ///
    /// See PRIVACY.md for what the payload contains and why.
    /// </summary>
    internal static class Analytics
    {
        /// <summary>Ingest endpoint. Overridable from config for wrangler dev.</summary>
        private const string DefaultEndpoint =
            "https://seapower-telemetry.seapower-multiplayer.workers.dev/v1/ingest";

        // There is deliberately no shared key here. A secret compiled into a DLL
        // that ships to every player is not a secret, and the one real option -
        // validating a Steam auth ticket - needs a publisher Web API key for the
        // game's appid, which a mod author cannot obtain. A key would also be a
        // trap rather than a lever: rotating it after abuse would break every
        // player who had not updated yet. Abuse is bounded server-side instead,
        // by per-install and per-IP rate limits, a body-size cap, R2 expiry and
        // a kill switch.

        private const float ConsentPromptDelaySec = 10f;   // stay off screen during the boot hitch
        private const float ErrorArmSec           = 5f;    // let the aftermath land in the same batch
        private const float ErrorCooldownSec      = 60f;
        private const int   MaxErrorFlushes       = 20;

        internal static bool   Enabled   { get; private set; }
        internal static string InstallId { get; private set; } = "";
        internal static string SessionId { get; private set; } = "";
        internal static string Endpoint  { get; private set; } = DefaultEndpoint;

        /// <summary>Read from the uploader thread, written on the main thread.</summary>
        internal static volatile bool LinkEstablished;
        internal static volatile bool AlwaysUpload;   // SPMP_TELEMETRY_ALWAYS, for solo testing

        private static float _startedAt;
        private static int   _errorSignalTick;   // Environment.TickCount of the last NoteError, 0 = none
        private static float _errorArmedAt = -1f;
        private static float _lastErrorFlush = -999f;
        private static int   _errorFlushes;
        private static long  _errors, _warnings;

        // ── Lifecycle ────────────────────────────────────────────────────────

        internal static void Init()
        {
            var p = Plugin.Instance;

            string urlOverride = p.CfgDiagnosticsUrl.Value;
            if (!string.IsNullOrEmpty(urlOverride)) Endpoint = urlOverride;

            // Fully qualified: SeaPower has its own Environment class.
            AlwaysUpload = System.Environment.GetEnvironmentVariable("SPMP_TELEMETRY_ALWAYS") == "1";
            string? urlEnv = System.Environment.GetEnvironmentVariable("SPMP_TELEMETRY_URL");
            if (!string.IsNullOrEmpty(urlEnv)) Endpoint = urlEnv!;

            SessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
            _startedAt = Time.realtimeSinceStartup;

            if (p.CfgShareDiagnostics.Value) Enable();
        }

        internal static void Shutdown()
        {
            if (!Enabled) return;
            PushSessionLine(end: true, reason: "quit");
            AnalyticsUploader.Stop(TimeSpan.FromSeconds(3));
            LogRingSink.Detach();
            Enabled = false;
        }

        /// <summary>Per-frame, from Plugin.Update. Cheap no-op when opted out.</summary>
        internal static void Tick()
        {
            if (!Enabled) return;

            bool wasLinked = LinkEstablished;
            LinkEstablished = NetworkManager.Instance.IsEstablished;
            TrackPeerName();
            TrackMissionClock();

            // Metrics are only meaningful during a session, and sampling in the
            // menu would just churn the ring with snapshots nobody uploads. The
            // window is restarted on connect so the first one isn't half-idle.
            if (LinkEstablished || AlwaysUpload)
            {
                if (!wasLinked && !AlwaysUpload) MetricSampler.Reset();
                MetricSampler.Tick();
            }

            // Error-flush debounce lives here rather than in NoteError so the
            // timing logic stays single-threaded; NoteError itself is called from
            // arbitrary threads and only sets a tick stamp.
            int signal = System.Threading.Interlocked.Exchange(ref _errorSignalTick, 0);
            float now = Time.realtimeSinceStartup;

            if (signal != 0 && _errorArmedAt < 0f
                && now - _lastErrorFlush > ErrorCooldownSec
                && _errorFlushes < MaxErrorFlushes)
            {
                _errorArmedAt = now;
            }

            if (_errorArmedAt >= 0f && now - _errorArmedAt >= ErrorArmSec)
            {
                _errorArmedAt = -1f;
                _lastErrorFlush = now;
                _errorFlushes++;
                AnalyticsUploader.RequestFlush("error");
            }
        }

        // ── Consent ──────────────────────────────────────────────────────────

        /// <summary>True while the one-time prompt should be on screen. Held back
        /// during the boot hitch, and suppressed entirely when init failed - the
        /// fatal popup matters more at that moment.</summary>
        internal static bool ShouldPromptConsent =>
            Plugin.Instance != null
            && !Plugin.Instance.CfgDiagnosticsAsked.Value
            && Plugin.FatalInitError == null
            && Time.realtimeSinceStartup > ConsentPromptDelaySec;

        internal static void AcceptConsent()
        {
            Plugin.Instance.CfgDiagnosticsAsked.Value = true;
            SetEnabled(true);
        }

        internal static void DeclineConsent()
        {
            Plugin.Instance.CfgDiagnosticsAsked.Value = true;
            SetEnabled(false);
        }

        /// <summary>The SETTINGS checkbox path. Takes effect immediately, no restart.</summary>
        internal static void SetEnabled(bool on)
        {
            var p = Plugin.Instance;
            if (p.CfgShareDiagnostics.Value != on) p.CfgShareDiagnostics.Value = on;

            if (on && !Enabled) Enable();
            else if (!on && Enabled) Disable();
        }

        private static void Enable()
        {
            var p = Plugin.Instance;

            // Generated on first enable, not first launch: a player who never opts
            // in never has an identifier written to disk at all.
            if (string.IsNullOrEmpty(p.CfgInstallId.Value))
                p.CfgInstallId.Value = Guid.NewGuid().ToString("N");
            InstallId = p.CfgInstallId.Value;

            Redactor.Init(InstallId);
            RegisterKnownSecrets();

            LogRingSink.Attach();
            MetricSampler.Reset();
            AnalyticsUploader.Start();
            Enabled = true;

            PushSessionLine(end: false, reason: null);
            Plugin.Log.LogInfo($"[Analytics] Diagnostics sharing ON (id {InstallId.Substring(0, 8)}…). " +
                               "Turn it off in the Ctrl+F9 SETTINGS section.");
        }

        private static void Disable()
        {
            // No farewell packet, and the pending ring is abandoned rather than
            // uploaded: turning it off must produce zero further traffic.
            Enabled = false;
            AnalyticsUploader.Stop(TimeSpan.FromMilliseconds(250), finalFlush: false);
            LogRingSink.Active?.Clear();
            LogRingSink.Detach();
            Plugin.Log.LogInfo("[Analytics] Diagnostics sharing OFF. Nothing further will be sent.");
        }

        /// <summary>Values no regex can find, so they have to be registered.</summary>
        private static void RegisterKnownSecrets()
        {
            try { Redactor.AddSecret(Application.persistentDataPath, "<appdata>"); } catch { }
            try { Redactor.AddSecret(Application.dataPath, "<gamedir>"); } catch { }
            try { Redactor.AddSecret(Steamworks.SteamFriends.GetPersonaName(), "<player>"); } catch { }
        }

        /// <summary>The peer's persona name only exists once they join, so it
        /// cannot be registered up front like the rest.</summary>
        private static string _knownPeerName = "";

        private static void TrackPeerName()
        {
            string peer = SeapowerMultiplayer.Transport.SteamLobbyManager.PeerName;
            if (peer == _knownPeerName) return;
            _knownPeerName = peer;
            Redactor.AddSecret(peer, "<peer>");
        }

        // ── Mission clock ────────────────────────────────────────────────────

        // Real elapsed time and mission elapsed time diverge fast: at 10x
        // compression an hour of mission fits in six minutes of wall clock. And
        // for lining up the two players' uploads, the mission clock is the thing
        // both machines genuinely agree on - wall-clock timestamps only match if
        // both PCs' system clocks do.
        private static DateTime _missionStart;
        private static bool     _missionStartSet;

        /// <summary>In-game date/time. Uses the full date rather than
        /// TimeSyncManager.MissionSeconds(), which is time-of-day only and wraps
        /// at midnight. False when no mission is loaded.</summary>
        internal static bool TryGetGameDateTime(out DateTime when)
        {
            when = default;
            try
            {
                if (!Singleton<SeaPower.Environment>.InstanceExists(false)) return false;
                when = Singleton<SeaPower.Environment>.Instance.DateTime;
                return true;
            }
            catch { return false; }   // uninitialised Year/Month would throw
        }

        /// <summary>Mission seconds elapsed, or NaN when no mission is loaded.</summary>
        internal static double MissionElapsedSec =>
            _missionStartSet && TryGetGameDateTime(out DateTime now)
                ? (now - _missionStart).TotalSeconds
                : double.NaN;

        private static void TrackMissionClock()
        {
            if (!TryGetGameDateTime(out DateTime now))
            {
                _missionStartSet = false;   // back in the menu; next mission re-baselines
                return;
            }

            // Re-baseline on a backwards jump too: joining a session loads the
            // host's save, which can set the clock earlier than where we were.
            if (!_missionStartSet || now < _missionStart)
            {
                _missionStart = now;
                _missionStartSet = true;
            }
        }

        // ── Signals ──────────────────────────────────────────────────────────

        /// <summary>Called from any thread. Arms an error flush; the timing is
        /// resolved on the main thread in <see cref="Tick"/>.</summary>
        internal static void NoteError(string kind)
        {
            if (!Enabled) return;
            if (AnalyticsUploader.SuppressSelfReport) return;   // never recurse on our own failures
            _lastErrorKind = kind;
            System.Threading.Interlocked.Increment(ref _errorSignalTick);
            System.Threading.Interlocked.Increment(ref _errors);
        }

        private static volatile string _lastErrorKind = "";

        internal static void NoteWarning() => System.Threading.Interlocked.Increment(ref _warnings);

        /// <summary>Cumulative since session start. MetricSampler diffs these to
        /// get a per-window count; both are written from arbitrary threads, so
        /// read them through Interlocked too.</summary>
        internal static long ErrorCount   => System.Threading.Interlocked.Read(ref _errors);
        internal static long WarningCount => System.Threading.Interlocked.Read(ref _warnings);

        // ── Payload framing ──────────────────────────────────────────────────

        /// <summary>First line of every batch. Built on the uploader thread, so it
        /// reads only cached/immutable state.</summary>
        internal static string BuildHeader(int seq, string trigger, int dropped, int lines, bool capped)
        {
            var p = Plugin.Instance;
            return new Json().Obj()
                .Str("t", "h").Num("v", 1)
                .Str("i", InstallId).Str("s", SessionId).Num("q", seq)
                .Num("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Str("pv", PluginInfo.PLUGIN_VERSION)
                .Str("role", p.CfgIsHost.Value ? "host" : "client")
                .Str("tr", p.CfgTransport.Value)
                .Str("mode", p.CfgPvP.Value ? "pvp" : "coop")
                .Str("trig", trigger)
                .Str("errKind", trigger == "error" ? _lastErrorKind : null)
                .Num("drop", dropped).Num("lines", lines)
                .Bool("capped", capped)
                .End().ToString();
        }

        private static void PushSessionLine(bool end, string? reason)
        {
            var sink = LogRingSink.Active;
            if (sink == null) return;

            var j = new Json().Obj()
                .Str("t", "s").Str("k", end ? "end" : "start")
                .Num("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (end)
            {
                // dur is real seconds since the mod loaded (menu time included);
                // misDur is how much mission actually elapsed. They only match at
                // 1x compression with no time in the menu.
                j.Num("dur", Time.realtimeSinceStartup - _startedAt)
                 .Num("misDur", MissionElapsedSec)
                 .Str("reason", reason)
                 .Num("errs", _errors).Num("warns", _warnings)
                 .Num("bin", Telemetry.TotalBytesIn).Num("bout", Telemetry.TotalBytesOut);

                if (TryGetGameDateTime(out DateTime gameEnd))
                    j.Str("misEnd", gameEnd.ToString("yyyy-MM-ddTHH:mm:ss"));
                if (_missionStartSet)
                    j.Str("misStart", _missionStart.ToString("yyyy-MM-ddTHH:mm:ss"));

                try { j.Str("mission", Redactor.Scrub(Globals.currentMissionFilePath)); } catch { }
            }
            else
            {
                var p = Plugin.Instance;
                try
                {
                    j.Str("os", SystemInfo.operatingSystem)
                     .Str("cpu", SystemInfo.processorType)
                     .Str("gpu", SystemInfo.graphicsDeviceName)
                     .Num("ram", SystemInfo.systemMemorySize)
                     .Str("unity", Application.unityVersion);
                }
                catch { }

                // Half of all "it's broken" reports come down to a non-default
                // sync rate, so the tuning block is worth its bytes.
                j.Sub("cfg")
                 .Num("unitHz", p.CfgUnitStateHz.Value)
                 .Num("nearHz", p.CfgUnitStateHzNear.Value)
                 .Num("missileHz", p.CfgMissileStateHz.Value)
                 .Bool("interp", p.CfgReplicaInterpolation.Value)
                 .Bool("contactSync", p.CfgContactSync.Value)
                 .Bool("drawingSync", p.CfgDrawingSync.Value)
                 .Num("timeoutSec", p.CfgDisconnectTimeoutSec.Value)
                 .Bool("verbose", p.CfgVerboseDebug.Value)
                 .End();
            }

            sink.PushStructured(LogKind.Session, j.End().ToString());
        }
    }
}
