# Diagnostics & Privacy

Seapower Multiplayer can send anonymous diagnostics to help fix desyncs and
connection bugs. **It is off by default and nothing is collected until you turn
it on.** You are asked once, in-game; either answer is remembered.

This document is the honest version. It says what leaks as well as what doesn't.

## Turning it on and off

- Ctrl+F9 → **SETTINGS** → **Share diagnostics**. Takes effect immediately, no
  restart, in both directions.
- Or edit `ShareDiagnostics` under `[Diagnostics]` in
  `<GameDir>/BepInEx/config/com.seapowermultiplayer.plugin.cfg`.

When it is off, the log listener is never attached and the upload thread is
never started. This is "collect nothing", not "collect but don't send". Turning
it off mid-session abandons whatever was buffered rather than uploading it.

## What is sent

Only while you are actually in a multiplayer session. Sitting in the main menu
uploads nothing.

**Log messages** — this mod's own log output, at every level (including `Debug`
lines that never reach `LogOutput.log`). Unity exception messages and stack
traces. Log lines from other mods you have installed are captured too, because
"the user has a conflicting mod" is a real cause of the bugs this exists to find.

**Connection quality** — ping, packet loss, bytes in/out per message type,
send failures, arrival jitter, and the host stream cadence your client actually
observes.

**Performance** — frame rate average and worst frame, count of stutters over
100 ms, managed memory, replica and unit counts.

**Sync health** — replica drift and prediction error, handshake and sim state,
and the mod's internal failure counters (missed spawns, stale batches, census
repairs, reconnects).

**Session shape** — mod version, protocol version, host or client, LiteNetLib or
Steam, PvP or co-op, mission file name, session length, the in-game mission clock
and how long the mission has been running, and your OS, CPU, GPU and RAM. Your
sync-rate settings are included, because a non-default rate explains a large
share of reports.

## What is not sent

- Your name, your Steam persona name, or your friends'
- Your Steam ID
- Your IP address
- Chat, saves, or screenshots
- Anything at all when diagnostics are off

## Redaction

Before any text is even held in memory, it passes through a scrubber that:

- replaces 17-digit IDs (Steam IDs) with a salted, non-reversible hash
- replaces your Windows username wherever it appears
- shortens Windows paths to their last two segments
- replaces IPv4 addresses (except `127.0.0.1`)
- strips URL query strings
- replaces your Steam persona name and your session partner's

**This is a blocklist, and blocklists leak.** Specifically:

- Mission and save names are sent as-is. On custom content those are
  user-authored and could contain anything. They are too useful for diagnosis to
  drop.
- Log lines written by *other* mods are outside our control. A third-party mod
  could log something in a shape none of the rules match.

The 30-day retention below is the backstop for both.

## Identity and retention

You are identified by a random ID generated the first time you enable
diagnostics — not at install, so declining means no identifier is ever written.
It is shown under the checkbox in SETTINGS and stored as `InstallId` in the
config file. Delete that line to become a new, unrelated user.

Because that ID is stable, the stored data is pseudonymous rather than truly
anonymous, and counts as personal data under GDPR. Accordingly:

- Raw uploads are **deleted after 30 days**, automatically.
- Aggregate metrics (numbers only, no log text) are retained for 90 days.
- To have your data deleted sooner, quote your anonymous ID on the Discord.

## Where it goes

To a Cloudflare Worker run by the mod author. Raw batches go to R2 object
storage; numeric metrics go to Workers Analytics Engine. Nothing is shared with
any third party, and there is no advertising or profiling of any kind.

Uploads are gzipped, capped at 512 KB per batch and 5 MB per session, and sent at
most once a minute. A typical two-hour session uploads about 3 MB.

## Note on the base game's telemetry

Sea Power ships its own Sentry crash reporting, but it disables itself whenever
mods are loaded (`SentryOptionConfiguration` returns null when
`FileManager.ModsEnabled`). So while this mod is installed, the developers
receive no crash data from your session at all. That is part of why this feature
exists — and it means enabling it here does not double up on anything.
