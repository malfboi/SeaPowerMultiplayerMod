using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SeaPower;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>A ground-cargo manifest (troops embarked on a ship or a transport launch)
    /// as one wire string. The game's own GroundCargoManifestCodec is internal, so this
    /// mirrors its shape: entries split by '|', fields by ':', the two free-text fields
    /// base64'd so player-facing names cannot break the split.</summary>
    internal static class GroundCargoWire
    {
        public static string Encode(IEnumerable<GroundCargoManifestEntry>? entries)
        {
            if (entries == null) return "";
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.GroundUnitId) || e.Personnel <= 0) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(B64(e.GroundUnitId)).Append(':')
                  .Append(B64(e.DisplayName)).Append(':')
                  .Append(e.Personnel.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static List<GroundCargoManifestEntry> Decode(string? value)
        {
            var result = new List<GroundCargoManifestEntry>();
            if (string.IsNullOrEmpty(value)) return result;
            foreach (var item in value!.Split('|'))
            {
                var parts = item.Split(':');
                if (parts.Length != 3) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int personnel)
                    || personnel <= 0) continue;
                string id = UnB64(parts[0]);
                if (string.IsNullOrEmpty(id)) continue;
                result.Add(new GroundCargoManifestEntry { GroundUnitId = id, DisplayName = UnB64(parts[1]), Personnel = personnel });
            }
            return result;
        }

        private static string B64(string? s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));

        private static string UnB64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (FormatException) { return ""; }
        }
    }
}
