# Execute-Gaming Playtime Tracker

A server-side **BepInEx (IL2CPP) mod for V Rising** that reports per-player play
sessions to the Execute-Gaming website, which powers the **playtime leaderboard**
at [execute-gaming.se/leaderboard](https://execute-gaming.se/leaderboard).

This is the game-server half of the leaderboard feature. The website half (the
ingest endpoint, aggregation, and the `/leaderboard` page) already lives in the
`Website/` project and was built and tested against the exact contract below.

## How it works

- Harmony-patches `ServerBootstrapSystem.OnUserConnected` / `OnUserDisconnected`
  to know when a player joins and leaves, reading their **SteamID** and
  **character name** from the `User` component.
- On connect it opens a session (a fresh GUID) and reports it. A timer re-reports
  every open session on a **heartbeat** (default 5 min), and disconnect/shutdown
  closes it — so a crash loses at most one heartbeat interval.
- Reports go to `POST {IngestUrl}` with an `X-Ingest-Secret` header. Everything is
  keyed by the per-connect `sessionId`, so the website **UPSERTs** and never
  double-counts, and a dropped request self-heals on the next heartbeat.

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

### Verifying

- The BepInEx console logs `Connect:` / `Disconnect:` lines as players come and go.
- Join the server, then load `…/leaderboard` — your character should appear once
  a heartbeat or your disconnect has posted some time. Steam-linked accounts show
  their site username and link to their profile; others show the character name.

## Keeping the mod alive

V Rising patches can rename or reshape the hooked methods. If reporting stops
after a game update, re-check these against the current assemblies (all in
`src/Patches/ServerBootstrapPatches.cs`):

- `ServerBootstrapSystem.OnUserConnected(int userIndex)`
- `ServerBootstrapSystem.OnUserDisconnected(int userIndex, …)`
- the `User` fields `PlatformId` and `CharacterName`

Then bump the package versions in the `.csproj` and rebuild.

## Roadmap: points

The leaderboard currently ranks by **time**. A future **points** layer (V Blood /
boss kills, PvP kills) can be added by patching the relevant game events and
POSTing them alongside sessions — no new data source needed, since it builds on
the same ingest path.
