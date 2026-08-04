using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Strips identifying detail out of log text before it is retained.
    ///
    /// This runs on the way *into* the ring, not on the way out, so an unredacted
    /// string never sits in memory waiting for a flush that may never come. It is
    /// called from whichever thread logged, so it must stay allocation-light and
    /// must not touch game state.
    ///
    /// It is a blocklist, and blocklists leak - see PRIVACY.md. The literal
    /// list exists because a persona name or Windows username cannot be found by
    /// pattern; it has to be registered.
    /// </summary>
    internal static class Redactor
    {
        private const int MaxLen = 2000;

        // Compiled once. Order matters: literals first (they are exact), then the
        // patterns from most specific to least.
        private static readonly Regex RxSteamId = new(@"\b\d{17}\b", RegexOptions.Compiled);
        private static readonly Regex RxWinPath = new(@"[A-Za-z]:\\[^\s""'<>|]+", RegexOptions.Compiled);
        private static readonly Regex RxUserDir = new(@"[\\/]Users[\\/][^\\/]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RxIpv4    = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
        private static readonly Regex RxQuery   = new(@"\?[^\s""']*", RegexOptions.Compiled);

        /// <summary>Exact strings to blank out, longest first so a name that is a
        /// substring of a path does not half-replace it.</summary>
        private static readonly List<KeyValuePair<string, string>> _literals = new();
        private static readonly object _gate = new();

        private static string _salt = "";

        internal static void Init(string installId)
        {
            _salt = installId ?? "";

            // The username appears in every persistentDataPath and every stack
            // trace that mentions a file, so it is the highest-value literal.
            try { AddSecret(Environment.UserName, "<user>"); } catch { }
        }

        /// <summary>Register a value that must never appear in uploaded text.
        /// Safe to call repeatedly with the same value.</summary>
        internal static void AddSecret(string? literal, string replacement)
        {
            if (string.IsNullOrEmpty(literal) || literal!.Length < 3) return;

            lock (_gate)
            {
                foreach (var kv in _literals)
                    if (string.Equals(kv.Key, literal, StringComparison.OrdinalIgnoreCase)) return;

                _literals.Add(new KeyValuePair<string, string>(literal, replacement));
                // Longest first: "Barnaby Hughes" must win over "Barnaby".
                _literals.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            }
        }

        internal static string Scrub(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            // Truncate BEFORE the regex pass, not after: another mod logging a
            // base64 blob would otherwise cost six regex scans over it on
            // whichever thread logged.
            string t = s!.Length > MaxLen
                ? s.Substring(0, MaxLen) + "…[+" + (s.Length - MaxLen) + "]"
                : s;

            lock (_gate)
            {
                for (int i = 0; i < _literals.Count; i++)
                    t = ReplaceCI(t, _literals[i].Key, _literals[i].Value);
            }

            // 17-digit ids: SteamID64s from SteamLobbyManager/SteamTransport, and
            // anything else that shape. Hashed rather than dropped so one player
            // stays followable across their own log without being identifiable.
            t = RxSteamId.Replace(t, m => "id:" + ShortHash(m.Value));

            t = RxWinPath.Replace(t, m => ShortenPath(m.Value));
            t = RxUserDir.Replace(t, @"\Users\<user>");

            // Loopback is not identifying and is genuinely useful to see.
            t = RxIpv4.Replace(t, m => m.Value == "127.0.0.1" ? m.Value : "<ip>");

            t = RxQuery.Replace(t, "?<query>");

            return t;
        }

        /// <summary>Keeps the last two path segments - enough to identify which
        /// file, not enough to identify whose machine.</summary>
        private static string ShortenPath(string path)
        {
            string[] parts = path.Split('\\');
            if (parts.Length <= 2) return path;
            return "…\\" + parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
        }

        /// <summary>Salted so the same id maps differently on a different install.
        /// Not reversible, but stable within one install's data.</summary>
        private static string ShortHash(string value)
        {
            using var sha = SHA1.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(_salt + value));
            var sb = new StringBuilder(8);
            for (int i = 0; i < 4; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        private static string ReplaceCI(string haystack, string needle, string replacement)
        {
            int idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return haystack;

            var sb = new StringBuilder(haystack.Length);
            int pos = 0;
            while (idx >= 0)
            {
                sb.Append(haystack, pos, idx - pos).Append(replacement);
                pos = idx + needle.Length;
                idx = haystack.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(haystack, pos, haystack.Length - pos);
            return sb.ToString();
        }
    }
}
