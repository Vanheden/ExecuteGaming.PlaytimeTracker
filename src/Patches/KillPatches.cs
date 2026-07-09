using System;
using HarmonyLib;
using ProjectM;                 // VBloodSystem, VBloodConsumed, DeathEventListenerSystem, DeathEvent, PlayerCharacter
using ProjectM.Network;         // User (PlatformId, CharacterName)
using Unity.Collections;        // Allocator, NativeArray
using Unity.Entities;           // Entity, EntityManager, EntityQuery
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Hooks V Rising's V Blood consumption + death events to record kills.
//
// Signatures verified against game v1.1.13.0 (see mod CLAUDE.md — dumped from the
// interop assemblies):
//   VBloodSystem.OnUpdate()                 field EventList : NativeList<VBloodConsumed>
//     VBloodConsumed { PrefabGUID Source (which V Blood); Entity Target (consumer) }
//   DeathEventListenerSystem.OnUpdate()     field _DeathEventQuery : EntityQuery
//     DeathEvent { Entity Died; Entity Killer; Entity Source; StatChangeReason }
//   PlayerCharacter { FixedString64Bytes Name; Entity UserEntity }
// If a future V Rising patch stops kills recording, re-dump these and re-check.
public static class KillPatchShared
{
    public static IngestClient Ingest;
    public static ManualLogSource Log;

    // Resolve the owning User for an entity that may be either a player *character*
    // (has PlayerCharacter → UserEntity → User) or a user entity directly. Returns
    // false for non-players (mobs, environment). Never throws.
    public static bool TryResolveUser(EntityManager em, Entity e, out User user)
    {
        user = default;
        try
        {
            if (e == Entity.Null) return false;
            if (em.HasComponent<User>(e))
            {
                user = em.GetComponentData<User>(e);
                return true;
            }
            if (em.HasComponent<PlayerCharacter>(e))
            {
                var pc = em.GetComponentData<PlayerCharacter>(e);
                if (em.HasComponent<User>(pc.UserEntity))
                {
                    user = em.GetComponentData<User>(pc.UserEntity);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"TryResolveUser failed: {ex.Message}");
        }
        return false;
    }

    // Best-effort character name for an entity (for the PvP victim label).
    public static string NameOf(EntityManager em, Entity e)
    {
        try
        {
            if (em.HasComponent<PlayerCharacter>(e))
                return em.GetComponentData<PlayerCharacter>(e).Name.ToString();
        }
        catch { /* ignore */ }
        return null;
    }
}

// V Blood boss kills. The consumer (Target) is a player; Source is the V Blood's
// PrefabGUID. Postfix reads EventList after the system has populated it this frame.
[HarmonyPatch(typeof(VBloodSystem), nameof(VBloodSystem.OnUpdate))]
public static class VBloodSystemPatch
{
    public static void Postfix(VBloodSystem __instance)
    {
        var ingest = KillPatchShared.Ingest;
        if (ingest == null) return;
        try
        {
            var em = __instance.EntityManager;
            var events = __instance.EventList;
            for (int i = 0; i < events.Length; i++)
            {
                var ev = events[i];
                if (!KillPatchShared.TryResolveUser(em, ev.Target, out var user)) continue;
                ingest.PostKill(user.PlatformId, user.CharacterName.ToString(), "vblood",
                    ev.Source.GuidHash.ToString());
                KillPatchShared.Log?.LogInfo($"V Blood: {user.CharacterName} ({user.PlatformId})");
            }
        }
        catch (Exception ex)
        {
            KillPatchShared.Log?.LogWarning($"VBlood patch failed: {ex.Message}");
        }
    }
}

// PvP kills. A death is PvP when both killer and victim are player characters and
// distinct. Prefix reads the death events before the system consumes/destroys them.
[HarmonyPatch(typeof(DeathEventListenerSystem), nameof(DeathEventListenerSystem.OnUpdate))]
public static class DeathEventPatch
{
    public static void Prefix(DeathEventListenerSystem __instance)
    {
        var ingest = KillPatchShared.Ingest;
        if (ingest == null) return;

        NativeArray<DeathEvent> deaths;
        try
        {
            deaths = __instance._DeathEventQuery.ToComponentDataArray<DeathEvent>(Allocator.Temp);
        }
        catch (Exception ex)
        {
            KillPatchShared.Log?.LogWarning($"Death query failed: {ex.Message}");
            return;
        }

        try
        {
            var em = __instance.EntityManager;
            for (int i = 0; i < deaths.Length; i++)
            {
                var de = deaths[i];
                if (de.Killer == de.Died) continue;                       // suicide/environment
                if (!em.HasComponent<PlayerCharacter>(de.Killer)) continue; // killer must be a player
                if (!em.HasComponent<PlayerCharacter>(de.Died)) continue;   // victim must be a player
                if (!KillPatchShared.TryResolveUser(em, de.Killer, out var killer)) continue;

                var victim = KillPatchShared.NameOf(em, de.Died);
                ingest.PostKill(killer.PlatformId, killer.CharacterName.ToString(), "pvp", victim);
                KillPatchShared.Log?.LogInfo($"PvP: {killer.CharacterName} killed {victim}");
            }
        }
        catch (Exception ex)
        {
            KillPatchShared.Log?.LogWarning($"Death patch failed: {ex.Message}");
        }
        finally
        {
            deaths.Dispose();
        }
    }
}
