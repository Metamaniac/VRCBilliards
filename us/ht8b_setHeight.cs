
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_setHeight : UdonSharpBehaviour
{
    public GameObject heightCalibrator;
    public Material mat;

    void Start()
    {
        mat.SetFloat("_ShadowOffset", heightCalibrator.transform.position.y);
    }
}
