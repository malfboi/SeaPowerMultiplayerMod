using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which task force the local player has been assigned to control.
    /// Delegates to PlayerRegistry for per-player authority in N-player mode.
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
        /// Returns true if the local player is allowed to issue orders to this unit.
        /// Delegates to PlayerRegistry for per-player task force authority.
        /// </summary>
        public static bool ClientMayControl(ObjectBase unit)
        {
            return PlayerRegistry.IsLocallyAuthoritative(unit);
        }
    }
}
