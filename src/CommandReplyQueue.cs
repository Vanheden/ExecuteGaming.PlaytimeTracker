using System;
using System.Collections.Concurrent;
using BepInEx.Logging;
using ProjectM;                 // ServerChatUtils
using ProjectM.Network;         // User
using Unity.Collections;        // FixedString512Bytes
using Unity.Entities;           // EntityManager

namespace ExecuteGaming.PlaytimeTracker;

// Thread-safe holding pen for private chat-command replies. A player types "!rank",
// the mod fetches the answer from the website on a background thread, and enqueues
// the reply lines here paired with the requesting User. The game thread then Drain()s
// them (from the chat/death patches, which already run on-thread) and sends each
// privately to just that player via ServerChatUtils.SendSystemMessageToClient.
// Best-effort throughout: a send failure (e.g. the player left) is logged and dropped.
public static class CommandReplyQueue
{
    public static ManualLogSource Log;

    const int MaxPerDrain = 8;   // cap sends per tick so a burst can't hog a frame
    const int MaxQueued = 128;   // drop oldest beyond this (bounded during a flood)
    const int MaxChars = 400;    // FixedString512Bytes holds ~509 UTF-8 bytes; stay under

    static readonly ConcurrentQueue<(User user, string message)> _pending = new();

    public static void Enqueue(User user, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        while (_pending.Count >= MaxQueued && _pending.TryDequeue(out _)) { }
        _pending.Enqueue((user, message.Length > MaxChars ? message.Substring(0, MaxChars) : message));
    }

    // Send up to MaxPerDrain queued replies, each to its own requester. MUST run on
    // the game thread (ServerChatUtils touches ECS). No-op when empty.
    public static void Drain(EntityManager em)
    {
        if (_pending.IsEmpty) return;
        int sent = 0;
        while (sent < MaxPerDrain && _pending.TryDequeue(out var item))
        {
            try
            {
                var fs = new FixedString512Bytes(item.message);
                var user = item.user;
                ServerChatUtils.SendSystemMessageToClient(em, user, ref fs);
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"Command reply failed: {ex.Message}");
            }
            sent++;
        }
    }
}
