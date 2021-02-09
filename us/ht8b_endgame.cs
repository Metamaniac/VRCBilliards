
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_endgame : UdonSharpBehaviour
{
    public ht8b gameStateManager;

    public override void Interact()
    {
        EndGame();
    }

    public void OnButtonPressed()
    {
        EndGame();
    }

    private void EndGame()
    {
        gameStateManager.ForceReset();
    }
}
