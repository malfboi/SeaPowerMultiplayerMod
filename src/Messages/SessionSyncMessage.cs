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

        public void Serialize(NetDataWriter w)
        {
            w.Put(LoadByName);
            PutLargeString(w, SaveFileContent);
            w.Put(MissionFileName);  // short filename, fits in ushort
            PutLargeString(w, MissionFileContent);
            w.Put(RngSeed);
            w.Put(GameSeconds);
        }

        public static SessionSyncMessage Deserialize(NetDataReader r) => new SessionSyncMessage
        {
            LoadByName         = r.GetBool(),
            SaveFileContent    = GetLargeString(r),
            MissionFileName    = r.GetString(),
            MissionFileContent = GetLargeString(r),
            RngSeed            = r.GetInt(),
            GameSeconds        = r.GetFloat(),
        };

        /// <summary>
        /// Write a string as int32-length-prefixed UTF-8 bytes.
        /// LiteNetLib's Put(string) uses ushort length (max 65535 bytes),
        /// which is too small for save files.
        /// </summary>
        private static void PutLargeString(NetDataWriter w, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                w.Put(0);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            w.Put(bytes.Length);
            w.Put(bytes);
        }

        private static string GetLargeString(NetDataReader r)
        {
            int length = r.GetInt();
            if (length <= 0) return "";
            byte[] bytes = new byte[length];
            r.GetBytes(bytes, length);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
