using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GroundControlFactions;

/// <summary>
/// Up to ten factions in one match, each with its own HQ, funds, warheads, supplies and base. The
/// base game has exactly two HQs, both scene objects of the map; the extra ones are copies of a map
/// HQ given a fixed scene id of their own, so Mirage spawns and syncs them exactly like the stock two
/// on the host and on every guest.
/// </summary>
/// <remarks>
/// The count is the mission tag "GC Factions N" (Customize Mission or the editor), capped in a match
/// at the number of bases the mission has, carriers included. Every peer builds all eight copies,
/// inactive, before the scene's objects are collected; only the host knows the count, so it spawns
/// that many and keeps the rest out of the spawn by clearing their scene id.
/// </remarks>
internal static class FactionsService
{
    internal const int MinFactions = 2;

    internal const int MaxFactions = 10;

    internal const string TagPrefix = "GC Factions ";

    /// <summary>Seconds after load before the base deal: carriers are mission units and spawn after
    /// the airbases.</summary>
    private const float DealDelaySeconds = 8f;

    /// <summary>How long the deal waits on <see cref="FactionsApi.HoldDeal"/> before going ahead.</summary>
    private const float HoldWaitSeconds = 60f;

    /// <summary>The built-in rule: a faction holding no base this long is out.</summary>
    private const float NoBaseSeconds = 30f;

    /// <summary>Grace after the deal before the built-in rule starts counting.</summary>
    private const float RuleGraceSeconds = 60f;

    private const float RuleIntervalSeconds = 5f;

    /// <summary>"GCFA" in the high half: far from any id Unity bakes into a scene.</summary>
    private const ulong SceneIdBase = 0x4743464100000000UL;

    private static readonly FieldInfo SceneIdField = AccessTools.Field(typeof(NetworkIdentity), "_sceneId");

    private static readonly FieldInfo HasSpawnedField = AccessTools.Field(typeof(NetworkIdentity), "_hasSpawned");

    private static readonly string[] CommandLine = Environment.GetCommandLineArgs();

    private static readonly Spec[] Extras =
    {
        new("Veydra", "Veydra Union", "VEY", new Color(0.25f, 0.75f, 0.3f)),
        new("Kharrow", "Kharrow Republic", "KHR", new Color(0.95f, 0.55f, 0.15f)),
        new("Solmara", "Solmara Coalition", "SOL", new Color(0.95f, 0.85f, 0.2f)),
        new("Drakmor", "Drakmor Dominion", "DRK", new Color(0.6f, 0.3f, 0.85f)),
        new("Nordhal", "Nordhal Federation", "NRD", new Color(0.2f, 0.8f, 0.9f)),
        new("Rosmere", "Rosmere Accord", "RSM", new Color(0.95f, 0.4f, 0.7f)),
        new("Ironvale", "Ironvale Compact", "IRV", new Color(0.85f, 0.85f, 0.85f)),
        new("Cindral", "Cindral Directorate", "CND", new Color(0.6f, 0.4f, 0.25f)),
    };

    private static readonly Faction?[] Factions = new Faction?[Extras.Length];

    private static readonly NetworkIdentity?[] Copies = new NetworkIdentity?[Extras.Length];

    /// <summary>The stock faction each extra was copied from, for its music and liveries.</summary>
    private static readonly Dictionary<Faction, Faction> SourceOf = new();

    private static readonly HashSet<FactionHQ> Eliminated = new();

    private static readonly Dictionary<FactionHQ, float> NoBaseSince = new();

    /// <summary>Extras the host spawned this scene.</summary>
    private static int activeExtras;

    private static bool dealt;

    private static float dealtAt;

    private static float nextRuleAt;

    /// <summary>True while this mod itself declares the result, so the end-game prefix lets it by.</summary>
    internal static bool Declaring { get; private set; }

    /// <summary>Ground Control RTS is installed: it plays the AI and enforces the unit cap.</summary>
    internal static bool GcLoaded => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.groundcontrol.rts");

