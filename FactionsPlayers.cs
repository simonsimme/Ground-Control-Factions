using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Chat;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// The players' side of a multi-faction match: a knocked-out faction's players become spectators,
/// and a guest without this mod is told what it is missing.
/// </summary>
/// <remarks>
/// Spectating is the game's own state (a player with no HQ). The base game never takes a player out of
/// a faction, so the host clears the synced HQ itself, once the player is out of the cockpit; every
/// guest, with the mod or without, then sees it. A guest without the mod still joins: the extra HQs
/// fail to spawn on its side (one logged error each) and it can play Boscali or Primeva. Guests
/// with the mod say hello once their player spawns; the host warns the ones that did not.
/// </remarks>
internal static class FactionsPlayers
{
    private const string MissingModText = "This match has {0} factions, but without the Ground Control Factions mod you only see "
        + "and can join Boscali and Primeva. Download Ground Control Factions to see and join all of them.";

    private const float HelloWaitSeconds = 10f;

    private const float SpectateIntervalSeconds = 1f;

    private static readonly MethodInfo HqSetter = AccessTools.PropertySetter(typeof(Player), "Network<HQ>k__BackingField");

    private static readonly HashSet<INetworkPlayer> ModdedGuests = new();

    private static readonly List<(INetworkPlayer Guest, float DueAt)> PendingChecks = new();

    private static readonly List<Player> Leaving = new();

    private static MessageHandler? boundServer;

    private static INetworkPlayer? helloSentAs;

    private static float nextSpectateAt;

    private static bool registered;

    /// <summary>Teaches Mirage the hello. Once, at plugin load.</summary>
    internal static void Initialize()
    {
        Writer<FactionsHello>.Write = static (writer, message) => writer.WriteInt32(message.Version);
        Reader<FactionsHello>.Read = static reader => new FactionsHello { Version = reader.ReadInt32() };
        int id = MessagePacker.GetId<FactionsHello>();
        if (MessagePacker.MessageTypes.TryGetValue(id, out Type existing) && existing != typeof(FactionsHello))
        {
            FactionsPlugin.Log.LogError($"Mod check is off: message id {id} already belongs to {existing.FullName}.");
            return;
        }

        MessagePacker.RegisterMessage<FactionsHello>();
        registered = true;
    }

    internal static void Tick()
    {
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (manager == null)
        {
            return;
        }

        if (manager.Server != null && manager.Server.Active)
        {
            TickHost(manager.Server);
            return;
        }

        boundServer = null;
        ModdedGuests.Clear();
        PendingChecks.Clear();
        NetworkClient? client = manager.Client;
        if (registered && client != null && client.Active && client.Player != helloSentAs
            && GameManager.GetLocalPlayer<Player>(out _))
        {
            helloSentAs = client.Player;
            client.Send(new FactionsHello { Version = 1 });
        }
    }

    /// <summary>Host: a guest's player spawned; check for the hello a little later.</summary>
    internal static void OnPlayerStarted(Player player)
    {
        if (player.Owner != null && !player.Owner.IsHost)
        {
            PendingChecks.Add((player.Owner, Time.realtimeSinceStartup + HelloWaitSeconds));
        }
    }

    /// <summary>Host: a player whose faction is out may not join any faction again this match.</summary>
    internal static bool IsKnockedOut(Player player)
    {
        FactionHQ? saved = player.GetAuthData()?.SaveData?.Faction;
        return saved != null && FactionsService.IsEliminated(saved);
    }

    /// <summary>The local player just lost its HQ in a running match: show what a spectator sees.</summary>
    internal static void OnLocalHqCleared()
    {
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            return;
        }

        map.SetFaction(null);
        SceneSingleton<GameplayUI>.i?.HideSelectAirbase();
        map.Maximize();
    }

    private static void TickHost(NetworkServer server)
    {
        if (registered && !ReferenceEquals(boundServer, server.MessageHandler))
        {
            boundServer = server.MessageHandler;
            boundServer.RegisterHandler<FactionsHello>(OnHello, allowUnauthenticated: false);
        }

        float now = Time.realtimeSinceStartup;
        for (int i = PendingChecks.Count - 1; i >= 0; i--)
        {
            (INetworkPlayer guest, float dueAt) = PendingChecks[i];
            if (now < dueAt)
            {
                continue;
            }

            PendingChecks.RemoveAt(i);
            if (guest.IsConnected && !ModdedGuests.Contains(guest) && FactionsApi.IsMultiFaction)
            {
                Tell(guest, string.Format(System.Globalization.CultureInfo.InvariantCulture, MissingModText, FactionRegistry.HQLookup.Count));
            }
        }

        ModdedGuests.RemoveWhere(g => !g.IsConnected);
        if (now >= nextSpectateAt && MissionManager.IsRunning)
        {
            nextSpectateAt = now + SpectateIntervalSeconds;
            MoveKnockedOutToSpectate();
        }
    }

    private static void OnHello(INetworkPlayer guest, FactionsHello hello)
    {
        ModdedGuests.Add(guest);
    }

    /// <summary>Every player of a knocked-out faction who is not flying becomes a spectator. One still
    /// in the air finishes the sortie first: pulling the HQ from under a live aircraft is untested.</summary>
    private static void MoveKnockedOutToSpectate()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !FactionsService.IsEliminated(hq))
            {
                continue;
            }

            Leaving.Clear();
            foreach (Player player in hq.GetPlayers(sortByScore: false))
            {
                if (player.Aircraft == null || player.Aircraft.disabled)
                {
                    Leaving.Add(player);
                }
            }

            foreach (Player player in Leaving)
            {
                hq.RemovePlayer(player); // saves the faction, so a rejoin is refused (IsKnockedOut)
                HqSetter.Invoke(player, new object?[] { null });
                FactionsPlugin.Log.LogInfo($"{player} is spectating: {hq.faction.factionName} is out.");
                if (player.Owner != null)
                {
                    Tell(player.Owner, $"{hq.faction.factionName} is out of the war. You are spectating the rest of the match.");
                }
            }
        }
    }

    private static void Tell(INetworkPlayer guest, string text)
    {
        ChatManager? chat = NetworkSceneSingleton<ChatManager>.i;
        if (chat != null)
        {
            chat.RpcTargetServerMessage(guest, text, false);
        }
    }

    /// <summary>The one message this mod sends: a guest with the mod, to the host.</summary>
    internal struct FactionsHello
    {
        public int Version;
    }
}
