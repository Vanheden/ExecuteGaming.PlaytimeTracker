using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker;

// Fire-and-forget HTTP client that POSTs to the website's ingest endpoints.
// Matches the contract the site was built and tested against:
//   POST /api/ingest/session  { sessionId, serverId, steamId, charName, startedAt, endedAt?, seconds }
//   POST /api/ingest/kill     { eventId, serverId, steamId, charName, kind, victim, occurredAt }
// (both with header X-Ingest-Secret: <secret>)
// Posts run off the game thread; failures are logged and dropped. Sessions
// self-heal (the next heartbeat/disconnect re-sends and the server UPSERTs by
// sessionId); kills are idempotent server-side by a per-kill eventId, so a lost
// request just means one missing kill — never a double count on retry.
public sealed class IngestClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly PlaytimeConfig _config;
    readonly ManualLogSource _log;

    public IngestClient(PlaytimeConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
    }

    bool Enabled => !string.IsNullOrWhiteSpace(_config.IngestSecret.Value)
                    && !string.IsNullOrWhiteSpace(_config.IngestUrl.Value);

    // The kill endpoint is a sibling of the configured session endpoint: swap the
    // trailing "/session" for "/kill" so operators only configure one base URL.
    string KillUrl
    {
        get
        {
            var u = _config.IngestUrl.Value ?? "";
            const string seg = "/session";
            return u.EndsWith(seg, StringComparison.OrdinalIgnoreCase)
                ? u.Substring(0, u.Length - seg.Length) + "/kill"
                : u;
        }
    }

    // Send a session state. `endedAt == null` means the player is still online.
    public void Post(Session s, DateTime? endedAt)
    {
        if (!Enabled) return;

        var seconds = (long)Math.Max(0, ((endedAt ?? DateTime.UtcNow) - s.StartedAt).TotalSeconds);
        var json = BuildJson(s, endedAt, seconds);
        Send(_config.IngestUrl.Value, json, $"session {s.SteamId}");
    }

    // Send one kill event. `kind` is "vblood" or "pvp"; `victim` is the boss's
    // PrefabGUID hash (V Blood) or the victim's character name (PvP), or null.
    public void PostKill(ulong steamId, string charName, string kind, string victim)
    {
        if (!Enabled) return;
        var json = BuildKillJson(steamId, charName, kind, victim);
        Send(KillUrl, json, $"{kind} kill {steamId}");
    }

    // Shared fire-and-forget POST. Never blocks the game/heartbeat thread.
    void Send(string url, string json, string tag)
    {
        var secret = _config.IngestSecret.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("X-Ingest-Secret", secret);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    _log.LogWarning($"Ingest ({tag}) returned {(int)res.StatusCode}.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Ingest ({tag}) failed: {ex.Message}");
            }
        });
    }

    string BuildKillJson(ulong steamId, string charName, string kind, string victim)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        Field(sb, "eventId", Guid.NewGuid().ToString("N")); sb.Append(',');
        Field(sb, "serverId", _config.ServerId.Value); sb.Append(',');
        Field(sb, "steamId", steamId.ToString()); sb.Append(',');
        Field(sb, "charName", charName ?? ""); sb.Append(',');
        Field(sb, "kind", kind); sb.Append(',');
        Field(sb, "victim", victim ?? ""); sb.Append(',');
        Field(sb, "occurredAt", Iso(DateTime.UtcNow));
        sb.Append('}');
        return sb.ToString();
    }

    string BuildJson(Session s, DateTime? endedAt, long seconds)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        Field(sb, "sessionId", s.SessionId); sb.Append(',');
        Field(sb, "serverId", _config.ServerId.Value); sb.Append(',');
        Field(sb, "steamId", s.SteamId.ToString()); sb.Append(',');
        Field(sb, "charName", s.CharName ?? ""); sb.Append(',');
        Field(sb, "startedAt", Iso(s.StartedAt)); sb.Append(',');
        if (endedAt.HasValue) { Field(sb, "endedAt", Iso(endedAt.Value)); sb.Append(','); }
        sb.Append("\"seconds\":").Append(seconds);
        sb.Append('}');
        return sb.ToString();
    }

    static void Field(StringBuilder sb, string key, string value)
        => sb.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');

    static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // Minimal JSON string escaping (enough for names + our own fields).
    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
