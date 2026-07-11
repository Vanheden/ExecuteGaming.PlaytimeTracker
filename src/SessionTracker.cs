using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker;

// One in-progress play session. SessionId is a per-connect GUID so every
// heartbeat/disconnect for the same visit UPSERTs the same row on the server.
public sealed class Session
{
    public string SessionId { get; init; }
    public ulong SteamId { get; init; }
    public string CharName { get; set; }
    // Clan captured at event time (null = clanless). Refreshed on disconnect, since
    // the clan often isn't loaded yet at connect — same pattern as CharName.
    public string ClanGuid { get; set; }
    public string ClanName { get; set; }
    public DateTime StartedAt { get; init; }
}

// Tracks who is currently online and drives the ingest posts. Thread-safe: the
// Harmony patches call from the game thread while the heartbeat timer calls from
// a background thread, so the active set is a ConcurrentDictionary.
public sealed class SessionTracker
{
    readonly ConcurrentDictionary<ulong, Session> _active = new();
    readonly IngestClient _ingest;
    readonly ManualLogSource _log;

    public SessionTracker(IngestClient ingest, ManualLogSource log)
    {
        _ingest = ingest;
        _log = log;
    }

    // A player connected — open a session and report it (seconds = 0).
    public void Connect(ulong steamId, string charName, string clanGuid = null, string clanName = null)
    {
        var session = new Session
        {
            SessionId = Guid.NewGuid().ToString("N"),
            SteamId = steamId,
            CharName = charName,
            ClanGuid = clanGuid,
            ClanName = clanName,
            StartedAt = DateTime.UtcNow,
        };
        _active[steamId] = session;
        _log.LogInfo($"Connect: {charName} ({steamId}) → session {session.SessionId}");
        _ingest.Post(session, endedAt: null);
    }

    // A player disconnected — close their session and report the final time.
    // The character name + clan are usually populated by now (they often aren't at
    // connect time), so refresh them on the way out for a correct final record.
    public void Disconnect(ulong steamId, string charName = null, string clanGuid = null, string clanName = null)
    {
        if (_active.TryRemove(steamId, out var session))
        {
            if (!string.IsNullOrEmpty(charName)) session.CharName = charName;
            // A resolved clan on disconnect wins; keep the earlier value otherwise
            // (don't clobber a known clan with a transient null).
            if (!string.IsNullOrEmpty(clanGuid))
            {
                session.ClanGuid = clanGuid;
                session.ClanName = clanName;
            }
            _log.LogInfo($"Disconnect: {session.CharName} ({steamId})");
            _ingest.Post(session, endedAt: DateTime.UtcNow);
        }
    }

    // Called on the timer: re-report every open session with its current time.
    public void Heartbeat()
    {
        foreach (var session in _active.Values)
            _ingest.Post(session, endedAt: null);
    }

    // Called on shutdown/unload: close everyone still online (best effort).
    public void FlushAll()
    {
        var now = DateTime.UtcNow;
        foreach (var session in new List<Session>(_active.Values))
            _ingest.Post(session, endedAt: now);
        _active.Clear();
    }
}
