
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_otherhand : UdonSharpBehaviour
{

    public GameObject objPrimary;
    private ht8b_cue usPrimary;

    private Vector3 originalDelta;
    private bool isHolding = false;
    public bool bOtherHold = false;  // Primary is being held
    private Vector3 lockpos;

    public void Start()
    {
        usPrimary = objPrimary.GetComponent<ht8b_cue>();
        OnDrop();
    }

    public void Update()
    {
        // Pseudo-parented while it left is let go
        if (!isHolding && bOtherHold)
        {
            gameObject.transform.position = objPrimary.transform.TransformPoint(originalDelta);
        }
    }

    public override void OnPickupUseDown()
    {
        usPrimary.LockOther();
        lockpos = gameObject.transform.position;
    }

    public override void OnPickupUseUp()    // VR
    {
        usPrimary.UnlockOther();
    }

    public override void OnPickup()
    {
        isHolding = true;
    }

    public override void OnDrop()
    {
        originalDelta = objPrimary.transform.InverseTransformPoint(gameObject.transform.position);

        // Clamp within 1 meters in case something got messed up
        if (originalDelta.sqrMagnitude > 0.6084f)
        {
            originalDelta = originalDelta.normalized * 0.78f;
        }

        isHolding = false;
        usPrimary.UnlockOther();
    }
}
