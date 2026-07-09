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
- **Resilience** (S) — small on-disk queue so kills survive a website outage instead
  of being dropped (sessions already self-heal via heartbeat; kills currently don't).
- **Thunderstore packaging** (S) — ship a proper package via `manifest.json` if we
  ever want to distribute it beyond our own servers.

See the website's `ROADMAP.md` for the site-side leaderboard plans.
