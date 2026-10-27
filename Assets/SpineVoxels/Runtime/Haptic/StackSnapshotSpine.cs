using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    // Immutable snapshot of one PunctureAccess segment's tissue data.
    // Published atomically (volatile reference) from PunctureAccessStack → read by haptic thread.
    public sealed class StackSnapshotSpine
    {
        // Tissue layers ordered by depth (metres physical from origin along axis).
        public LayerDataSpine[] layers;

        // Insertion axis: unit vector pointing from skin surface toward cord (Unity +Z = anterior).
        public Vector3 axis;

        // World-space point where depth = 0 (entry face of the first layer).
        public Vector3 origin;

        // visual/physical ratio: world_units = physical_metres * visualScale.
        // = 1000 * unityUnitsPerMm (e.g. 1000*0.003 = 3.0).
        public float visualScale;

        // Physical depth (m) to the far end of the last layer.
        public float totalDepth;

        // World-space AABB for broad-phase inside/outside check.
        public Bounds bounds;
    }
}
