using System.Text;

namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Renders a Steam lobby's CSteamID as a short shareable code and back.
    /// Crockford base32 over the raw 64 bits: 13 characters, no I/L/O/U, so the
    /// code survives being read aloud or retyped from a chat window.
    /// </summary>
    public static class LobbyCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int CodeChars = 13; // 13 * 5 bits covers the full 64

        public static string Encode(ulong id)
        {
            var chars = new char[CodeChars];
            for (int i = CodeChars - 1; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(id & 31)];
                id >>= 5;
            }

            // Grouped 5-4-4 for readability
            var sb = new StringBuilder(CodeChars + 2);
            sb.Append(chars, 0, 5).Append('-');
            sb.Append(chars, 5, 4).Append('-');
            sb.Append(chars, 9, 4);
            return sb.ToString();
        }

        /// <summary>
        /// Parses a code back into the raw SteamID. Case-insensitive; dashes and
        /// whitespace are ignored, and Crockford's confusable letters are folded
        /// (I/L to 1, O to 0) so a hand-typed code still works.
        /// </summary>
        public static bool TryDecode(string? code, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(code)) return false;

            int digits = 0;
            foreach (char raw in code!)
            {
                if (raw == '-' || char.IsWhiteSpace(raw)) continue;

                char c = char.ToUpperInvariant(raw);
                if (c == 'O') c = '0';
                else if (c == 'I' || c == 'L') c = '1';

                int value = Alphabet.IndexOf(c);
                if (value < 0) return false;

                if (digits >= CodeChars) return false;
                // 13 digits carry 65 bits; the leading one only has 4 to spare.
                if (digits == 0 && value > 15) return false;
                id = (id << 5) | (uint)value;
                digits++;
            }

            return digits == CodeChars;
        }
    }
}
