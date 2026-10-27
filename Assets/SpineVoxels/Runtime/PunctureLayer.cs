using UnityEngine;

namespace SpineVoxels.Runtime
{
    // Marks a tissue slab the needle crosses during a midline lumbar puncture.
    // The needle simulator reads `order` to know the crossing sequence and
    // `thicknessMm` to model the resistance/depth of each layer.
    //
    // Order is relative to the three modelled ligamentous/membranous layers,
    // posterior → anterior (the needle travels along +Z in this scene):
    //   0 = supraspinous ligament   (most posterior, first after skin+fat)
    //   1 = ligamentum flavum
    //   2 = dura mater              (most anterior, last before the CSF/target)
    // Skin and fat already exist as separate structures; offset these if the
    // simulator needs a single global ordering across all tissues.
    public sealed class PunctureLayer : MonoBehaviour
    {
        [Tooltip("Crossing order along the needle path (0 = crossed first).")]
        public int order;

        [Tooltip("Human-readable layer name, e.g. \"ligamentum_flavum\".")]
        public string layerName;

        [Tooltip("Slab thickness in millimetres along the needle (Z) axis.")]
        public float thicknessMm;
    }
}
