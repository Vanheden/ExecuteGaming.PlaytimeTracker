using System;
using System.Collections.Concurrent;
using BepInEx.Logging;
using ProjectM;                 // ServerChatUtils
using Unity.Collections;        // FixedString512Bytes
using Unity.Entities;           // EntityManager

namespace ExecuteGaming.PlaytimeTracker;

// Thread-safe holding pen for in-game broadcast messages (killstreak/world-first
// hype the website returns in the kill-ingest response). HTTP responses arrive on
// background threads, but ECS/chat calls must run on the game thread — so producers
// Enqueue() here and the game thread Drain()s (called from a Harmony patch that
// already runs on-thread) to send them to all clients via ServerChatUtils.
// Best-effort throughout: a failure is logged and swallowed, never propagated.
public static class BroadcastQueue
{
    public static ManualLogSource Log;

    const int MaxPerDrain = 6;  // cap sends per tick so a burst can't hog a frame
    const int MaxQueued = 64;   // drop oldest beyond this (bounded during a flood)
    const int MaxChars = 200;   // FixedString512Bytes holds ~509 UTF-8 bytes; stay well under

    static readonly ConcurrentQueue<string> _pending = new();

    public static void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        while (_pending.Count >= MaxQueued && _pending.TryDequeue(out _)) { }
        _pending.Enqueue(message.Length > MaxChars ? message.Substring(0, MaxChars) : message);
    }

    // Send up to MaxPerDrain queued messages to every connected client. MUST be
    // called from the game thread (ServerChatUtils touches ECS). No-op when empty.
    public static void Drain(EntityManager em)
    {
        if (_pending.IsEmpty) return;
        int sent = 0;
        while (sent < MaxPerDrain && _pending.TryDequeue(out var msg))
        {
            try
            {
                var fs = new FixedString512Bytes(msg);
                ServerChatUtils.SendSystemMessageToAllClients(em, ref fs);
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"Broadcast failed: {ex.Message}");
            }
            sent++;
        }
    }
}
