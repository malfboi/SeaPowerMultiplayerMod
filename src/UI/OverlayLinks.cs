using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SeapowerMultiplayer.UI
{
    /// <summary>External links the overlay can open.</summary>
    internal static class OverlayLinks
    {
        /// <summary>Same invite the launcher and README use.</summary>
        internal const string DiscordInvite = "https://discord.gg/rMMnwJHc8w";

        /// <summary>
        /// Opens the Discord invite in the user's browser.
        /// </summary>
        /// <returns>
        /// False if no browser could be launched, in which case the invite has
        /// been put on the clipboard instead so the address is not simply lost.
        /// </returns>
        internal static bool OpenDiscord()
        {
            try
            {
                // Not Application.OpenURL: that reports nothing when it fails, so
                // there would be no way to know the clipboard fallback is needed.
                Process.Start(new ProcessStartInfo(DiscordInvite) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[UI] Could not open a browser for the Discord invite ({ex.Message}); copied it to the clipboard instead.");
                GUIUtility.systemCopyBuffer = DiscordInvite;
                return false;
            }
        }

        /// <summary>
        /// This mod's Workshop item id, taken from the DLL's install path
        /// (steamapps/workshop/content/&lt;app&gt;/&lt;fileId&gt;/).
        /// Null for non-workshop installs (dev builds in StreamingAssets), which
        /// is what keeps the update check quiet on a developer's machine.
        /// </summary>
        internal static readonly ulong? WorkshopFileId = ResolveFileId();

        /// <summary>Workshop page of this mod; null when the id could not be resolved.</summary>
        internal static readonly string? WorkshopUrl = WorkshopFileId.HasValue
            ? "https://steamcommunity.com/sharedfiles/filedetails/?id=" + WorkshopFileId.Value
            : null;

        private static ulong? ResolveFileId()
        {
            var m = Regex.Match(
                typeof(OverlayLinks).Assembly.Location,
                @"[\\/]workshop[\\/]content[\\/]\d+[\\/](\d+)[\\/]");
            return m.Success && ulong.TryParse(m.Groups[1].Value, out ulong id) ? id : (ulong?)null;
        }

        internal static void OpenWorkshopPage()
        {
            if (WorkshopUrl == null) return;
            try
            {
                if (Steamworks.SteamUtils.IsOverlayEnabled())
                {
                    Steamworks.SteamFriends.ActivateGameOverlayToWebPage(WorkshopUrl);
                    return;
                }
            }
            catch { /* fall through to the browser */ }
            Application.OpenURL(WorkshopUrl);
        }
    }
}
