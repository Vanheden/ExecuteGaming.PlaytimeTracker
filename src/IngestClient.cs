using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker;

// Fire-and-forget HTTP client that POSTs one session upsert to the website.
// Matches the contract the site was built and tested against:
//   POST /api/ingest/session   (header X-Ingest-Secret: <secret>)
//   { sessionId, serverId, steamId, charName, startedAt, endedAt?, seconds }
// Posts run off the game thread; failures are logged and dropped — the next
// heartbeat (or the disconnect) re-sends the latest state, so a lost request
// self-heals and nothing is double-counted (the server UPSERTs by sessionId).
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

    // Send a session state. `endedAt == null` means the player is still online.
    public void Post(Session s, DateTime? endedAt)
    {
        if (!Enabled) return;

        var seconds = (long)Math.Max(0, ((endedAt ?? DateTime.UtcNow) - s.StartedAt).TotalSeconds);
        var json = BuildJson(s, endedAt, seconds);
        var url = _config.IngestUrl.Value;
        var secret = _config.IngestSecret.Value;

        // Don't await — never block the game/heartbeat thread on network I/O.
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("X-Ingest-Secret", secret);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    _log.LogWarning($"Ingest for {s.SteamId} returned {(int)res.StatusCode}.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Ingest for {s.SteamId} failed: {ex.Message}");
            }
        });
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