    internal static void Tick()
    {
        if (GameManager.gameState == GameState.Editor)
        {
            TickEditor();
            return;
        }

        if (activeExtras == 0 || !MissionManager.IsRunning || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            return;
        }

        if (!dealt && Time.timeSinceLevelLoad > DealDelaySeconds)
        {
            TryDeal();
        }

        if (dealt && !FactionsApi.ExternalVictory && Time.time >= nextRuleAt && Time.time - dealtAt > RuleGraceSeconds)
        {
            nextRuleAt = Time.time + RuleIntervalSeconds;
            ApplyNoBaseRule();
        }

        TestKnockout();
    }

    /// <summary>A new scene: the copies of the last one are gone, and so is its match.</summary>
    internal static void ResetSession()
    {
        Eliminated.Clear();
        NoBaseSince.Clear();
        dealt = false;
        nextRuleAt = 0f;
    }

    internal static bool IsExtra(Faction? faction)
    {
        return faction != null && SourceOf.ContainsKey(faction);
    }

    internal static Faction? SourceFaction(Faction faction)
    {
        return SourceOf.TryGetValue(faction, out Faction source) ? source : null;
    }

    /// <summary>The source faction's name for an extra faction's name; any other name unchanged.</summary>
    internal static string SourceFactionName(string name)
    {
        foreach (KeyValuePair<Faction, Faction> pair in SourceOf)
        {
            if (pair.Key.factionName == name)
            {
                return pair.Value.factionName;
            }
        }

        return name;
    }

