using System.Collections.Generic;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which enemy units are authorized to spawn weapons (from network fire orders).
    /// When a network order arrives for an enemy ship, we authorize N shots.
    /// When a weapon spawns from that ship, we consume one authorization.
    /// Unauthorized spawns (local AI firing independently) get destroyed.
    /// </summary>
    static class PvPFireAuth
    {
        private static readonly Dictionary<int, int> _auths = new();

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int SuppressedCount { get; private set; }
        public static void RecordSuppression() => SuppressedCount++;
        public static int ActiveAuthCount
        {
            get { int t = 0; foreach (var v in _auths.Values) t += v; return t; }
        }

        public static void Authorize(int unitId, int shots)
        {
            _auths.TryGetValue(unitId, out int existing);
            _auths[unitId] = existing + shots;
        }

        public static bool ConsumeAuth(int unitId)
        {
            if (!_auths.TryGetValue(unitId, out int remaining) || remaining <= 0)
                return false;
            remaining--;
            if (remaining <= 0) _auths.Remove(unitId);
            else _auths[unitId] = remaining;
            return true;
        }

        public static void Revoke(int unitId, int shots)
        {
            if (!_auths.TryGetValue(unitId, out int existing)) return;
            existing = System.Math.Max(0, existing - shots);
            if (existing <= 0) _auths.Remove(unitId);
            else _auths[unitId] = existing;
        }

        public static int ActiveAuthForUnit(int unitId)
        {
            return _auths.TryGetValue(unitId, out int count) ? count : 0;
        }

        public static void Clear()
        {
            _auths.Clear();
            SuppressedCount = 0;
        }
    }
}
