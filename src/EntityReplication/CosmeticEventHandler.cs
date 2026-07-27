using System;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Client-side playback for host firing cosmetics: replays gun bursts through
    /// the mount's own native fire path (muzzle flash, dust, recoil, tracer
    /// projectile - whose impacts are damage-free because Blastzone is suppressed)
    /// and drives CIWS tracer state at the real target replica. Also applies
    /// authoritative magazine counts so the weapon panel reads honestly.
    /// </summary>
    public static class CosmeticEventHandler
    {
        // StopEngage is private on WeaponSystemCIWS - cached open delegate
        private static readonly Action<WeaponSystemCIWS>? _ciwsStopEngage = BuildStopEngage();

        private static Action<WeaponSystemCIWS>? BuildStopEngage()
        {
            var m = AccessTools.Method(typeof(WeaponSystemCIWS), "StopEngage");
            if (m == null) return null;
            return (Action<WeaponSystemCIWS>)Delegate.CreateDelegate(typeof(Action<WeaponSystemCIWS>), m);
        }

        public static void HandleGunBurst(GunBurstEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.ShooterId) ?? StateSerializer.FindById(msg.ShooterId);
            // Traced either as the shooter or as the thing being shot at: an undelayed
            // tracer against a render-delayed target is a visible misalignment.
            if (MotionTrace.IsTracing(msg.ShooterId) || MotionTrace.IsTracing(msg.TargetId))
                MotionTrace.TerminalEvent("GUNBURST", msg.ShooterId, unit,
                    Utils.longLatToLocalV3(
                        new GeoPosition(msg.AimLatDeg, msg.AimLonDeg, msg.AimHeightM),
                        Globals._currentCenterTile),
                    $"kind={msg.Kind} target={msg.TargetId} toTargetTime={msg.ToTargetTime:F3}");
            if (unit == null || unit._obp?._weaponSystems == null) return;
            if (msg.MountIndex < 0 || msg.MountIndex >= unit._obp._weaponSystems.Count) return;
            var ws = unit._obp._weaponSystems[msg.MountIndex];

            switch (msg.Kind)
            {
                case GunBurstKind.GunBurst:
                {
                    if (!(ws is WeaponSystemGun gun)) return;

                    var ammo = gun._vwp?._associatedMagazine?.getAmmunitionByName(msg.AmmoName)
                               ?? gun.getOnWeaponAmmunition();
                    if (ammo == null) { Telemetry.Count("v2.gunBurstNoAmmo"); return; }

                    float heading = GeoCodec.UnpackHeading(msg.SolutionHeadingQ);
                    float pitch   = GeoCodec.UnpackAngleCdeg(msg.SolutionPitchQ);
                    gun._solutionVector = Quaternion.Euler(pitch, heading, 0f) * Vector3.forward;
                    gun._ammoForEngage = ammo;
                    gun._targetObject = ReplicaRegistry.Find(msg.TargetId) ?? StateSerializer.FindById(msg.TargetId);
                    // Projectile.MoveProjectile lerps start→aim over _toTargetTime;
                    // without the host's solve the time sits at float.MaxValue and
                    // shells climb vertically then hang frozen at apex.
                    gun._projectileToTargetTime  = msg.ToTargetTime;
                    gun._projectileAimGeoPosition = new GeoPosition(msg.AimLatDeg, msg.AimLonDeg, msg.AimHeightM);
                    gun._solution = 1f; // host validated the solution - bypass the naval spot-shot gate

                    using (Authority.Allowed())
                        gun.fire();
                    Telemetry.Count("v2.playedGunBurst");
                    break;
                }

                case GunBurstKind.CiwsStart:
                {
                    if (!(ws is WeaponSystemCIWS ciws)) return;
                    // Weapon-replica targets live in ReplicaRegistry - without this
                    // the CIWS gets a null target and fires straight ahead.
                    ciws._currentClosestTarget = ReplicaRegistry.Find(msg.TargetId) ?? StateSerializer.FindById(msg.TargetId);
                    using (Authority.Allowed())
                        ciws.StartFire();
                    Telemetry.Count("v2.playedCiwsStart");
                    break;
                }

                case GunBurstKind.CiwsStop:
                {
                    if (!(ws is WeaponSystemCIWS ciws)) return;
                    using (Authority.Allowed())
                        _ciwsStopEngage?.Invoke(ciws);
                    break;
                }
            }
        }

        public static void HandleAmmoState(AmmoStateEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit == null) return;

            // Magazine first, when this change came from one: its own bookkeeping
            // moves the display total as a side effect, so the authoritative total
            // below has to be written after it, not before.
            if (msg.MagazineCount >= 0 && unit._obp?._weaponSystems != null)
            {
                foreach (var ws in unit._obp._weaponSystems)
                {
                    var mag = ws._vwp?._associatedMagazine;
                    if (mag == null || mag.getAmmunitionByName(msg.AmmoName) == null) continue;

                    int delta = msg.MagazineCount - mag.getAmmunitionCount(msg.AmmoName);
                    if (delta > 0)
                        mag.increaseAmmunitionCount(msg.AmmoName, delta);
                    else if (delta < 0)
                        mag.decreaseAmmunitionCount(msg.AmmoName, -delta);
                    break;
                }
            }

            // The number the player reads, taken verbatim from the host. Not derived
            // locally and not recomputed with UpdateAmmoCount(): the client's weapon
            // systems never run launch() or its reload, so their loaded counts and
            // container state stop matching the host as soon as anything fires, and
            // UpdateAmmoCount's per-system term is _vwp._internalAmmoCount - a static
            // ini capacity no launch ever decrements. setAmmunitionAmount writes the
            // ReactiveDictionary the weapon panel observes, so the panel refreshes.
            unit.setAmmunitionAmount(msg.AmmoName, msg.DisplayTotal);
            Telemetry.Count("v2.appliedAmmoState");
        }
    }
}
