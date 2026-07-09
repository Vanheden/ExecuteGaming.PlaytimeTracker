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
- `src/Patches/ServerBootstrapPatches.cs` — connect/disconnect patches (playtime).
- `src/Patches/KillPatches.cs` — V Blood + PvP kill patches (see below).

## The ingest contract (keep in sync with the site)

Two endpoints, same secret header. The kill URL is **derived** from `Url` by
swapping the trailing `/session` for `/kill`, so operators configure one base URL.

- `POST {Url}` (`…/session`) — `{ sessionId, serverId, steamId, charName, startedAt, endedAt?, seconds }`.
  Keyed by a **per-connect `sessionId` (GUID)** so heartbeats + the final disconnect
  UPSERT one row — idempotent, no double counting, self-healing.
- `POST …/kill` — `{ eventId, serverId, steamId, charName, kind, victim, occurredAt }`
  where `kind` is `vblood` or `pvp`. Keyed by a **per-kill `eventId` (GUID)** so a
  retry can't double-count (server does INSERT OR IGNORE). `victim` is the V Blood's
  PrefabGUID hash (vblood) or the victim's character name (pvp).

If you change either shape, change `../Website/server/playtime.js` too.

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
- **Kill hooks (v1.1.13.0, verified LIVE as of v0.2.3):** BOTH V Blood and PvP come
  through the **same** patch — a **Prefix** on `DeathEventListenerSystem.OnUpdate()`
  (`KillPatches.cs → DeathEventPatch`) that reads
  `_DeathEventQuery.ToComponentDataArray<DeathEvent>(Allocator.Temp)` (the death
  entities still exist before the system consumes them; dispose the array).
  `DeathEvent { Entity Died; Entity Killer; Entity Source; StatChangeReason }`.
  - A death is a **V Blood kill** when `Died` has `VBloodUnit` **or**
    `VBloodConsumeSource` (both `ProjectM.Shared`) and the scorer — `Killer` if it's a
    player, else `Source` — is a player **and `Died` is not itself a player**. Players
    carry `VBloodConsumeSource` (they can be fed on) so without the `!diedIsPlayer`
    guard every PvP death was misclassified as a V Blood kill (`CHAR_VampireMale`,
    GUID `38526109`). `victim` sent is `Died`'s `PrefabGUID.GuidHash`
    (confirmed live: **Alpha Wolf = `-1905691330`**).
  - A death is a **PvP kill** when `Killer ≠ Died` and **both** have `PlayerCharacter`;
    score the `Killer`, `victim` is the dead player's character name.
  - `PlayerCharacter { FixedString64Bytes Name; Entity UserEntity }`, `DeathEvent`,
    `VBloodUnit`, `VBloodConsumeSource` live in `ProjectM.Shared.dll`; `PrefabGUID`
    (`.GuidHash`) in `Stunlock.Core.dll`. Watch for `→ V Blood kill` / `→ PvP kill` logs.
  - **DEAD END (don't retry):** the earlier `VBloodSystem.OnUpdate` **Postfix** +
    `EventList : NativeList<VBloodConsumed>` approach (from
    `ProjectM.Gameplay.Systems.dll`) only reflects **consumption/feeding**, not the
    kill, and never fired on Postfix in testing. Dropped in v0.2.2 along with the
    `ProjectM.Gameplay.Systems` csproj reference.
  - Each hook is patched **independently** in `Plugin.cs#ApplyPatches()` (logs
    `Patched X ✓` / `FAILED to patch X`), so one bad signature after a game update
    doesn't take down the others.
- After a big V Rising patch, re-verify all of the above against the current
  assemblies (re-run the metadata dumper). Bump the plugin version and rebuild.

## Debugging "not tracking" on live

When the live leaderboard stays empty, the site is almost never the cause — check
in this order:

1. **Is the site healthy?** `POST https://execute-gaming.se/api/ingest/session` with
   a wrong secret. `401` = the site is up and ingest is enabled (so it's a mod-side
   problem); `503` = no `INGEST_SECRET` set on the site; a `200` leaderboard fetch
   returning a `metric` field confirms the current code is deployed. A correct secret
   with a bad body returns `400` (auth passed, nothing written) — a safe way to test
   the secret without polluting data.
2. **Is the mod reporting?** Game-server log: `reporting=on` (secret set) and
   `Connect:` lines on join. `reporting=OFF` = blank `Secret` in the `.cfg`.
3. **Right target + secret?** `Ingest (…) returned 401` = the `.cfg` `Secret` doesn't
   match the site's `INGEST_SECRET`. A connection error to `localhost` = `Url` still
   holds the local test value — point it at the live host.

The classic failure: the site secret was rotated but the game servers' `.cfg` still
has the old test secret and/or `Url = http://localhost:3001/...`.

## Conventions

- Never block the game thread on I/O — HTTP is always fire-and-forget (`Task.Run`).
- Keep the JSON/contract in `IngestClient` byte-for-byte compatible with the site.
- Config is operator-facing; sensible defaults, no secrets committed.
