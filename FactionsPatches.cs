using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SceneLoading;
using NuclearOption.UI;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// Ground Control Factions' hooks: the extra HQs go in just before Mirage collects the scene's
/// objects, the faction list (which is both SELECT FACTION and the TAB scoreboard) grows a column
/// per faction, and an extra faction plays its source faction's music.
/// </summary>
[HarmonyPatch]
internal static class FactionsPatches
{
    [HarmonyPatch(typeof(ClientObjectManager), nameof(ClientObjectManager.PrepareToSpawnSceneObjects))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void PreparePrefix()
    {
        FactionsService.PrepareSceneHqs();
    }

    [HarmonyPatch(typeof(ServerObjectManager), nameof(ServerObjectManager.SpawnSceneObjects))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SpawnPrefix()
    {
        FactionsService.HoldServerHqs();
    }

    // Real matches load through MapLoader, which collects and spawns scene objects itself.
    [HarmonyPatch(typeof(MapLoader), "ClientSceneSetup")]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void MapClientPrefix()
    {
        FactionsService.PrepareSceneHqs();
    }

    [HarmonyPatch(typeof(MapLoader), "ServerSceneSetup")]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void MapServerPrefix()
    {
        FactionsService.HoldServerHqs();
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void StartMissionPrefix()
    {
        FactionsService.SpawnMatchHqs();
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void StartMissionPostfix()
    {
        FactionsObjectives.GiveCaptureObjectives();
    }

    // Each faction reads its AI aircraft limit from its mission faction here; shared out, eight
    // factions fly what two would, which is most of the frame rate in a big multi-faction match.
    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void MissionLoadPostfix(FactionHQ __instance)
    {
        FactionsAiSupplies.Apply(__instance);
        FactionsService.ShareAiLimit(__instance);
    }

    // The end-game RPC is two-sided (this HQ wins or everyone else does), so with more factions a
    // defeat would end the match for all: it knocks that faction out instead.
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.DeclareEndGame))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static bool DeclareEndGamePrefix(FactionHQ __instance, NuclearOption.SavedMission.Outcomes.EndType endType)
    {
        if (FactionsService.Declaring || !FactionsApi.IsMultiFaction || endType != NuclearOption.SavedMission.Outcomes.EndType.Defeat)
        {
            return true;
        }

        FactionsService.Eliminate(__instance, "was defeated");
        return false;
    }

    // A knocked-out faction's players are spectators (FactionsPlayers): they may not join any faction
    // again, a reconnect's automatic rejoin included.
    [HarmonyPatch(typeof(Player), "ValidateFactionChange")]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static bool ValidateFactionChangePrefix(Player __instance, ref bool __result)
    {
        if (__instance.IsServer && FactionsPlayers.IsKnockedOut(__instance))
        {
            __result = false;
            return false;
        }

        return true;
    }

    // Nobody spawns for a faction that is out, in the second before its players are moved to spectate.
    [HarmonyPatch(typeof(Spawner), "AllowedToSpawn")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void AllowedToSpawnPostfix(Player player, ref bool __result)
    {
        if (__result && player.HQ != null && FactionsService.IsEliminated(player.HQ))
        {
            __result = false;
        }
    }

    [HarmonyPatch(typeof(Player), "HQChanged")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void HqChangedPostfix(Player __instance)
    {
        if (__instance.IsLocalPlayer && __instance.HQ == null && MissionManager.IsRunning)
        {
            FactionsPlayers.OnLocalHqCleared();
        }
    }

    [HarmonyPatch(typeof(Player), "OnStartServer")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void PlayerStartServerPostfix(Player __instance)
    {
        FactionsPlayers.OnPlayerStarted(__instance);
    }

    [HarmonyPatch(typeof(LeaderboardFactionList), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void LeaderboardAwakePostfix(LeaderboardFactionList __instance)
    {
        __instance.gameObject.AddComponent<FactionsLeaderboard>();
    }

    // Liveries are tagged with a stock faction; an extra one wears its source faction's.
    [HarmonyPatch(typeof(AircraftParameters), nameof(AircraftParameters.GetRandomLiveryForFaction))]
    [HarmonyPatch(typeof(AircraftParameters), nameof(AircraftParameters.GetFirstLiveryForFaction))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void LiveryPrefix(ref Faction faction)
    {
        if (faction != null && FactionsService.SourceFaction(faction) is Faction source)
        {
            faction = source;
        }
    }

    // The spawn menu and editor list liveries whose faction name matches; with none for an extra
    // faction the spawn menu reads option 0 of an empty list and stops half set up (no cursor).
    [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.GetLiveryOptions))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void LiveryOptionsPrefix(ref string aircraftFaction)
    {
        if (aircraftFaction != null)
        {
            aircraftFaction = FactionsService.SourceFactionName(aircraftFaction);
        }
    }

    // With more than two factions "enemy" is several sides, so an enemy shows its own faction's
    // colour on the map and the pilot's HUD instead of the theme's one red.
    [HarmonyPatch(typeof(UnitMapIcon), "GetColor")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void MapIconColorPostfix(UnitMapIcon __instance, bool ___isSelected, ref Color __result)
    {
        if (EnemyFaction(__instance.unit) is Faction faction)
        {
            __result = ___isSelected ? faction.selectedColor : faction.color;
        }
    }

    [HarmonyPatch(typeof(HUDUnitMarker), "SetFactionColor")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void HudMarkerColorPostfix(HUDUnitMarker __instance, ref Color ___color)
    {
        if (EnemyFaction(__instance.unit) is Faction faction)
        {
            ___color = faction.color;
        }
    }

    private static Faction? EnemyFaction(Unit? unit)
    {
        FactionHQ? hq = unit != null ? unit.NetworkHQ : null;
        return FactionsApi.IsMultiFaction && hq != null && hq.faction != null
            && DynamicMap.GetFactionMode(hq) == FactionMode.Enemy ? hq.faction : null;
    }

    /// <summary>The kill feed and other messages paint every enemy "hostile"; like the map icons,
    /// each enemy faction shows in its own colour instead.</summary>
    [HarmonyPatch(typeof(MessageManager), "ColorFromFaction")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void MessageColorPostfix(FactionHQ hq, ref Color __result)
    {
        if (FactionsApi.IsMultiFaction && hq != null && hq.faction != null && DynamicMap.GetFactionMode(hq) == FactionMode.Enemy)
        {
            __result = hq.faction.color;
        }
    }

    [HarmonyPatch(typeof(MapSettings), nameof(MapSettings.GetStartMusic), typeof(Faction))]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void StartMusicPostfix(MapSettings __instance, Faction faction, ref AudioClip __result)
    {
        if (__result == null && faction != null && FactionsService.SourceFaction(faction) is Faction source)
        {
            __result = __instance.GetStartMusic(source);
        }
    }
}
