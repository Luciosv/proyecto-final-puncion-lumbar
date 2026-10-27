using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    public enum PalpationShape { Sphere, Plane, Capsule, Box }

    // Place on a bone surface or process so the haptic device feels it during palpation.
    // HapticPalpationController reads these in Update() and caches them thread-safely.
    [AddComponentMenu("SpineVoxels/Haptic Palpation Target")]
    [ExecuteAlways]
    public class HapticPalpationTarget : MonoBehaviour
    {
        public PalpationShape shape     = PalpationShape.Sphere;
        [Range(0.001f, 0.05f)] public float radius        = 0.015f;
        [Range(0.001f, 0.2f)]  public float capsuleHeight = 0.05f;
        public Vector3 boxSize = new Vector3(0.03f, 0.03f, 0.03f);

        [Range(0, 800)] public float stiffness = 300f;
        [Range(0, 3)]   public float damping   = 1f;

        [Header("Firmness (progressive stiffness)")]
        [Tooltip("Ramp exponent. 1 = linear (soft spring, original). 2 = quadratic (firm wall): " +
                 "gentle on contact, resists much harder the deeper you push, reaching the device " +
                 "force ceiling in a short depth so it feels like a firm boundary instead of a trampoline.")]
        [Range(1f, 4f)] public float firmnessExponent = 2f;
        [Tooltip("Penetration scale (mm) for the firmness ramp: at this depth the force equals the " +
                 "linear value (stiffness × depth). Smaller = firmer wall with less give.")]
        [Min(0.1f)] public float firmnessDepthMm = 3f;

        [Range(100, 2000)] public int hapticLoopHz = 1000;

        public Color gizmoColor = new Color(0.2f, 0.8f, 1f, 0.7f);

        public float ColgateDampingMin => stiffness * (1f / hapticLoopHz) / 2f;

        void OnDrawGizmos()
        {
            Gizmos.color = gizmoColor;
            switch (shape)
            {
                case PalpationShape.Sphere:
                    Gizmos.DrawWireSphere(transform.position, radius);
                    break;
                case PalpationShape.Box:
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawWireCube(Vector3.zero, boxSize);
                    Gizmos.matrix = Matrix4x4.identity;
                    break;
                case PalpationShape.Plane:
                {
                    var pos = transform.position;
                    float s = radius * 2f;
                    Gizmos.DrawLine(pos - transform.right   * s, pos + transform.right   * s);
                    Gizmos.DrawLine(pos - transform.forward * s, pos + transform.forward * s);
                    Gizmos.DrawLine(pos, pos + transform.up * radius);
                    break;
                }
                case PalpationShape.Capsule:
                {
                    var   up  = transform.up;
                    var   rt  = transform.right;
                    var   fw  = transform.forward;
                    var   pos = transform.position;
                    float hh  = capsuleHeight * 0.5f;
                    Vector3 a = pos + up * hh;   // top cap centre
                    Vector3 b = pos - up * hh;   // bottom cap centre

                    // End caps.
                    Gizmos.DrawWireSphere(a, radius);
                    Gizmos.DrawWireSphere(b, radius);

                    // Cylinder side lines (tangent to the caps) so it reads as one capsule.
                    Gizmos.DrawLine(a + rt * radius, b + rt * radius);
                    Gizmos.DrawLine(a - rt * radius, b - rt * radius);
                    Gizmos.DrawLine(a + fw * radius, b + fw * radius);
                    Gizmos.DrawLine(a - fw * radius, b - fw * radius);
                    break;
                }
            }
        }
    }
}
