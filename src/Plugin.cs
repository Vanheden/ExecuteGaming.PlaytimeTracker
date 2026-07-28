using System;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using ExecuteGaming.PlaytimeTracker.Patches;

namespace ExecuteGaming.PlaytimeTracker;

[BepInPlugin(GUID, "Execute-Gaming Playtime Tracker", "0.8.0")]
[BepInProcess("VRisingServer.exe")]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "se.execute-gaming.playtimetracker";

    static ManualLogSource _log;
    Harmony _harmony;
    Timer _heartbeat;
    SessionTracker _tracker;
    IngestClient _ingest;

    public override void Load()
    {
        _log = Log;

        var config = new PlaytimeConfig(Config);
        _ingest = new IngestClient(config, Log);
        var ingest = _ingest;
        _tracker = new SessionTracker(ingest, Log);

        // Initialise the on-disk kill queue (plugin directory for the queue file).
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(pluginDir) && !string.IsNullOrWhiteSpace(config.IngestSecret.Value))
            ingest.InitKillQueue(pluginDir);

        // Make the tracker + ingest client reachable from the static Harmony patches.
        BootstrapPatchShared.Tracker = _tracker;
        BootstrapPatchShared.Log = Log;
        KillPatchShared.Ingest = ingest;
        KillPatchShared.Log = Log;
        BroadcastQueue.Log = Log;
        ClanResolver.Log = Log;
        CastleRaidPatchShared.Ingest = ingest;
        CastleRaidPatchShared.Log = Log;
        CastleRaidResolver.Log = Log;
        ChatCommandPatch.Ingest = ingest;
        ChatCommandPatch.Log = Log;
        ChatCommandPatch.Enabled = config.EnableChatCommands.Value;
        CommandReplyQueue.Log = Log;
        VendorSpawner.Log = Log;

        _harmony = new Harmony(GUID);
        ApplyPatches();

        // Heartbeat: re-report open sessions so a crash loses at most one interval.
        var period = TimeSpan.FromMinutes(Math.Max(1, config.HeartbeatMinutes.Value));
        _heartbeat = new Timer(_ => SafeHeartbeat(), null, period, period);

        Log.LogInfo(
            $"Playtime Tracker loaded — serverId='{config.ServerId.Value}', " +
            $"heartbeat={period.TotalMinutes}min, " +
            $"reporting={(string.IsNullOrWhiteSpace(config.IngestSecret.Value) ? "OFF (no secret)" : "on")}.");
    }

    // Patch each hook independently so one bad signature (e.g. after a game update)
    // doesn't stop the others, and the log shows exactly what applied.
    void ApplyPatches()
    {
        var types = new[]
        {
            typeof(OnUserConnectedPatch),
            typeof(OnUserDisconnectedPatch),
            typeof(DeathEventPatch),
            typeof(CastleRaidPatch),
            typeof(ChatCommandPatch),
        };
        foreach (var t in types)
        {
            try
            {
                _harmony.PatchAll(t);
                Log.LogInfo($"Patched {t.Name} ✓");
            }
            catch (Exception ex)
            {
                Log.LogError($"FAILED to patch {t.Name}: {ex.Message}");
            }
        }
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
        _ingest?.Dispose();
        _harmony?.UnpatchSelf();
        return true;
    }
}
