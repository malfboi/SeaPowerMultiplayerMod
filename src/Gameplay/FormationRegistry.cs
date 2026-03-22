using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Maps units to formation groups parsed from the save file.
    /// Formations ("Belknap-class") group multiple units together.
    /// Lone units (subs, airbases) get individual assignable entries.
    /// Group keys are stable across host/client (based on taskforce + formation name).
    /// </summary>
    public static class FormationRegistry
    {
        private static readonly Dictionary<int, string> _unitToGroup = new();

        private class GroupData
        {
            public string DisplayName;
            public readonly List<int> UnitIds = new();
            public byte TeamSide;
        }
        private static readonly Dictionary<string, GroupData> _groups = new();

        private static readonly Dictionary<string, int> _loneNameCounts = new();

        public static void Build() => Build(Globals.currentMissionFilePath);

        public static void Build(string savePath)
        {
            _unitToGroup.Clear();
            _groups.Clear();
            _loneNameCounts.Clear();

            if (string.IsNullOrEmpty(savePath))
            {
                Plugin.Log.LogWarning("[FormationRegistry] No save path provided");
                return;
            }

            // Parse formation definitions from save file
            var unitKeyToGroup = ParseFormations(savePath);

            if (!Singleton<TaskforceManager>.InstanceExists(false)) return;

            foreach (var tf in Singleton<TaskforceManager>.Instance._taskForces)
            {
                string tfName = tf._nameInMissionFile;
                if (string.IsNullOrEmpty(tfName)) continue;
                byte teamSide = PlayerRegistry.IsOnTeamSide(tf, 0) ? (byte)0 : (byte)1;

                MapUnitsForType<Vessel>(tf, tfName, "Vessel", teamSide, unitKeyToGroup);
                MapUnitsForType<Submarine>(tf, tfName, "Submarine", teamSide, unitKeyToGroup);
                MapUnitsForType<Aircraft>(tf, tfName, "Aircraft", teamSide, unitKeyToGroup);
                MapUnitsForType<Helicopter>(tf, tfName, "Helicopter", teamSide, unitKeyToGroup);
                MapUnitsForType<LandUnit>(tf, tfName, "LandUnit", teamSide, unitKeyToGroup);
            }

            Plugin.Log.LogInfo($"[FormationRegistry] Built {_groups.Count} groups, {_unitToGroup.Count} units");
            foreach (var kvp in _groups)
                Plugin.Log.LogInfo($"[FormationRegistry]   {kvp.Key} = \"{kvp.Value.DisplayName}\" ({kvp.Value.UnitIds.Count} units, team {kvp.Value.TeamSide})");
        }

        /// <summary>
        /// Parse Taskforce{N}_Formation{M} entries from the save/mission file.
        /// Format: unitKey1,unitKey2,...|FormationName|FormationType|Spacing
        /// Returns mapping from save-file unit key → (groupKey, displayName).
        /// </summary>
        private static Dictionary<string, (string groupKey, string displayName)> ParseFormations(string savePath)
        {
            var result = new Dictionary<string, (string, string)>();
            var usedGroupKeys = new HashSet<string>();

            // Gather all key=value pairs from IniHandler cache or disk
            var flatKeys = ReadAllKeys(savePath);
            if (flatKeys == null) return result;

            foreach (var kvp in flatKeys)
            {
                var match = Regex.Match(kvp.Key, @"^(Taskforce\d+)_Formation\d+$");
                if (!match.Success) continue;

                string tfName = match.Groups[1].Value;
                var parts = kvp.Value.Split('|');
                if (parts.Length < 2) continue;

                string formationName = parts[1].Trim();
                string groupKey = $"{tfName}:{formationName}";

                // Disambiguate duplicate names within same taskforce
                if (usedGroupKeys.Contains(groupKey))
                {
                    int n = 2;
                    while (usedGroupKeys.Contains($"{groupKey} ({n})")) n++;
                    groupKey = $"{groupKey} ({n})";
                    formationName = $"{formationName} ({n})";
                }
                usedGroupKeys.Add(groupKey);

                foreach (var unitKey in parts[0].Split(','))
                {
                    string key = unitKey.Trim();
                    if (!string.IsNullOrEmpty(key))
                        result[key] = (groupKey, formationName);
                }
            }

            Plugin.Log.LogInfo($"[FormationRegistry] Parsed {usedGroupKeys.Count} formations, {result.Count} unit keys from save");
            return result;
        }

        private static Dictionary<string, string> ReadAllKeys(string savePath)
        {
            // Try IniHandler cache first (always populated after scene load)
            var ini = IniHandler.get(savePath);
            if (ini?.Data != null && ini.Data.Count > 0)
            {
                var keys = new Dictionary<string, string>();
                foreach (var section in ini.Data.Values)
                    foreach (var kvp in section)
                        keys[kvp.Key] = kvp.Value;
                return keys;
            }

            // Fall back to reading from disk
            try
            {
                string content = File.ReadAllText(savePath);
                var keys = new Dictionary<string, string>();
                foreach (Match m in Regex.Matches(content, @"(?m)^([^=\[\]\r\n]+)=(.*)$"))
                    keys[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
                return keys;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[FormationRegistry] Cannot read save: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Enumerate units of a given type within a taskforce, sorted by UniqueID
        /// (creation order = save file order), and map them to formation groups.
        /// The Nth unit of type T in taskforce X = "Taskforce{X}{T}{N}" in save file.
        /// </summary>
        private static void MapUnitsForType<T>(Taskforce tf, string tfName, string typeKey, byte teamSide,
            Dictionary<string, (string groupKey, string displayName)> unitKeyToGroup) where T : ObjectBase
        {
            var units = Object.FindObjectsByType<T>(FindObjectsSortMode.None)
                .Where(u => u._taskforce == tf && !u.IsDestroyed)
                .OrderBy(u => u.UniqueID)
                .ToList();

            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                string saveKey = $"{tfName}{typeKey}{i + 1}";

                if (unitKeyToGroup.TryGetValue(saveKey, out var formation))
                {
                    AddToGroup(unit.UniqueID, formation.groupKey, formation.displayName, teamSide);
                }
                else
                {
                    // Lone unit — individual assignable group
                    string displayName = unit.name.Replace("(Clone)", "").Trim();
                    string baseName = $"{tfName}:lone:{unit.name}";

                    if (!_loneNameCounts.TryGetValue(baseName, out int count))
                        count = 0;
                    count++;
                    _loneNameCounts[baseName] = count;

                    string groupKey = count == 1 ? baseName : $"{baseName}#{count}";
                    if (count > 1) displayName = $"{displayName} #{count}";

                    AddToGroup(unit.UniqueID, groupKey, displayName, teamSide);
                }
            }
        }

        private static void AddToGroup(int unitId, string groupKey, string displayName, byte teamSide)
        {
            if (!_groups.TryGetValue(groupKey, out var group))
            {
                group = new GroupData { DisplayName = displayName, TeamSide = teamSide };
                _groups[groupKey] = group;
            }
            group.UnitIds.Add(unitId);
            _unitToGroup[unitId] = groupKey;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Returns the group key for a unit, or null if unknown.</summary>
        public static string GetGroupKey(int uniqueId)
        {
            _unitToGroup.TryGetValue(uniqueId, out var key);
            return key;
        }

        /// <summary>
        /// Register a dynamically spawned unit (e.g., launched aircraft) to inherit
        /// its parent's group assignment.
        /// </summary>
        public static void RegisterSpawnedUnit(int unitId, int parentId)
        {
            if (_unitToGroup.TryGetValue(parentId, out var groupKey))
            {
                _unitToGroup[unitId] = groupKey;
                if (_groups.TryGetValue(groupKey, out var group))
                    group.UnitIds.Add(unitId);
                Plugin.Log.LogDebug($"[FormationRegistry] Spawned unit {unitId} inherits group '{groupKey}' from parent {parentId}");
            }
        }

        /// <summary>
        /// Returns formation/lone-unit groups for the given team side.
        /// Used by UI to populate the assignment dropdown.
        /// </summary>
        public static List<(string groupKey, string displayName, int unitCount)> GetTeamGroups(byte teamSide)
        {
            var result = new List<(string, string, int)>();
            foreach (var kvp in _groups)
            {
                if (kvp.Value.TeamSide == teamSide)
                    result.Add((kvp.Key, kvp.Value.DisplayName, kvp.Value.UnitIds.Count));
            }
            return result;
        }

        /// <summary>
        /// Returns true if the unit belongs to one of the assigned groups.
        /// </summary>
        public static bool IsInAssignedGroup(ObjectBase unit, HashSet<string> assignedGroups)
        {
            if (unit == null || assignedGroups == null || assignedGroups.Count == 0) return false;
            if (_unitToGroup.TryGetValue(unit.UniqueID, out var groupKey))
                return assignedGroups.Contains(groupKey);
            return false;
        }

        /// <summary>
        /// Returns true if any player has the given unit's group assigned.
        /// Used for host fallback authority (unassigned units default to host).
        /// </summary>
        public static bool IsAssignedToAnyPlayer(ObjectBase unit)
        {
            if (unit == null) return false;
            string groupKey = GetGroupKey(unit.UniqueID);
            if (string.IsNullOrEmpty(groupKey)) return false;
            foreach (var p in PlayerRegistry.Players.Values)
                if (p.AssignedTfNames.Contains(groupKey)) return true;
            return false;
        }
    }
}
