using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which task force the client has been assigned to control.
    /// TfType.None = free control (all friendly units).
    /// Host tracks this too so it can display the current assignment in the UI.
    /// </summary>
    public static class TaskforceAssignmentManager
    {
        public static Taskforce.TfType ClientAssignedTfType { get; private set; } = Taskforce.TfType.None;

        /// <summary>Host: assign a task force to the client and broadcast the assignment.</summary>
        public static void HostAssign(Taskforce.TfType tfType)
        {
            ClientAssignedTfType = tfType;
            NetworkManager.Instance.BroadcastToClients(new GameEventMessage
            {
                EventType = GameEventType.TaskforceAssigned,
                Param     = (float)(int)tfType,
            });
            Plugin.Log.LogInfo($"[TF] Host assigned client task force: {tfType}");
        }

        /// <summary>Client: called by GameEventHandler when a TaskforceAssigned event arrives.</summary>
        public static void OnAssignmentReceived(float param)
        {
            ClientAssignedTfType = (Taskforce.TfType)(int)param;
            Plugin.Log.LogInfo($"[TF] Assigned task force: {ClientAssignedTfType}");
        }

        /// <summary>
        /// Returns true if the client is allowed to issue orders to this unit.
        /// Always true on host, or when no task force is assigned (free control).
        /// </summary>
        public static bool ClientMayControl(ObjectBase unit)
        {
            if (ClientAssignedTfType == Taskforce.TfType.None) return true;
            return unit._taskforce?.Side == ClientAssignedTfType;
        }

        /// <summary>Reset assignment on disconnect so stale assignment doesn't carry to next session.</summary>
        public static void Reset()
        {
            ClientAssignedTfType = Taskforce.TfType.None;
            Plugin.Log.LogInfo("[TF] Task force assignment reset on disconnect.");
        }
    }
}
