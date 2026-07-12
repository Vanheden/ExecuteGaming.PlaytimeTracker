using BepInEx.Configuration;

namespace ExecuteGaming.PlaytimeTracker;

// All operator-tunable settings, written to
// BepInEx/config/se.execute-gaming.playtimetracker.cfg on first run.
public sealed class PlaytimeConfig
{
    public ConfigEntry<string> IngestUrl { get; }
    public ConfigEntry<string> IngestSecret { get; }
    public ConfigEntry<string> ServerId { get; }
    public ConfigEntry<int> HeartbeatMinutes { get; }
    public ConfigEntry<bool> EnableChatCommands { get; }

    public PlaytimeConfig(ConfigFile cfg)
    {
        IngestUrl = cfg.Bind(
            "Ingest", "Url", "https://execute-gaming.se/api/ingest/session",
            "Full URL of the website's session ingest endpoint.");

        IngestSecret = cfg.Bind(
            "Ingest", "Secret", "",
            "Shared secret — must match INGEST_SECRET in the website's .env. " +
            "Sent as the X-Ingest-Secret header. Leave blank to disable reporting.");

        ServerId = cfg.Bind(
            "Ingest", "ServerId", "vrising-pve",
            "Which server this instance is, matching an id in the site's " +
            "src/data/servers.js (e.g. vrising-pve or vrising-duo).");

        HeartbeatMinutes = cfg.Bind(
            "Ingest", "HeartbeatMinutes", 5,
            "How often to re-report each open session so a crash loses at most " +
            "this many minutes of playtime. Keep it >= a couple of minutes.");

        EnableChatCommands = cfg.Bind(
            "Chat", "EnableCommands", true,
            "Answer in-game chat commands (!rank, !top, !vbloods, !online, !help) by " +
            "querying the website and replying privately to the player. Needs the " +
            "Ingest secret/url set.");
    }
}