    /// <summary>The count the mission asks for, before the base cap.</summary>
    internal static int RequestedCount(Mission? mission)
    {
        List<MissionTag>? tags = mission?.missionSettings?.Tags;
        if (tags != null)
        {
            foreach (MissionTag tag in tags)
            {
                if (tag.Tag != null && tag.Tag.StartsWith(TagPrefix, StringComparison.Ordinal)
                    && int.TryParse(tag.Tag.Substring(TagPrefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    return Mathf.Clamp(value, MinFactions, MaxFactions);
                }
            }
        }

        return MinFactions;
    }

    /// <summary>Writes the count into the mission's tags; the stock two leaves no tag at all.</summary>
    internal static void SetRequestedCount(Mission mission, int count)
    {
        List<MissionTag> tags = mission.missionSettings.Tags ??= new List<MissionTag>();
        tags.RemoveAll(t => t.Tag != null && t.Tag.StartsWith(TagPrefix, StringComparison.Ordinal));
        count = Mathf.Clamp(count, MinFactions, MaxFactions);
        if (count > MinFactions)
        {
            // Fields, not the constructor: a game patch once added parameters to it.
            tags.Add(new MissionTag { Tag = TagPrefix + count.ToString(CultureInfo.InvariantCulture), Color = new Color(0.3f, 0.85f, 0.8f), SortOrder = 9 });
        }
    }

    /// <summary>Gives the mission a faction entry for each extra its count asks for and drops the
    /// rest, so the faction settings tab lists them. In the editor a spawned extra keeps its entry
    /// until the next load, like its HQ.</summary>
    internal static void SyncMissionFactions(Mission mission)
    {
        int extras = RequestedCount(mission) - MinFactions;
        for (int i = 0; i < Extras.Length; i++)
        {
            string name = Extras[i].Name;
            if (i < extras)
            {
                EnsureMissionFaction(mission, name);
            }
            else if (GameManager.gameState != GameState.Editor || i >= activeExtras)
            {
                mission.factions.RemoveAll(f => f.factionName == name);
            }
        }
    }

    /// <summary>
    /// Builds the eight inactive HQ copies for this scene, once. Run before the client collects the
    /// scene's spawnable objects and before the server spawns them, so the ids are there for both.
    /// </summary>
    internal static void PrepareSceneHqs()
    {
        if (Copies[0] != null)
        {
            return;
        }

        FactionHQ? source = FindSourceHq();
        if (source == null || SceneIdField == null || HasSpawnedField == null)
        {
            return;
        }

        if (source.GetComponentsInChildren<NetworkIdentity>(true).Length != 1)
        {
            FactionsPlugin.Log.LogError("The map's faction HQ has changed shape; no extra factions this match.");
            return;
        }

        // Under an inactive holder no Awake runs while the copy's ids are rewritten; Mirage's own
        // Awake would otherwise see _hasSpawned copied from the source and destroy the copy.
        activeExtras = 0;
        ResetSession();
        GameObject holder = new("Ground Control Factions HQs");
        holder.SetActive(false);
        Transform parent = source.transform.parent;
        for (int i = 0; i < Extras.Length; i++)
        {
            GameObject copy = Object.Instantiate(source.gameObject, holder.transform);
            copy.name = "FactionHQ_" + Extras[i].Name;
            copy.SetActive(false);
            NetworkIdentity identity = copy.GetComponent<NetworkIdentity>();
            HasSpawnedField.SetValue(identity, false);
            SceneIdField.SetValue(identity, SceneIdBase + (ulong)(i + 1));
            copy.GetComponent<FactionHQ>().faction = GetFaction(i, source.faction);
            copy.transform.SetParent(parent, false);
            Copies[i] = identity;
        }

        Object.Destroy(holder);
    }

    /// <summary>
    /// Host, at the scene's spawn: the copies stay out of it (scene id cleared), because the map and
    /// its airbases are enabled after it and the count is capped by those. They are spawned by
    /// <see cref="SpawnMatchHqs"/>, or in the editor by the first tick.
    /// </summary>
    internal static void HoldServerHqs()
    {
        PrepareSceneHqs();
        foreach (NetworkIdentity? identity in Copies)
        {
            if (identity != null && !identity.IsSpawned)
            {
                SceneIdField.SetValue(identity, 0UL);
            }
        }
    }

    /// <summary>
    /// Host, just before the mission starts: every airbase of the map is registered by now, so the
    /// count is capped at the bases there are, and the HQs are in before the mission hands out
    /// airbases, units and objectives by faction name.
    /// </summary>
    internal static void SpawnMatchHqs()
    {
        Mission? mission = MissionManager.CurrentMission;
        if (GameManager.gameState == GameState.Editor || mission == null || NetworkManagerNuclearOption.i == null
            || !NetworkManagerNuclearOption.i.Server.Active || activeExtras > 0)
        {
            return;
        }

        int asked = RequestedCount(mission);
        int bases = CountBases(mission);
        int want = Mathf.Min(asked, Mathf.Max(MinFactions, bases));
        SpawnExtras(mission, want - MinFactions);
        for (int i = activeExtras; i < Extras.Length; i++)
        {
            string name = Extras[i].Name;
            mission.factions.RemoveAll(f => f.factionName == name);
        }

        if (asked > MinFactions)
        {
            FactionsPlugin.Log.LogInfo($"{MinFactions + activeExtras} factions this match ({asked} asked for, {bases} bases).");
        }
    }

    /// <summary>
    /// Bases the mission will have: the map's registered airbases it does not disable, its own
    /// custom ones (spawned later, at the start), and every ship of the mission that carries a deck.
    /// </summary>
    internal static int CountBases(Mission mission)
    {
        int count = 0;
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            SavedAirbase? saved = airbase != null ? airbase.SavedAirbase : null;
            if (saved == null || airbase!.AttachedAirbase)
            {
                continue;
            }

            SavedAirbase? overridden = mission.airbases.Find(a => a.UniqueName == saved.UniqueName);
            count += (overridden ?? saved).Disabled ? 0 : 1;
        }

        foreach (SavedAirbase saved in mission.airbases)
        {
            count += !saved.IsOverride && !saved.IsAttached() && !saved.Disabled ? 1 : 0;
        }

        foreach (SavedShip ship in mission.ships)
        {
            if (Encyclopedia.i != null
                && Encyclopedia.Lookup.TryGetValue(ship.type, out UnitDefinition definition)
                && definition.unitPrefab != null
                && definition.unitPrefab.GetComponentInChildren<Airbase>(true) != null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Run at plugin load: the catalogue has room for the slider's maximum, names are
    /// unique and none is a stock faction's, the tag reads back, and the army share adds up.</summary>
    internal static void SelfCheck()
    {
        bool ok = Extras.Length == MaxFactions - MinFactions;
        HashSet<string> names = new();
        foreach (Spec spec in Extras)
        {
            ok &= names.Add(spec.Name) && spec.Name != FactionHelper.Boscali && spec.Name != FactionHelper.Primeva;
        }

        Mission mission = new() { missionSettings = new MissionSettings() };
        ok &= RequestedCount(mission) == MinFactions;
        SetRequestedCount(mission, 14);
        ok &= RequestedCount(mission) == MaxFactions;
        SetRequestedCount(mission, 5);
        ok &= RequestedCount(mission) == 5 && mission.missionSettings.Tags.Count == 1;
        SetRequestedCount(mission, MinFactions);
        ok &= mission.missionSettings.Tags.Count == 0;
        if (!ok)
        {
            FactionsPlugin.Log.LogError("Ground Control Factions self-check failed.");
        }
    }

    internal static bool IsEliminated(FactionHQ hq)
    {
        return Eliminated.Contains(hq);
    }

    /// <summary>
    /// Free-for-all result: a knocked-out faction leaves the match, which goes on until one is left
    /// and wins. Returns true once the match is decided.
    /// </summary>
    internal static bool Eliminate(FactionHQ hq, string reason)
    {
        if (hq == null || hq.faction == null || !Eliminated.Add(hq))
        {
            return false;
        }

        FactionsPlugin.Log.LogInfo($"{hq.faction.factionName} {reason}: out of the match.");
        FactionsApi.Announce?.Invoke($"{hq.faction.factionName.ToUpperInvariant()} IS OUT OF THE WAR");
        FactionHQ? last = null;
        int alive = 0;
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (IsContender(other))
            {
                alive++;
                last = other;
            }
        }

        if (alive > 1 || last == null)
        {
            return false;
        }

        FactionsPlugin.Log.LogInfo($"{last.faction.factionName} is the last faction standing.");
        Declaring = true;
        try
        {
            last.DeclareEndGame(EndType.Victory);
        }
        finally
        {
            Declaring = false;
        }

        return true;
    }

    /// <summary>The base game's AI aircraft limit is per faction: shared out, so eight factions fly
    /// what two would.</summary>
    internal static void ShareAiLimit(FactionHQ hq)
    {
        if (FactionsApi.IsMultiFaction && hq.AIAircraftLimit > 0)
        {
            hq.AIAircraftLimit = FactionsApi.ShareOf(hq.AIAircraftLimit);
        }
    }

    private static bool IsContender(FactionHQ? hq)
    {
        return hq != null && hq.faction != null && !Eliminated.Contains(hq) && FactionsApi.IsBystander?.Invoke(hq) != true;
    }

    /// <summary>
    /// The built-in rule, for the base game: a faction that has held no base (carriers count) for
    /// <see cref="NoBaseSeconds"/> is out. A mod with its own rule sets
    /// <see cref="FactionsApi.ExternalVictory"/> and this stays off.
    /// </summary>
    private static void ApplyNoBaseRule()
    {
        if (GameManager.gameResolution != GameResolution.Ongoing)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetSortedHQs())
        {
            if (!IsContender(hq))
            {
                continue;
            }

            if (CountBases(hq) > 0)
            {
                NoBaseSince.Remove(hq);
                continue;
            }

            if (!NoBaseSince.TryGetValue(hq, out float since))
            {
                NoBaseSince[hq] = Time.time;
            }
            else if (Time.time - since >= NoBaseSeconds && Eliminate(hq, "lost its last base"))
            {
                return;
            }
        }
    }

    /// <summary>Test only: <c>-gcfKnockout Name@seconds</c> knocks that faction out that many
    /// seconds into the match, so spectating can be tried without playing a war to the end.</summary>
    private static void TestKnockout()
    {
        string[] args = CommandLine;
        int at = Array.IndexOf(args, "-gcfKnockout");
        if (at < 0 || at + 1 >= args.Length)
        {
            return;
        }

        string[] parts = args[at + 1].Split('@');
        float seconds = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 60f;
        FactionHQ? hq = FactionRegistry.HqFromName(parts[0]);
        if (hq != null && !IsEliminated(hq) && Time.timeSinceLevelLoad > seconds)
        {
            Eliminate(hq, "was knocked out by -gcfKnockout");
        }
    }

    private static int CountBases(FactionHQ hq)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            count += airbase != null && !airbase.disabled ? 1 : 0;
        }

        return count;
    }

