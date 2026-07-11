using System;
using HarmonyLib;
using ProjectM;                 // DeathEventListenerSystem, DeathEvent, PlayerCharacter, VBloodUnit, VBloodConsumeSource
using ProjectM.Network;         // User (PlatformId, CharacterName)
using Stunlock.Core;            // PrefabGUID
using Unity.Collections;        // Allocator, NativeArray
using Unity.Entities;           // Entity, EntityManager, EntityQuery
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Records kills from V Rising's death events. Both V Blood boss kills and PvP kills
// come through the same DeathEventListenerSystem — a death is a V Blood kill when
// the dead entity is a V Blood unit, and a PvP kill when killer and victim are both
// players. (The earlier VBloodSystem.EventList hook only saw *consumption*/feeding,
// not the kill, so it's been dropped in favour of this.)
//
// Verified against game v1.1.13.0:
//   DeathEventListenerSystem.OnUpdate()   field _DeathEventQuery : EntityQuery
//   DeathEvent { Entity Died; Entity Killer; Entity Source; StatChangeReason }
//   PlayerCharacter { FixedString64Bytes Name; Entity UserEntity }  (ProjectM.Shared)
//   VBloodUnit / VBloodConsumeSource mark a V Blood entity          (ProjectM.Shared)
public static class KillPatchShared
{
    public static IngestClient Ingest;
    public static ManualLogSource Log;

    // Resolve the owning User for an entity that may be a player character (has
    // PlayerCharacter → UserEntity → User) or a user entity directly. False for
    // non-players. Never throws.
    public static bool TryResolveUser(EntityManager em, Entity e, out User user)
    {
        user = default;
        try
        {
            if (e == Entity.Null) return false;
            if (em.HasComponent<User>(e)) { user = em.GetComponentData<User>(e); return true; }
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
        catch (Exception ex) { Log?.LogWarning($"TryResolveUser failed: {ex.Message}"); }
        return false;
    }

    public static bool IsPlayer(EntityManager em, Entity e)
        => e != Entity.Null && em.HasComponent<PlayerCharacter>(e);

    public static bool IsVBlood(EntityManager em, Entity e)
        => e != Entity.Null && (em.HasComponent<VBloodUnit>(e) || em.HasComponent<VBloodConsumeSource>(e));

    // The dead entity's own prefab id (e.g. the V Blood boss), as a string, or "".
    public static string PrefabGuid(EntityManager em, Entity e)
    {
        try
        {
            if (e != Entity.Null && em.HasComponent<PrefabGUID>(e))
                return em.GetComponentData<PrefabGUID>(e).GuidHash.ToString();
        }
        catch { /* ignore */ }
        return "";
    }

    // Best-effort character name for a player entity (PvP victim label).
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

[HarmonyPatch(typeof(DeathEventListenerSystem), nameof(DeathEventListenerSystem.OnUpdate))]
public static class DeathEventPatch
{
    // Prefix: the death entities still exist before the system consumes them.
    public static void Prefix(DeathEventListenerSystem __instance)
    {
        var ingest = KillPatchShared.Ingest;
        if (ingest == null) return;

        // Flush any pending in-game broadcasts on the game thread. This system ticks
        // whenever a death is processed, so hype (rampages/world-firsts — themselves
        // triggered by kills) is delivered on the next death, which on an active
        // server is continuous.
        try { BroadcastQueue.Drain(__instance.EntityManager); }
        catch (Exception ex) { KillPatchShared.Log?.LogWarning($"Broadcast drain failed: {ex.Message}"); }

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
                var killerIsPlayer = KillPatchShared.IsPlayer(em, de.Killer);
                var sourceIsPlayer = KillPatchShared.IsPlayer(em, de.Source);
                var diedIsPlayer = KillPatchShared.IsPlayer(em, de.Died);
                var diedIsVBlood = KillPatchShared.IsVBlood(em, de.Died);

                // The scoring player is the killer if they're a player, else the source
                // (some kills attribute the player via Source, e.g. via a summon/DoT).
                var scorer = killerIsPlayer ? de.Killer : sourceIsPlayer ? de.Source : Entity.Null;

                // A V Blood kill requires the dead entity to be a V Blood unit AND not a
                // player — players carry VBloodConsumeSource (they can be fed on) but are
                // not bosses, so without !diedIsPlayer every PvP death was misclassified
                // as a V Blood kill (CHAR_VampireMale, GUID 38526109).
                if (diedIsVBlood && !diedIsPlayer && scorer != Entity.Null)
                {
                    if (!KillPatchShared.TryResolveUser(em, scorer, out var user)) continue;
                    ClanResolver.TryGetClan(em, user, out var clanGuid, out var clanName);
                    ingest.PostKill(user.PlatformId, user.CharacterName.ToString(), "vblood",
                        KillPatchShared.PrefabGuid(em, de.Died), clanGuid, clanName);
                    KillPatchShared.Log?.LogInfo($"  → V Blood kill for {user.CharacterName} ({user.PlatformId})");
                }
                else if (killerIsPlayer && diedIsPlayer && de.Killer != de.Died)
                {
                    if (!KillPatchShared.TryResolveUser(em, de.Killer, out var killer)) continue;
                    ClanResolver.TryGetClan(em, killer, out var clanGuid, out var clanName);
                    // Victim clan (for clan-vs-clan wars) — best effort; null if the
                    // dead player has no clan or their User doesn't resolve.
                    string victimClanGuid = null, victimClanName = null;
                    if (KillPatchShared.TryResolveUser(em, de.Died, out var victimUser))
                        ClanResolver.TryGetClan(em, victimUser, out victimClanGuid, out victimClanName);
                    ingest.PostKill(killer.PlatformId, killer.CharacterName.ToString(), "pvp",
                        KillPatchShared.NameOf(em, de.Died), clanGuid, clanName, victimClanGuid, victimClanName);
                    KillPatchShared.Log?.LogInfo($"  → PvP kill: {killer.CharacterName} killed {KillPatchShared.NameOf(em, de.Died)}");
                }
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
