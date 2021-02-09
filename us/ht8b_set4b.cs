
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_set4b : UdonSharpBehaviour
{
    public ht8b target;
    public bool isKorean;

    public override void Interact()
    {
        if (isKorean)
        {
            target.SelectedKoreanFourBall();
        }
        else
        {
            target.SelectedJapaneseFourBall();
        }
    }
}
