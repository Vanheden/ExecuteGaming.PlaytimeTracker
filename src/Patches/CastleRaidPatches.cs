using System;
using HarmonyLib;
using ProjectM.Gameplay.Systems;  // CastleHeartEventSystem
using ProjectM.Network;           // FromCharacter, User
using Unity.Entities;             // Entity, EntityManager
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Records castle RAIDS — when a player raids another player's/clan's castle heart —
// so the site can build a raid feed + per-clan raid record, the standout of the clan
// feature. Hooks the server-side raid handler, which runs AFTER the game validates the
// raid, so we only see actual raids (not rejected attempts). The attacker arrives as
// FromCharacter; the defender is the heart's owner (see CastleRaidResolver). Both are
// resolved to a clan via ClanResolver.
//
// Verified against game v1.1.13.0 (via the metadata dumper):
//   CastleHeartEventSystem.ProcessRaidEvent(Entity heart, FromCharacter attacker, …)
//     (ProjectM.Gameplay.Systems.dll)
//   FromCharacter { Entity User; Entity Character }   (ProjectM.dll, ns ProjectM.Network)
//   CastleHeartInteractEventType has a `Raid` value — this handler is the raid path.
//
// PENDING LIVE VERIFICATION: raids are rare and can't be staged in a quick smoke test
// (they need a siege + breach), exactly like the kill hooks before v0.2.2. The patch is
// built + compile-verified; the "→ Raid:" log line lets the first real raid confirm the
// hook fires and the attacker/defender resolve. It's patched INDEPENDENTLY in Plugin.cs,
// so a wrong signature after a game update fails alone without touching session/kill
// tracking. If a future game update inlines ProcessRaidEvent (so the patch fails), the
// documented fallback is a Prefix on CastleHeartEventSystem.OnUpdate reading
// _CastleHeartInteractEventQuery for CastleHeartInteractEvent + FromCharacter (resolving
// the heart via NetworkId). See mod CLAUDE.md.
public static class CastleRaidPatchShared
{
    public static IngestClient Ingest;
    public static ManualLogSource Log;
}

[HarmonyPatch(typeof(CastleHeartEventSystem), "ProcessRaidEvent")]
public static class CastleRaidPatch
{
    // __0 = the castle-heart entity, __1 = the raiding character (FromCharacter).
    // Pure observer: returns void and never blocks (PostRaid is fire-and-forget), so it
    // can't affect the game's raid processing.
    public static void Prefix(CastleHeartEventSystem __instance, Entity __0, FromCharacter __1)
    {
        var ingest = CastleRaidPatchShared.Ingest;
        if (ingest == null) return;
        try
        {
            var em = __instance.EntityManager;

            // Attacker (the raider) — FromCharacter.User is the user entity.
            ulong atkSteam = 0;
            string atkName = null, atkClanGuid = null, atkClanName = null;
            if (em.HasComponent<User>(__1.User))
            {
                var au = em.GetComponentData<User>(__1.User);
                atkSteam = au.PlatformId;
                atkName = au.CharacterName.ToString();
                ClanResolver.TryGetClan(em, au, out atkClanGuid, out atkClanName);
            }

            // Defender (the raided) — the castle heart's owner.
            ulong defSteam = 0;
            string defName = null, defClanGuid = null, defClanName = null;
            if (CastleRaidResolver.TryGetOwner(em, __0, out var du))
            {
                defSteam = du.PlatformId;
                defName = du.CharacterName.ToString();
                ClanResolver.TryGetClan(em, du, out defClanGuid, out defClanName);
            }

            // Nothing resolved → don't record an empty raid.
            if (atkSteam == 0 && defSteam == 0 && atkClanGuid == null && defClanGuid == null)
            {
                CastleRaidPatchShared.Log?.LogWarning("Raid event with no resolvable participants — skipped.");
                return;
            }

            ingest.PostRaid(atkSteam, atkName, atkClanGuid, atkClanName,
                            defSteam, defName, defClanGuid, defClanName);
            CastleRaidPatchShared.Log?.LogInfo(
                $"  → Raid: {atkName ?? "?"} [{atkClanName ?? "no clan"}] raided " +
                $"{defName ?? "?"} [{defClanName ?? "no clan"}]");
        }
        catch (Exception ex)
        {
            CastleRaidPatchShared.Log?.LogWarning($"Raid patch failed: {ex.Message}");
        }
    }
}
