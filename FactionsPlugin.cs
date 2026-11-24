using System.Diagnostics.CodeAnalysis;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GroundControlFactions;

/// <summary>
/// Ground Control Factions: up to ten factions in one match. Works on the base game on its own; Ground Control
/// RTS hooks in through <see cref="FactionsApi"/> when it is installed too.
/// </summary>
[BepInPlugin(PluginId, "Ground Control Factions", "1.0.0.4")]
[SuppressMessage("Usage", "CA2243:Attribute string literals should parse correctly", Justification = "A BepInEx plugin GUID is a reverse-DNS name, not a System.Guid.")]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Unity owns a plugin's lifetime; OnDestroy unpatches Harmony.")]
public sealed class FactionsPlugin : BaseUnityPlugin
{
    /// <summary>The BepInEx plugin id other mods check for.</summary>
    public const string PluginId = "com.groundcontrol.factions";

    private Harmony? harmony;

    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;
        harmony = new Harmony(PluginId);
        harmony.PatchAll(typeof(FactionsPlugin).Assembly);
        FactionsService.SelfCheck();
        FactionsPlayers.Initialize();
        GameObject runner = new("Ground Control Factions");
        DontDestroyOnLoad(runner);
        runner.hideFlags = HideFlags.HideAndDontSave;
        runner.AddComponent<FactionsRunner>();
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
    }
}
