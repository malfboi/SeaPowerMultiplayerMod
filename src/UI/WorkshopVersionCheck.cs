using System;
using Steamworks;

namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// Detects that the Workshop has a newer build than the one running.
    ///
    /// Detection only - it never calls DownloadItem. Anchor Chain loads mods with
    /// Assembly.LoadFile, so for a subscriber the DLL being replaced is the one
    /// mapped into this process; and starting a download puts the item into
    /// k_EItemStateDownloading, which FileManager.GetSearchDirectories() skips
    /// outright. Either way the preloader only runs once per process, so nothing
    /// takes effect without a restart. Telling the player is the useful part.
    ///
    /// Two sources, because neither alone is enough:
    ///   - GetItemState is instant but only as fresh as the Steam client's last
    ///     refresh, which on a machine that never restarts Steam can be days old.
    ///   - A UGC details query asks the server, so it is correct regardless.
    /// </summary>
    internal static class WorkshopVersionCheck
    {
        /// <summary>True once we know the Workshop copy is newer than this one.</summary>
        internal static bool UpdateAvailable { get; private set; }

        private static CallResult<SteamUGCQueryCompleted_t>? _pending;
        private static UGCQueryHandle_t _handle = UGCQueryHandle_t.Invalid;
        private static PublishedFileId_t _fileId;
        private static bool _started;

        internal static void Start()
        {
            if (_started) return;
            _started = true;

            if (!OverlayLinks.WorkshopFileId.HasValue)
            {
                Plugin.Log.LogInfo("[Workshop] Not a workshop install - skipping the update check.");
                return;
            }

            _fileId = new PublishedFileId_t(OverlayLinks.WorkshopFileId.Value);

            try
            {
                // Free local answer first: when Steam already knows, this costs
                // nothing and covers the case where the query below fails.
                if (((EItemState)SteamUGC.GetItemState(_fileId)).HasFlag(EItemState.k_EItemStateNeedsUpdate))
                {
                    UpdateAvailable = true;
                    Plugin.Log.LogInfo("[Workshop] Steam already flags this mod as needing an update.");
                }

                _handle = SteamUGC.CreateQueryUGCDetailsRequest(new[] { _fileId }, 1);
                if (_handle == UGCQueryHandle_t.Invalid)
                {
                    Plugin.Log.LogWarning("[Workshop] Could not create the UGC query - skipping the server check.");
                    return;
                }

                // 0 = do not answer from the client's cache. This is the whole
                // point of the query: the cache is what goes stale.
                SteamUGC.SetAllowCachedResponse(_handle, 0);

                var call = SteamUGC.SendQueryUGCRequest(_handle);
                if (call == SteamAPICall_t.Invalid)
                {
                    Plugin.Log.LogWarning("[Workshop] UGC query could not be sent - skipping the server check.");
                    Release();
                    return;
                }

                // The game pumps SteamAPI.RunCallbacks() every frame, so this
                // completes on its own (see SteamLobbyManager).
                _pending = CallResult<SteamUGCQueryCompleted_t>.Create(OnQueryCompleted);
                _pending.Set(call);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Workshop] Update check skipped: {ex.Message}");
                Release();
            }
        }

        private static void OnQueryCompleted(SteamUGCQueryCompleted_t result, bool ioFailure)
        {
            try
            {
                // Never prompt off a failed lookup - a false "you are outdated"
                // sends people to unsubscribe for no reason.
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned == 0)
                {
                    Plugin.Log.LogInfo(
                        $"[Workshop] Update check inconclusive (result={result.m_eResult}, ioFailure={ioFailure}) - not prompting.");
                    return;
                }

                if (!SteamUGC.GetQueryUGCResult(_handle, 0, out SteamUGCDetails_t details)
                    || details.m_eResult != EResult.k_EResultOK)
                {
                    Plugin.Log.LogInfo("[Workshop] Update check returned no usable details - not prompting.");
                    return;
                }

                // punTimeStamp is when the *installed* content was published, so
                // this compares like with like.
                if (!SteamUGC.GetItemInstallInfo(_fileId, out _, out _, 1024u, out uint installedAt))
                {
                    Plugin.Log.LogInfo("[Workshop] No install info for this item - not prompting.");
                    return;
                }

                // m_bCachedData is logged so a stale answer is diagnosable rather
                // than something to guess at.
                if (details.m_rtimeUpdated > installedAt)
                {
                    UpdateAvailable = true;
                    Plugin.Log.LogWarning(
                        $"[Workshop] Update available - workshop {Stamp(details.m_rtimeUpdated)} is newer than " +
                        $"installed {Stamp(installedAt)} (cached={result.m_bCachedData}).");
                }
                else
                {
                    Plugin.Log.LogInfo(
                        $"[Workshop] Up to date - installed {Stamp(installedAt)}, " +
                        $"workshop {Stamp(details.m_rtimeUpdated)} (cached={result.m_bCachedData}).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Workshop] Update check failed: {ex.Message}");
            }
            finally
            {
                Release();
            }
        }

        private static string Stamp(uint unixSeconds)
            => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(unixSeconds).ToString("yyyy-MM-dd HH:mm 'UTC'");

        private static void Release()
        {
            if (_handle != UGCQueryHandle_t.Invalid)
            {
                try { SteamUGC.ReleaseQueryUGCRequest(_handle); } catch { /* shutting down */ }
                _handle = UGCQueryHandle_t.Invalid;
            }
            _pending = null;
        }
    }
}
