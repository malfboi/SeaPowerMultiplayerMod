# Seapower Multiplayer

Real-time 2-player multiplayer (PvP) for **Sea Power: Naval Combat in the Missile Age**.

Play any scenario head-to-head, each player controlling an opposing task force. One player hosts the session while the other
   connects as a client. Both instances run their own units authoritatively, syncing game state over UDP or Steam P2P.

  Combat is resolved by the target of the engagement. If Player A fires a missile at Player B, then Player B decides the
  outcome, they are authoritative for attacks against their own units. If B's air defence intercepts the missile, that
  result syncs to A. If B determines the missile hits, both sides see the hit. If the two instances disagree, the target's
  outcome is always final.

---

## Installation

There are three ways to install the mod. Pick whichever suits you best.

### Option 1: Use the Launcher (Recommended)

The launcher handles everything automatically — it installs BepInEx, copies the plugin, and launches the game.

1. Download **SeapowerMultiplayer.Launcher.exe** from the [Releases](../../releases) page.
2. Run the launcher.
3. It will auto-detect your Sea Power installation, install BepInEx if needed, and copy the plugin DLL into the correct folder.
4. Click **Launch** to start the game with the mod loaded.

### Option 2: Manual DLL Install

If you prefer to manage things yourself:

1. Download and install **[BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases)** into your Sea Power game directory.
   - Extract the BepInEx zip so that `BepInEx/` sits alongside `Sea Power.exe`.
   - Run the game once to let BepInEx generate its folder structure, then close it.
2. Download **SeapowerMultiplayer.dll** from the [Releases](../../releases) page.
3. Copy the DLL into `Sea Power/BepInEx/plugins/`.
4. Launch the game normally.

### Option 3: Build from Source

1. Clone this repository:
   ```bash
   git clone https://github.com/your-username/SeapowerMultiplayer.git
   ```
2. Make sure **BepInEx 5.4.x** is installed in your game directory (see Option 2, step 1).
3. Build the plugin:
   ```bash
   dotnet build src/SeapowerMultiplayer.csproj
   ```
   If your game is not installed in the default Steam location, specify the path:
   ```bash
   dotnet build src/SeapowerMultiplayer.csproj /p:GameDir="D:\Games\Steam\steamapps\common\Sea Power"
   ```
   The build automatically copies the DLL and its dependencies into `BepInEx/plugins/`.

---

## How to Play

### Connecting via Steam

1. Launch the game with the mod installed.
2. Open a mission.
3. Press **Ctrl F9** to open the multiplayer overlay.
4. Click **Host Lobby**.
5. Click **Invite Friend** and invite your friend through Steam.
6. Your friend accepts the invite and is automatically connected and synced into the mission.

### Connecting via Direct Connect

1. Launch the game with the mod installed.
2. Press **Ctrl+F9** to open the multiplayer overlay (top-right corner).
3. **Host:** Open a mission, then click **Start Hosting**. The default port is 7777.
4. **Client:** Enter the host's IP address, then click **Connect**.
5. Once connected, the host clicks **Send Scene to Client** to sync the current scenario.

The client will automatically receive and load the host's save — no need to manually load the same mission.

### Controls

Both players use the normal game controls. In PvP, both sides are authoritative for their own units. In co-op, the host's game is authoritative — client orders are sent to the host for execution.

- **Time controls** are synced. Either player can pause/unpause/change time compression; the host decides and broadcasts the result.
- **Resync** - either player can press **Ctrl F10** and force a resync.

### Configuration

The mod generates a config file at `BepInEx/config/SeapowerMultiplayer.cfg` on first launch. Key settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `IsHost` | `true` | Run as host (server) or client |
| `HostIP` | `127.0.0.1` | IP to connect to (client only) |
| `Port` | `7777` | UDP port (must match on both sides) |
| `AutoConnect` | `false` | Automatically host/connect on game launch |
| `TransportType` | `LiteNetLib` | Network transport (`LiteNetLib` or `Steam`) |

### Network Requirements

- **LiteNetLib (UDP):** The host must have port 7777 (or your configured port) open/forwarded for UDP traffic. Both players need a direct network path (LAN, port forwarding, or VPN).
- **Steam P2P:** Uses Steam's relay network — no port forwarding required.

---

## Troubleshooting

- **Mod not loading?** Check that `BepInEx/` is in the correct location (same folder as `Sea Power.exe`) and that the plugin DLL is in `BepInEx/plugins/`.
- **Can't connect?** Verify the host's IP and port are correct, and that the UDP port is open in the host's firewall/router.
- **Desync or drift?** The mod includes automatic drift detection and correction. If issues persist, the host can re-send the scene via the F9 overlay.

---

## Known Issues

- When giving a unit its first order it may snap back to the original position, dragging or deleting the order should resolve this.
- Carrier ops can desync at 10x time compression and above.
- Defensive missiles can desync in high missile scenarios but should not affect combat outcomes.
- Weapons fired at a position rather than a target do not sync (e.g., torpedoes or Tomahawks fired at a location instead of a unit).

---

## Roadmap
- PvP Beta bug fixes
- Co-op mode
- More players
- Headless persistent server (this might be a pipe dream but I am looking into it)

This is a very abstract overview of the roadmap, I have no timeframes in mind for these features and all is subject to change.

---

## License

MIT — mod the mod freely.
