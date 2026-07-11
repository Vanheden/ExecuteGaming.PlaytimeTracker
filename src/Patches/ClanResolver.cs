using System;
using ProjectM;                 // ClanTeam (ProjectM.Shared.dll)
using ProjectM.Network;         // User
using Unity.Entities;           // Entity, EntityManager
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Resolves a player's clan to a stable GUID + display name, sent alongside every
// session/kill so the site can build clan leaderboards and clan-vs-clan wars.
//
// Verified against game v1.1.13.0 (via the metadata dumper):
//   User.ClanEntity : NetworkedEntity           (ProjectM.Shared, ProjectM.Network.User)
//   NetworkedEntity._Entity : Entity            (ProjectM.CodeGeneration.dll)
//   ClanTeam { FixedString64Bytes Name; Guid ClanGuid; ... }  (ProjectM.Shared, ns ProjectM)
// The ClanGuid is stable across renames, so the site keys clans by it and shows the
// latest captured Name. Clanless players have ClanEntity == Entity.Null.
public static class ClanResolver
{
    public static ManualLogSource Log;

    // Resolve a user's clan. Returns false (with null outs) for clanless players or
    // if the clan entity/component isn't available yet. Never throws — clan capture
    // must never break session/kill tracking, so any failure degrades to "no clan".
    public static bool TryGetClan(EntityManager em, User user, out string clanGuid, out string clanName)
    {
        clanGuid = null;
        clanName = null;
        try
        {
            var clanEntity = user.ClanEntity._Entity;
            if (clanEntity == Entity.Null) return false;
            if (!em.HasComponent<ClanTeam>(clanEntity)) return false;

            var clan = em.GetComponentData<ClanTeam>(clanEntity);
            var name = clan.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) return false;

            clanGuid = clan.ClanGuid.ToString();
            clanName = name;
            return true;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"TryGetClan failed: {ex.Message}");
            return false;
        }
    }
}
