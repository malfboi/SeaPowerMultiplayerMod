using System.Collections.Generic;
using SeaPower;
using SeaPower.Decals;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public static class DamageStateSerializer
    {
        /// <summary>
        /// Capture the full compartment + system damage state for a unit.
        /// Returns null for aircraft or units without compartments.
        /// </summary>
        public static DamageStateMessage Capture(ObjectBase unit)
        {
            var comps = unit.Compartments;
            if (comps == null) return null;

            int count = comps._compartmentsCount;

            // Count total systems across all compartments
            int totalSys = 0;
            var sysCounts = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int c = comps._systemCompartments[i]._listOfSystems.Count;
                sysCounts[i] = (byte)c;
                totalSys += c;
            }

            var msg = new DamageStateMessage
            {
                TargetEntityId  = unit.UniqueID,
                CompartmentCount = (byte)count,
                IsSinking       = comps._isSinking,
                FloodingData    = new float[count * 2 * 4],
                SystemData      = new float[count * 2],
                DcTeams         = new int[count + 1],
                TotalSystemCount = (byte)totalSys,
                SystemCountsPerCompartment = sysCounts,
                SystemIntegrities = new float[totalSys],
                SystemInoperables = new byte[totalSys],
            };

            // Flooding compartments: port[0..count-1] then starboard[0..count-1]
            for (int i = 0; i < count; i++)
            {
                var port = comps._portCompartments[i];
                int pi = i * 4;
                msg.FloodingData[pi + 0] = port._currentIntegrity;
                msg.FloodingData[pi + 1] = port._currentFlooding;
                msg.FloodingData[pi + 2] = port._currentWaterLevel;
                msg.FloodingData[pi + 3] = port._floodingRate;

                var stbd = comps._starboardCompartments[i];
                int si = (count + i) * 4;
                msg.FloodingData[si + 0] = stbd._currentIntegrity;
                msg.FloodingData[si + 1] = stbd._currentFlooding;
                msg.FloodingData[si + 2] = stbd._currentWaterLevel;
                msg.FloodingData[si + 3] = stbd._floodingRate;
            }

            // System compartments (fire)
            for (int i = 0; i < count; i++)
            {
                var sys = comps._systemCompartments[i];
                msg.SystemData[i * 2 + 0] = sys.FireSeverity;
                msg.SystemData[i * 2 + 1] = sys._fireGrowRate;
            }

            // Damage control teams
            var dcNums = comps.DamageControlTeamsNumbers;
            for (int i = 0; i < msg.DcTeams.Length && i < dcNums.Length; i++)
                msg.DcTeams[i] = dcNums[i];

            // Per-system integrity (propulsion, power, rudder, weapons, sensors, etc.)
            int sysIdx = 0;
            for (int i = 0; i < count; i++)
            {
                var systems = comps._systemCompartments[i]._listOfSystems;
                for (int j = 0; j < systems.Count; j++)
                {
                    msg.SystemIntegrities[sysIdx] = systems[j].CurrentIntegrity;
                    msg.SystemInoperables[sysIdx] = (byte)(systems[j].Inoperable.Value ? 1 : 0);
                    sysIdx++;
                }
            }

            return msg;
        }

        /// <summary>
        /// Apply a received damage state snapshot to the local unit.
        /// </summary>
        public static void Apply(DamageStateMessage msg)
        {
            var unit = StateSerializer.FindById(msg.TargetEntityId);
            if (unit == null || unit.IsDestroyed) return;
            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized) return;

            var comps = unit.Compartments;
            if (comps == null) return;
            if (comps._compartmentsCount != msg.CompartmentCount) return;

            int count = msg.CompartmentCount;

            // Trigger sinking BEFORE setting values — Sink() modifies flooding rates,
            // and our snapshot values (captured after sinking started) should overwrite them.
            if (msg.IsSinking && !comps._isSinking)
                comps.Sink(Compartments.SinkFocus.All, false);

            // Apply flooding compartment data
            for (int i = 0; i < count; i++)
            {
                var port = comps._portCompartments[i];
                int pi = i * 4;
                port._currentIntegrity  = msg.FloodingData[pi + 0];
                port._currentFlooding   = msg.FloodingData[pi + 1];
                port._currentWaterLevel = msg.FloodingData[pi + 2];
                port._floodingRate      = msg.FloodingData[pi + 3];

                var stbd = comps._starboardCompartments[i];
                int si = (count + i) * 4;
                stbd._currentIntegrity  = msg.FloodingData[si + 0];
                stbd._currentFlooding   = msg.FloodingData[si + 1];
                stbd._currentWaterLevel = msg.FloodingData[si + 2];
                stbd._floodingRate      = msg.FloodingData[si + 3];
            }

            // Apply system compartment data (fire)
            // FireSeverity setter calls SetFireState() which updates fire visuals.
            // Do NOT call CheckFire() — it calls UpdateFireDamage() which applies
            // fire damage to systems (propulsion, power). That would be an extra
            // damage tick on top of the normal OnFixedUpdate cycle.
            for (int i = 0; i < count; i++)
            {
                var sys = comps._systemCompartments[i];
                sys.FireSeverity  = msg.SystemData[i * 2 + 0];
                sys._fireGrowRate = msg.SystemData[i * 2 + 1];
            }

            // Apply damage control teams
            var dcNums = comps.DamageControlTeamsNumbers;
            for (int i = 0; i < msg.DcTeams.Length && i < dcNums.Length; i++)
                dcNums[i] = msg.DcTeams[i];

            // Apply per-system integrity (propulsion, power, rudder, weapons, etc.)
            // Setting CurrentIntegrity triggers CurrentIntegrityPercent → SetCurrentDamageType()
            // which updates DamageType thresholds that control speed/function limits.
            int sysIdx = 0;
            for (int i = 0; i < count; i++)
            {
                int expected = msg.SystemCountsPerCompartment[i];
                var systems = comps._systemCompartments[i]._listOfSystems;
                int actual = systems.Count;

                for (int j = 0; j < expected && j < actual; j++)
                {
                    var system = systems[j];
                    system.CurrentIntegrity = msg.SystemIntegrities[sysIdx];

                    bool hostInop = msg.SystemInoperables[sysIdx] != 0;
                    if (hostInop && !system.Inoperable.Value)
                        system.MakeInoperable();
                    else if (!hostInop && system.Inoperable.Value)
                        system.Inoperable.Value = false;

                    sysIdx++;
                }
                // Skip any extra systems if count mismatch
                if (expected > actual)
                    sysIdx += expected - actual;
            }
        }

        /// <summary>
        /// Apply a damage decal on the client.
        /// </summary>
        public static void ApplyDecal(DamageDecalMessage msg)
        {
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            var unit = StateSerializer.FindById(msg.TargetEntityId);
            if (unit == null || unit.IsDestroyed) return;
            if (string.IsNullOrEmpty(msg.DecalClass)) return;

            var worldPos = unit.transform.TransformPoint(new Vector3(msg.LocalX, msg.LocalY, msg.LocalZ));
            var worldNorm = unit.transform.TransformDirection(new Vector3(msg.NormalX, msg.NormalY, msg.NormalZ));

            Singleton<DecalsManager>.Instance.createDecalFromClass(
                msg.DecalClass, worldPos, worldNorm, msg.Scale, unit.transform, false);
        }
    }
}
