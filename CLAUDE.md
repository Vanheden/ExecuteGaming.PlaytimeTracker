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
- `src/Patches/ClanResolver.cs` — resolves a `User` to its clan (stable GUID + name),
  shared by both patch files. Never throws — clan capture degrades to "no clan" so it
  can never break session/kill tracking.
- `src/Patches/CastleRaidPatches.cs` — the castle-raid patch (`CastleRaidPatch`): a
  Prefix on `CastleHeartEventSystem.ProcessRaidEvent` that records a raid (attacker +
  defender clans) via `IngestClient.PostRaid`. Pure observer, never throws.
- `src/Patches/CastleRaidResolver.cs` — resolves a castle-heart entity to its owning
  `User` (the defender), via `UserOwner`/`CastleHeart.LastUserOwner`. Never throws.
- `src/KillQueue.cs` — on-disk NDJSON kill queue. Failed kill POSTs are appended to
  `killqueue.ndjson` next to the plugin DLL; a background timer retries every 2 min.
  Capped at 500 entries. The site deduplicates by `eventId` so retries are harmless.
- `src/BroadcastQueue.cs` (v0.5.0) — thread-safe holding pen for in-game hype messages
  (killstreaks / world-first) the site returns in the kill-ingest response. Producers
  (HTTP-response threads) `Enqueue`; the game thread `Drain(EntityManager)`s and sends
  each to all clients via `ServerChatUtils.SendSystemMessageToAllClients`. Drained from
  `DeathEventPatch.Prefix` (already on the game thread). Bounded + best-effort.
- `src/Patches/ChatCommandPatches.cs` + `src/CommandReplyQueue.cs` (v0.6.0) — in-game
  chat commands. A Prefix on `ChatMessageSystem.OnUpdate` iterates `_ChatMessageQuery`,
  reads each `ChatMessageEvent.MessageText`, and on a known `!command` (rank/top/vbloods/
  online/help) resolves the sender's `User` (via `FromCharacter.User`) and calls
  `IngestClient.FetchCommand` → `GET /api/mod/cmd`. The site returns colour-tagged `lines`;
  the mod enqueues them (paired with the `User`) into `CommandReplyQueue`, drained on the
  game thread (from the chat + death patches) to reply **privately** via
  `ServerChatUtils.SendSystemMessageToClient(em, User, ref FixedString512Bytes)`. Gated by
  `Chat.EnableCommands`. All wording/formatting is site-side — the mod just recognises the
  command and prints the answer.

## The ingest contract (keep in sync with the site)

Two endpoints, same secret header. The kill URL is **derived** from `Url` by
swapping the trailing `/session` for `/kill`, so operators configure one base URL.

- `POST {Url}` (`…/session`) — `{ sessionId, serverId, steamId, charName, clanGuid?,
  clanName?, startedAt, endedAt?, seconds }`.
  Keyed by a **per-connect `sessionId` (GUID)** so heartbeats + the final disconnect
  UPSERT one row — idempotent, no double counting, self-healing.
- `POST …/kill` — `{ eventId, serverId, steamId, charName, kind, victim, clanGuid?,
  clanName?, victimClanGuid?, victimClanName?, occurredAt }`
  where `kind` is `vblood` or `pvp`. Keyed by a **per-kill `eventId` (GUID)** so a
  retry can't double-count (server does INSERT OR IGNORE). `victim` is the V Blood's
  PrefabGUID hash (vblood) or the victim's character name (pvp).
- **Clan fields** (both endpoints, added in v0.3.0): `clanGuid`/`clanName` is the
  reporting player's clan; `victimClanGuid`/`victimClanName` (kill, PvP only) is the
  dead player's clan, for clan-vs-clan wars. All are **empty strings when clanless**;
  the site treats empty/absent as "no clan" and keys clans by the stable `clanGuid`
  (rename-proof), displaying the latest `clanName`. See `Patches/ClanResolver.cs`.
