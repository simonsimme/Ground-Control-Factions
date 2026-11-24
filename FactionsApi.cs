using System;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// For other mods (Ground Control RTS): set the hooks once at load. Everything works without them;
/// they only hand a mod with its own rules the decisions it already makes.
/// </summary>
public static class FactionsApi
{
    /// <summary>True while more than the stock two factions are in play.</summary>
    public static bool IsMultiFaction => FactionRegistry.HQLookup.Count > FactionsService.MinFactions;

    /// <summary>Set by a mod that decides itself when a faction is beaten and calls
    /// <see cref="Eliminate"/>: the built-in rule (no base held for a while) then stays off.</summary>
    public static bool ExternalVictory { get; set; }

    /// <summary>The base deal waits while this returns true (another mod is still dealing the map).</summary>
    public static Func<bool>? HoldDeal { get; set; }

    /// <summary>Called on the host for each faction with no vehicle depot after the deal, with its
    /// first land base: a mod that builds bases gives it one there.</summary>
    public static Action<FactionHQ, Airbase>? GiveStartingKit { get; set; }

    /// <summary>A faction this returns true for is not a contender (a spectator side).</summary>
    public static Func<FactionHQ, bool>? IsBystander { get; set; }

    /// <summary>Shows a line to the players; the log only when unset.</summary>
    public static Action<string>? Announce { get; set; }

    /// <summary>The size of a whole army in a stock two-faction match, shared by the factions in
    /// play: what one faction may field when <paramref name="perSide"/> is what one side of two
    /// may. Never under a tenth of it.</summary>
    /// <param name="perSide">The per-side figure of a two-faction match.</param>
    /// <returns>The figure for one faction of this match.</returns>
    public static int ShareOf(int perSide)
    {
        int factions = Mathf.Max(FactionsService.MinFactions, FactionRegistry.HQLookup.Count);
        return Mathf.Max(Mathf.CeilToInt(perSide / 10f), Mathf.CeilToInt(perSide * (float)FactionsService.MinFactions / factions));
    }

    /// <summary>True for a faction knocked out of a multi-faction match that is still going.</summary>
    /// <param name="hq">The faction.</param>
    /// <returns>Whether it is out.</returns>
    public static bool IsEliminated(FactionHQ hq)
    {
        return FactionsService.IsEliminated(hq);
    }

    /// <summary>Host: knocks a faction out. When one faction is left it wins.</summary>
    /// <param name="hq">The beaten faction.</param>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>True once the match is decided.</returns>
    public static bool Eliminate(FactionHQ hq, string reason)
    {
        return FactionsService.Eliminate(hq, reason);
    }

    /// <summary>True for one of the eight factions this mod adds.</summary>
    /// <param name="faction">The faction.</param>
    /// <returns>Whether it is an added one.</returns>
    public static bool IsAddedFaction(Faction? faction)
    {
        return FactionsService.IsExtra(faction);
    }
}