    private static bool IsLandBase(Airbase? airbase)
    {
        return airbase != null && !airbase.disabled && airbase.center != null && !airbase.TryGetAttachedUnit(out _);
    }

    private static string Label(Airbase airbase)
    {
        string? name = airbase.SavedAirbase?.DisplayName;
        return string.IsNullOrEmpty(name) ? airbase.name : name!;
    }

    /// <summary>
    /// The editor's Factions row raised the count: spawn the new HQs at once so their names are in
    /// the airbase and unit faction lists straight away. Lowering it takes effect on the next load,
    /// because a spawned HQ cannot be taken back out from under the editor's lists.
    /// </summary>
    private static void TickEditor()
    {
        Mission? mission = MissionManager.CurrentMission;
        if (mission != null && RequestedCount(mission) - MinFactions > activeExtras
            && NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active)
        {
            SpawnExtras(mission, RequestedCount(mission) - MinFactions);
        }
    }

    /// <summary>Spawns the copies up to <paramref name="extras"/>. In the editor the map's bases
    /// and units were linked before these HQs existed, so the ones the file gives them are handed
    /// over again.</summary>
    private static void SpawnExtras(Mission mission, int extras)
    {
        ServerObjectManager som = NetworkManagerNuclearOption.i.ServerObjectManager;
        for (int i = activeExtras; i < extras && i < Extras.Length; i++)
        {
            NetworkIdentity? identity = Copies[i];
            if (identity == null)
            {
                break;
            }

            EnsureMissionFaction(mission, Extras[i].Name);
            SceneIdField.SetValue(identity, SceneIdBase + (ulong)(i + 1));
            identity.gameObject.SetActive(true);
            som.Spawn(identity.gameObject);
            activeExtras = i + 1;
            if (GameManager.gameState == GameState.Editor)
            {
                RelinkEditorFaction(Extras[i].Name, identity.GetComponent<FactionHQ>());
                FactionsPlugin.Log.LogInfo($"{Extras[i].Name} added to the editor.");
            }
        }
    }

