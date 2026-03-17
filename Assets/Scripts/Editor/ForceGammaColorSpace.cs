using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
static class ForceGammaColorSpace
{
    static ForceGammaColorSpace()
    {
        if (PlayerSettings.colorSpace != ColorSpace.Gamma)
        {
            PlayerSettings.colorSpace = ColorSpace.Gamma;
            Debug.Log("[SparkDemo] Forced color space to Gamma.");
        }
    }
}
