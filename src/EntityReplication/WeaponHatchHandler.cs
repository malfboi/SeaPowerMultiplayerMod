using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side weapon-hatch playback. The host launcher's engage state machine
    /// (which opens VLS lids / torpedo-tube doors before a launch) doesn't run on
    /// client puppets, so hatch animations never play locally. Replays the host's
    /// WeaponHatchEvent on the twin container and pumps it each frame from Tick() -
    /// these clones aren't pumped by anything client-side.
    ///
    /// Playback goes through the container's OWN openHatches/closeHatches and update(),
    /// rather than poking the animation directly. Those methods do more than play a
    /// clip: update() calls ToggleWeaponsVisibility as the lid moves and sets
    /// _areHatchesOpen when the clip FINISHES, so driving the animation by hand left the
    /// weapons inside the container hidden and the open flag set a whole animation too
    /// early.
    ///
    /// A close is DEFERRED by the delay the host asked for. The engage path schedules
    /// its close with a 3 s delay so the lid stays open behind a launch; replaying it at
    /// call time slammed the client's hatch shut at the exact moment the host finished
    /// opening it, which is why a launch looked like the hatch never opened at all.
    /// </summary>
    public static class WeaponHatchHandler
    {
        /// <summary>The launcher's staged round. Private on WeaponSystemLauncher, and
        /// needed on both ends: the host reads it to say WHAT is being loaded, the
        /// client writes it so spawnWeapons() has something to put on the rails. One
        /// lookup, shared - if a future build renames it the rail replication degrades
        /// to nothing rather than throwing.</summary>
        internal static readonly AccessTools.FieldRef<WeaponSystemLauncher, Ammunition>? SpawnedAmmoRef =
            AccessTools.FieldRefAccess<WeaponSystemLauncher, Ammunition>("_spawnedAmmunition");

        /// <summary>Containers with an animation in flight, pumped every frame.</summary>
        private static readonly List<WeaponContainer> _playing = new();

        /// <summary>Closes waiting out the host's delay, with the time they are due.</summary>
        private static readonly List<(WeaponContainer container, float dueAt)> _pendingClose = new();

        /// <summary>The same two lists for launcher-level (outer door) animations.</summary>
        private static readonly List<WeaponSystemLauncher> _playingSystem = new();
        private static readonly List<(WeaponSystemLauncher launcher, float dueAt)> _pendingSystemClose = new();

        public static void Handle(WeaponHatchEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit == null) return;

            var systems = unit._obp?._weaponSystems;
            if (systems == null || msg.MountIndex < 0 || msg.MountIndex >= systems.Count) return;
            if (!(systems[msg.MountIndex] is WeaponSystemLauncher launcher)) return;

            // Rail load / unload is launcher-level and must be tested before the
            // container bound below - it carries no container index of its own.
            if (msg.Unload) { UnloadRails(launcher); return; }
            if (!string.IsNullOrEmpty(msg.LoadAmmo)) { LoadRails(unit, launcher, msg.LoadAmmo); return; }

            if (msg.ContainerId >= launcher._containers.Count) return;

            if (msg.IsSystem)
            {
                HandleSystem(launcher, msg);
                return;
            }

            var container = launcher._containers[msg.ContainerId];
            if (container == null) return;

            if (msg.Open)
            {
                CancelPendingClose(container);

                // openHatches() skips the animation outright when _areHatchesOpen is
                // already set. It should not be - the host only sends transitions - but a
                // close we never applied (mid-battle join, a container torn down and
                // rebuilt) would otherwise disable this container's hatch animation for
                // the rest of the battle. Cheap to make self-healing.
                container._areHatchesOpen = false;

                container.openHatches();
                Track(container);
                return;
            }

            if (msg.DelaySec > 0f)
            {
                CancelPendingClose(container);
                _pendingClose.Add((container, Time.time + msg.DelaySec));
                return;
            }

            container.closeHatches();
            Track(container);
        }

        /// <summary>Put the host's round on this launcher's rails.
        ///
        /// Driven through the launcher's OWN playLoadAnimation(spawnWeapons) - the exact
        /// pair its state machine uses (WeaponSystemLauncher.cs:1463) - rather than
        /// calling WeaponContainer.load per container. That method handles the case with
        /// no load animation (spawn immediately, mark LoadingDone), refuses to start on
        /// top of an open/close/unload clip already playing, and hands the spawned
        /// objects to the animation so they move with it. Reproducing any of that by
        /// hand would be guessing at it.
        ///
        /// The Ammunition instance comes from the unit's own dictionary: both machines
        /// build it from the same ini, so the host's ammunition file name resolves to
        /// the twin instance here.</summary>
        private static void LoadRails(ObjectBase unit, WeaponSystemLauncher launcher, string ammoName)
        {
            if (SpawnedAmmoRef == null) return;

            var dict = unit.AmmunitionNameToAmmunitionDictionary;
            if (dict == null || !dict.TryGetValue(ammoName, out var ammo) || ammo == null)
            {
                Plugin.Log.LogWarning($"[Hatch] {unit.name} mount has no ammunition '{ammoName}' " +
                                      "locally - rails left empty.");
                return;
            }

            SpawnedAmmoRef(launcher) = ammo;
            launcher.playLoadAnimation(launcher.spawnWeapons);
        }

        /// <summary>Take everything back off the rails. returnAmmo:false because the
        /// client's magazine counts are replicated from the host by WeaponStatusSync -
        /// crediting a round back here would fight that with a number nobody asked
        /// for.</summary>
        internal static void UnloadRails(WeaponSystemLauncher launcher)
        {
            var containers = launcher._containers;
            if (containers == null) return;
            for (int i = 0; i < containers.Count; i++)
                containers[i]?.unload(returnAmmo: false);
        }

        /// <summary>The launcher's own outer door. Same shape as a container hatch but
        /// one level up: the animation lives on the system (BaseSystem._openSystemAnimation)
        /// and its progress is tracked by _systemState rather than a container flag, so it
        /// needs its own little playback list.</summary>
        private static void HandleSystem(WeaponSystemLauncher launcher, WeaponHatchEventMessage msg)
        {
            if (msg.Open)
            {
                CancelPendingSystemClose(launcher);

                var anim = launcher._openSystemAnimation;
                if (anim == null)
                {
                    launcher._systemState = WeaponSystem.SystemState.SystemOpen;
                    return;
                }
                anim.playAnim();
                launcher._systemState = WeaponSystem.SystemState.OpeningSystem;
                TrackSystem(launcher);
                return;
            }

            if (msg.DelaySec > 0f)
            {
                CancelPendingSystemClose(launcher);
                _pendingSystemClose.Add((launcher, Time.time + msg.DelaySec));
                return;
            }

            StartSystemClose(launcher);
        }

        private static void StartSystemClose(WeaponSystemLauncher launcher)
        {
            var anim = launcher._closeSystemAnimation;
            if (anim == null)
            {
                launcher._systemState = WeaponSystem.SystemState.SystemClosed;
                return;
            }
            anim.playAnim();
            launcher._systemState = WeaponSystem.SystemState.ClosingSystem;
            TrackSystem(launcher);
        }

        private static void TrackSystem(WeaponSystemLauncher l)
        {
            if (!_playingSystem.Contains(l)) _playingSystem.Add(l);
        }

        private static void CancelPendingSystemClose(WeaponSystemLauncher l)
        {
            for (int i = _pendingSystemClose.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_pendingSystemClose[i].launcher, l)) _pendingSystemClose.RemoveAt(i);
        }

        private static void Track(WeaponContainer c)
        {
            if (!_playing.Contains(c)) _playing.Add(c);
        }

        private static void CancelPendingClose(WeaponContainer c)
        {
            for (int i = _pendingClose.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_pendingClose[i].container, c)) _pendingClose.RemoveAt(i);
        }

        /// <summary>Per-frame pump (Plugin.Update, client) - the launcher states that
        /// normally advance these animations don't run on client puppets.</summary>
        public static void Tick()
        {
            float now = Time.time;

            for (int i = _pendingClose.Count - 1; i >= 0; i--)
            {
                var (container, dueAt) = _pendingClose[i];
                if (now < dueAt) continue;
                _pendingClose.RemoveAt(i);
                if (container == null) continue;
                container.closeHatches();
                Track(container);
            }

            // update() is void and self-terminating: it clears _playOpenAnimation /
            // _playCloseAnimation when the clip ends, so "neither flag set" is the
            // finished condition.
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                var c = _playing[i];
                if (c == null) { _playing.RemoveAt(i); continue; }
                if (!c._playOpenAnimation && !c._playCloseAnimation)
                {
                    _playing.RemoveAt(i);
                    continue;
                }
                c.update();
            }

            for (int i = _pendingSystemClose.Count - 1; i >= 0; i--)
            {
                var (launcher, dueAt) = _pendingSystemClose[i];
                if (now < dueAt) continue;
                _pendingSystemClose.RemoveAt(i);
                if (launcher != null) StartSystemClose(launcher);
            }

            // The system animation reports its own completion (update() returns false
            // once it stops playing), and the end state is whichever direction we were
            // driving - exactly what the launcher's own update would have set.
            for (int i = _playingSystem.Count - 1; i >= 0; i--)
            {
                var l = _playingSystem[i];
                if (l == null) { _playingSystem.RemoveAt(i); continue; }

                bool opening = l._systemState == WeaponSystem.SystemState.OpeningSystem;
                var anim = opening ? l._openSystemAnimation : l._closeSystemAnimation;
                if (anim == null || !anim.update())
                {
                    l._systemState = opening
                        ? WeaponSystem.SystemState.SystemOpen
                        : WeaponSystem.SystemState.SystemClosed;
                    _playingSystem.RemoveAt(i);
                }
            }
        }

        public static void Reset()
        {
            _playing.Clear();
            _pendingClose.Clear();
            _playingSystem.Clear();
            _pendingSystemClose.Clear();
        }
    }
}
