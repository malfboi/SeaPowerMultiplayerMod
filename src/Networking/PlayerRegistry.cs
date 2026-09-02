using System.Collections.Generic;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using SeapowerMultiplayer.Transport;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Which side a player commands.
    ///
    /// Blue is always the host's <c>Globals._playerTaskforce</c>; Red is the other
    /// principal side. This is a property of a PLAYER, not of the session - that is the
    /// whole point of replacing the old session-wide PvP/co-op toggle. A 2v1 is co-op
    /// for the pair and PvP for everyone, which no single session flag could express.
    /// </summary>
    public enum Team : byte { Blue = 0, Red = 1 }

    public sealed class PlayerInfo
    {
        public byte   Slot;                 // 0 = host
        public PeerId Peer;                 // PeerId.None for the local entry
        public ulong  SteamId;              // 0 on LiteNetLib
        public string Name = "";
        public Team   Team;
        public bool   Connected;
        public bool   Established;          // handshake complete
        public bool   Ready;                // SessionReady for the current sync epoch
        public int    UidBase;

        /// <summary>What the roster and the "send to player" menu show. Resolved by the
        /// sender as: the configured Username, else the Steam persona, else empty - and
        /// empty falls back to the slot here, which is exactly the "index number" a
        /// LiteNetLib player gets when they have not set a name.</summary>
        public string DisplayName => string.IsNullOrEmpty(Name) ? $"Player {Slot + 1}" : Name;
    }

    /// <summary>
    /// Who is in the session, which team they are on, and what slot they hold.
    ///
    /// Host-authoritative: the host owns the table and replicates it verbatim with
    /// <see cref="PlayerRosterMessage"/>. A guest learns its OWN slot and team from its
    /// Welcome (the only message that can carry per-recipient data) and everything else
    /// from the roster.
    /// </summary>
    public static class PlayerRegistry
    {
        public const int MaxPlayers = 4;

        private static readonly PlayerInfo?[] _slots = new PlayerInfo?[MaxPlayers];
        private static readonly List<PlayerInfo> _dense = new();

        /// <summary>Bumped on ANY roster change. The overlay rebuilds its (interactive)
        /// roster rows only when this moves - rebuilding live Buttons on the 10 Hz UI
        /// tick would eat clicks mid-press.</summary>
        public static int Version { get; private set; }

        /// <summary>This machine's own entry. Never null once a session starts.</summary>
        public static PlayerInfo Local { get; private set; } = new PlayerInfo { Slot = 0, Team = Team.Blue, Connected = true };

        public static byte LocalSlot => Local.Slot;
        public static Team LocalTeam => Local.Team;

        /// <summary>Every known player, slot-ordered. Includes the local entry.</summary>
        public static IReadOnlyList<PlayerInfo> All => _dense;

        // ── Ambient sender ────────────────────────────────────────────────────
        //
        // Set for the duration of a message's apply so handlers can ask WHO sent it
        // without every message growing a redundant slot field. Without this the host
        // cannot validate that an order came from the unit's owner, and ownership
        // degrades to advisory-only.

        /// <summary>Slot of the peer whose message is being applied right now, or
        /// <see cref="NoSender"/> when the call originated locally.</summary>
        public static byte SenderSlot { get; private set; } = NoSender;

        public const byte NoSender = 255;

        internal static void BeginApply(byte slot) => SenderSlot = slot;
        internal static void EndApply() => SenderSlot = NoSender;

        // ── Queries ───────────────────────────────────────────────────────────

        public static bool TryGet(byte slot, out PlayerInfo player)
        {
            player = slot < MaxPlayers ? _slots[slot]! : null!;
            return player != null;
        }

        public static bool TryGetByPeer(PeerId peer, out PlayerInfo player)
        {
            foreach (var p in _dense)
            {
                if (p.Peer == peer) { player = p; return true; }
            }
            player = null!;
            return false;
        }

        public static string DisplayName(byte slot)
            => TryGet(slot, out var p) ? p.DisplayName : $"Player {slot + 1}";

        // The counters below index _slots directly rather than iterating _dense.
        // _slots is a fixed-length array, so a concurrent roster change can at worst
        // give a caller a slightly stale count - it can never throw the way enumerating
        // a mutating List does. Analytics reads these from its uploader thread.

        /// <summary>Players actually in the session on a team - connected AND past the
        /// handshake. A peer still handshaking must not count, or the host would suppress
        /// its AI for a side nobody is commanding yet.</summary>
        public static int CountOnTeam(Team team)
        {
            int n = 0;
            for (int i = 0; i < MaxPlayers; i++)
            {
                var p = _slots[i];
                if (p != null && p.Team == team && p.Connected && p.Established) n++;
            }
            return n;
        }

        public static bool AnyOnTeam(Team team) => CountOnTeam(team) > 0;

        public static int TeammateCount(byte slot)
        {
            if (!TryGet(slot, out var me)) return 0;
            int n = 0;
            for (int i = 0; i < MaxPlayers; i++)
            {
                var p = _slots[i];
                if (p != null && p.Slot != slot && p.Team == me.Team && p.Connected && p.Established) n++;
            }
            return n;
        }

        /// <summary>Longest-present remaining player on the same team, excluding
        /// <paramref name="slot"/>. Drives formation hand-over on disconnect - and
        /// because _slots is indexed BY slot, "lowest index" is "joined earliest".</summary>
        public static PlayerInfo? FirstRemainingTeammate(byte slot)
        {
            if (!TryGet(slot, out var me)) return null;
            for (int i = 0; i < MaxPlayers; i++)
            {
                var p = _slots[i];
                if (p != null && p.Slot != slot && p.Team == me.Team && p.Connected) return p;
            }
            return null;
        }

        /// <summary>Compact seating label, e.g. "2v1". Used by analytics in place of the
        /// old pvp/coop mode string, which no longer describes anything.</summary>
        public static string SeatingLabel() => $"{CountOnTeam(Team.Blue)}v{CountOnTeam(Team.Red)}";

        /// <summary>May this slot command this unit at all? Team-level only - per-formation
        /// ownership is a separate, finer gate.</summary>
        public static bool MayCommand(byte slot, SeaPower.ObjectBase? unit)
        {
            if (unit?._taskforce == null) return false;
            if (!TryGet(slot, out var p)) return false;
            var tf = Teams.TeamOf(unit._taskforce);
            return tf.HasValue && tf.Value == p.Team;
        }

        // ── Host mutations ────────────────────────────────────────────────────

        /// <summary>Seat the host itself. Slot 0, always Blue - the authoritative sim runs
        /// on the host's own unswapped save, so Blue IS whatever the mission calls the
        /// player side.</summary>
        public static void HostInit(string name)
        {
            Clear();
            Local = new PlayerInfo
            {
                Slot = 0,
                Peer = PeerId.None,
                Name = name,
                Team = Team.Blue,
                Connected = true,
                Established = true,
                UidBase = 0,
            };
            Seat(Local);
        }

        /// <summary>Seat the local guest before its Welcome arrives, so the overlay has
        /// something coherent to show while handshaking.</summary>
        public static void GuestInit(string name, Team requested)
        {
            Clear();
            Local = new PlayerInfo
            {
                Slot = 1,
                Peer = PeerId.Server,
                Name = name,
                Team = requested,
                Connected = true,
                Established = false,
            };
            Seat(Local);
        }

        /// <summary>
        /// Host: admit a peer. Fails only when the session is full - never for a team
        /// reason. Two people accepting the same "Invite to Red" both ask for Red, and
        /// refusing the loser would be a baffling way to lose a joiner; seat them on the
        /// other team and let the host correct it from the roster picker instead.
        /// </summary>
        public static bool HostTryAdd(PeerId peer, ulong steamId, string name, Team requested,
                                      out PlayerInfo player, out string? refusal)
        {
            player = null!;
            refusal = null;

            if (TryGetByPeer(peer, out player)) return true;   // already seated

            byte slot = 0;
            for (byte i = 1; i < MaxPlayers; i++)
            {
                if (_slots[i] == null) { slot = i; break; }
            }
            if (slot == 0)
            {
                refusal = $"Session is full ({MaxPlayers}/{MaxPlayers} players).";
                return false;
            }

            player = new PlayerInfo
            {
                Slot = slot,
                Peer = peer,
                SteamId = steamId,
                Name = name,
                Team = requested,
                Connected = true,
                Established = true,
                UidBase = ProtocolInfo.UidBaseForSlot(slot),
            };
            Seat(player);
            Plugin.Log.LogInfo($"[Players] {player.DisplayName} joined as slot {slot} on {player.Team} (uidBase={player.UidBase}).");
            return true;
        }

        public static void HostRemove(PeerId peer)
        {
            // HOST ONLY. This is called from the shared disconnect path, and on a guest
            // it matched the LOCAL player - whose Peer is PeerId.Server - and deleted it
            // from the guest's own roster. Harmless in that the session was ending
            // anyway, but it logged "<you> left" on your own machine, which reads as a
            // fault rather than a teardown.
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!TryGetByPeer(peer, out var p)) return;
            Plugin.Log.LogInfo($"[Players] {p.DisplayName} (slot {p.Slot}) left.");
            _slots[p.Slot] = null;
            Rebuild();
        }

        public static void HostSetTeam(byte slot, Team team)
        {
            if (!TryGet(slot, out var p) || p.Team == team) return;
            p.Team = team;
            Version++;
            Plugin.Log.LogInfo($"[Players] {p.DisplayName} moved to {team}.");
        }

        public static void HostSetName(byte slot, string name)
        {
            if (!TryGet(slot, out var p) || p.Name == name) return;
            p.Name = name;
            Version++;
        }

        /// <summary>Host: record whether this player has loaded the current session.
        ///
        /// This is the REPLICATED half of readiness - it rides out on the roster
        /// (FlagReady), which is what lets a guest's overlay say where the other players
        /// are. SimSyncManager's _readySlots is the host's own bookkeeping and never
        /// leaves the machine, nor does it ever contain slot 0, so neither a guest nor
        /// the host's own row could be read from it.
        ///
        /// Returns true when the value actually moved, so the caller can broadcast only
        /// on a change.</summary>
        public static bool HostSetReady(byte slot, bool ready)
        {
            if (!TryGet(slot, out var p) || p.Ready == ready) return false;
            p.Ready = ready;
            Version++;
            return true;
        }

        /// <summary>Host: set readiness and publish it if it moved.</summary>
        public static void HostSetReadyAndPublish(byte slot, bool ready)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (HostSetReady(slot, ready)) HostBroadcastRoster();
        }

        /// <summary>Host: mark every player not-loaded (a new session, or a teardown).</summary>
        public static void HostClearAllReady()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            bool moved = false;
            for (int i = 0; i < MaxPlayers; i++)
                if (_slots[i] != null && HostSetReady((byte)i, false)) moved = true;
            if (moved) HostBroadcastRoster();
        }

        /// <summary>Host: publish the whole table. Cheap enough (4 short entries) that
        /// deltas would be false economy.</summary>
        public static void HostBroadcastRoster()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            var msg = new PlayerRosterMessage();
            foreach (var p in _dense)
            {
                msg.Entries.Add(new PlayerRosterMessage.Entry
                {
                    Slot    = p.Slot,
                    Team    = (byte)p.Team,
                    Flags   = (byte)((p.Connected ? PlayerRosterMessage.FlagConnected : 0)
                                   | (p.Established ? PlayerRosterMessage.FlagEstablished : 0)
                                   | (p.Ready ? PlayerRosterMessage.FlagReady : 0)),
                    SteamId = p.SteamId,
                    Name    = p.Name ?? "",
                });
            }
            NetworkManager.Instance.BroadcastToClients(msg);
        }

        // ── Guest application ─────────────────────────────────────────────────

        /// <summary>Guest: adopt the slot, team and UID band the host assigned us. Runs
        /// before any SessionSync can arrive (BlockedPreHandshake drops everything except
        /// Hello/Welcome until Established), which is what makes
        /// <see cref="Teams.LocalSaveSwapped"/> safe to read during save application.</summary>
        public static void ApplyWelcome(WelcomeMessage msg)
        {
            Local.Slot        = msg.AssignedSlot;
            Local.Team        = (Team)msg.AssignedTeam;
            Local.UidBase     = msg.ClientUidBase;
            Local.Connected   = true;
            Local.Established = true;

            // Re-seat: the slot the host gave us is probably not the one we guessed.
            Clear();
            Seat(Local);
            Plugin.Log.LogInfo($"[Players] Seated as slot {Local.Slot} on {Local.Team}.");
        }

        public static void ApplyRoster(PlayerRosterMessage msg)
        {
            var localSlot = Local.Slot;
            for (int i = 0; i < MaxPlayers; i++) _slots[i] = null;

            foreach (var e in msg.Entries)
            {
                if (e.Slot >= MaxPlayers) continue;
                PlayerInfo p = e.Slot == localSlot ? Local : new PlayerInfo();
                p.Slot        = e.Slot;
                p.Team        = (Team)e.Team;
                p.SteamId     = e.SteamId;
                p.Name        = e.Name ?? "";
                p.Connected   = (e.Flags & PlayerRosterMessage.FlagConnected) != 0;
                p.Established = (e.Flags & PlayerRosterMessage.FlagEstablished) != 0;
                p.Ready       = (e.Flags & PlayerRosterMessage.FlagReady) != 0;
                _slots[e.Slot] = p;
            }

            // The host's table is authoritative for everyone EXCEPT our own team and
            // slot, which came from Welcome and must not be second-guessed here: a
            // roster that arrived before our Welcome would otherwise flip the team the
            // save swap was already decided from.
            if (_slots[localSlot] == null) _slots[localSlot] = Local;

            Rebuild();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public static void Reset()
        {
            Clear();
            Local = new PlayerInfo { Slot = 0, Team = Team.Blue, Connected = true };
            SenderSlot = NoSender;
        }

        private static void Clear()
        {
            for (int i = 0; i < MaxPlayers; i++) _slots[i] = null;
            Rebuild();
        }

        private static void Seat(PlayerInfo p)
        {
            _slots[p.Slot] = p;
            Rebuild();
        }

        private static void Rebuild()
        {
            _dense.Clear();
            for (int i = 0; i < MaxPlayers; i++)
                if (_slots[i] != null) _dense.Add(_slots[i]!);
            Version++;
        }
    }
}
