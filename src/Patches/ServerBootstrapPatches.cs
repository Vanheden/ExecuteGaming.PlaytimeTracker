using System;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;   // the User component (PlatformId, CharacterName)
using Stunlock.Network;   // NetConnectionId — the OnUser* parameter type
using Unity.Entities;
using BepInEx.Logging;

namespace ExecuteGaming.PlaytimeTracker.Patches;

// Hooks V Rising's connect/disconnect so we can open/close play sessions.
//
// Signatures verified against game v1.1.13.0:
//   ServerBootstrapSystem.OnUserConnected(NetConnectionId netConnectionId)
//   ServerBootstrapSystem.OnUserDisconnected(NetConnectionId, ConnectionStatusChangeReason, string)
// The connection id is mapped to the approved-user index via the
// _NetEndPointToApprovedUserIndex field, then to the User component. If a future
// V Rising patch stops the mod recording, re-check these against the current
// assemblies (see README "Keeping the mod alive").
public static class BootstrapPatchShared
{
    public static SessionTracker Tracker;
    public static ManualLogSource Log;

    // Resolve the User component for a connection. Returns false if not resolvable
    // yet (e.g. the index isn't in the map). Never throws — logs and bails.
    public static bool TryGetUser(ServerBootstrapSystem sys, NetConnectionId conn, out User user)
    {
        user = default;
        try
        {
            var map = sys._NetEndPointToApprovedUserIndex;
            if (!map.TryGetValue(conn, out var userIndex)) return false;
            var client = sys._ApprovedUsersLookup[userIndex];
            var userEntity = client.UserEntity;
            var em = sys.EntityManager;
            if (!em.HasComponent<User>(userEntity)) return false;
            user = em.GetComponentData<User>(userEntity);
            return true;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"TryGetUser failed: {ex.Message}");
            return false;
        }
    }
}

[HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected))]
public static class OnUserConnectedPatch
{
    public static void Postfix(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
    {
        var tracker = BootstrapPatchShared.Tracker;
        if (tracker == null) return;
        if (BootstrapPatchShared.TryGetUser(__instance, netConnectionId, out var user))
        {
            ClanResolver.TryGetClan(__instance.EntityManager, user, out var clanGuid, out var clanName);
            tracker.Connect(user.PlatformId, user.CharacterName.ToString(), clanGuid, clanName);
        }
    }
}

[HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected))]
public static class OnUserDisconnectedPatch
{
    // Prefix: the mapping still exists before the game tears the connection down.
    public static void Prefix(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
    {
        var tracker = BootstrapPatchShared.Tracker;
        if (tracker == null) return;
        if (BootstrapPatchShared.TryGetUser(__instance, netConnectionId, out var user))
        {
            ClanResolver.TryGetClan(__instance.EntityManager, user, out var clanGuid, out var clanName);
            tracker.Disconnect(user.PlatformId, user.CharacterName.ToString(), clanGuid, clanName);
        }
    }
}
