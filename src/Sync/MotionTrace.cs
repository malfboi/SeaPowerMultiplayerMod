using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Diagnostic CSV tracer for replica motion. Off by default; Ctrl+F11 toggles
    /// it (or [Debug] MotionTrace=true to start armed).
    ///
    /// It traces exactly ONE entity, PINNED by id when tracing starts: it latches
    /// whatever is selected at that moment (or the next thing selected) and then
    /// follows that id regardless of selection. Select the same unit on host and
    /// client, start the trace on both, then DESELECT on both - the two files
    /// describe the same unit over the same mission clock and can be lined up
    /// column-for-column, and deselecting releases the co-op ally lock so the unit
    /// can still be ordered while the trace runs. Nothing else is written, so the
    /// files stay small enough to open in a spreadsheet.
    ///
    /// Row kinds (the `kind` column):
    ///   FRAME     both   end-of-Update transform snapshot. The rendered motion.
    ///                    Comparing HOST FRAME rows against CLIENT FRAME rows for
    ///                    the same mission-second IS the jitter measurement.
    ///   HOST_SEND host   entity was included in an EntityStateBatch this tick.
    ///   HOST_HOLD host   entity was captured but suppressed (unchanged, heartbeat
    ///                    not yet due) - this is where send-cadence gaps come from.
    ///   CLI_RECV  client a host sample landed: wire values, transit, gap since the
    ///                    previous sample, and how wrong the motion model had been.
    ///   CLI_CORR  client one correction pass: pose resolution internals, the
    ///                    movement the LOCAL sim made since our last write, and the
    ///                    movement the correction added on top. Jitter is these two
    ///                    fighting; the columns separate them.
    ///   CLI_WPN   client kinematic weapon replica drive (missiles/torpedoes/bombs);
    ///                    poseMode is always DR - weapons extrapolate, never interpolate.
    ///   CLI_DECK  client carrier-relative deck puppet drive.
    ///   EVENT     client terminal/cosmetic event arrival (impact, despawn, destroy,
    ///                    gun burst) - carries how far the transform was teleported.
    ///   MARK      both   traced unit changed, trace started, mission clock snapped.
    ///
    /// Distances are METRES throughout (x/z are converted from ~67.2 m Unity units,
    /// y is already metres) so every delta column is directly comparable.
    ///
    /// Column legend (blank = not meaningful for that row kind):
    ///   missionSec  local mission clock - the common time axis between the two files.
    ///   pos*/hdg/spdKts  the unit's transform AFTER this row's work. On HOST_SEND /
    ///                    CLI_RECV these are the QUANTIZED wire values instead, so
    ///                    diffing them against FRAME shows what quantization cost.
    ///   simD*m      movement the LOCAL sim made between our last write and this frame.
    ///   corrD*m     movement the correction added on top of that this frame.
    ///   netD*m      total rendered movement since the previous row of the same kind.
    ///   simSpdKts / netSpdKts  those deltas as knots (game-time). Steady straight-line
    ///                    motion should hold netSpdKts flat; jitter is netSpdKts
    ///                    oscillating while spdKts stays constant.
    ///   pose*       the target the driver resolved: mode (INTERP/HOLD/EXTRAP/STALE/DR),
    ///                    interpolation fraction, and extrapolation age in game-seconds.
    ///   errXZm      horizontal distance from the transform to that target.
    ///   easeK       units: the correction fraction applied this frame.
    ///               CLI_WPN: metres of unapplied model error still being carried - the
    ///                    quantity that has to converge to zero.
    ///   transit     localMissionSec - hostSec at arrival. Its VARIANCE is the link
    ///                    jitter the render delay has to hide; emaDev tracks it.
    ///   gapSec      game-seconds since the previous sample for THIS entity; entInt is
    ///                    its EMA. Compare entInt against emaInt (the global batch-arrival
    ///                    EMA the render delay is actually derived from): the two
    ///                    disagreeing is the render delay being sized off the wrong
    ///                    cadence, and it shows up as poseMode sitting on EXTRAP.
    ///   poseAdvM    signed along-track movement of the TARGET since the previous frame,
    ///                    metres. Steady flight should hold this at speed x dtGame and
    ///                    positive; a NEGATIVE spike on a packet boundary is the target
    ///                    being reset backwards to a sample captured one transit ago.
    ///   netAdvM     the same projection for the rendered transform - what the eye sees.
    ///   batchN      entries in the batch this sample arrived in.
    ///   predErrM    how wrong the motion model was, graded on arrival.
    ///   sampleAge   CLI_*: real seconds since the newest sample arrived.
    ///               HOST_HOLD: real seconds until this entity's heartbeat is due.
    /// </summary>
    public static class MotionTrace
    {
        private const string Header =
            "role,kind,rt,frame,dtReal,dtGame,tc,missionSec,gameTime,"
            + "id,name,ukind,note,"
            + "posX,posY,posZ,hdg,pitch,roll,spdKts,"
            + "lat,lon,hgtM,"
            + "telegraph,rudder,cmdKts,desAlt,flags,"
            + "simDXm,simDYm,simDZm,corrDXm,corrDYm,corrDZm,netDXm,netDYm,netDZm,"
            + "simSpdKts,netSpdKts,winSpdKts,netAdvM,hdgSim,hdgCorr,hdgNet,"
            + "tick,batchN,hostSec,transit,rttMs,renderSec,renderDelay,sampleAge,gapSec,"
            + "poseMode,poseT,poseAge,poseX,poseY,poseZ,poseHdg,poseSpd,poseAdvM,"
            + "errXZm,easeK,turnRate,bufN,predErrM,emaInt,emaDev,entInt,baseBackM";

        private const float M = GeoCodec.MetresPerUnityUnit;
        private const float KnotsPerMetreSec = 1f / 0.514444f;
        private const float FlushIntervalSec = 1f;

        private static StreamWriter? _writer;
        private static string _filePath = "";
        private static bool _active;
        private static bool _armedFromConfig;
        private static float _nextFlushRealTime;

        private static int _tracedId;
        private static ObjectBase? _tracedUnit;
        private static float _nextReresolveRealTime;

        // Previous FRAME sample (rendered motion delta)
        private static Vector3 _prevFramePos;
        private static float _prevFrameHdg;
        private static bool _prevFrameValid;

        // Previous CLI_CORR post-correction pose (splits local sim from correction)
        private static Vector3 _prevCorrPos;
        private static float _prevCorrHdg;
        private static bool _prevCorrValid;

        // Previous resolved TARGET, for the along-track advance of the target itself.
        // A target that steps backwards is the single clearest jitter signature.
        private static Vector3 _prevPosePos;
        private static bool _prevPoseValid;

        // Per-entity sample cadence, measured on the host's own stamps. Fallback for
        // the weapon path, which keeps no stamp history of its own; the unit driver
        // reports its own figure and that one is preferred.
        private static double _lastRecvHostSec = double.NaN;
        private static float _entIntEma = float.NaN;

        private static readonly StringBuilder _sb = new(1024);
        private static readonly Row _row = new();

        public static bool Active => _active;
        public static string FilePath => _filePath;
        public static int TracedId => _tracedId;

        /// <summary>Cheap guard for the hot paths - only the selected unit is traced.</summary>
        public static bool IsTracing(int entityId) => _active && entityId != 0 && entityId == _tracedId;

        // ── Lifecycle ────────────────────────────────────────────────────────

        /// <summary>Called once per frame from Plugin.Update, after the replica
        /// drivers have run, so the FRAME row shows the transform the player
        /// actually sees this frame.</summary>
        public static void Tick()
        {
            if (!_armedFromConfig)
            {
                _armedFromConfig = true;
                if (Plugin.Instance.CfgMotionTrace.Value) SetEnabled(true);
            }

            if (Input.GetKeyDown(KeyCode.F11)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                SetEnabled(!_active);

            if (!_active) return;

            ResolveTracedUnit();

            var unit = _tracedUnit;
            if (unit != null)
            {
                var r = NewRow("FRAME");
                FillUnit(r, unit);

                var pos = unit.transform.position;
                float hdg = unit.transform.eulerAngles.y;
                if (_prevFrameValid)
                {
                    Vector3 d = pos - _prevFramePos;
                    r.NetDXm = d.x * M; r.NetDYm = d.y; r.NetDZm = d.z * M;
                    r.HdgNet = Mathf.DeltaAngle(_prevFrameHdg, hdg);
                    r.NetSpdKts = SpeedKts(r.NetDXm, r.NetDZm);
                    r.NetAdvM = AlongTrackM(_prevFramePos, pos, hdg);
                }
                _prevFramePos = pos;
                _prevFrameHdg = hdg;
                _prevFrameValid = true;

                r.WinSpdKts = WindowSpeedKts(MissionSeconds(), pos);
                Write(r);
            }

            float now = Time.unscaledTime;
            if (now >= _nextFlushRealTime)
            {
                _nextFlushRealTime = now + FlushIntervalSec;
                try { _writer?.Flush(); } catch { /* diagnostic only */ }
            }
        }

        public static void SetEnabled(bool on)
        {
            if (on == _active) return;

            if (!on)
            {
                Write(NewRow("MARK", "trace stopped"));
                Close();
                Plugin.Log.LogInfo($"[MotionTrace] stopped -> {_filePath}");
                return;
            }

            try
            {
                string role = Plugin.Instance.CfgIsHost.Value ? "HOST" : "CLIENT";
                string dir = Path.Combine(Application.persistentDataPath, "MPTrace");
                Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir,
                    $"spmp-trace-{role}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _writer = new StreamWriter(
                    new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8) { AutoFlush = false };
                _writer.WriteLine(Header);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MotionTrace] could not open trace file: {ex.Message}");
                _writer = null;
                return;
            }

            _active = true;
            _tracedId = 0;
            _tracedUnit = null;
            _prevFrameValid = false;
            _prevCorrValid = false;
            _prevPoseValid = false;
            _lastRecvHostSec = double.NaN;
            _entIntEma = float.NaN;
            _speedWindow.Clear();
            _nextFlushRealTime = 0f;
            _nextReresolveRealTime = 0f;
            Plugin.Log.LogInfo($"[MotionTrace] started -> {_filePath} " +
                "(pins the selected unit; if nothing is selected it pins the next selection)");
        }

        public static void Close()
        {
            _active = false;
            _tracedId = 0;
            _tracedUnit = null;
            _prevFrameValid = false;
            _prevCorrValid = false;
            _prevPoseValid = false;
            _lastRecvHostSec = double.NaN;
            _entIntEma = float.NaN;
            _speedWindow.Clear();
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* diagnostic only */ }
            _writer = null;
        }

        /// <summary>
        /// Resolve the pinned unit. The target is LATCHED from the selection once, then
        /// held by id - selection deliberately stops mattering after that.
        ///
        /// This matters in co-op: the ally lock refuses every order for a unit the
        /// remote player has selected (UnitLockManager.BlocksOrdersFor), so both players
        /// selecting the same unit to trace it leaves each side thinking the other owns
        /// it and NEITHER can order it. Latching means both players select the unit,
        /// start the trace, then deselect - the lock clears and the trace continues.
        /// </summary>
        private static void ResolveTracedUnit()
        {
            if (_tracedId == 0)
            {
                var sel = Singleton<RenderPosition>.InstanceExists(false)
                    ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
                if (sel == null || sel.IsDestroyed || sel.UniqueID == 0) return;

                _tracedId = sel.UniqueID;
                _tracedUnit = sel;
                var r = NewRow("MARK", "pinned - safe to deselect now (clears the co-op ally lock)");
                FillUnit(r, sel);
                Write(r);
                Plugin.Log.LogInfo($"[MotionTrace] pinned to {sel.name} (id {_tracedId}). " +
                    "Deselect on BOTH machines so orders work again - the trace follows the id.");
                return;
            }

            if (_tracedUnit != null && !_tracedUnit.IsDestroyed) return;

            // Lost the reference (destroyed, or re-keyed by an alignment pass). Re-resolve
            // occasionally rather than scanning every frame once it is genuinely gone.
            if (Time.unscaledTime < _nextReresolveRealTime) return;
            _nextReresolveRealTime = Time.unscaledTime + 1f;

            _tracedUnit = ReplicaRegistry.Find(_tracedId) ?? StateSerializer.FindById(_tracedId);
            if (_tracedUnit != null && _tracedUnit.IsDestroyed) _tracedUnit = null;
            _prevFrameValid = false;
        }

        // ── Host hooks ───────────────────────────────────────────────────────

        /// <summary>One streamer tick's verdict for the traced entity.</summary>
        public static void HostStream(in EntityState e, uint tick, double gameSec,
            bool included, string reason, float nextHeartbeat)
        {
            var r = NewRow(included ? "HOST_SEND" : "HOST_HOLD", reason);
            FillWire(r, in e);
            r.Tick = tick;
            r.HostSec = gameSec;
            if (!included) r.SampleAge = nextHeartbeat - Time.unscaledTime; // seconds until heartbeat
            Write(r);
        }

        // ── Client hooks ─────────────────────────────────────────────────────

        /// <summary>A host sample landed for the traced entity.</summary>
        public static void ClientReceive(in EntityState e, uint tick, int batchCount, double hostSec,
            double localMissionSec, double gapSec, float predErrM,
            int bufN, float turnRate, float emaIntervalGameSec, float emaDevGameSec,
            float entIntervalGameSec)
        {
            // Per-entity cadence and stamp ordering, measured here so it covers the
            // weapon path too (which keeps no stamp history of its own).
            string note = "";
            if (!double.IsNaN(_lastRecvHostSec))
            {
                double d = hostSec - _lastRecvHostSec;
                if (d <= 0d) note = "REORDERED/DUP";
                else
                {
                    if (double.IsNaN(gapSec)) gapSec = d;
                    _entIntEma = float.IsNaN(_entIntEma) ? (float)d : Mathf.Lerp(_entIntEma, (float)d, 0.1f);
                }
            }
            if (double.IsNaN(_lastRecvHostSec) || hostSec > _lastRecvHostSec) _lastRecvHostSec = hostSec;

            var r = NewRow("CLI_RECV", note);
            FillWire(r, in e);
            r.Tick = tick;
            r.BatchN = batchCount;
            r.HostSec = hostSec;
            r.Transit = localMissionSec - hostSec;
            r.RttMs = NetworkManager.Instance.LastRttMs;
            r.GapSec = gapSec;
            // Prefer the driver's own figure (it is the one the render delay is sized
            // from); fall back to the tracer's when the driver keeps none, as for weapons.
            r.EntInt = float.IsNaN(entIntervalGameSec) || entIntervalGameSec <= 0f
                ? _entIntEma : entIntervalGameSec;
            r.PredErrM = predErrM;   // metres
            r.BufN = bufN;
            r.TurnRate = turnRate;
            r.EmaInt = emaIntervalGameSec;
            r.EmaDev = emaDevGameSec;
            Write(r);
        }

        /// <summary>
        /// One correction pass. <paramref name="prePos"/> is the transform BEFORE we
        /// touched it this frame, so (prePos - previous post-correction pos) is what
        /// the local sim did on its own and (post - prePos) is what the correction
        /// added. Jitter is those two disagreeing.
        /// </summary>
        public static void ClientCorrection(ObjectBase unit,
            Vector3 prePos, float preHdg,
            Vector3 posePos, float poseHdg, float poseSpd,
            string poseMode, float poseT, float poseAge,
            double nowMissionSec, double renderMissionSec, float renderDelay,
            float sampleAgeReal, float easeK, float turnRate, int bufN,
            double newestHostSec, string note)
        {
            var r = NewRow("CLI_CORR", note);
            FillUnit(r, unit);

            Vector3 post = unit.transform.position;
            float postHdg = unit.transform.eulerAngles.y;

            if (_prevCorrValid)
            {
                Vector3 sim = prePos - _prevCorrPos;
                r.SimDXm = sim.x * M; r.SimDYm = sim.y; r.SimDZm = sim.z * M;
                r.SimSpdKts = SpeedKts(r.SimDXm, r.SimDZm);
                r.HdgSim = Mathf.DeltaAngle(_prevCorrHdg, preHdg);

                Vector3 net = post - _prevCorrPos;
                r.NetDXm = net.x * M; r.NetDYm = net.y; r.NetDZm = net.z * M;
                r.NetSpdKts = SpeedKts(r.NetDXm, r.NetDZm);
                r.NetAdvM = AlongTrackM(_prevCorrPos, post, poseHdg);
                r.HdgNet = Mathf.DeltaAngle(_prevCorrHdg, postHdg);
            }
            Vector3 corr = post - prePos;
            r.CorrDXm = corr.x * M; r.CorrDYm = corr.y; r.CorrDZm = corr.z * M;
            r.HdgCorr = Mathf.DeltaAngle(preHdg, postHdg);

            _prevCorrPos = post;
            _prevCorrHdg = postHdg;
            _prevCorrValid = true;

            r.PoseMode = poseMode;
            r.PoseT = poseT;
            r.PoseAge = poseAge;
            r.PoseX = posePos.x; r.PoseY = posePos.y; r.PoseZ = posePos.z;
            r.PoseHdg = poseHdg;
            r.PoseSpd = poseSpd;
            if (_prevPoseValid) r.PoseAdvM = AlongTrackM(_prevPosePos, posePos, poseHdg);
            _prevPosePos = posePos;
            _prevPoseValid = true;
            r.EntInt = _entIntEma;

            float ex = (posePos.x - prePos.x) * M;
            float ez = (posePos.z - prePos.z) * M;
            r.ErrXZm = Mathf.Sqrt(ex * ex + ez * ez);

            r.MissionSecOverrideD = nowMissionSec;
            r.RenderSec = renderMissionSec;
            r.RenderDelay = renderDelay;
            r.SampleAge = sampleAgeReal;
            r.HostSec = newestHostSec;
            r.EaseK = easeK;
            r.TurnRate = turnRate;
            r.BufN = bufN;
            r.RttMs = NetworkManager.Instance.LastRttMs;
            Write(r);
        }

        /// <summary>
        /// Kinematic weapon replica drive (missile/torpedo/bomb). The driver holds one
        /// sample and dead-reckons off it, so `poseAdvM` going negative on a packet
        /// boundary is the target being reset back to a position captured one transit
        /// ago - and `poseAge`/`poseT` diverging is the extrapolation cap biting in
        /// game-seconds rather than real ones.
        /// </summary>
        public static void ClientWeapon(ObjectBase wb, Vector3 prePos, Vector3 target, Vector3 basePos,
            float offsetM, float gameTimeSinceSample, float speedKts, float headingDeg, float pitchDeg)
        {
            var r = NewRow("CLI_WPN");
            FillUnit(r, wb);

            Vector3 post = wb.transform.position;
            if (_prevCorrValid)
            {
                Vector3 sim = prePos - _prevCorrPos;
                r.SimDXm = sim.x * M; r.SimDYm = sim.y; r.SimDZm = sim.z * M;
                r.SimSpdKts = SpeedKts(r.SimDXm, r.SimDZm);

                Vector3 net = post - _prevCorrPos;
                r.NetDXm = net.x * M; r.NetDYm = net.y; r.NetDZm = net.z * M;
                r.NetSpdKts = SpeedKts(r.NetDXm, r.NetDZm);
                r.NetAdvM = AlongTrackM(_prevCorrPos, post, headingDeg);
            }
            Vector3 corr = post - prePos;
            r.CorrDXm = corr.x * M; r.CorrDYm = corr.y; r.CorrDZm = corr.z * M;

            _prevCorrPos = post;
            _prevCorrHdg = wb.transform.eulerAngles.y;
            _prevCorrValid = true;

            r.PoseMode = "DR";
            r.PoseAge = gameTimeSinceSample;   // game-seconds since the packet arrived
            r.EaseK = offsetM;                 // metres of unapplied model error, must converge to 0
            r.PoseX = target.x; r.PoseY = target.y; r.PoseZ = target.z;
            r.PoseHdg = headingDeg;
            r.PoseSpd = speedKts;
            r.Pitch = pitchDeg;
            if (_prevPoseValid) r.PoseAdvM = AlongTrackM(_prevPosePos, target, headingDeg);
            _prevPosePos = target;
            _prevPoseValid = true;
            r.EntInt = _entIntEma;

            // Along-track offset from the rendered weapon to the raw sample position -
            // the point the target snaps back to on every packet. Normally negative
            // (the sample is one transit behind), and its magnitude IS the amplitude
            // of the reset, so it should track speed x transit.
            r.BaseBackM = AlongTrackM(post, basePos, headingDeg);

            float ex = (target.x - prePos.x) * M;
            float ez = (target.z - prePos.z) * M;
            r.ErrXZm = Mathf.Sqrt(ex * ex + ez * ez);
            r.RttMs = NetworkManager.Instance.LastRttMs;
            Write(r);
        }

        /// <summary>A sample was discarded before it reached the buffer (reordered or
        /// duplicate host stamp). Units drop these; weapons do not, so a run of these
        /// on a unit is the protection the weapon path is missing.</summary>
        public static void SampleDropped(int entityId, uint tick, double hostSec, double newestHostSec)
        {
            var r = NewRow("CLI_RECV", "DROPPED reordered/dup");
            r.Id = entityId;
            r.Tick = tick;
            r.HostSec = hostSec;
            r.GapSec = hostSec - newestHostSec;
            r.RttMs = NetworkManager.Instance.LastRttMs;
            Write(r);
        }

        /// <summary>
        /// Carrier-relative deck puppet drive. The driver works in carrier-local space;
        /// everything here is converted to world so the columns mean the same thing they
        /// do on every other row. easeK is the fixed per-frame fraction (not
        /// frame-rate-independent), so pairing it with dtReal shows the FPS dependence.
        /// </summary>
        public static void DeckPuppet(ObjectBase unit, ObjectBase carrier, Vector3 preLocal,
            Vector3 targetLocal, float targetYawDeg, float lerpFactor)
        {
            var r = NewRow("CLI_DECK", $"carrier={carrier.UniqueID}");
            FillUnit(r, unit);

            var ctr = carrier.transform;
            Vector3 preWorld = ctr.TransformPoint(preLocal);
            Vector3 target = ctr.TransformPoint(targetLocal);
            Vector3 post = unit.transform.position;

            Vector3 corr = post - preWorld;
            r.CorrDXm = corr.x * M; r.CorrDYm = corr.y; r.CorrDZm = corr.z * M;
            if (_prevCorrValid)
            {
                Vector3 net = post - _prevCorrPos;
                r.NetDXm = net.x * M; r.NetDYm = net.y; r.NetDZm = net.z * M;
                r.NetSpdKts = SpeedKts(r.NetDXm, r.NetDZm);
            }
            _prevCorrPos = post;
            _prevCorrHdg = unit.transform.eulerAngles.y;
            _prevCorrValid = true;

            r.PoseMode = "DECK";
            r.PoseX = target.x; r.PoseY = target.y; r.PoseZ = target.z;
            r.PoseHdg = targetYawDeg + ctr.eulerAngles.y;
            r.EaseK = lerpFactor;

            float ex = (target.x - preWorld.x) * M;
            float ez = (target.z - preWorld.z) * M;
            r.ErrXZm = Mathf.Sqrt(ex * ex + ez * ez);
            Write(r);
        }

        /// <summary>
        /// A terminal or cosmetic event landed. These carry no host timestamp, so they
        /// are applied the instant they arrive - <paramref name="eventPos"/> against the
        /// replica's current transform is exactly the teleport the player sees at the
        /// end of a flight.
        /// </summary>
        public static void TerminalEvent(string what, int entityId, ObjectBase? unit,
            Vector3 eventPos, string detail)
        {
            var r = NewRow("EVENT", detail);
            r.Id = entityId;
            r.PoseMode = what;
            r.PoseX = eventPos.x; r.PoseY = eventPos.y; r.PoseZ = eventPos.z;
            if (unit != null)
            {
                FillUnit(r, unit);
                r.Id = entityId;
                Vector3 pos = unit.transform.position;
                r.CorrDXm = (eventPos.x - pos.x) * M;
                r.CorrDYm = eventPos.y - pos.y;
                r.CorrDZm = (eventPos.z - pos.z) * M;
                r.ErrXZm = Mathf.Sqrt(r.CorrDXm * r.CorrDXm + r.CorrDZm * r.CorrDZm);
                r.BaseBackM = AlongTrackM(pos, eventPos, unit.transform.eulerAngles.y);
            }
            r.RttMs = NetworkManager.Instance.LastRttMs;
            Write(r);
        }

        /// <summary>
        /// The client's mission clock was snapped to the host's. Every buffered
        /// snapshot stamp and render-time comparison either side of this row is on a
        /// different clock, so motion immediately after it is not comparable to motion
        /// before it.
        /// </summary>
        public static void ClockSnap(double beforeMissionSec, double afterMissionSec, float newTc)
        {
            if (!_active) return;
            var r = NewRow("MARK", $"mission clock snapped {beforeMissionSec:F3} -> {afterMissionSec:F3} " +
                $"(delta {afterMissionSec - beforeMissionSec:F3}s, tc {newTc:F1})");
            r.HostSec = afterMissionSec;
            r.MissionSecOverrideD = beforeMissionSec;
            r.RttMs = NetworkManager.Instance.LastRttMs;
            Write(r);
            // Deltas across the discontinuity are meaningless.
            _prevFrameValid = false;
            _prevCorrValid = false;
            _prevPoseValid = false;
            _lastRecvHostSec = double.NaN;
        }

        // ── Row assembly ─────────────────────────────────────────────────────

        /// <summary>Signed displacement along <paramref name="headingDeg"/>, in metres.
        /// Negative means it moved backwards along its own track.</summary>
        private static float AlongTrackM(Vector3 from, Vector3 to, float headingDeg)
        {
            if (float.IsNaN(headingDeg)) return float.NaN;
            float rad = headingDeg * Mathf.Deg2Rad;
            return ((to.x - from.x) * Mathf.Sin(rad) + (to.z - from.z) * Mathf.Cos(rad)) * M;
        }

        private static float SpeedKts(float dxMetres, float dzMetres)
        {
            float dt = GameTime.deltaTime;
            if (dt <= 0.0001f) return float.NaN;
            return Mathf.Sqrt(dxMetres * dxMetres + dzMetres * dzMetres) / dt * KnotsPerMetreSec;
        }

        // ── Windowed speed ───────────────────────────────────────────────────
        //
        // netSpdKts is a per-FRAME difference, and unit transforms are written by
        // physics at ~30 Hz while this samples every Update at whatever the render
        // rate is (~144 fps in practice). Four frames in five therefore show zero
        // movement and the fifth shows five frames' worth: the mean is right and the
        // variance is meaningless. Measured on a real session, the HOST's per-frame
        // figure was as noisy as the client's (sd ~25 kt on a 22 kt ship), which is
        // proof the noise is sampling, not replication.
        //
        // This measures displacement over a fixed window of game-time instead, which
        // spans several physics ticks and so reads the motion the eye actually sees.
        private const float SpeedWindowGameSec = 0.25f;
        // While the sim is paused the mission clock stops, so the age test below can
        // never retire an entry - without a hard cap the queue would grow every frame
        // for as long as the game sits paused.
        private const int MaxWindowSamples = 512;
        private static readonly Queue<(double missionSec, Vector3 pos)> _speedWindow = new();

        private static float WindowSpeedKts(double missionSec, Vector3 pos)
        {
            if (double.IsNaN(missionSec)) return float.NaN;

            _speedWindow.Enqueue((missionSec, pos));
            // Keep the oldest entry that is still at least a window old, so the
            // measurement always spans the full window rather than shrinking to nothing.
            while (_speedWindow.Count > 2
                   && (missionSec - _speedWindow.Peek().missionSec > SpeedWindowGameSec
                       || _speedWindow.Count > MaxWindowSamples))
                _speedWindow.Dequeue();

            var oldest = _speedWindow.Peek();
            double dt = missionSec - oldest.missionSec;
            if (dt < SpeedWindowGameSec * 0.5f) return float.NaN;  // not enough history yet

            float dx = (pos.x - oldest.pos.x) * M;
            float dz = (pos.z - oldest.pos.z) * M;
            return Mathf.Sqrt(dx * dx + dz * dz) / (float)dt * KnotsPerMetreSec;
        }

        private static Row NewRow(string kind, string note = "")
        {
            _row.Reset();
            _row.Kind = kind;
            _row.Note = note;
            return _row;
        }

        private static void FillUnit(Row r, ObjectBase unit)
        {
            var tr = unit.transform;
            r.Id = unit.UniqueID;
            r.Name = unit.name;
            r.UKind = unit._type.ToString();
            r.PosX = tr.position.x; r.PosY = tr.position.y; r.PosZ = tr.position.z;
            var eul = tr.eulerAngles;
            r.Hdg = eul.y;
            r.Pitch = Utils.WrapAngle(eul.x);
            r.Roll = Utils.WrapAngle(eul.z);
            r.SpdKts = unit._velocityInKnots;

            var geo = GeoCodec.ToGeo(tr.position);
            r.Lat = geo._latitude;
            r.Lon = geo._longitude;
            r.HgtM = (float)geo._height;

            r.Telegraph = unit.getTelegraph();
            if (unit is Vessel v) r.Rudder = StateSerializer.GetRudderAngle(v);
            r.CmdKts = StateSerializer.CustomCommandKnots(unit);
            if (unit is Aircraft || unit is Helicopter || unit is Submarine)
                r.DesAlt = (float)unit.DesiredAltitude.Value;
        }

        /// <summary>Fill from a wire record (host send / client receive) - these are
        /// the QUANTIZED values, so a diff against the FRAME rows shows exactly what
        /// quantization cost.</summary>
        private static void FillWire(Row r, in EntityState e)
        {
            r.Id = e.EntityId;
            r.UKind = e.Kind.ToString();
            r.Lat = e.LatDeg;
            r.Lon = e.LonDeg;
            r.HgtM = e.HeightM;
            r.Hdg = GeoCodec.UnpackHeading(e.HeadingQ);
            r.Pitch = GeoCodec.UnpackAngleCdeg(e.PitchQ);
            r.Roll = GeoCodec.UnpackAngleCdeg(e.RollQ);
            r.SpdKts = GeoCodec.UnpackSpeedKts(e.SpeedQ);
            r.Telegraph = e.Telegraph;
            r.Rudder = e.RudderQ / 2f;
            r.CmdKts = (e.Flags & EntityState.FlagCustomSpeed) != 0 ? e.CmdSpeedQ / 10f : float.NaN;
            r.DesAlt = e.DesiredAlt;
            r.Flags = e.Flags;

            // Wire lat/lon in Unity space, so poseX/Z line up with the FRAME columns
            Vector3 u = GeoCodec.ToUnity(e.LatDeg, e.LonDeg, e.HeightM);
            r.PosX = u.x; r.PosY = e.HeightM; r.PosZ = u.z;
        }

        private static double MissionSeconds() => TimeSyncManager.MissionSeconds();

        private static void Write(Row r)
        {
            var w = _writer;
            if (w == null) return;

            _sb.Length = 0;
            A(Plugin.Instance.CfgIsHost.Value ? "HOST" : "CLIENT");
            A(r.Kind);
            A(F(Time.unscaledTime, 4));
            A(Time.frameCount.ToString(CultureInfo.InvariantCulture));
            A(F(Time.unscaledDeltaTime, 5));
            A(F(GameTime.deltaTime, 5));
            A(F(GameTime.TimeCompression, 2));
            A(D5(double.IsNaN(r.MissionSecOverrideD) ? MissionSeconds() : r.MissionSecOverrideD));
            A(F(GameTime.time, 5));

            A(r.Id.ToString(CultureInfo.InvariantCulture));
            A(Csv(r.Name));
            A(r.UKind);
            A(Csv(r.Note));

            A(F(r.PosX, 4)); A(F(r.PosY, 3)); A(F(r.PosZ, 4));
            A(F(r.Hdg, 4)); A(F(r.Pitch, 3)); A(F(r.Roll, 3)); A(F(r.SpdKts, 3));

            A(D(r.Lat)); A(D(r.Lon)); A(F(r.HgtM, 3));

            A(I(r.Telegraph)); A(F(r.Rudder, 2)); A(F(r.CmdKts, 2)); A(F(r.DesAlt, 2)); A(I(r.Flags));

            A(F(r.SimDXm, 4)); A(F(r.SimDYm, 4)); A(F(r.SimDZm, 4));
            A(F(r.CorrDXm, 4)); A(F(r.CorrDYm, 4)); A(F(r.CorrDZm, 4));
            A(F(r.NetDXm, 4)); A(F(r.NetDYm, 4)); A(F(r.NetDZm, 4));
            A(F(r.SimSpdKts, 3)); A(F(r.NetSpdKts, 3)); A(F(r.WinSpdKts, 3)); A(F(r.NetAdvM, 4));
            A(F(r.HdgSim, 4)); A(F(r.HdgCorr, 4)); A(F(r.HdgNet, 4));

            A(r.Tick < 0 ? "" : r.Tick.ToString(CultureInfo.InvariantCulture));
            A(I(r.BatchN));
            A(D5(r.HostSec)); A(D5(r.Transit)); A(I(r.RttMs));
            A(D5(r.RenderSec)); A(F(r.RenderDelay, 5)); A(F(r.SampleAge, 4)); A(D5(r.GapSec));

            A(r.PoseMode);
            A(F(r.PoseT, 4)); A(F(r.PoseAge, 4));
            A(F(r.PoseX, 4)); A(F(r.PoseY, 3)); A(F(r.PoseZ, 4));
            A(F(r.PoseHdg, 4)); A(F(r.PoseSpd, 3)); A(F(r.PoseAdvM, 4));

            A(F(r.ErrXZm, 4)); A(F(r.EaseK, 5)); A(F(r.TurnRate, 4)); A(I(r.BufN));
            A(F(r.PredErrM, 4)); A(F(r.EmaInt, 4)); A(F(r.EmaDev, 4));
            A(F(r.EntInt, 4)); A(F(r.BaseBackM, 4));

            _sb.Length -= 1; // trailing comma
            try { w.WriteLine(_sb.ToString()); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MotionTrace] write failed, tracing off: {ex.Message}");
                Close();
            }
        }

        private static void A(string s) { _sb.Append(s); _sb.Append(','); }

        private static string F(float v, int dp)
            => float.IsNaN(v) || float.IsInfinity(v)
                ? "" : v.ToString("F" + dp, CultureInfo.InvariantCulture);

        private static string D(double v)
            => double.IsNaN(v) ? "" : v.ToString("F7", CultureInfo.InvariantCulture);

        /// <summary>Clock columns: 5 dp, so sub-millisecond motion of the render time is visible.</summary>
        private static string D5(double v)
            => double.IsNaN(v) ? "" : v.ToString("F5", CultureInfo.InvariantCulture);

        private static string I(int v)
            => v == int.MinValue ? "" : v.ToString(CultureInfo.InvariantCulture);

        private static string Csv(string s)
            => s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        /// <summary>Reused row buffer - one row is built and written at a time.</summary>
        private sealed class Row
        {
            public string Kind = "", Note = "", Name = "", UKind = "", PoseMode = "";
            public int Id;
            public float PosX, PosY, PosZ, Hdg, Pitch, Roll, SpdKts;
            public double Lat, Lon;
            public float HgtM;
            public int Telegraph, Flags;
            public float Rudder, CmdKts, DesAlt;
            public float SimDXm, SimDYm, SimDZm, CorrDXm, CorrDYm, CorrDZm, NetDXm, NetDYm, NetDZm;
            public float SimSpdKts, NetSpdKts, WinSpdKts, NetAdvM, HdgSim, HdgCorr, HdgNet;
            public long Tick;
            public int RttMs, BatchN;
            public double HostSec, Transit, RenderSec, GapSec, MissionSecOverrideD;
            public float RenderDelay, SampleAge;
            public float PoseT, PoseAge, PoseX, PoseY, PoseZ, PoseHdg, PoseSpd, PoseAdvM;
            public float ErrXZm, EaseK, TurnRate, PredErrM, EmaInt, EmaDev, EntInt, BaseBackM;
            public int BufN;

            public void Reset()
            {
                Kind = Note = Name = UKind = PoseMode = "";
                Id = 0;
                Telegraph = Flags = RttMs = BufN = BatchN = int.MinValue;
                Tick = -1;
                Lat = Lon = double.NaN;
                PosX = PosY = PosZ = Hdg = Pitch = Roll = SpdKts = float.NaN;
                HgtM = Rudder = CmdKts = DesAlt = float.NaN;
                SimDXm = SimDYm = SimDZm = CorrDXm = CorrDYm = CorrDZm = float.NaN;
                NetDXm = NetDYm = NetDZm = SimSpdKts = NetSpdKts = WinSpdKts = NetAdvM = float.NaN;
                HdgSim = HdgCorr = HdgNet = float.NaN;
                HostSec = Transit = RenderSec = GapSec = MissionSecOverrideD = double.NaN;
                RenderDelay = SampleAge = float.NaN;
                PoseT = PoseAge = PoseX = PoseY = PoseZ = PoseHdg = PoseSpd = PoseAdvM = float.NaN;
                ErrXZm = EaseK = TurnRate = PredErrM = EmaInt = EmaDev = float.NaN;
                EntInt = BaseBackM = float.NaN;
            }
        }
    }
}