- `POST …/raid` (added in v0.4.0) — `{ eventId, serverId, kind:"raid", occurredAt,
  attackerSteamId?, attackerName?, attackerClanGuid?, attackerClanName?,
  defenderSteamId?, defenderName?, defenderClanGuid?, defenderClanName? }`.
  The **raid URL is derived** from `Url` (swap `/session`→`/raid`), same as `/kill`.
  Keyed by a per-raid `eventId` (INSERT OR IGNORE). Attacker = the raider (from
  `FromCharacter`), defender = the raided castle's owner. Every attacker/defender field
  is optional (empty string when unresolved — a clanless solo raider, or an owner whose
  User doesn't resolve); the site records a raid as long as **one** side is identifiable.
  Powers the raid feed + per-clan raid record. See `Patches/CastleRaidPatches.cs`.

- **Kill response (v0.5.0):** `POST …/kill` now returns `200 { ok, broadcasts:[...] }`
  (was `204`). `broadcasts` is an array of ready-to-print strings — killstreak/world-first
  hype the site computed; usually empty. `IngestClient.HandleKillResponse` parses it on a
  successful POST and enqueues each into `BroadcastQueue`. All detection/wording is the
  site's job (`recordKill` highlights → `buildKillBroadcasts` in `../Website/server/`); the
  mod is a thin printer. Queued retries (`KillQueue`) intentionally ignore broadcasts (stale).

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
- **Clan resolution (v1.1.13.0, verified via the metadata dumper):** a player's clan
  hangs off the resolved `User`. `User.ClanEntity` is a **`NetworkedEntity`**
  (`ProjectM.CodeGeneration.dll` — needs its own `<Reference>`); read its `._Entity`
  field to get the clan `Entity` on the server. That entity carries **`ClanTeam`**
  (`ProjectM.Shared.dll`, namespace `ProjectM`) with `FixedString64Bytes Name` and a
  stable `Guid ClanGuid`. Clanless players have `ClanEntity._Entity == Entity.Null`.
  The site keys clans by `ClanGuid` (survives renames) and shows the latest `Name`.
  Captured on the game thread only (same rule as everything else): at connect (often
  not loaded yet — refreshed on disconnect, like `CharName`) and at each kill.
- **Castle raids (v1.1.13.0, verified via the metadata dumper — but PENDING LIVE
  verification, like the kill hooks before v0.2.2):** a Prefix on
  `CastleHeartEventSystem.ProcessRaidEvent(Entity heart, FromCharacter attacker, …)`
  (`ProjectM.Gameplay.Systems.dll` — needs its own `<Reference>`). This is the
  server-side handler that runs **after** the raid is validated, so we only see actual
  raids (not rejected attempts). `CastleHeartInteractEventType` has a `Raid` value
  confirming this is the raid path.
  - **Attacker** = `FromCharacter { Entity User; Entity Character }` (`ProjectM.dll`,
    ns `ProjectM.Network`) → `User` component → PlatformId/CharacterName + clan.
  - **Defender** = the heart's owner: `UserOwner { NetworkedEntity Owner }` (ns
    `ProjectM`) or `CastleHeart.LastUserOwner` (ns `ProjectM.CastleBuilding`), both
    `NetworkedEntity` → `._Entity` = the owning user entity → `User` + clan. See
    `CastleRaidResolver`. `CastleHeart.ActiveEvent` is a `CastleHeartEvent` enum
    (`None/FreeClaim/Attacked/Breached/Raided`) if you ever need finer state.
  - Raids are **rare** (need a siege + breach) and can't be staged in a quick smoke
    test, so this ships built + compile-verified; the `→ Raid:` log line lets the first
    real raid confirm the hook fires and both sides resolve. Patched **independently**
    in `ApplyPatches()`, so a wrong signature after a game update fails alone.
  - **Known fallback** if a future update inlines `ProcessRaidEvent` (patch fails): a
    Prefix on `CastleHeartEventSystem.OnUpdate` reading `_CastleHeartInteractEventQuery`
    for `CastleHeartInteractEvent { NetworkId CastleHeart; CastleHeartInteractEventType
    EventType }` + `FromCharacter` (resolve the heart via `NetworkIdSystem`). Captures
    attempts, not just confirmed raids, and needs NetworkId→Entity resolution.
- **In-game broadcasts (v0.5.0, signature dumped from the interop DLLs — but chat
  delivery PENDING LIVE PLAY-TEST):** send a global chat message with
  `ProjectM.ServerChatUtils.SendSystemMessageToAllClients(EntityManager em, ref
  FixedString512Bytes msg)` (in `ProjectM.dll`; `ServerChatUtils` also has
  `SendSystemMessageToClient(em/ecb, User, ref msg)`). Build the message with
  `new FixedString512Bytes(string)` (`Unity.Collections`; ~509-byte cap — `BroadcastQueue`
  caps at 200 chars). **Must run on the game thread** (touches ECS), so HTTP-response
  threads only `Enqueue`; `DeathEventPatch.Prefix` `Drain`s. Consequence: hype flushes on
  the **next death event** (continuous on an active server; the death system doesn't tick
  on an empty one — so this can't be smoke-tested solo). VampireCommandFramework is **not**
  installed, so we use the game's own `ServerChatUtils` directly. The mod compiles clean
  and **loads clean on the live server** (all 4 patches ✓); the actual chat line needs a
  real kill with a player online to confirm. Emoji render as missing-glyph boxes in the
  chat font, so hype is colour-coded with TextMeshPro `<color=#RRGGBB>…</color>` tags
  (built site-side) instead — the chat renders rich-text colour fine.
- **Chat commands (v0.6.0, signatures dumped from the interop DLLs — chat READ + private
  reply PENDING LIVE PLAY-TEST):** read incoming chat with a Prefix on
  `ProjectM.ChatMessageSystem.OnUpdate` → `__instance._ChatMessageQuery.ToEntityArray(...)`;
  each entity has `ProjectM.Network.ChatMessageEvent { ChatMessageType MessageType;
  FixedString512Bytes MessageText; NetworkId ReceiverEntity }` and
  `ProjectM.Network.FromCharacter { Entity User; Entity Character }`. Resolve the sender via
  `em.GetComponentData<User>(fromCharacter.User)` (→ `PlatformId`, `CharacterName`). Reply to
  just that player with `ServerChatUtils.SendSystemMessageToClient(EntityManager, User, ref
  FixedString512Bytes)`. Same game-thread rule as broadcasts: the async HTTP reply only
  `Enqueue`s into `CommandReplyQueue`; the chat + death patches `Drain`. Consequence: a reply
  flushes on the next chat **or** death tick, so a solo tester may need a second message to
  see the previous reply. The `!command` still echoes in public chat (not suppressed in v1).
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
