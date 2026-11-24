using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;

namespace GroundControlFactions;

/// <summary>
/// The base game's AI goes where its faction's active objectives are: vehicles, ships, landing craft,
/// helicopters and pilots all head for the nearest objective position (<c>MissionPosition</c>), and
/// only with none chase the nearest known enemy. A mission's objectives are written for Boscali and
/// Primeva by name, so an extra faction had none. Each one gets a capture-airbase objective for every
/// base, the kind Escalation uses: its positions are the bases the faction does not hold, so its
/// units always head for the nearest one.
/// </summary>
/// <remarks>
/// Host only, hidden, and never announced to guests: the AI runs on the host, and a vanilla guest
/// would fail on an objective name its mission does not have. Started straight into the runner, so
/// the mission's own objective graph is untouched.
/// </remarks>
internal static class FactionsObjectives
{
    private static readonly MethodInfo AddActive = AccessTools.Method(typeof(MissionRunner), "AddActiveObjective");

    private static readonly FieldInfo Items = AccessTools.Field(typeof(CompleteOrderObjective<SavedAirbase>), "allItems");

    private static readonly FieldInfo Order = AccessTools.Field(typeof(CompleteOrderObjective<SavedAirbase>), "completeOrder");

    /// <summary>Host, right after the mission started its own objectives.</summary>
    internal static void GiveCaptureObjectives()
    {
        MissionRunner? runner = MissionManager.Runner;
        if (runner == null || !FactionsApi.IsMultiFaction || NetworkManagerNuclearOption.i == null
            || !NetworkManagerNuclearOption.i.Server.Active)
        {
            return;
        }

        // ponytail: bases are read once here; a custom airbase that spawns later is not a target.
        List<SavedAirbase> bases = new();
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase != null && !airbase.AttachedAirbase && airbase.SavedAirbase is SavedAirbase saved && saved.Capturable)
            {
                bases.Add(saved);
            }
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || hq.faction == null || FactionsService.SourceFaction(hq.faction) == null)
            {
                continue;
            }

            CaptureAirbaseSavedObjective saved = new($"GCF Capture {hq.faction.factionName}")
            {
                Faction = hq.faction.factionName,
                DisplayName = "Capture enemy airbases",
                Hidden = true,
            };

            // MissionManager carries its progress (a synced list a guest ignores for a name it lacks).
            CaptureAirbaseObjective objective = new(saved) { FactionHQ = hq, MissionManager = NetworkSceneSingleton<MissionManager>.i };
            Items.SetValue(objective, new List<SavedAirbase>(bases));
            Order.SetValue(objective, CompleteOrder.CompleteAll);
            objective.Status = ObjectiveStatus.Running;
            AddActive.Invoke(runner, new object[] { objective });
        }

        FactionsPlugin.Log.LogInfo($"Extra factions given a capture objective over {bases.Count} bases.");
    }
}
