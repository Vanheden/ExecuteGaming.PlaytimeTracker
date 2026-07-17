using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
public sealed class IngestClient : IDisposable
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly PlaytimeConfig _config;
    readonly ManualLogSource _log;
    KillQueue _killQueue;

    public IngestClient(PlaytimeConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
    }

    // Called from Plugin.Load after the plugin directory is known, to initialise
    // the on-disk kill queue for resilient retry during website outages.
    public void InitKillQueue(string pluginDir)
    {
        _killQueue = new KillQueue(pluginDir, KillUrl, _config.IngestSecret.Value, _log);
    }

    bool Enabled => !string.IsNullOrWhiteSpace(_config.IngestSecret.Value)
                    && !string.IsNullOrWhiteSpace(_config.IngestUrl.Value);

    // The kill/raid endpoints are siblings of the configured session endpoint: swap the
    // trailing "/session" for "/kill" or "/raid" so operators only configure one URL.
    string SiblingUrl(string leaf)
    {
        var u = _config.IngestUrl.Value ?? "";
        const string seg = "/session";
        return u.EndsWith(seg, StringComparison.OrdinalIgnoreCase)
            ? u.Substring(0, u.Length - seg.Length) + leaf
            : u;
    }

    string KillUrl => SiblingUrl("/kill");
    string RaidUrl => SiblingUrl("/raid");

    // The chat-command endpoint lives under /api/mod/cmd, a sibling of /api/ingest.
    // Derive it from the configured .../api/ingest/session URL.
    string CmdUrl
    {
        get
        {
            var u = _config.IngestUrl.Value ?? "";
            const string seg = "/ingest/session";
            return u.EndsWith(seg, StringComparison.OrdinalIgnoreCase)
                ? u.Substring(0, u.Length - seg.Length) + "/mod/cmd"
                : u;
        }
    }

    // Fetch a chat command's reply lines from the website and hand them to `onLines`
    // (invoked on a background thread). Fire-and-forget: any failure is logged and the
    // command silently produces no reply. `cmd` is a known command name (rank/top/…).
    public void FetchCommand(ulong steamId, string cmd, string charName, Action<string[]> onLines)
    {
        if (!Enabled || onLines == null) return;
        var url = CmdUrl
            + "?cmd=" + Uri.EscapeDataString(cmd)
            + "&steamId=" + steamId
            + "&charName=" + Uri.EscapeDataString(charName ?? "");
        var secret = _config.IngestSecret.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Ingest-Secret", secret);
                using var res = await Http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return;
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(body)) return;
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("lines", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                    return;
                var lines = new System.Collections.Generic.List<string>();
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
                }
                if (lines.Count > 0) onLines(lines.ToArray());
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Command fetch ({cmd}) failed: {ex.Message}");
            }
        });
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
    // clanGuid/clanName is the scorer's clan; victimClan* is the dead player's clan
    // (PvP only) for clan-vs-clan wars. All clan fields may be null (clanless).
    public void PostKill(ulong steamId, string charName, string kind, string victim,
        string clanGuid = null, string clanName = null, string victimClanGuid = null, string victimClanName = null,
        ulong victimSteamId = 0)
    {
        if (!Enabled) return;
        var json = BuildKillJson(steamId, charName, kind, victim, clanGuid, clanName, victimClanGuid, victimClanName, victimSteamId);
        Send(KillUrl, json, $"{kind} kill {steamId}", isKill: true);
    }

    // Send one castle raid. `attacker*` is the raider (a steamId of 0 / null clan means
    // unresolved); `defender*` is the raided castle's owner. Fire-and-forget; a lost
    // raid POST just means one missing raid (idempotent server-side by eventId).
    public void PostRaid(ulong attackerSteamId, string attackerName, string attackerClanGuid, string attackerClanName,
        ulong defenderSteamId, string defenderName, string defenderClanGuid, string defenderClanName)
    {
        if (!Enabled) return;
        var json = BuildRaidJson(attackerSteamId, attackerName, attackerClanGuid, attackerClanName,
            defenderSteamId, defenderName, defenderClanGuid, defenderClanName);
        Send(RaidUrl, json, $"raid {attackerSteamId}->{defenderSteamId}");
    }

    // Shared fire-and-forget POST. Never blocks the game/heartbeat thread.
    // Kill POSTs that fail are enqueued for retry via the on-disk KillQueue.
    void Send(string url, string json, string tag, bool isKill = false)
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
                {
                    _log.LogWarning($"Ingest ({tag}) returned {(int)res.StatusCode}.");
                    if (isKill && _killQueue != null)
                        _killQueue.Enqueue(json);
                }
                else if (isKill)
                {
                    // A successful kill POST may return hype to announce in-game.
                    await HandleKillResponse(res).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Ingest ({tag}) failed: {ex.Message}");
                if (isKill && _killQueue != null)
                    _killQueue.Enqueue(json);
            }
        });
    }

    // Parse a kill response's optional `broadcasts` array and queue each string for
    // the game thread to send to all clients. Best-effort — a malformed body or a
    // missing array is simply ignored (most kills carry no broadcasts).
    async Task HandleKillResponse(HttpResponseMessage res)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(body)) return;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("broadcasts", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return;
            foreach (var el in arr.EnumerateArray())
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                BroadcastQueue.Enqueue(s);
                _log.LogInfo($"Broadcast queued: {s}");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Kill response parse failed: {ex.Message}");
        }
    }

    string BuildKillJson(ulong steamId, string charName, string kind, string victim,
        string clanGuid, string clanName, string victimClanGuid, string victimClanName, ulong victimSteamId)
    {
        var sb = new StringBuilder(340);
        sb.Append('{');
        Field(sb, "eventId", Guid.NewGuid().ToString("N")); sb.Append(',');
        Field(sb, "serverId", _config.ServerId.Value); sb.Append(',');
        Field(sb, "steamId", steamId.ToString()); sb.Append(',');
        Field(sb, "charName", charName ?? ""); sb.Append(',');
        Field(sb, "kind", kind); sb.Append(',');
        Field(sb, "victim", victim ?? ""); sb.Append(',');
        // Clan fields (empty when clanless) — the site treats empty as "no clan".
        Field(sb, "clanGuid", clanGuid ?? ""); sb.Append(',');
        Field(sb, "clanName", clanName ?? ""); sb.Append(',');
        Field(sb, "victimClanGuid", victimClanGuid ?? ""); sb.Append(',');
        Field(sb, "victimClanName", victimClanName ?? ""); sb.Append(',');
        // Victim SteamID (PvP only; 0 = unknown → empty). Lets the site resolve
        // rivalries exactly instead of guessing from the victim's character name.
        Field(sb, "victimSteamId", victimSteamId == 0 ? "" : victimSteamId.ToString()); sb.Append(',');
        Field(sb, "occurredAt", Iso(DateTime.UtcNow));
        sb.Append('}');
        return sb.ToString();
    }

    // Raid payload. steamIds of 0 and null clans are sent as empty strings; the site
    // treats empty/absent as "unknown" (so a clanless solo raider still records).
    string BuildRaidJson(ulong attackerSteamId, string attackerName, string attackerClanGuid, string attackerClanName,
        ulong defenderSteamId, string defenderName, string defenderClanGuid, string defenderClanName)
    {
        var sb = new StringBuilder(360);
        sb.Append('{');
        Field(sb, "eventId", Guid.NewGuid().ToString("N")); sb.Append(',');
        Field(sb, "serverId", _config.ServerId.Value); sb.Append(',');
        Field(sb, "kind", "raid"); sb.Append(',');
        Field(sb, "attackerSteamId", attackerSteamId == 0 ? "" : attackerSteamId.ToString()); sb.Append(',');
        Field(sb, "attackerName", attackerName ?? ""); sb.Append(',');
        Field(sb, "attackerClanGuid", attackerClanGuid ?? ""); sb.Append(',');
        Field(sb, "attackerClanName", attackerClanName ?? ""); sb.Append(',');
        Field(sb, "defenderSteamId", defenderSteamId == 0 ? "" : defenderSteamId.ToString()); sb.Append(',');
        Field(sb, "defenderName", defenderName ?? ""); sb.Append(',');
        Field(sb, "defenderClanGuid", defenderClanGuid ?? ""); sb.Append(',');
        Field(sb, "defenderClanName", defenderClanName ?? ""); sb.Append(',');
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
        Field(sb, "clanGuid", s.ClanGuid ?? ""); sb.Append(',');
        Field(sb, "clanName", s.ClanName ?? ""); sb.Append(',');
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

    public void Dispose() => _killQueue?.Dispose();
}
