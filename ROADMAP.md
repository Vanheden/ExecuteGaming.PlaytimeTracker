# Roadmap — Playtime Tracker mod

Small, focused mod, so this is short. Effort: **S** = an hour · **M** = an afternoon.

## ✅ Done

- **Playtime tracking** — connect/disconnect + heartbeat sessions, idempotent by a
  per-connect `sessionId`. Verified live end-to-end against the site.
- **V Blood + PvP kill tracking** (v0.2.0) — Harmony patches on `VBloodSystem` and
  `DeathEventListenerSystem`, POSTed to `/api/ingest/kill` (idempotent by a per-kill
  `eventId`). Built and compile-verified against the real v1.1.13.0 interop
  assemblies. Powers the Points / V Blood / PvP leaderboard tabs.
- **Portable build** — references the local server install via `$(VRisingServer)` /
  `VRISING_SERVER`, no NuGet game packages; fails fast if the path is wrong.

## 🔜 Now

- **Live-verify the kill hooks** on the two production game servers — the VBlood
  Postfix / Death Prefix timing is the only untested bit. Watch for `V Blood:` /
  `PvP:` log lines after a real kill. If V Bloods don't register, flip the VBlood
  patch Prefix↔Postfix (see `CLAUDE.md`).

## 💡 Ideas

- **Prettier V Blood victim** (S) — resolve the boss `PrefabGUID` hash to a readable
  name (e.g. "Alpha Wolf") instead of the numeric id, so the site can show it.
- **Boss-difficulty weighting** (S) — send the V Blood tier so the site can score a
  late-game V Blood higher than an early one.
- **Resilience** (S) — small on-disk queue so kills survive a website outage instead
  of being dropped (sessions already self-heal via heartbeat; kills currently don't).
- **Thunderstore packaging** (S) — ship a proper package via `manifest.json` if we
  ever want to distribute it beyond our own servers.

See the website's `ROADMAP.md` for the site-side leaderboard plans.
