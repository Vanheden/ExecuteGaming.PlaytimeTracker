using System;
using BepInEx.Logging;
using ProjectM;                 // PrefabCollectionSystem, Immortal
using Stunlock.Core;            // PrefabGUID, PrefabLookupMap
using Unity.Entities;           // EntityManager, Entity, World
using Unity.Mathematics;        // float3, quaternion
using Unity.Transforms;         // LocalTransform

namespace ExecuteGaming.PlaytimeTracker;

// Phase 2a of the reward vendor: spawn a persistent, immortal trader NPC on demand
// (admin `!vendor` command). This first cut spawns a vanilla trader with its default
// wares so we can confirm in-game that the NPC appears, stays put, is unkillable and
// is tradeable. Custom stock + a Blood Coin currency come next once this is verified.
//
// Verified against game v1.1.13.0:
//   PrefabCollectionSystem.GetPrefabLookupMap(World) -> PrefabLookupMap
//   PrefabLookupMap.TryGetValue(PrefabGUID, ref Entity)
//   LocalTransform { float3 Position; quaternion Rotation; float Scale }  (Unity.Transforms)
//   Immortal { bool IsImmortal }  (ProjectM.Shared)
public static class VendorSpawner
{
    public static ManualLogSource Log;

    // CHAR_Trader_Farbane_RareGoods_T01 — an early-game trader with default wares.
    const int TraderPrefabHash = -1810631919;

    // Spawn the trader near `pos` (offset so it isn't on top of the caller). MUST run
    // on the game thread. Returns true if the entity was created.
    public static bool Spawn(EntityManager em, World world, float3 pos, out Entity vendor)
    {
        vendor = Entity.Null;
        try
        {
            var lookup = PrefabCollectionSystem.GetPrefabLookupMap(world);
            var guid = new PrefabGUID(TraderPrefabHash);
            if (!lookup.TryGetValue(guid, out var prefab) || prefab == Entity.Null)
            {
                Log?.LogWarning($"Vendor: trader prefab {TraderPrefabHash} not found in lookup.");
                return false;
            }

            vendor = em.Instantiate(prefab);

            var spot = new float3(pos.x + 2f, pos.y, pos.z);
            if (em.HasComponent<LocalTransform>(vendor))
                em.SetComponentData(vendor, new LocalTransform { Position = spot, Rotation = quaternion.identity, Scale = 1f });
            else
                Log?.LogWarning("Vendor: spawned entity has no LocalTransform (position not set).");

            // Make the merchant unkillable so players can't grief it.
            var immortal = new Immortal { IsImmortal = true };
            if (em.HasComponent<Immortal>(vendor)) em.SetComponentData(vendor, immortal);
            else em.AddComponentData(vendor, immortal);

            Log?.LogInfo($"Vendor: spawned trader at {spot.x:F1},{spot.y:F1},{spot.z:F1} (entity {vendor.Index}:{vendor.Version}).");
            return true;
        }
        catch (Exception ex)
        {
            Log?.LogError($"Vendor spawn failed: {ex.Message}");
            return false;
        }
    }
}
