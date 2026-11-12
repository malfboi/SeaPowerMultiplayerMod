using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Keeps the side relationship matrix identical on every machine.
    ///
    /// The save writes every pair, so a session starts in agreement. What can move it
    /// afterwards is a mission trigger's SetRelations or the AI scripting runtime, and
    /// both only ever run on the host (client trigger evaluation is gated) - so a guest
    /// would otherwise keep shooting at, or sharing sensors with, a side the mission
    /// had just made peace with or turned on. The host forwards each change; a guest
    /// applies the host's and makes none of its own.
    /// </summary>
    public static class SideRelationSync
    {
        private static bool _applying;

        internal static void HostCapture(Taskforce a, Taskforce b)
        {
            if (!Plugin.Instance.CfgIsHost.Value || !NetworkManager.Instance.IsEstablished) return;
            if (a == null || b == null || !Singleton<TaskforceManager>.InstanceExists(false)) return;

            var relations = Singleton<TaskforceManager>.Instance.Relations;
            NetworkManager.Instance.BroadcastToClients(new SideRelationMessage
            {
                SideA         = a._nameInMissionFile,
                SideB         = b._nameInMissionFile,
                State         = (byte)relations.Get(a, b),
                SensorSharing = relations.GetSensorSharing(a, b),
            });
        }

        public static void ApplyReceived(SideRelationMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value || !Singleton<TaskforceManager>.InstanceExists(false)) return;
            var manager = Singleton<TaskforceManager>.Instance;
            var a = manager.FindByName(msg.SideA);
            var b = manager.FindByName(msg.SideB);
            if (a == null || b == null)
            {
                Plugin.Log.LogWarning($"[Sides] Relation change for unknown side(s) {msg.SideA}/{msg.SideB} - ignored");
                return;
            }

            _applying = true;
            try { manager.Relations.Set(a, b, (RelationsState)msg.State, msg.SensorSharing); }
            finally { _applying = false; }
            Plugin.Log.LogInfo($"[Sides] {msg.SideA} <-> {msg.SideB}: {(RelationsState)msg.State} (sharing={msg.SensorSharing})");
        }

        /// <summary>Every relation change funnels through the four-argument Set (the
        /// three-argument one forwards to it).</summary>
        [HarmonyPatch(typeof(SideRelations), nameof(SideRelations.Set),
            new[] { typeof(Taskforce), typeof(Taskforce), typeof(RelationsState?), typeof(bool?) })]
        public static class Patch_SideRelations_Set
        {
            static bool Prefix() => !Suppression.ClientActive || _applying;

            static void Postfix(Taskforce a, Taskforce b) => HostCapture(a, b);
        }

        /// <summary>A guest must not act out a ceasefire itself. StopHostilities clears
        /// engage tasks, recalls strikes and self-destructs weapons in flight - all of
        /// which the host does for real and replicates, and the last of which, run on an
        /// inert weapon replica, would detonate it locally. The scoring half of
        /// OnRelationChanged still runs, so a guest's score banking matches.</summary>
        [HarmonyPatch(typeof(SideRelationEffects), "StopHostilities")]
        public static class Patch_SideRelationEffects_StopHostilities
        {
            static bool Prefix() => !Suppression.ClientActive;
        }
    }
}