    private static void RelinkEditorFaction(string name, FactionHQ hq)
    {
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase != null && airbase.SavedAirbase?.faction == name && !airbase.AttachedAirbase)
            {
                airbase.EditorSetFaction(name, setAttachedAirbaseFaction: false);
            }
        }

        List<SavedUnit> units = new();
        MissionManager.GetAllSavedUnitsNonAlloc(units, includeBuiltIn: true);
        foreach (SavedUnit saved in units)
        {
            if (saved.faction == name && saved.Unit != null)
            {
                saved.Unit.NetworkHQ = hq;
            }
        }
    }

    /// <summary>
    /// Every faction starts with a base: one with none is given the free base furthest from
    /// everyone else's, or failing that one from the faction holding the most. A mod that builds
    /// bases then gets to kit out each faction with no depot.
    /// </summary>
    private static void TryDeal()
    {
        if (FactionsApi.HoldDeal?.Invoke() == true && Time.timeSinceLevelLoad < HoldWaitSeconds)
        {
            return;
        }

        dealt = true;
        dealtAt = Time.time;
        List<FactionHQ> hqs = new(FactionRegistry.GetSortedHQs());
        foreach (FactionHQ hq in hqs)
        {
            if (hq == null || hq.faction == null || CountBases(hq) > 0)
            {
                continue;
            }

            Airbase? pick = PickBase(hq, hqs);
            if (pick == null)
            {
                FactionsPlugin.Log.LogWarning($"No base left to give {hq.faction.factionName}.");
                continue;
            }

            string from = pick.CurrentHQ != null ? pick.CurrentHQ.faction.factionName : "neutral";
            pick.capture.ForceCapture(hq);
            FactionsPlugin.Log.LogInfo($"{hq.faction.factionName} starts at {Label(pick)} (was {from}).");
        }

        // On the base game the stock AI only drives what a depot rolls out, so a bare base gets one.
        Action<FactionHQ, Airbase> kit = FactionsApi.GiveStartingKit ?? GiveDepot;
        foreach (FactionHQ hq in hqs)
        {
            if (hq == null || hq.faction == null)
            {
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (IsLandBase(airbase))
                {
                    // Only a base with no depot of its own (within reach of what GiveDepot places).
                    if (hq.TryGetNearestDepot(airbase.center.position, Mathf.Max(airbase.GetRadius(), 1000f), out _))
                    {
                        break;
                    }

                    kit(hq, airbase);
                    break;
                }
            }
        }
    }

    /// <summary>A vehicle depot on open, flat ground 300-900 m from the base's centre.</summary>
    private static void GiveDepot(FactionHQ hq, Airbase airbase)
    {
        BuildingDefinition? depot = Encyclopedia.i?.buildings.Find(d => d != null && d.unitPrefab != null && d.jsonKey == "VehicleDepot1");
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (depot == null || spawner == null || GameAssets.i == null)
        {
            return;
        }

        Vector3 centre = airbase.center.position;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            Vector3 spot = centre + (Quaternion.Euler(0f, angle, 0f) * Vector3.forward * UnityEngine.Random.Range(300f, 900f));
            if (!TryOpenGround(spot, out Vector3 ground))
            {
                continue;
            }

            // Door facing the base, so what it builds rolls out towards it.
            Quaternion facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(centre - ground, Vector3.up));
            spawner.SpawnFromUnitDefinitionInEditor(depot, (ground + (facing * depot.spawnOffset)).ToGlobalPosition(), facing, hq, $"{hq.faction.factionName} Depot");
            FactionsPlugin.Log.LogInfo($"{hq.faction.factionName} given a vehicle depot at {Label(airbase)}.");
            return;
        }

        FactionsPlugin.Log.LogWarning($"No open ground for {hq.faction.factionName}'s depot at {Label(airbase)}.");
    }

    // ponytail: five-point flatness probe, not GC's footprint check; good enough for one depot per base.
    private static bool TryOpenGround(Vector3 spot, out Vector3 ground)
    {
        ground = default;
        const float Half = 30f;
        Vector3[] probes = { Vector3.zero, new(Half, 0f, Half), new(-Half, 0f, Half), new(Half, 0f, -Half), new(-Half, 0f, -Half) };
        for (int i = 0; i < probes.Length; i++)
        {
            Vector3 origin = new(spot.x + probes[i].x, Datum.LocalSeaY + 10000f, spot.z + probes[i].z);

            // The first thing below must be bare terrain: no building, runway, tree or sea.
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 20000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
                || hit.collider.sharedMaterial != GameAssets.i.terrainMaterial
                || hit.normal.y < 0.97f
                || hit.point.y < Datum.LocalSeaY + 2f
                || (i > 0 && Mathf.Abs(hit.point.y - ground.y) > 2f))
            {
                return false;
            }

            if (i == 0)
            {
                ground = hit.point;
            }
        }

        return true;
    }

    private static Airbase? PickBase(FactionHQ hq, List<FactionHQ> hqs)
    {
        // Donors keep at least one base: a faction is only asked when it holds two or more.
        FactionHQ? donor = null;
        foreach (FactionHQ other in hqs)
        {
            if (other != null && other != hq && CountLandBases(other) >= 2
                && (donor == null || CountLandBases(other) > CountLandBases(donor)))
            {
                donor = other;
            }
        }

        Airbase? best = null;
        float bestScore = -1f;
        bool bestNeutral = false;
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (!IsLandBase(airbase))
            {
                continue;
            }

            bool neutral = airbase.CurrentHQ == null;
            if (!neutral && airbase.CurrentHQ != donor)
            {
                continue;
            }

            float score = NearestHeldBase(airbase);
            if ((neutral && !bestNeutral) || (neutral == bestNeutral && score > bestScore))
            {
                best = airbase;
                bestScore = score;
                bestNeutral = neutral;
            }
        }

        return best;
    }

    /// <summary>Distance from <paramref name="airbase"/> to the nearest other base anybody holds.</summary>
    private static float NearestHeldBase(Airbase airbase)
    {
        float nearest = float.MaxValue;
        Vector3 at = airbase.center.position;
        foreach (Airbase other in FactionRegistry.airbaseLookup.Values)
        {
            if (other != null && other != airbase && other.CurrentHQ != null && other.center != null)
            {
                nearest = Mathf.Min(nearest, (other.center.position - at).sqrMagnitude);
            }
        }

        return nearest;
    }

    private static int CountLandBases(FactionHQ hq)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            count += IsLandBase(airbase) ? 1 : 0;
        }

        return count;
    }

    /// <summary>An extra faction plays by the first faction's rules (funds, income, warheads,
    /// supplies, restrictions, AI aircraft) as its own copy, joinable.</summary>
    private static void EnsureMissionFaction(Mission mission, string name)
    {
        if (mission.TryGetFaction(name, out _))
        {
            return;
        }

        MissionFaction? template = null;
        foreach (MissionFaction existing in mission.factions)
        {
            if (!IsExtraName(existing.factionName))
            {
                template = existing;
                break;
            }
        }

        MissionFaction copy = new(name);
        if (template != null)
        {
            copy = (MissionFaction)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(template, null);
            copy.factionName = name;
            copy.supplies = new List<UnitCount>(template.supplies ?? new List<UnitCount>());
            copy.restrictions = template.restrictions == null
                ? null!
                : new Restrictions { aircraft = new List<string>(template.restrictions.aircraft), weapons = new List<string>(template.restrictions.weapons) };
            copy.cameraStartPosition = default;
            copy.FactionHQ = null!;
        }

        copy.preventJoin = false;
        mission.factions.Add(copy);
    }

    private static bool IsExtraName(string name)
    {
        for (int i = 0; i < Extras.Length; i++)
        {
            if (Extras[i].Name == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The map's own HQ with the lowest scene id, so every peer copies the same one.</summary>
    private static FactionHQ? FindSourceHq()
    {
        FactionHQ? best = null;
        ulong bestId = ulong.MaxValue;
        foreach (FactionHQ hq in Resources.FindObjectsOfTypeAll<FactionHQ>())
        {
            if (hq == null || !hq.gameObject.scene.IsValid() || hq.faction == null || IsExtra(hq.faction))
            {
                continue;
            }

            NetworkIdentity identity = hq.GetComponent<NetworkIdentity>();
            if (identity != null && identity.SceneId != 0 && identity.SceneId < bestId)
            {
                best = hq;
                bestId = identity.SceneId;
            }
        }

        return best;
    }

    /// <summary>The extra faction's asset, made once per session from a stock one (its convoys,
    /// music and sprite slots), with its own name, colour and emblem.</summary>
    private static Faction GetFaction(int index, Faction source)
    {
        Faction? existing = Factions[index];
        if (existing != null)
        {
            return existing;
        }

        Spec spec = Extras[index];

        // Kept through the asset cleanup every scene load runs (Resources.UnloadUnusedAssets).
        Faction faction = Object.Instantiate(source);
        faction.hideFlags = HideFlags.DontUnloadUnusedAsset;
        faction.name = spec.Name;
        faction.factionName = spec.Name;
        faction.factionTag = spec.Tag;
        faction.factionExtendedName = spec.ExtendedName;
        faction.color = spec.Color;
        faction.selectedColor = Color.Lerp(spec.Color, Color.white, 0.3f);
        faction.LeaderboardOrder = MinFactions + index;
        faction.factionColorLogo = MakeEmblem(spec.Color, index + 3, false);
        faction.factionHeaderSprite = faction.factionColorLogo;
        faction.factionGrayscaleLogo = MakeEmblem(spec.Color, index + 3, true);
        Factions[index] = faction;
        SourceOf[faction] = source;
        return faction;
    }

    /// <summary>A broken ring round a flag hung from a bar, in the faction's colour, with a star
    /// of <paramref name="points"/> points cut through the flag, so each faction's emblem differs in
    /// shape as well as colour.</summary>
    private static Sprite MakeEmblem(Color color, int points, bool gray)
    {
        const int size = 256;
        if (gray)
        {
            float luma = (color.r * 0.299f) + (color.g * 0.587f) + (color.b * 0.114f);
            color = new Color(luma, luma, luma);
        }

        Texture2D texture = new(size, size, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[size * size];
        const float centre = size / 2f;
        const float radius = size * 0.48f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Four samples a pixel, for smooth edges; Unity textures count rows up.
                int hits = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    float u = (x + 0.25f + (0.5f * (sample & 1)) - centre) / radius;
                    float v = (size - y - 0.75f + (0.5f * (sample >> 1)) - centre) / radius;
                    hits += IsEmblem(u, v, points) ? 1 : 0;
                }

                pixels[(y * size) + x] = new Color(color.r, color.g, color.b, hits / 4f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    /// <summary>Whether a point of the emblem is filled; <paramref name="u"/> runs right and
    /// <paramref name="v"/> down, in units of the ring's outer radius.</summary>
    private static bool IsEmblem(float u, float v, int points)
    {
        float side = Mathf.Abs(u);
        float distance = Mathf.Sqrt((u * u) + (v * v));
        if (distance >= 0.93f && distance <= 1f)
        {
            // Gaps top and bottom, and either side of a short arc at each flank.
            float slope = Mathf.Atan2(Mathf.Abs(v), side) * Mathf.Rad2Deg;
            return slope < 15f || (slope > 33f && slope < 85f);
        }

        if (v >= -0.53f && v <= -0.455f)
        {
            // The bar the flag hangs from, with a cap at each end.
            return side <= 0.3f || (side >= 0.34f && side <= 0.42f);
        }

        const float halfWidth = 0.325f;
        if (side > halfWidth || v < -0.42f || v > 0.52f - (0.2f * (1f - (side / halfWidth))))
        {
            return false;
        }

        // The star is a hole in the flag.
        const float star = 0.27f;
        float dy = v + 0.03f;
        float spike = Mathf.Abs(Mathf.Cos((Mathf.Atan2(-dy, u) + (Mathf.PI / 2f)) * points / 2f));
        return Mathf.Sqrt((u * u) + (dy * dy)) > Mathf.Lerp(star * 0.35f, star * 0.85f, spike * spike);
    }

    private readonly struct Spec
    {
        internal Spec(string name, string extendedName, string tag, Color color)
        {
            Name = name;
            ExtendedName = extendedName;
            Tag = tag;
            Color = color;
        }

        internal string Name { get; }

        internal string ExtendedName { get; }

        internal string Tag { get; }

        internal Color Color { get; }
    }
}
