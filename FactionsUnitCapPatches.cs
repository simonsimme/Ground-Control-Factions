using System.Globalization;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// The Unit Cap setting (GC Factions Settings): the most vehicles, ships and aircraft a side keeps
/// on the map, shared out between the factions like GC's. It is the mission tag GC RTS writes
/// ("GC Unit Cap N", 0 off, then one of four sizes), so both mods read the same value; with GC
/// loaded GC enforces it, without it the stock AI's deploys are held at the cap. Players are not
/// stopped.
/// </summary>
[HarmonyPatch]
internal static class FactionsUnitCapPatches
{
    internal const string TagPrefix = "GC Unit Cap ";

    private static readonly int[] Caps = { 60, 150, 300, 450 };

    private static readonly string[] Labels = { "PERFORMANCE", "NORMAL", "LARGE", "ULTRA" };

    internal static int ModeCount => Caps.Length;

    internal static string Label(int value)
    {
        return value <= 0 ? "Off" : $"{Labels[Mathf.Min(value, Caps.Length) - 1]}: {Caps[Mathf.Min(value, Caps.Length) - 1]} per side";
    }

    /// <summary>0 for off, else the mode plus one, as GC writes it.</summary>
    internal static int Read(Mission? mission)
    {
        return ReadTag(mission, TagPrefix, Caps.Length);
    }

    internal static void Write(Mission mission, int value)
    {
        WriteTag(mission, TagPrefix, value);
    }

    /// <summary>A "prefix N" mission tag, 0 when missing, clamped to 0..<paramref name="max"/>.</summary>
    internal static int ReadTag(Mission? mission, string prefix, int max)
    {
        if (mission?.missionSettings?.Tags != null)
        {
            foreach (MissionTag tag in mission.missionSettings.Tags)
            {
                if (tag.Tag != null && tag.Tag.StartsWith(prefix, System.StringComparison.Ordinal)
                    && float.TryParse(tag.Tag.Substring(prefix.Length).Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return Mathf.Clamp(Mathf.RoundToInt(value), 0, max);
                }
            }
        }

        return 0;
    }

    /// <summary>Writes a "prefix N" mission tag; 0 removes it.</summary>
    internal static void WriteTag(Mission mission, string prefix, int value)
    {
        mission.missionSettings.Tags ??= new System.Collections.Generic.List<MissionTag>();
        mission.missionSettings.Tags.RemoveAll(t => t.Tag != null && t.Tag.StartsWith(prefix, System.StringComparison.Ordinal));
        if (value > 0)
        {
            mission.missionSettings.Tags.Add(new MissionTag { Tag = prefix + value.ToString(CultureInfo.InvariantCulture), Color = new Color(0.3f, 0.85f, 0.8f), SortOrder = 9 });
        }
    }

    /// <summary>True when the stock AI of <paramref name="hq"/> should deploy nothing more now.</summary>
    private static bool IsFull(FactionHQ hq)
    {
        int mode = Read(MissionManager.CurrentMission);
        if (mode == 0 || hq.factionUnits == null || FactionsService.GcLoaded)
        {
            return false;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && !unit.disabled && (unit is GroundVehicle || unit is Ship || unit is Aircraft))
            {
                count++;
            }
        }

        return count >= FactionsApi.ShareOf(Caps[mode - 1]);
    }

    // ponytail: checked once per deploy tick, so one tick of DeployVehicles (one per vehicle type)
    // can overshoot by a few; count per spawn in TrySpawnVehicle if that ever matters.
    [HarmonyPatch(typeof(FactionHQ), "DeployAIAircraft")]
    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static bool DeployPrefix(FactionHQ __instance)
    {
        return !IsFull(__instance);
    }
}
