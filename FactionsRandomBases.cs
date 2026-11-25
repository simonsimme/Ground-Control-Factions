using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// Random Bases: every faction starts at a random airbase of the map, whoever the mission gave
/// it to, with the smaller bases beside it, and every unit belongs to its nearest base. The
/// loaded mission is rewritten on the host before the game links bases and units by faction name,
/// so the match spawns that way; mission units that spawn later are checked as they appear.
/// </summary>
internal static class FactionsRandomBases
{
    private const float CheckIntervalSeconds = 0.5f;

    /// <summary>A neutral object (a warhead store, a plant) joins a base this close; further out
    /// the map's towns and bridges stay neutral, or the AI would attack them.</summary>
    private const float NeutralReach = 10000f;

    /// <summary>Units already checked, so each is looked at once, when it appears.</summary>
    private static readonly HashSet<Unit> Seen = new();

    private static float nextCheckAt;

    /// <summary>What a base is worth dealing: the map's airbases with a runway first.</summary>
    private enum Tier
    {
        Other,
        MissionStrip,
        MapAirbase,
    }

    /// <summary>The map's airbases with a runway the mission leaves enabled: where factions start.</summary>
    internal static int CountAirstrips(Mission mission)
    {
        int count = 0;
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase == null || airbase.AttachedAirbase || airbase.IsCustom || airbase.SavedAirbase == null
                || airbase.runways == null || airbase.runways.Length == 0)
            {
                continue;
            }

