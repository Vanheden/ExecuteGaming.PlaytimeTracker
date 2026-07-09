# Execute-Gaming Playtime Tracker

A server-side **BepInEx (IL2CPP) mod for V Rising** that reports per-player
**playtime, V Blood boss kills and PvP kills** to the Execute-Gaming website, which
powers the **leaderboard** at
[execute-gaming.se/leaderboard](https://execute-gaming.se/leaderboard) (ranked by
Points, Playtime, V Blood or PvP kills).

This is the game-server half of the leaderboard feature. The website half (the
ingest endpoints, aggregation, and the `/leaderboard` page) already lives in the
`Website/` project and was built and tested against the exact contract below.

## How it works

**Playtime**
- Harmony-patches `ServerBootstrapSystem.OnUserConnected` / `OnUserDisconnected`
  to know when a player joins and leaves, reading their **SteamID** and
  **character name** from the `User` component.
- On connect it opens a session (a fresh GUID) and reports it. A timer re-reports
  every open session on a **heartbeat** (default 5 min), and disconnect/shutdown
  closes it — so a crash loses at most one heartbeat interval.
- Keyed by the per-connect `sessionId`, so the website **UPSERTs** and never
  double-counts, and a dropped request self-heals on the next heartbeat.

**Kills**
- Harmony-patches `VBloodSystem.OnUpdate` (V Blood consumed = boss killed) and
  `DeathEventListenerSystem.OnUpdate` (a death where killer and victim are both
  players = a PvP kill).
- Each kill is POSTed once with a fresh per-kill `eventId`; the website does
  `INSERT OR IGNORE`, so a network retry can never double-count.

All reports go with an `X-Ingest-Secret` header. The kill endpoint is derived from
`Url` automatically (`…/session` → `…/kill`), so you only configure one URL.

### The contract (must match the website)

```
POST /api/ingest/session
Header: X-Ingest-Secret: <shared secret>
{
  "sessionId": "<per-connect GUID>",
  "serverId":  "vrising-pve",
  "steamId":   "7656119...",
  "charName":  "Vlad",
  "startedAt": "2026-07-09T18:00:00.000Z",
  "endedAt":   "2026-07-09T19:30:00.000Z",   // omitted while still online
  "seconds":   5400
}

POST /api/ingest/kill            // same host, derived from Url
Header: X-Ingest-Secret: <shared secret>
{
  "eventId":    "<per-kill GUID>",
  "serverId":   "vrising-pve",
  "steamId":    "7656119...",
  "charName":   "Vlad",
  "kind":       "vblood",          // or "pvp"
  "victim":     "-1905691330",     // V Blood PrefabGUID hash, or PvP victim's name
  "occurredAt": "2026-07-09T18:20:00.000Z"
}
```

## Configuration

On first run the mod writes `BepInEx/config/se.execute-gaming.playtimetracker.cfg`:

| Key | Meaning |
|-----|---------|
| `[Ingest] Url` | The website ingest URL (default `https://execute-gaming.se/api/ingest/session`). |
| `[Ingest] Secret` | **Must equal `INGEST_SECRET` in the website's `.env`.** Blank = reporting off. |
| `[Ingest] ServerId` | This instance's id from `src/data/servers.js` — `vrising-pve` or `vrising-duo`. |
| `[Ingest] HeartbeatMinutes` | How often open sessions are re-reported (default 5). |

Run **one instance of the mod per game server**, each with its own `ServerId`.

## Building

Requires the .NET 6 SDK. The `.csproj` compiles against the BepInEx + V Rising
interop assemblies from a **real dedicated-server install** (no NuGet game
packages), so it always matches your exact game version. The interop assemblies
are generated the first time the server is launched with BepInEx — boot it once
if `BepInEx/interop/` is empty.

Point the build at that install (in priority order):

1. `VRISING_SERVER` environment variable (recommended for a fresh clone):
   ```bash
   setx VRISING_SERVER "C:\VRisingServer\serverfiles"   # Windows, once
   dotnet build -c Release
   ```
2. Or a one-off command-line override:
   ```bash
   dotnet build -c Release -p:VRisingServer="C:\VRisingServer\serverfiles"
   ```
3. Or just `dotnet build -c Release` to use the per-machine default baked into the
   `.csproj`.

`VRisingServer` is the **serverfiles** folder — the one with `VRisingServer.exe`
and a `BepInEx/` subfolder. If it's wrong or unbooted, the build fails fast with a
clear message instead of a wall of missing-type errors.

The output DLL is `bin/Release/ExecuteGaming.PlaytimeTracker.dll`.

## Installing on the server

1. The server needs **BepInExPack V Rising** installed (the mod uses only BepInEx
   + Harmony + the game assemblies — no other runtime dependency).
2. Copy `ExecuteGaming.PlaytimeTracker.dll` into `BepInEx/plugins/`.
3. Start the server once to generate the config, set `Secret` + `ServerId`, then
   restart.
4. On the website VM, set `INGEST_SECRET` in `.env` to the **same** secret and
   restart the site. (If it's blank, `/api/ingest/session` returns 503 and the
   leaderboard just stays empty — a safe "off" state.)

## Deploying to your live servers

Run the **same DLL** on every game server; each just needs its own `.cfg` with the
right `ServerId`. For each server:

1. **Stop** the server — the DLL in `BepInEx/plugins/` is **file-locked while it
   runs**, so you can't overwrite it live.
2. Copy `bin/Release/ExecuteGaming.PlaytimeTracker.dll` → `BepInEx/plugins/`.
3. Start once to generate the `.cfg` (or copy a prepared one), stop again, and set:
   ```ini
   [Ingest]
   Url = https://execute-gaming.se/api/ingest/session   ; kill URL is derived (/kill)
   Secret = <exactly the site's INGEST_SECRET>
   ServerId = vrising-pve                                 ; vrising-duo on the other
   HeartbeatMinutes = 5
   ```
4. **Start** the server.

On the website VM, set the **same** `INGEST_SECRET` in `.env` and restart the site.

### Verifying

- On load the console logs `Playtime Tracker loaded — … reporting=on` (`on` means a
  secret is set). It also logs `Connect:` / `Disconnect:`, `V Blood:` and `PvP:`
  lines as those events happen.
- Join the server, then load `…/leaderboard` — your character should appear once a
  heartbeat, kill, or disconnect has posted. Steam-linked accounts show their site
  username and link to their profile; others show the character name.
- **Empty leaderboard?** Check the game-server log: `reporting=OFF` = blank secret;
  `Ingest (…) returned 401` = secret doesn't match the site; a connection error to
  `localhost` = `Url` still points at the test value. The site is almost never the
  problem (confirm with a `POST …/api/ingest/session` — `401` means the site is up
  and the mismatch is on the mod side).

## Keeping the mod alive

V Rising patches can rename or reshape the hooked methods. If reporting stops after
a game update, re-dump the interop assemblies (see `CLAUDE.md`) and re-check these:

- `ServerBootstrapSystem.OnUserConnected(NetConnectionId)` /
  `OnUserDisconnected(NetConnectionId, ConnectionStatusChangeReason, string)`
  (`src/Patches/ServerBootstrapPatches.cs`), and the `User` fields `PlatformId`
  and `CharacterName`.
- `VBloodSystem.OnUpdate` (`EventList : NativeList<VBloodConsumed>`) and
  `DeathEventListenerSystem.OnUpdate` (`_DeathEventQuery` → `DeathEvent`)
  (`src/Patches/KillPatches.cs`).

Then bump the version in `.csproj` / `manifest.json` / `Plugin.cs` and rebuild.

See `ROADMAP.md` for what's next.
