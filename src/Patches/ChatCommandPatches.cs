using System;
using HarmonyLib;
using ProjectM;                 // ChatMessageSystem
using ProjectM.Network;         // ChatMessageEvent, FromCharacter, User
using Unity.Collections;        // Allocator, NativeArray
using Unity.Entities;           // Entity, EntityManager
using Unity.Mathematics;        // float3
using Unity.Transforms;         // LocalTransform
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Intercepts in-game "!rank / !top / !vbloods / !online / !help" chat commands and
// answers each privately to the sender. The mod stays thin: it recognises a known
// command, asks the website for the ready-to-print lines (with TextMeshPro colour
// tags), and the CommandReplyQueue prints them on the game thread. All wording lives
// site-side, same split as the kill broadcasts.
//
// Verified against game v1.1.13.0:
//   ChatMessageSystem.OnUpdate()          field _ChatMessageQuery : EntityQuery
//   ChatMessageEvent { ChatMessageType MessageType; FixedString512Bytes MessageText; NetworkId ReceiverEntity }
//   FromCharacter { Entity User; Entity Character }
//   ServerChatUtils.SendSystemMessageToClient(EntityManager, User, ref FixedString512Bytes)
[HarmonyPatch(typeof(ChatMessageSystem), nameof(ChatMessageSystem.OnUpdate))]
public static class ChatCommandPatch
{
    public static IngestClient Ingest;
    public static ManualLogSource Log;
    public static bool Enabled = true;

    static readonly string[] Known = { "rank", "top", "vbloods", "online", "help", "commands", "vendor" };

    // Prefix: the chat event entities still exist before the system consumes them.
    public static void Prefix(ChatMessageSystem __instance)
    {
        EntityManager em;
        try { em = __instance.EntityManager; } catch { return; }

        // Flush any pending private replies on the game thread (they arrive async).
        try { CommandReplyQueue.Drain(em); }
        catch (Exception ex) { Log?.LogWarning($"Reply drain failed: {ex.Message}"); }

        if (!Enabled || Ingest == null) return;

        NativeArray<Entity> ents;
        try { ents = __instance._ChatMessageQuery.ToEntityArray(Allocator.Temp); }
        catch (Exception ex) { Log?.LogWarning($"Chat query failed: {ex.Message}"); return; }

        // `!vendor` spawns an entity (a structural change), which we defer until AFTER
        // the query array is disposed so we don't mutate ECS mid-iteration.
        bool spawnVendor = false;
        User vendorUser = default;
        float3 vendorPos = default;

        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                if (!em.HasComponent<ChatMessageEvent>(e) || !em.HasComponent<FromCharacter>(e)) continue;

                var ev = em.GetComponentData<ChatMessageEvent>(e);
                var text = ev.MessageText.ToString();
                if (string.IsNullOrEmpty(text) || text[0] != '!') continue;

                var cmd = ParseCommand(text);
                if (cmd == null) continue;

                var from = em.GetComponentData<FromCharacter>(e);
                if (!em.HasComponent<User>(from.User)) continue;
                var user = em.GetComponentData<User>(from.User);

                // Admin-only local command: spawn the reward vendor at the caller's feet.
                if (cmd == "vendor")
                {
                    if (!user.IsAdmin)
                    {
                        CommandReplyQueue.Enqueue(user, "<color=#ff4d63>Only admins can spawn the vendor.</color>");
                    }
                    else if (em.HasComponent<LocalTransform>(from.Character))
                    {
                        vendorPos = em.GetComponentData<LocalTransform>(from.Character).Position;
                        vendorUser = user;
                        spawnVendor = true;
                    }
                    continue;
                }

                var steamId = user.PlatformId;
                var charName = user.CharacterName.ToString();
                var capturedUser = user; // captured by value for the async reply

                Ingest.FetchCommand(steamId, cmd, charName, lines =>
                {
                    foreach (var l in lines) CommandReplyQueue.Enqueue(capturedUser, l);
                });
                Log?.LogInfo($"Chat command '!{cmd}' from {charName} ({steamId})");
            }
        }
        catch (Exception ex) { Log?.LogWarning($"Chat command patch failed: {ex.Message}"); }
        finally { ents.Dispose(); }

        // Deferred spawn (structural change) — safe now the query array is disposed.
        if (spawnVendor)
        {
            var ok = VendorSpawner.Spawn(em, __instance.World, vendorPos, out _);
            CommandReplyQueue.Enqueue(vendorUser, ok
                ? "<color=#7cf267>Vendor spawned next to you.</color>"
                : "<color=#ff4d63>Vendor spawn failed — check the server log.</color>");
        }
    }

    // Normalised command name for a known "!cmd ..." message, else null (ignored).
    static string ParseCommand(string text)
    {
        var body = text.Substring(1).Trim();
        if (body.Length == 0) return null;
        var sp = body.IndexOf(' ');
        var cmd = (sp < 0 ? body : body.Substring(0, sp)).ToLowerInvariant();
        foreach (var k in Known)
            if (cmd == k) return cmd == "commands" ? "help" : cmd;
        return null;
    }
}
