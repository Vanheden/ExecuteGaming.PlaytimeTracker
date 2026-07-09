# CLAUDE.md — Playtime Tracker mod

Guidance for Claude Code (and humans) working on this mod.

## What this is

A **server-side BepInEx (IL2CPP) mod for V Rising** that reports per-player play
sessions to the Execute-Gaming website, powering its playtime leaderboard. This is
the game-server half of a feature whose website half lives in `../Website/`
(`server/playtime.js`, `GET /api/leaderboard`, the `/leaderboard` page). See the
user-facing `README.md` here for install/config; this file is the working notes.

## Tech

- **C# / net6.0**, BepInEx `6.0.0-be` (IL2CPP), HarmonyX (ships inside BepInEx).
- No other runtime deps — VampireCommandFramework is **not** used.
- Target game: **V Rising dedicated server** (developed against **v1.1.13.0**).

## Layout

- `src/Plugin.cs` — BepInEx entry (`BasePlugin`). Wires config → `IngestClient` →
  `SessionTracker`, applies Harmony patches, runs the heartbeat timer, flushes on
  unload. Hands the tracker + logger to `BootstrapPatchShared`.
- `src/PlaytimeConfig.cs` — the `.cfg` bindings (Url, Secret, ServerId, HeartbeatMinutes).
- `src/SessionTracker.cs` — thread-safe active-session set (`ConcurrentDictionary`
  keyed by SteamID). `Connect` / `Disconnect` (called from the game thread) and
  `Heartbeat` / `FlushAll` (called from the timer thread).
- `src/IngestClient.cs` — fire-and-forget HTTP POST, hand-rolled JSON, off the game
  thread. Matches the site's `POST /api/ingest/session` contract exactly.
- `src/Patches/ServerBootstrapPatches.cs` — the Harmony patches (see below).

## The ingest contract (keep in sync with the site)

`POST {Url}` with header `X-Ingest-Secret: {Secret}` and body
`{ sessionId, serverId, steamId, charName, startedAt, endedAt?, seconds }`.
Everything is keyed by a **per-connect `sessionId` (GUID)** so heartbeats and the
final disconnect UPSERT one row — idempotent, no double counting, self-healing.
If you change this shape, change `../Website/server/playtime.js` too.

## Building & testing (no NuGet game packages)

The `.csproj` references the **local server install's** BepInEx + interop DLLs via
`$(VRisingServer)` HintPaths, so it always matches the installed game version.

1. The interop assemblies (`BepInEx/interop/`) only exist **after the server has
   booted once** with BepInEx. Boot it once if `interop/` is empty.
2. `dotnet build -c Release` (override the install path with
   `-p:VRisingServer="X:\path\to\serverfiles"` if it differs from the default).
3. Copy `bin/Release/ExecuteGaming.PlaytimeTracker.dll` into the server's
   `BepInEx/plugins/`, set the `.cfg`, restart the server.
4. A successful load logs `Playtime Tracker loaded — … reporting=on`. Connecting a
   client logs `Connect: <name> (<steamId>)`; the leaderboard fills after a
   heartbeat or a disconnect.

Inspecting game types/signatures: a `PEReader` + `MetadataReader` throwaway console
(net8.0) reading the interop DLLs is the reliable way to find a type's assembly or
a method's real signature — that's how the v1.1.13.0 signatures below were found.

## IL2CPP / game-version gotchas (this is the fragile part)

- **Signatures (v1.1.13.0):** `ServerBootstrapSystem.OnUserConnected(NetConnectionId)`
  and `OnUserDisconnected(NetConnectionId, ConnectionStatusChangeReason, string)` —
  **not** `(int userIndex)`. A wrong signature fails at patch time with
  `HarmonyException: IL Compile Error … Parameter "x" not found`.
- **Connection → user:** map `NetConnectionId` via
  `__instance._NetEndPointToApprovedUserIndex` → `_ApprovedUsersLookup[index]` →
  `client.UserEntity` → `EntityManager.GetComponentData<User>()`.
- **The `User` component** (`PlatformId`, `CharacterName`) is in namespace
  `ProjectM.Network` but defined in **`ProjectM.Shared.dll`** (not `ProjectM.dll`),
  which needs its own `<Reference>`. `NetConnectionId` is in `Stunlock.Network.dll`.
- **CharacterName is often empty at connect** (fires before the character loads),
  so it's re-captured on disconnect (game thread, populated by then). The site
  leaderboard shows the latest non-null name per SteamID; Steam-linked members show
  their site username regardless.
- **Threading:** only touch ECS (EntityManager/components) from the game thread
  (the Harmony patches). The heartbeat timer thread must not read game state — it
  only re-POSTs already-captured session data.
- After a big V Rising patch, re-verify all of the above against the current
  assemblies. Bump the plugin version and rebuild.

## Conventions

- Never block the game thread on I/O — HTTP is always fire-and-forget (`Task.Run`).
- Keep the JSON/contract in `IngestClient` byte-for-byte compatible with the site.
- Config is operator-facing; sensible defaults, no secrets committed.
