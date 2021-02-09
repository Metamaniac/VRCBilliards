
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_positioner : UdonSharpBehaviour
{
    public ht8b gameStateManager;

    // Since v0.3.0: OnPickupUseDown -> OnDrop
    public override void OnDrop()
    {
        gameStateManager.PlaceBall();
    }
}