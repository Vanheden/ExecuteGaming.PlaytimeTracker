using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker;

// On-disk kill queue — survives a website outage. When a kill POST fails (non-2xx
// or exception), the JSON body is appended to a file. A background timer retries
// queued kills every 2 minutes. The site deduplicates by eventId (INSERT OR IGNORE),
// so a retry after the site already received it is harmless.
//
// File format: one JSON object per line (NDJSON). File lives next to the plugin
// DLL under BepInEx/plugins/killqueue.ndjson. Capped at 500 entries (oldest
// dropped) to avoid unbounded growth during a long outage.
public sealed class KillQueue : IDisposable
{
    const int MaxEntries = 500;
    const int RetryIntervalMs = 120_000; // 2 minutes

    readonly string _queuePath;
    readonly string _killUrl;
    readonly string _secret;
    readonly ManualLogSource _log;
    readonly Timer _retryTimer;
    readonly object _lock = new();
    readonly Queue<string> _pending = new();

    public KillQueue(string pluginDir, string killUrl, string secret, ManualLogSource log)
    {
        _queuePath = Path.Combine(pluginDir, "killqueue.ndjson");
        _killUrl = killUrl;
        _secret = secret;
        _log = log;
        LoadFromDisk();
        _retryTimer = new Timer(_ => RetrySweep(), null, RetryIntervalMs, RetryIntervalMs);
    }

    // Enqueue a failed kill JSON body. Called from IngestClient when a POST fails.
    public void Enqueue(string jsonBody)
    {
        lock (_lock)
        {
            if (_pending.Count >= MaxEntries)
                _pending.Dequeue(); // drop oldest
            _pending.Enqueue(jsonBody);
            AppendToDisk(jsonBody);
        }
    }

    // Try to send all queued kills. Runs on a timer thread.
    async void RetrySweep()
    {
        List<string> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = new List<string>(_pending);
        }

        var remaining = new List<string>();
        foreach (var json in batch)
        {
            if (await TrySend(json))
                continue; // success — drop from queue
            remaining.Add(json); // still failing — keep for next sweep
        }

        lock (_lock)
        {
            _pending.Clear();
            foreach (var j in remaining) _pending.Enqueue(j);
            RewriteDisk();
        }

        if (remaining.Count > 0)
            _log.LogInfo($"Kill queue: {remaining.Count} kill(s) still pending, will retry.");
        else if (batch.Count > 0)
            _log.LogInfo($"Kill queue: flushed {batch.Count} queued kill(s).");
    }

    async Task<bool> TrySend(string jsonBody)
    {
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, _killUrl);
            req.Headers.Add("X-Ingest-Secret", _secret);
            req.Content = new System.Net.Http.StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var res = await IngestClient.Http.SendAsync(req).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_queuePath)) return;
            foreach (var line in File.ReadAllLines(_queuePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _pending.Enqueue(line.Trim());
                if (_pending.Count >= MaxEntries) break;
            }
            if (_pending.Count > 0)
                _log.LogInfo($"Kill queue: loaded {_pending.Count} pending kill(s) from disk.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Kill queue: failed to load from disk: {ex.Message}");
        }
    }

    void AppendToDisk(string jsonBody)
    {
        try
        {
            File.AppendAllText(_queuePath, jsonBody + "\n");
        }
        catch
        {
            // Best effort — the in-memory queue still holds it.
        }
    }

    void RewriteDisk()
    {
        try
        {
            if (_pending.Count == 0)
            {
                if (File.Exists(_queuePath)) File.Delete(_queuePath);
                return;
            }
            var sb = new StringBuilder();
            foreach (var j in _pending) sb.AppendLine(j);
            File.WriteAllText(_queuePath, sb.ToString());
        }
        catch
        {
            // Best effort.
        }
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();
    }
}
