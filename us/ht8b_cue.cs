using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[RequireComponent(typeof(SphereCollider))]
public class ht8b_cue : UdonSharpBehaviour
{
    public GameObject target;
    public ht8b_otherhand targetController;

    public GameObject cue;

    public ht8b gameController;

    public GameObject cueTip;
    public GameObject pressE;

    // Pickuip components
    private VRC_Pickup thisPickup;
    private VRC_Pickup targetPickup;

    public bool desktopPrimaryControl;
    public bool useDesktop;

    // ( Experimental ) Allow player ownership autoswitching routine 
    public bool allowAutoSwitch = true;
    public int playerID = 0;

    private Vector3 objectTarget;
    private Vector3 objectBase;

    private Vector3 vBase;
    private Vector3 vLineNorm;
    private Vector3 targetOriginalDelta;

    private Vector3 vSnOff;
    private float vSnDet;

    private bool isArmed = false;
    private bool isHolding = false;
    private bool isOtherLock = false;

    private Vector3 cueResetPosition;
    private Vector3 targetResetPosition;
    private Vector3 desktopCursorPosition = Vector3.zero;

    private SphereCollider ownCollider;
    private SphereCollider targetCollider;

    public void Start()
    {
        ownCollider = GetComponent<SphereCollider>();

        targetCollider = target.GetComponent<SphereCollider>();
        if (!targetCollider)
        {
            Debug.LogError("ht8b_cue: Start: target is missing a SphereCollider. Aborting cue setup.");
            gameObject.SetActive(false);
            return;
        }

        // Match lerped positions at start
        objectBase = gameObject.transform.position;
        objectTarget = target.transform.position;

        targetOriginalDelta = gameObject.transform.InverseTransformPoint(target.transform.position);
        OnDrop();

        thisPickup = (VRC_Pickup)gameObject.GetComponent(typeof(VRC_Pickup));
        if (!thisPickup)
        {
            Debug.LogError("ht8b_cue: Start: this object is missing a VRC_Pickup script. Aborting cue setup.");
            gameObject.SetActive(false);
            return;
        }

        targetPickup = (VRC_Pickup)target.GetComponent(typeof(VRC_Pickup));
        if (!targetPickup)
        {
            Debug.LogError("ht8b_cue: Start: target object is missing a VRC_Pickup script. Aborting cue setup.");
            gameObject.SetActive(false);
            return;
        }

        targetResetPosition = target.transform.position;
        cueResetPosition = gameObject.transform.position;

#if !UNITY_ANDROID
        useDesktop = false; // TODO: (@Xieve please look at this when reviewing!!) Is UseDesktop ever supposed to be true???
        desktopPrimaryControl = true;
#endif
    }

    public void Update()
    {
        // Put cue in hand
        if (desktopPrimaryControl)
        {
            if (useDesktop && isHolding)
            {
                gameObject.transform.position = Networking.LocalPlayer.GetBonePosition(HumanBodyBones.RightHand);

                // Temporary target
                target.transform.position = gameObject.transform.position + Vector3.up;

                Vector3 playerpos = gameController.gameObject.transform.InverseTransformPoint(Networking.LocalPlayer.GetPosition());

                // Check turn entry
                if ((Mathf.Abs(playerpos.x) < 2.0f) && (Mathf.Abs(playerpos.z) < 1.5f))
                {
                    VRCPlayerApi.TrackingData hmd = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                    pressE.SetActive(true);
                    pressE.transform.position = hmd.position + hmd.rotation * Vector3.forward;
                    if (Input.GetKeyDown(KeyCode.E))
                    {
                        desktopPrimaryControl = false;
                        gameController.OnPickupCueLocally();
                    }
                }
                else
                {
                    pressE.SetActive(false);
                }
            }

            objectBase = Vector3.Lerp(objectBase, gameObject.transform.position, Time.deltaTime * 16.0f);

            if (!isOtherLock)
            {
                objectTarget = Vector3.Lerp(objectTarget, target.transform.position, Time.deltaTime * 16.0f);
            }

            if (isArmed)
            {
                vSnOff = objectBase - vBase;
                vSnDet = Vector3.Dot(vSnOff, vLineNorm);
                cue.transform.position = vBase + vLineNorm * vSnDet;
            }
            else
            {
                // put cue at base position	
                cue.transform.position = objectBase;
                cue.transform.LookAt(objectTarget);
            }
        }

        //Xiexe: I find this to be a little silly, hard coding bounds is a little nuts. I think it should either be exposed to the inspector
        // or should be set using a trigger volume and using it's bounds via the editor. We're in a modern game engine, no need to do this. We have the technology.
        // FSP 8/2/20: You can just enable a collider around the table whilst holding the cue. If you're playing the game you shouldn't be wandering off anyway.
        if (isHolding) // TODO: Refactor.
        {
            // Clamp controllers to play boundaries while we have hold of them
            Vector3 temp = this.transform.localPosition;
            temp.x = Mathf.Clamp(temp.x, -4.0f, 4.0f);
            temp.y = Mathf.Clamp(temp.y, -0.8f, 1.5f);
            temp.z = Mathf.Clamp(temp.z, -3.25f, 3.25f);
            this.transform.localPosition = temp;
            temp = target.transform.localPosition;
            temp.x = Mathf.Clamp(temp.x, -4.0f, 4.0f);
            temp.y = Mathf.Clamp(temp.y, -0.8f, 1.5f);
            temp.z = Mathf.Clamp(temp.z, -3.25f, 3.25f);
            target.transform.localPosition = temp;
        }
    }

    public override void OnPickupUseDown()
    {
        if (!useDesktop)    // VR only controls
        {
            isArmed = true;

            // copy target position in
            vBase = this.transform.position;

            // Set up line normal
            vLineNorm = (target.transform.position - vBase).normalized;

            // It should now be able to impulse ball
            gameController.StartHit();
        }
    }

    public override void OnPickupUseUp()    // VR
    {
        if (!useDesktop)
        {
            isArmed = false;
            gameController.EndHit();
        }

        isArmed = false;
        gameController.EndHit();
    }

    public override void OnPickup()
    {
        if (!useDesktop)    // We dont need other hand to be availible for desktop player
        {
            target.transform.localScale = Vector3.one;
        }

        target.transform.localScale = Vector3.one; //TODO: This code is defective.

        // Register the cuetip with main game
        // gameController.cuetip = objTip; 

        // Not sure if this is necessary to do both since we pickup this one,
        // but just to be safe
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        Networking.SetOwner(Networking.LocalPlayer, target);
        isHolding = true;
        targetController.bOtherHold = true;
        targetCollider.enabled = true;

    }

    public override void OnDrop()
    {
        target.transform.localScale = Vector3.zero;
        isHolding = false;
        targetController.bOtherHold = false;
        targetCollider.enabled = false;

        if (useDesktop)
        {
            pressE.SetActive(false);
            gameController.OnPutDownCueLocally();
        }
    }


    // Set if local player can hold onto cue grips or not
    public void AllowAccess()
    {
        ownCollider.enabled = true;
        targetCollider.enabled = true;
    }

    public void DenyAccess()
    {
        // Put back on the table
        target.transform.position = targetResetPosition;
        gameObject.transform.position = cueResetPosition;

        ownCollider.enabled = false;
        targetCollider.enabled = false;

        // Force user to drop it
        thisPickup.Drop();
        targetPickup.Drop();
    }

    public void LockOther()
    {
        isOtherLock = true;
    }

    public void UnlockOther()
    {
        isOtherLock = false;
    }
}
