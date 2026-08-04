using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Batches the ring into gzipped NDJSON and POSTs it, on one dedicated
    /// background thread.
    ///
    /// Why a thread and HttpWebRequest rather than a UnityWebRequest coroutine:
    /// a coroutine cannot complete a final flush at quit, because Unity tears
    /// coroutines down around OnApplicationQuit - and the session-end batch is
    /// the most valuable one there is. A background thread can be joined with a
    /// bounded grace period. HttpWebRequest also lives in System.dll, which is
    /// already referenced, so this adds no assembly reference; on the Anchor
    /// Chain path an unresolvable reference means the whole mod is skipped.
    ///
    /// TLS is not a concern on this runtime (Unity 6000's Mono uses UnityTls over
    /// SChannel - the game's own Sentry posts over HTTPS from the same process).
    /// We do NOT install a ServerCertificateValidationCallback: it is
    /// process-global and would weaken the game's own traffic.
    /// </summary>
    internal static class AnalyticsUploader
    {
        // Caps. These are the whole bandwidth story - see PRIVACY.md.
        private const int MaxBatchBytes        = 512 * 1024;   // compressed
        private const int MaxSessionBytes      = 5 * 1024 * 1024;
        private const int MaxBatchesPerSession = 200;
        private const int IntervalSec          = 60;
        private const int RequestTimeoutMs     = 15000;
        private const int MaxAttempts          = 3;

        private static Thread? _thread;
        private static readonly AutoResetEvent _wake = new(false);
        private static volatile bool _running;

        private static readonly object _flagGate = new();
        private static bool   _flushPending;
        private static string _flushReason = "interval";
        private static int    _seq;
        private static long   _sessionBytes;
        private static int    _batches;

        /// <summary>Set while the uploader is logging about itself. Without this a
        /// failed upload logged at Error would trigger an error flush, which would
        /// fail, which would log... </summary>
        [ThreadStatic] internal static bool SuppressSelfReport;

        internal static void Start()
        {
            if (_running) return;
            _running = true;
            _sendFinal = true;
            _postAllowed = true;
            _seq = 0;
            _sessionBytes = 0;
            _batches = 0;

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            _thread = new Thread(Loop)
            {
                IsBackground = true,   // never blocks process exit
                Name = "SPMP-Analytics",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        /// <param name="finalFlush">False when the player has just revoked consent.
        /// Turning diagnostics off must produce zero further traffic, so the
        /// pending ring is abandoned rather than uploaded.</param>
        internal static void Stop(TimeSpan grace, bool finalFlush = true)
        {
            if (!_running) return;
            _running = false;
            _sendFinal = finalFlush;
            _postAllowed = finalFlush;
            _wake.Set();
            try { _thread?.Join(grace); } catch { }
            _thread = null;
        }

        private static volatile bool _sendFinal   = true;
        private static volatile bool _postAllowed = true;

        internal static void RequestFlush(string reason)
        {
            lock (_flagGate)
            {
                _flushPending = true;
                // First reason wins: "error" must not be overwritten by a later
                // "interval" before the batch is built.
                if (_flushReason == "interval") _flushReason = reason;
            }
            _wake.Set();
        }

        // ── Worker thread ────────────────────────────────────────────────────

        private static void Loop()
        {
            while (_running)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(IntervalSec));
                if (!_running) break;   // woken by Stop, not by a flush request

                bool pending;
                string reason;
                lock (_flagGate)
                {
                    pending = _flushPending;
                    reason = _flushReason;
                    _flushPending = false;
                    _flushReason = "interval";
                }

                // Idle in the menu produces no traffic at all: that is both the
                // right behaviour and what keeps request volume inside the free
                // tier (see PRIVACY.md).
                if (!pending && !Analytics.LinkEstablished && !Analytics.AlwaysUpload) continue;

                try { BuildAndSend(pending ? reason : "interval", MaxAttempts); }
                catch (Exception ex) { SelfLog("batch failed: " + ex.Message); }
            }

            // Final drain on shutdown - the session_end line lives here, and it is
            // the batch most worth getting out. Single attempt: the game is
            // closing and Stop() only waits a few seconds. Skipped entirely when
            // consent was just revoked.
            if (_sendFinal)
            {
                try { BuildAndSend("session_end", 1); }
                catch (Exception ex) { SelfLog("final batch failed: " + ex.Message); }
            }
        }

        private static void BuildAndSend(string trigger, int maxAttempts)
        {
            var sink = LogRingSink.Active;
            if (sink == null) return;

            var records = sink.Drain(out int dropped);
            // A header-only batch tells us nothing and still costs a request.
            if (records.Count == 0) return;

            if (_batches >= MaxBatchesPerSession) return;
            bool capped = _sessionBytes >= MaxSessionBytes;

            var sb = new StringBuilder(64 * 1024);
            int seq = Interlocked.Increment(ref _seq);

            sb.Append(Analytics.BuildHeader(seq, trigger, dropped, records.Count, capped)).Append('\n');

            foreach (var r in records)
            {
                // Past the session byte cap we keep the metric series - which is
                // small and is what the aggregate analysis needs - and drop the
                // log bodies, which are what actually cost bandwidth.
                if (capped && r.Kind != LogKind.Metric && r.Kind != LogKind.Session) continue;
                if (!ShouldUpload(r, trigger)) continue;

                sb.Append(Serialize(r)).Append('\n');
                if (sb.Length > 4 * 1024 * 1024) break;   // paranoia: pre-compression bound
            }

            byte[] payload = Gzip(sb.ToString());
            if (payload.Length > MaxBatchBytes)
            {
                // Rather than split (which would need the ring back), drop the log
                // bodies and resend just the structured lines. A batch this large
                // is an error storm, and the metrics are what survive it.
                var trimmed = new StringBuilder(16 * 1024);
                trimmed.Append(Analytics.BuildHeader(seq, trigger, dropped + records.Count, records.Count, true)).Append('\n');
                foreach (var r in records)
                    if (r.Kind == LogKind.Metric || r.Kind == LogKind.Session)
                        trimmed.Append(Serialize(r)).Append('\n');
                payload = Gzip(trimmed.ToString());
            }

            _batches++;
            _sessionBytes += payload.Length;
            Post(payload, seq, maxAttempts);
        }

        /// <summary>Debug lines are the extra detail the opt-in buys, but shipping
        /// them every minute is most of the bandwidth for least of the value. They
        /// ride along only in an error batch, where the run-up is the point.</summary>
        private static bool ShouldUpload(in LogRecord r, string trigger)
        {
            if (r.Kind != LogKind.Log) return true;
            const byte debugBit = 32;   // BepInEx LogLevel.Debug
            if (r.Level == debugBit) return trigger != "interval";
            return true;
        }

        private static string Serialize(in LogRecord r)
        {
            var j = new Json().Obj();
            switch (r.Kind)
            {
                case LogKind.Exception:
                    j.Str("t", "x").Num("ts", r.UtcMs).Str("m", r.Message)
                     .Str("st", r.Stack).Num("n", r.Repeat);
                    break;

                case LogKind.Metric:
                case LogKind.Session:
                    // Already complete JSON objects; emit verbatim.
                    return r.Message;

                default:
                    j.Str("t", "l").Num("ts", r.UtcMs).Str("lv", LevelChar(r.Level))
                     .Str("src", r.Source).Str("m", r.Message);
                    break;
            }
            return j.End().ToString();
        }

        private static string LevelChar(byte level) => level switch
        {
            1  => "F",
            2  => "E",
            4  => "W",
            8  => "M",
            16 => "I",
            32 => "D",
            _  => "?",
        };

        private static byte[] Gzip(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
                gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private static void Post(byte[] gzipped, int seq, int maxAttempts)
        {
            string url = Analytics.Endpoint;
            if (string.IsNullOrEmpty(url) || !_postAllowed) return;

            int delayMs = 4000;
            for (int attempt = 1; attempt <= maxAttempts && _postAllowed; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/octet-stream";
                    req.Headers["Content-Encoding"] = "gzip";
                    req.Headers["X-SPMP-Version"] = PluginInfo.PLUGIN_VERSION;
                    req.Headers["X-SPMP-Install"] = Analytics.InstallId;
                    req.Headers["X-SPMP-Session"] = Analytics.SessionId;
                    req.Headers["X-SPMP-Seq"]     = seq.ToString();
                    req.UserAgent = "SeapowerMP/" + PluginInfo.PLUGIN_VERSION;
                    req.Timeout = RequestTimeoutMs;
                    req.ReadWriteTimeout = RequestTimeoutMs;
                    req.ServicePoint.Expect100Continue = false;
                    req.ContentLength = gzipped.Length;

                    using (var s = req.GetRequestStream()) s.Write(gzipped, 0, gzipped.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        SelfLog($"uploaded {gzipped.Length}B -> {(int)resp.StatusCode}");
                        return;
                    }
                }
                catch (WebException wex)
                {
                    var resp = wex.Response as HttpWebResponse;
                    int code = resp != null ? (int)resp.StatusCode : 0;

                    // A schema or auth error will never succeed. Retrying it
                    // forever is how a client gets itself rate-limited out.
                    if (code >= 400 && code < 500 && code != 408 && code != 429)
                    {
                        SelfLog($"upload rejected ({code}); dropping batch");
                        return;
                    }
                    SelfLog($"upload attempt {attempt} failed ({(code == 0 ? wex.Status.ToString() : code.ToString())})");
                }
                catch (Exception ex)
                {
                    SelfLog($"upload attempt {attempt} failed: {ex.Message}");
                }

                if (attempt < maxAttempts && _postAllowed)
                {
                    Thread.Sleep(delayMs);
                    delayMs *= 4;
                }
            }
        }

        /// <summary>Analytics must never be a source of user-visible noise, and an
        /// Error here would recurse into an error flush. Debug only, always.</summary>
        private static void SelfLog(string msg)
        {
            SuppressSelfReport = true;
            try { Plugin.Log?.LogDebug("[Analytics] " + msg); }
            catch { }
            finally { SuppressSelfReport = false; }
        }
    }
}
