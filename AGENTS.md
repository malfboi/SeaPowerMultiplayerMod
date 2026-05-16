# Repository Guidelines

## Project Structure & Module Organization

This repository contains a Sea Power multiplayer mod and supporting tools.

- `src/`: main BepInEx plugin, networking, sync, gameplay patches, messages, and logging.
- `tests/SeapowerMultiplayer.Tests/`: lightweight .NET Framework test harness for protocol helpers.
- `Launcher/`: WPF launcher project.
- `worker/`: backend/helper code used by the launcher workflow.
- `openspec/`: design proposals and implementation task specs.
- `README.md`: user-facing setup and usage notes.

Keep gameplay patches in `src/Gameplay`, transport code in `src/Networking`, message DTOs in `src/Messages`, and state/session logic in `src/Sync`.

## Message DTO Reference

Network DTOs live in `src/Messages` and implement `INetMessage`. `NetworkManager` writes the leading `MessageType` byte before each DTO serializes its own payload.

| DTO | Direction | Purpose | Notes |
| --- | --- | --- | --- |
| `StateUpdateMessage` | Mostly host -> client, PvP bidirectional slices | Periodic unit/projectile snapshot. | Large when projectile count grows; LiteNet may fragment it. |
| `PlayerOrderMessage` | Bidirectional | Player commands: move, fire, speed, depth, sensors, RTB. | Many fields are reused by `OrderType`; keep writer/reader order stable. |
| `GameEventMessage` | Bidirectional | Discrete events: time changes, sync requests, selection, taskforce assignment. | `Param` is overloaded by event type. |
| `SessionSyncMessage` | Host -> client | Full scenario/save transfer and host settings. | Large strings are GZip-compressed with `int` length headers. |
| `SessionReadyMessage` | Client -> host | Client finished scene load. | Used before host unpauses synchronized play. |
| `CombatEventMessage` | Mostly host -> client, PvP impact sync | Authoritative combat outcomes. | Reliable ordered; prevents divergent local combat resolution. |
| `DamageStateMessage` | Host -> client | Compartment flooding, fire, systems, DC teams. | Array lengths derive from `CompartmentCount` and `TotalSystemCount`. |
| `DamageDecalMessage` | Host -> client | Visual damage decal placement. | Local position/normal are relative to target unit. |
| `ProjectileSpawnMessage` | Host -> client / owner -> remote | Immediate projectile spawn mapping. | Carries target and launch direction for force-spawn matching. |
| `ProjectileReconciliationMessage` | Host -> client | Periodic active projectile list. | Used to repair ID mapper mismatches. |
| `FlightOpsMessage` | Bidirectional PvP | Carrier launch/spawn synchronization. | `Launch` is notification-only; `SpawnVehicle` calls `launchVehicle` remotely. |
| `MissileStateSyncMessage` | Bidirectional PvP | Missile guidance/jamming/position state. | Packs bool flags into bytes to keep packets smaller. |
| `ChaffLaunchMessage` | Bidirectional PvP | Manual or auto chaff launch event. | Minimal unit ID payload. |
| `AircraftRecoveryMessage` | Bidirectional | Request/response for missing aircraft recreation. | Response can be `NotFound` and then omits spawn details. |

## Build, Test, and Development Commands

Build and deploy to both local game installs:

```powershell
dotnet build src/SeapowerMultiplayer.csproj /p:GameDir=<SeaPowerHostDir> /p:GameDir2=<SeaPowerClientDir>
```

Build without copying into the game directory:

```powershell
dotnet build src/SeapowerMultiplayer.csproj /p:GameDir=<SeaPowerInstallDir> /p:SkipCopyToPlugins=true
```

Use local Sea Power install paths for these properties. `GameDir2` is optional and is useful when developing with two local game copies.

Run tests:

```powershell
dotnet run --project tests/SeapowerMultiplayer.Tests/SeapowerMultiplayer.Tests.csproj
```

If deployment fails with a locked DLL, close Sea Power and rebuild.

## Coding Style & Naming Conventions

Use C# with 4-space indentation and existing file-local style. Prefer explicit, narrowly scoped classes and methods. Keep message types serializable through `LiteNetLib.Utils.NetDataWriter/NetDataReader`. Use `PascalCase` for public types/members, `_camelCase` for private fields, and clear category prefixes in logs, such as `[FlightOps]` or `[LiteNet]`.

Use `MpLog` for new plugin logs where practical. Reserve `Warning` for actionable problems; use `Info` or `Trace` for expected diagnostics.

## Testing Guidelines

Add focused tests for protocol, codec, and serialization logic under `tests/SeapowerMultiplayer.Tests`. The current harness is a console-style runner, so add assertions near related cases in `Program.cs` unless a broader test structure is introduced. Always run the test command above before deployment-sensitive changes.

## Runtime Log Tracking

BepInEx writes runtime logs under each game install at `BepInEx/LogOutput.log`. During local two-instance testing, tail both host and client logs in separate terminals:

```powershell
Get-Content <SeaPowerHostDir>\BepInEx\LogOutput.log -Wait -Tail 80
Get-Content <SeaPowerClientDir>\BepInEx\LogOutput.log -Wait -Tail 80
```

Filter mod-only lines:

```powershell
Get-Content <SeaPowerHostDir>\BepInEx\LogOutput.log -Wait | Select-String "SeapowerMultiplayer"
```

Useful patterns include `[TimeSync]`, `[FlightOps]`, `[LiteNet]`, `[Net]`, `[StateApplier]`, `Dropped`, `TooBigPacket`, and `Reassembled`.

## Commit & Pull Request Guidelines

Recent commit messages are short, imperative or descriptive, for example `hotfix for 0.2.0` and `add staggered updates`. Keep commits focused and mention the subsystem when useful, such as `network: reduce StateUpdate warning noise`.

Pull requests should include the gameplay/networking scenario tested, build/test output, affected game directories or config keys, and screenshots only for UI changes.

## Configuration & Safety Notes

Do not commit local game paths, generated binaries, logs, or private config. Preserve compatibility with existing config keys such as `VerboseLogging`. Avoid changing authoritative gameplay behavior without an OpenSpec proposal or a clear test scenario.
