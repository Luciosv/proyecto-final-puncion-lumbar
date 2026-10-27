using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    public struct LayerDataSpine
    {
        public LayerTypeSpine  type;
        public string          name;

        // Geometry (physical metres from the stack origin along the insertion axis).
        public float dStartM;
        public float dEndM;
        public float widthM;

        // Domain width of the fitted curve (mm from AnatomicalNoMuscle2.json).
        public float jsonWidthMm;

        // dzScale = jsonWidthMm / (widthM * 1000): maps local dz (mm scene) → dz (mm JSON domain).
        public float dzScale;

        public LayerCurveSpine curve;
        public Color           bandColor;
    }
}
