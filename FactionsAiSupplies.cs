using System.Collections.Generic;
using NuclearOption.SavedMission;

namespace GroundControlFactions;

/// <summary>
/// The AI Supplies setting (GC Factions Settings): 0 keeps the mission editor's AI aircraft limit
/// and supply list; a size replaces them, at match load on the host, with that many of every
/// allowed aircraft and fighting vehicle for every faction. The mission file is not changed.
/// </summary>
internal static class FactionsAiSupplies
{
    private const string TagPrefix = "GC AI Supplies ";

    // Per size: each aircraft type, each vehicle type, AI aircraft limit (per side, then shared out).
    private static readonly (int Aircraft, int Vehicles, int AiLimit)[] Sizes = { (3, 5, 6), (6, 10, 10), (12, 20, 16) };

    private static readonly string[] Labels = { "LIGHT", "NORMAL", "LARGE" };

    // Trucks, UGVs, artillery, radar SAMs and radars are left out: the stock AI only drives them
    // off the depot, and trailers and containers jammed it before.
    private static readonly HashSet<VehicleType> Fighting = new() { VehicleType.LCV, VehicleType.AFV, VehicleType.MBT, VehicleType.AAA, VehicleType.IR_SAM };

    internal static int ModeCount => Sizes.Length;

    internal static int Read(Mission? mission)
    {
        return FactionsUnitCapPatches.ReadTag(mission, TagPrefix, Sizes.Length);
    }

    internal static void Write(Mission mission, int value)
    {
        FactionsUnitCapPatches.WriteTag(mission, TagPrefix, value);
    }

    internal static string Label(int value)
    {
        if (value <= 0)
        {
            return "Mission (editor)";
        }

        (int Aircraft, int Vehicles, int AiLimit) size = Sizes[value - 1];
        return $"{Labels[value - 1]}: {size.Aircraft} jets, {size.Vehicles} vehicles each";
    }

    /// <summary>Host, from <c>FactionHQ.OnMissionLoad</c>, after the mission's figures are read.</summary>
    internal static void Apply(FactionHQ hq)
    {
        int mode = Read(MissionManager.CurrentMission);
        if (mode == 0 || Encyclopedia.i == null)
        {
            return;
        }

        (int Aircraft, int Vehicles, int AiLimit) size = Sizes[mode - 1];
        hq.AIAircraftLimit = size.AiLimit;
        hq.AircraftSupply.Clear();
        hq.VehicleSupply.Clear();
        foreach (AircraftDefinition aircraft in Encyclopedia.i.aircraft)
        {
            if (aircraft != null && aircraft.IsAllowed(false))
            {
                hq.ModifyUnitSupply(aircraft, size.Aircraft);
            }
        }

        foreach (VehicleDefinition vehicle in Encyclopedia.i.vehicles)
        {
            if (vehicle != null && vehicle.unitPrefab != null && vehicle.IsAllowed(false) && Fighting.Contains(vehicle.vehicleType))
            {
                hq.ModifyUnitSupply(vehicle, size.Vehicles);
            }
        }
    }
}
