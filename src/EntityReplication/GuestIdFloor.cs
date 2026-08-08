using System.Threading;
using HarmonyLib;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Keeps the guest's own id allocator out of the band the host replicates into.
    ///
    /// Every ObjectBase and Projectile takes its id from SceneCreator.GenerateUid in a
    /// FIELD INITIALISER, so a guest numbers its own objects out of the same counter the
    /// host's entity ids live in - and weapons make a collision certain rather than
    /// unlikely, because every chaff round, decoy and SAM consumes one, hundreds per
    /// battle. Nothing crashes (the two registries are separate) but every lookup that
    /// spans them is ambiguous, and StateSerializer.FindById is the one that matters: it
    /// tries local objects first, so on a collision it confidently returns the wrong one.
    /// That is a weapon replica trying to launch from a chaff cloud, a home-base lookup
    /// silently deciding the wrong taskforce, and a flight whose members each fail to
    /// resolve their leader and never form up.
    ///
    /// SPMM already negotiated a private band for this - ProtocolInfo.ClientUidBase, sent
    /// in Welcome - and rebased the counter to it in SessionManager.OnSceneReady. That
    /// rebase could not work, for a reason the once-per-battle shape hides:
    ///
    ///   * SceneCreator assigns _UID DIRECTLY partway through a load, from the mission's
    ///     [File] CurrentUid key or from the highest UniqueID in the save
    ///     (SceneCreator.cs:277-309), so any rebase applied BEFORE a load is discarded.
    ///   * OnSceneReady runs after the load has settled - but the guest allocates ids all
    ///     the way THROUGH the load (the weapon pool alone takes hundreds), so by the time
    ///     it re-armed, the objects that collide had already been numbered. Playtest 33's
    ///     guest had a usn_rr144_chaff on 720 while host entity 720 was an aircraft.
    ///
    /// So the floor is held on the allocator itself and re-checked on every call: there is
    /// no single moment to rebase at, only an invariant to maintain. Objects restored from
    /// the host's save are untouched - they take their ids through ObjectBase.SetUniqueId,
    /// not through here.
    /// </summary>
    internal static class GuestIdFloor
    {
        /// <summary>Lowest id this machine may allocate to its own objects; 0 = disarmed
        /// (host, or no session). Armed from Welcome, which lands before the guest starts
        /// loading the session - the whole point being to be up for the load, not after
        /// it.</summary>
        internal static int Floor;

        internal static void Arm(int floor)
        {
            if (Plugin.Instance.CfgIsHost.Value || floor <= 0) return;
            Floor = floor;
            Plugin.Log.LogInfo($"[GuestId] Local id allocation floored at {floor}");
        }

        internal static void Disarm() => Floor = 0;
    }

    [HarmonyPatch(typeof(SceneCreator), nameof(SceneCreator.GenerateUid))]
    public static class Patch_SceneCreator_GenerateUid
    {
        /// <summary>CAS rather than a plain write: the method this fronts is
        /// Interlocked.Increment, so the counter is treated as shared and a
        /// read-then-write here could drop a concurrent allocation.</summary>
        static void Prefix(SceneCreator __instance)
        {
            int floor = GuestIdFloor.Floor;
            if (floor <= 0) return;

            int cur;
            while ((cur = __instance._UID) < floor)
            {
                if (Interlocked.CompareExchange(ref __instance._UID, floor, cur) == cur) break;
            }
        }
    }
}
