# Roadmap — Playtime Tracker mod

Small, focused mod, so this is short. Effort: **S** = an hour · **M** = an afternoon.

## ✅ Done

- **Playtime tracking** — connect/disconnect + heartbeat sessions, idempotent by a
  per-connect `sessionId`. Verified live end-to-end against the site.
- **V Blood + PvP kill tracking** (v0.2.2, **verified LIVE**) — a single Harmony
  **Prefix** on `DeathEventListenerSystem.OnUpdate` detects both: a V Blood kill when
  the dead entity is a V Blood unit (`VBloodUnit`/`VBloodConsumeSource`) and the
  scorer is a player, a PvP kill when killer and victim are both players. POSTed to
  `/api/ingest/kill` (idempotent by a per-kill `eventId`). Confirmed live killing
  Alpha Wolf (`-1905691330`). Powers the Points / V Blood / PvP leaderboard tabs.
  (The earlier `VBloodSystem.EventList` Postfix approach was a dead end — see
  `CLAUDE.md` — dropped in v0.2.2 with its csproj reference.)
- **V Blood misclassification fix** (v0.2.3) — players carry `VBloodConsumeSource`
  (`CHAR_VampireMale`, GUID `38526109`), so every PvP death was logged as a V Blood
  kill. Added `!diedIsPlayer` guard to the V Blood condition. Deployed to PvE server;
  PvP server pending manual DLL copy.
- **On-disk kill queue** (v0.2.4) — failed kill POSTs are saved to `killqueue.ndjson`
  and retried every 2 minutes, so kills survive a website outage instead of being
  dropped. Sessions already self-heal via heartbeat; this closes the gap for kills.
  Capped at 500 entries; site-side `eventId` dedup makes retries harmless.
- **Castle raid tracking** (v0.4.0) — a Prefix on `CastleHeartEventSystem.ProcessRaidEvent`
  reports each raid's attacker + defender clans to `/api/ingest/raid`. Compiles + loads
  clean; the hook itself is **pending a live raid** to confirm (raids can't be smoke-tested).
- **In-game hype broadcasts** (v0.5.0) — killstreak / world-first announcements printed to
  global chat. The mod reads the kill-ingest response's `broadcasts` array and sends each
  via `ServerChatUtils.SendSystemMessageToAllClients` on the game thread (`BroadcastQueue`
  bridges the HTTP-response thread → game thread, drained in the death patch). All wording
  is computed site-side — the mod just prints. Compiles + **loads clean on the live server**;
  the actual chat line is **pending a live play-test** (needs a real kill with a player online).
  Wording is **colour-coded** with TextMeshPro `<color>` tags (emoji don't render in the
  chat font — they show as boxes — so tier/hype is carried by colour, built site-side).
- **Exact PvP identity** (v0.7.0) — PvP kills now also send the **victim's SteamID**
  (`victimSteamId`, resolved from the dead player's `User.PlatformId`), so the site
  matches rivalries/nemeses to an exact player instead of guessing from the victim's
  character name (which breaks on renames / shared names). The site prefers it and
  falls back to name-matching for older rows. No new patch — one extra JSON field.
- **In-game chat commands** (v0.6.0) — players type `!rank`, `!top`, `!vbloods`, `!online`,
  `!help` in chat and get a **private** reply. A Prefix on `ChatMessageSystem.OnUpdate` reads
  each `ChatMessageEvent`, and on a known `!command` asks the site (`GET /api/mod/cmd`) for the
  ready-to-print, colour-tagged lines, then replies to just that player via
  `ServerChatUtils.SendSystemMessageToClient` (`CommandReplyQueue` bridges the async HTTP thread
  → game thread; drained from the chat + death patches). Toggle: `Chat.EnableCommands`. Compiles
  clean; **pending a live play-test** (needs a player typing in-game). The `!command` line still
  echoes in public chat (not suppressed in v1).
- **Portable build** — references the local server install via `$(VRisingServer)` /
  `VRISING_SERVER`, no NuGet game packages; fails fast if the path is wrong.
- **Resilient patching** — each hook is applied independently and logs `Patched X ✓`
  / `FAILED to patch X`, so one bad signature after a game update doesn't kill the rest.

## ✅ Also done (site side)

- **"Latest Kill: <boss>"** — the site shows each player's most recent V Blood using
  the `victim` PrefabGUID hash the mod already sends, resolved to a name via a
  hash→name map on the site (`Website/src/data/vbloods.js`) seeded with **all 64**
  V Blood bosses from the official wiki's "V Blood Unit IDs" table. No mod change was
  needed; a new boss after a game update just needs one row added on the site.

## 💡 Ideas

- **Send V Blood boss names** (S) — resolve the boss `PrefabGUID` to a readable name
  in-mod (if a prefab-name map is reachable) so the site needn't maintain a hash table.
- **Boss-difficulty weighting** (S) — send the V Blood tier so the site can score a
  late-game V Blood higher than an early one.
- ~~**Resilience**~~ (S) ✅ — on-disk kill queue (`KillQueue.cs`, v0.2.4) so kills
  survive a website outage instead of being dropped. Sessions already self-heal via
  heartbeat. Retries are harmless (site deduplicates by `eventId`).
- **Thunderstore packaging** (S) — ship a proper package via `manifest.json` if we
  ever want to distribute it beyond our own servers.

See the website's `ROADMAP.md` for the site-side leaderboard plans.
