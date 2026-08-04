<div align="center">

# Seapower Multiplayer

**Real-time multiplayer for Sea Power: Naval Combat in the Missile Age**

[![GitHub Release](https://img.shields.io/github/v/release/malfboi/SeaPowerMultiplayerMod?style=flat-square&color=blue)](../../releases)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Discord](https://img.shields.io/badge/discord-join%20us-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.gg/rMMnwJHc8w)
[![.NET](https://img.shields.io/badge/.NET_Framework-4.7.2-purple?style=flat-square)](https://dotnet.microsoft.com/)

[Getting Started](#getting-started) · [How to Play](#how-to-play) · [Configuration](#configuration) · [Contributing](#contributing) · [Roadmap](#roadmap)

</div>

<br>

Play any Sea Power scenario head-to-head with a friend. One player hosts, the other connects, and you're in. Both instances run their own units authoritatively, with game state synced over UDP or Steam P2P.

### How combat works

Combat is resolved by the **target** of the engagement. If Player A fires a missile at Player B, then Player B's game decides the outcome. If B's air defence intercepts it, that result syncs to A. If the missile hits, both sides see the hit. When the two instances disagree, the target's outcome is always final.

> This is a community mod and is not affiliated with Triassic Games.

<br>

## Getting Started

There are four ways to install the mod. Pick whichever suits you best.

<details>
<summary><b>Option 1: Use Steam Workshop (Recommended)</b></summary>

Steam workshop recieves the most up to date fixes and is the simplest installation.

1. DInstall Anchor Chain and follow its setup guide
2. Install the multiplayer mod from [workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3769566124).
3. Enable the mod in the mod menu.
4. Restart the game.

</details>

<details>
<summary><b>Option 2: Use the Launcher</b></summary>

1. Subscribe to **Anchor Chain** on the Steam Workshop and follow its [installation guide](https://seapower-modders.github.io/AnchorChain/) to install the BepInEx preloader.
2. Subscribe to **Seapower Multiplayer** on the Steam Workshop.
3. Launch the game, open the mod menu and **tick Seapower Multiplayer**, then **fully close and reopen the game**.

> Applying a mod change only reloads the scene (`SceneManager.LoadScene(0)`), it does not restart the process. Anchor Chain loads plugins once per process, so enabling or disabling any code mod needs a full restart to take effect.

</details>

<details>
<summary><b>Option 3: Manual DLL Install</b></summary>

If you prefer to manage things yourself:

1. Install **Anchor Chain** and its preloader as above (this also installs BepInEx).
2. Download **SeapowerMultiplayer.dll** and **_info.ini** from the [Releases](../../releases) page.
3. Create a folder `Sea Power/Sea Power_Data/StreamingAssets/SeapowerMultiplayer/` and put both files inside it.
4. Launch the game normally.

</details>

<details>
<summary><b>Option 4: Build from Source</b></summary>

1. Clone this repository:
   ```bash
   git clone https://github.com/malfboi/SeaPowerMultiplayerMod.git
   ```
2. Make sure **Anchor Chain** (and therefore BepInEx 5.4.x) is installed in your game directory (see Option 2 above).
3. Build the plugin:
   ```bash
   dotnet build src/SeapowerMultiplayer.csproj
   ```
   If your game is not installed in the default Steam location, specify the path:
   ```bash
   dotnet build src/SeapowerMultiplayer.csproj /p:GameDir="D:\Games\Steam\steamapps\common\Sea Power"
   ```
   The build merges LiteNetLib into the DLL and copies it, with `_info.ini`, into `Sea Power_Data/StreamingAssets/SeapowerMultiplayer/`. Nothing else needs copying.

</details>

<br>

## How to Play

### Via Steam (easiest)

1. Launch the game with the mod installed.
2. Open a mission.
3. Press **Ctrl+F9** to open the multiplayer overlay.
4. Click **Host Lobby**.
5. Click **Invite Friend** and send a Steam invite.
6. Your friend accepts and is automatically connected and synced into the mission.

### Via Direct Connect

1. Launch the game with the mod installed.
2. Press **Ctrl+F9** to open the multiplayer overlay (top-right corner).
3. **Host:** Open a mission, then click **Start Hosting**. Default port is `7777`.
4. **Client:** Enter the host's IP address, then click **Connect**.
5. Once connected, the host clicks **Send Scene to Client** to sync the scenario.

The client automatically receives and loads the host's save - no need to manually load the same mission.

### In-Game Controls

Both players use the normal game controls. In PvP, both sides are authoritative for their own units. In co-op, the host's game is authoritative and client orders are sent to the host for execution.

| Action | Shortcut | Notes |
|--------|----------|-------|
| Open multiplayer overlay | `Ctrl+F9` | Host, connect, invite, send scene |
| Force resync | `Ctrl+F10` | Either player can trigger this |
| Time controls | Normal keys | Synced - host decides and broadcasts the result |

<br>

## Configuration

Press **Ctrl+F9** in-game and open the **Settings** section of the overlay. Changes are saved immediately - there is no file to edit and no restart needed.

| Setting | Default | Description |
|---------|---------|-------------|
| `Role` | `HOST` | Run as host (server) or client |
| `Transport` | `Direct IP` | Network transport - `Direct IP` (LiteNetLib) or `Steam` |
| `Host IP` | `127.0.0.1` | IP to connect to (client only) |
| `Port` | `7777` | UDP port (must match on both sides) |
| `Mode` | `PvP` | PvP (opposing taskforces) or Co-op (shared control) |
| `Auto-connect` | off | Automatically host/connect on game launch |
| `Time vote` | off | Both players must agree on time compression changes |

Network settings are locked while a session is running - disconnect to change them. The **Advanced** subsection holds state-stream rates and debug options, which apply live.

Settings are still persisted to `BepInEx/config/com.seapowermultiplayer.plugin.cfg`, so the file can be edited directly if you prefer.

### Network Requirements

| Transport | Port Forwarding | Details |
|-----------|:-:|---------|
| **LiteNetLib (UDP)** | Required | Host must open port `7777` (or configured port) for UDP. Both players need a direct network path (LAN, port forwarding, or VPN). |
| **Steam P2P** | Not required | Uses Steam's relay network. Just works. |

<br>

**Key concepts:**

- **Authority model** - Each player is authoritative over their own units. Combat outcomes are decided by the target's game instance.
- **Transport abstraction** - Networking is abstracted behind `ITransport`, making it easy to swap between LiteNetLib (UDP) and Steam P2P.
- **Harmony patching** - The mod hooks into the game via [Harmony](https://github.com/pardeike/Harmony) patches, intercepting and extending game methods at runtime.
- **State reconciliation** - Host broadcasts authoritative unit state; clients apply snap/lerp corrections in `StateApplier` to keep puppet units aligned.

<br>

## Contributing

Contributions are welcome! Whether it's a bug fix, new feature, or documentation improvement, feel free to open a PR.

### Getting set up

1. **Fork & clone** the repository.
2. **Install [Anchor Chain](https://seapower-modders.github.io/AnchorChain/)** (and its BepInEx preloader) into your Sea Power game directory.
3. **Build:**
   ```bash
   dotnet build src/SeapowerMultiplayer.csproj /p:GameDir="<your Sea Power install path>"
   ```
4. The DLL is automatically copied to `Sea Power_Data/StreamingAssets/SeapowerMultiplayer/` on successful build.

### Guidelines

- **Keep PRs small and focused.** One feature or one fix per PR. Large, sweeping changesets are hard to review and slow down the merge process. If your work touches multiple areas, split it into separate PRs.
- **Follow existing code style** and naming conventions.
- **Test in a live multiplayer session** before submitting. At minimum, verify your change works in a two-player session (host + client). Pay special attention to combat sync - missiles, air defence, carrier ops, and ground attacks are all areas where subtle issues can hide.
- **Open an issue first for large features** so we can discuss the approach before you invest time building it.
- **Document what you changed** in your PR description. Explain *what* and *why*, not just *how*.

### Testing

> A formal testing and benchmarking process is in the works. Until that's in place, the expectations below apply.

There is no automated test suite yet. Before submitting a PR, manually verify:

- Host and client can connect and sync a scenario.
- Your changes don't break existing combat sync.
- Performance is not noticeably degraded - watch for frame drops or network spikes, especially in high-unit-count scenarios.

If your change is in a specific area (e.g., flight ops, missile sync, damage states), test that area thoroughly across different time compression levels (1x, 5x, 10x).

### Reporting Bugs

Found a bug? [Open an issue](../../issues/new) with:

- Steps to reproduce
- Expected vs. actual behavior
- Whether you were host or client
- Time compression level, if relevant
- Any relevant log output from `BepInEx/LogOutput.log`

Or hop into the [Discord](https://discord.gg/rMMnwJHc8w) and let us know.

### Sharing diagnostics

You can opt in to sending anonymous diagnostics — logs, ping and packet loss,
frame rate and replica drift — which is by far the most useful thing you can do
to help fix desyncs. Ctrl+F9 → **SETTINGS** → **Share diagnostics**.

It is **off by default**, you are asked once, and you can turn it off again at any
time. Steam IDs, names and file paths are scrubbed before anything leaves your PC,
uploads only happen during an actual multiplayer session, and raw data is deleted
after 30 days. See [PRIVACY.md](PRIVACY.md) for the full detail, including what
the redaction does *not* catch.

Worth knowing: Sea Power's own crash reporting turns itself off whenever mods are
loaded, so while this mod is installed nobody is receiving crash data from your
session at all.

<br>

## Troubleshooting

<details>
<summary><b>Mod not loading</b></summary>

Check that `BepInEx/` is in the correct location (same folder as `Sea Power.exe`) and that the plugin DLL is in `BepInEx/plugins/`. If you used the launcher, try closing and re-launching - the multiplayer overlay should appear in the top-right before you reach the main menu.

</details>

<details>
<summary><b>Can't connect</b></summary>

Verify the host's IP and port are correct, and that the UDP port is open in the host's firewall/router. If using Steam P2P, make sure both players are friends on Steam and that Steam is running.

</details>

<details>
<summary><b>Desync or drift</b></summary>

The mod includes automatic drift detection and correction. If issues persist, either player can press **Ctrl+F10** to force a resync, or the host can re-send the scene via the Ctrl+F9 overlay.

</details>

<br>

## Known Issues

| Issue | Status | Workaround |
|-------|--------|------------|
| Aircraft cluster bombs deal no damage to ground units | Open | None - under investigation |
| Unit snaps back to original position on first order | Open | Move the waypoint slightly by dragging it on the map or cancel and re-issue the order |
| Carrier ops can desync at 10x+ time compression | Improved in 0.1.4 | Aircraft now use tiered drift correction. Use lower time compression if issues persist. |
| Position-targeted weapons don't sync (e.g., torpedoes/Tomahawks at a location) | Open | Target a unit instead of a position |
| Defensive missiles desync in high-missile scenarios | Fixed in 0.1.4 | Enemy air defence missiles no longer get purged. Missile position syncing removed for performance. |

<br>

## Roadmap

> This is a rough overview. No timeframes, everything is subject to change.

- [x] Core PvP multiplayer
- [ ] PvP beta bug fixes
- [x] Co-op mode
- [ ] Support for more than 2 players
- [ ] Headless persistent server

Have ideas? [Open a discussion](../../discussions) or share them in the [Discord](https://discord.gg/rMMnwJHc8w).

<br>

## License

[MIT](LICENSE) - mod the mod freely.