using System;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using ExecuteGaming.PlaytimeTracker.Patches;

namespace ExecuteGaming.PlaytimeTracker;

[BepInPlugin(GUID, "Execute-Gaming Playtime Tracker", "0.2.0")]
[BepInProcess("VRisingServer.exe")]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "se.execute-gaming.playtimetracker";

    static ManualLogSource _log;
    Harmony _harmony;
    Timer _heartbeat;
    SessionTracker _tracker;

    public override void Load()
    {
        _log = Log;

        var config = new PlaytimeConfig(Config);
        var ingest = new IngestClient(config, Log);
        _tracker = new SessionTracker(ingest, Log);

        // Make the tracker + ingest client reachable from the static Harmony patches.
        BootstrapPatchShared.Tracker = _tracker;
        BootstrapPatchShared.Log = Log;
        KillPatchShared.Ingest = ingest;
        KillPatchShared.Log = Log;

        _harmony = new Harmony(GUID);
        _harmony.PatchAll(typeof(Plugin).Assembly);

        // Heartbeat: re-report open sessions so a crash loses at most one interval.
        var period = TimeSpan.FromMinutes(Math.Max(1, config.HeartbeatMinutes.Value));
        _heartbeat = new Timer(_ => SafeHeartbeat(), null, period, period);

        Log.LogInfo(
            $"Playtime Tracker loaded — serverId='{config.ServerId.Value}', " +
            $"heartbeat={period.TotalMinutes}min, " +
            $"reporting={(string.IsNullOrWhiteSpace(config.IngestSecret.Value) ? "OFF (no secret)" : "on")}.");
    }

    void SafeHeartbeat()
    {
        try { _tracker.Heartbeat(); }
        catch (Exception ex) { _log?.LogWarning($"Heartbeat failed: {ex.Message}"); }
    }

    public override bool Unload()
    {
        _heartbeat?.Dispose();
        try { _tracker?.FlushAll(); } catch { /* best effort on shutdown */ }
        _harmony?.UnpatchSelf();
        return true;
    }
}
