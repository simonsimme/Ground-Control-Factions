using UnityEngine;

namespace GroundControlFactions;

/// <summary>The per-frame tick: the editor's HQs, the base deal and the knockouts.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it through AddComponent.")]
internal sealed class FactionsRunner : MonoBehaviour
{
    private void Update()
    {
        FactionsService.Tick();
        FactionsPlayers.Tick();
    }
}
