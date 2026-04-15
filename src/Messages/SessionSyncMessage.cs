using System.IO;
using System.IO.Compression;
using System.Text;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Carries the full save file and base mission file from host to client.
    /// Sent once when host presses "Send Scene to Client".
    /// </summary>
    public class SessionSyncMessage : INetMessage
    {
        public MessageType Type => MessageType.SessionSync;

        public bool   LoadByName          = false; // true = client loads mission locally by filename
        public string SaveFileContent     = "";    // full text of the .sav file (empty if LoadByName)
        public string MissionFileName     = "";    // filename only (e.g. "MyScenario.ini")
        public string MissionFileContent  = "";    // full text of the base mission .ini (empty for built-ins)
        public int    RngSeed;                     // deterministic seed for synchronized RNG
        public float  GameSeconds;                  // Environment.Seconds (save format drops sub-minute precision)
        public bool   HostTimeVoteEnabled;         // host's CfgTimeVote — client must defer to this

        public void Serialize(NetDataWriter w)
        {
            w.Put(LoadByName);
            PutLargeString(w, SaveFileContent);
            w.Put(MissionFileName);  // short filename, fits in ushort
            PutLargeString(w, MissionFileContent);
            w.Put(RngSeed);
            w.Put(GameSeconds);
            w.Put(HostTimeVoteEnabled);
        }

        public static SessionSyncMessage Deserialize(NetDataReader r)
        {
            var msg = new SessionSyncMessage
            {
                LoadByName          = r.GetBool(),
                SaveFileContent     = GetLargeString(r),
                MissionFileName     = r.GetString(),
                MissionFileContent  = GetLargeString(r),
                RngSeed             = r.GetInt(),
                GameSeconds         = r.GetFloat(),
                HostTimeVoteEnabled = r.GetBool(),
            };

            return msg;
        }

        /// <summary>
        /// Write a string as GZip-compressed UTF-8 bytes with int32 length headers.
        /// Format: [uncompressed length (int)] [compressed length (int)] [compressed bytes (length-prefixed)]
        /// </summary>
        private static void PutLargeString(NetDataWriter w, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                w.Put(0); // uncompressed length = 0
                return;
            }
            byte[] raw = Encoding.UTF8.GetBytes(value);
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
                    gz.Write(raw, 0, raw.Length);
                compressed = ms.ToArray();
            }
            w.Put(raw.Length);        // int32: uncompressed byte count
            w.Put(compressed.Length); // int32: compressed byte count
            w.Put(compressed);        // raw bytes (no length prefix — avoids ushort overflow)
        }

        private static string GetLargeString(NetDataReader r)
        {
            int uncompressedLen = r.GetInt();
            if (uncompressedLen <= 0) return "";
            int compressedLen = r.GetInt();
            byte[] compressed = new byte[compressedLen];
            r.GetBytes(compressed, compressedLen);
            byte[] raw = new byte[uncompressedLen];
            using (var ms = new MemoryStream(compressed))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < uncompressedLen)
                {
                    int read = gz.Read(raw, offset, uncompressedLen - offset);
                    if (read == 0) break;
                    offset += read;
                }
            }
            return Encoding.UTF8.GetString(raw, 0, uncompressedLen);
        }
    }
}
