using System;
using ProjectM;                 // UserOwner (namespace ProjectM)
using ProjectM.CastleBuilding;  // CastleHeart
using ProjectM.Network;         // User
using Unity.Entities;           // Entity, EntityManager
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Resolves the DEFENDING owner of a castle-heart entity to its User, so a raid can be
// attributed to the raided player/clan. Shared by the raid patch.
//
// Verified against game v1.1.13.0 (via the metadata dumper):
//   UserOwner { NetworkedEntity Owner }              (ProjectM.Shared, ns ProjectM)
//   CastleHeart { … NetworkedEntity LastUserOwner }  (ProjectM.Shared, ns ProjectM.CastleBuilding)
//   NetworkedEntity._Entity : Entity                 (ProjectM.CodeGeneration.dll)
// Both point at the owning User entity on the server. Never throws — a raid without a
// resolvable defender is still recorded (attacker-only), so this can't break tracking.
public static class CastleRaidResolver
{
    public static ManualLogSource Log;

    // Resolve the castle owner's User from a heart entity. Prefers the live UserOwner,
    // falling back to CastleHeart.LastUserOwner. Returns false if neither resolves.
    public static bool TryGetOwner(EntityManager em, Entity heart, out User owner)
    {
        owner = default;
        try
        {
            if (heart == Entity.Null) return false;
            var ownerUser = Entity.Null;

            if (em.HasComponent<UserOwner>(heart))
                ownerUser = em.GetComponentData<UserOwner>(heart).Owner._Entity;

            if (ownerUser == Entity.Null && em.HasComponent<CastleHeart>(heart))
                ownerUser = em.GetComponentData<CastleHeart>(heart).LastUserOwner._Entity;

            if (ownerUser == Entity.Null || !em.HasComponent<User>(ownerUser)) return false;
            owner = em.GetComponentData<User>(ownerUser);
            return true;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"TryGetOwner failed: {ex.Message}");
            return false;
        }
    }
}