            string name = airbase.SavedAirbase.UniqueName;
            SavedAirbase? saved = mission.airbases.Find(a => a.UniqueName == name) ?? airbase.SavedAirbase;
            count += saved.Disabled ? 0 : 1;
        }

        return count;
    }

    /// <summary>Host, from the <c>StartMission</c> prefix, after the extra HQs spawned.</summary>
    internal static void Apply(Mission mission)
    {
        Seen.Clear();
        nextCheckAt = 0f;
        List<Base> bases = new();
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase == null || airbase.AttachedAirbase || airbase.IsCustom || airbase.SavedAirbase == null || airbase.center == null)
            {
                continue;
            }

            string name = airbase.SavedAirbase.UniqueName;
            SavedAirbase? saved = mission.airbases.Find(a => a.UniqueName == name);
            if (saved == null)
            {
                saved = SavedAirbase.CreateOverride(airbase.SavedAirbase);
                mission.airbases.Add(saved);
            }

            if (!saved.Disabled)
            {
                bool runway = airbase.runways != null && airbase.runways.Length > 0;
                bases.Add(new Base(saved, airbase.center.position, runway ? Tier.MapAirbase : Tier.Other));
            }
        }

        // The mission's own bases (Insane Gambler: Maris Heliport and The Farm, each with a short
        // strip, and two enrichment plants) rank below the map's airbases: not dealt while a map
        // airbase is free, they go with the airbase beside them.
        foreach (SavedAirbase saved in mission.airbases)
        {
            if (!saved.IsOverride && !saved.IsAttached() && !saved.Disabled)
            {
                bool runway = saved.runways != null && saved.runways.Count > 0;
                bases.Add(new Base(saved, saved.Center.ToLocalPosition(), runway ? Tier.MissionStrip : Tier.Other));
            }
        }

        List<string> factions = new();
        foreach (MissionFaction faction in mission.factions)
        {
            factions.Add(faction.factionName);
        }

        for (int i = factions.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (factions[i], factions[j]) = (factions[j], factions[i]);
        }

        foreach (string faction in factions)
        {
            Base? pick = Pick(bases, Tier.MapAirbase) ?? Pick(bases, Tier.MissionStrip) ?? Pick(bases, Tier.Other);
            if (pick == null)
            {
                FactionsPlugin.Log.LogWarning($"Random bases: no base left for {faction}.");
                continue;
            }

            pick.Owner = faction;
            FactionsPlugin.Log.LogInfo($"Random bases: {faction} starts at {pick.Saved.DisplayName} ({pick.Tier}).");
        }

        // Every base not dealt goes with the nearest base of the highest rank there is: a faction
        // gets its airstrip and what stands beside it, a neutral airstrip keeps its neighbours neutral.
        Tier top = Tier.Other;
        foreach (Base b in bases)
        {
            top = b.Tier > top ? b.Tier : top;
        }

        foreach (Base b in bases)
        {
            if (b.Tier == top || b.Owner != null)
            {
                continue;
            }

            Base? anchor = null;
            float best = float.MaxValue;
            foreach (Base other in bases)
            {
                float distance = (other.Position - b.Position).sqrMagnitude;
                if (other.Tier == top && distance < best)
                {
                    best = distance;
                    anchor = other;
                }
            }

            b.Owner = anchor?.Owner;
            FactionsPlugin.Log.LogInfo($"Random bases: {b.Saved.DisplayName} goes with {anchor?.Saved.DisplayName ?? "nothing"}: {b.Owner ?? "neutral"}.");
        }

        foreach (Base b in bases)
        {
            b.Saved.faction = b.Owner ?? string.Empty;
        }

        SettleSavedUnits(mission, bases);
    }

    /// <summary>
    /// Host, every frame of a match (checks twice a second), and from the <c>StartMission</c>
    /// postfix once the mission's units are up: a mission or map unit (neutral or not) seen for the first time takes
    /// the faction of its nearest land base as it is now, and goes if that base is neutral (ships
    /// may follow a carrier instead). Units bought or deployed in the match have no saved unit and
    /// are left alone, as are players.
    /// </summary>
    internal static void CheckNewUnits()
    {
        if (Time.time < nextCheckAt)
        {
            return;
        }

        nextCheckAt = Time.time + CheckIntervalSeconds;
        Seen.RemoveWhere(u => u == null);
        List<(Vector3 Position, FactionHQ? Owner, bool Deck)> bases = new();
        HashSet<Unit> skip = new();
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            skip.UnionWith(airbase.buildings);
            bool deck = airbase.TryGetAttachedUnit(out Unit carrier);
            if (deck)
            {
                skip.Add(carrier);
            }

            bases.Add((airbase.center.position, airbase.CurrentHQ, deck));
        }

        int moved = 0;
        int removed = 0;
        foreach (Unit unit in new List<Unit>(UnitRegistry.allUnits))
        {
            // The map's own objects count too, neutral ones included (a neutral warhead store beside
            // a dealt base stayed white).
            if (unit == null || !Seen.Add(unit) || unit.disabled || (unit.SavedUnit == null && !unit.BuiltIn)
                || skip.Contains(unit) || (unit is Aircraft aircraft && aircraft.Player != null))
            {
                continue;
            }

            bool found = false;
            FactionHQ? owner = null;
            float best = float.MaxValue;
            foreach ((Vector3 position, FactionHQ? hq, bool deck) in bases)
            {
                float distance = (unit.transform.position - position).sqrMagnitude;
                if (distance < best && (!deck || unit is Ship))
                {
                    best = distance;
                    owner = hq;
                    found = true;
                }
            }

            if (!found)
            {
                continue;
            }

            if (owner == null)
            {
                // A faction's unit at a neutral base goes; a neutral one stays as it is.
                if (unit.NetworkHQ != null)
                {
                    NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(unit.gameObject);
                    removed++;
                }
            }
            else if (owner != unit.NetworkHQ && (unit.NetworkHQ != null || best < NeutralReach * NeutralReach))
            {
                unit.NetworkHQ = owner;
                moved++;
            }
        }

        if (moved > 0 || removed > 0)
        {
            FactionsPlugin.Log.LogInfo($"Random bases at {Time.timeSinceLevelLoad:0.0} s: {moved} new units given their nearest base's faction, {removed} at neutral bases removed.");
        }
    }

    /// <summary>Mission units spawn with their nearest base's faction (ships may follow a carrier,
    /// which keeps its own). One nearest a neutral base is left as it is and removed when it
    /// appears: taking it out of the mission breaks the objectives and outcomes that name it (297
    /// load warnings).</summary>
    private static void SettleSavedUnits(Mission mission, List<Base> bases)
    {
        List<(Vector3 Position, string Owner)> carriers = new();
        foreach (SavedShip ship in mission.ships)
        {
            if (FactionsService.IsCarrier(ship))
            {
                carriers.Add((ship.globalPosition.ToLocalPosition(), ship.faction));
            }
        }

        List<SavedUnit> units = new();
        MissionManager.GetAllSavedUnitsNonAlloc(mission, units, includeBuiltIn: false);
        int moved = 0;
        foreach (SavedUnit unit in units)
        {
            if (unit is SavedShip carrierShip && FactionsService.IsCarrier(carrierShip))
            {
                continue;
            }

            Vector3 at = unit.globalPosition.ToLocalPosition();
            string? owner = null;
            float best = float.MaxValue;
            foreach (Base b in bases)
            {
                float distance = (b.Position - at).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    owner = b.Owner;
                }
            }

            if (unit is SavedShip)
            {
                foreach ((Vector3 position, string faction) in carriers)
                {
                    float distance = (position - at).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        owner = faction;
                    }
                }
            }

            bool neutral = string.IsNullOrEmpty(unit.faction);
            if (owner != null && owner != unit.faction && (!neutral || best < NeutralReach * NeutralReach))
            {
                unit.faction = owner;
                moved++;
            }
        }

        FactionsPlugin.Log.LogInfo($"Random bases: {bases.Count} bases, {moved} mission units given their nearest base's faction.");
    }

    /// <summary>A free base of <paramref name="tier"/>: the first a coin toss, later ones leaning
    /// away from the bases already held.</summary>
    private static Base? Pick(List<Base> bases, Tier tier)
    {
        Base? best = null;
        float bestScore = -1f;
        foreach (Base b in bases)
        {
            if (b.Tier != tier || b.Owner != null)
            {
                continue;
            }

            float nearest = float.MaxValue;
            foreach (Base held in bases)
            {
                if (held.Owner != null)
                {
                    nearest = Mathf.Min(nearest, (held.Position - b.Position).sqrMagnitude);
                }
            }

            float score = nearest == float.MaxValue ? Random.value : nearest * Random.Range(0.5f, 1f);
            if (score > bestScore)
            {
                best = b;
                bestScore = score;
            }
        }

        return best;
    }

    private sealed class Base
    {
        internal Base(SavedAirbase saved, Vector3 position, Tier tier)
        {
            Saved = saved;
            Position = position;
            Tier = tier;
        }

        internal SavedAirbase Saved { get; }

        internal Vector3 Position { get; }

        internal Tier Tier { get; }

        internal string? Owner { get; set; }
    }
}
