// Skin-over-bone palpation directly on the NRRD volume.
//
// IDEA (clinical palpation): you press on the SKIN, which yields a couple of millimetres and
// then resists firmly (an elastic limit the device pushes back against). The BONE underneath is
// far away in the voxel model, so you never reach it — instead its shape is "projected" through
// the skin: the closer the bone lies under a given spot, the FIRMER those same millimetres of
// skin feel. Sliding across the surface, that varying firmness traces the bony relief
// (spinous processes feel hard, the gaps between them feel soft).
//
// THREADING (mirrors TrajectoryProfiler):
//   UNITY THREAD (FixedUpdate ~50–60 Hz): ray-march the NRRD along the tool axis to find the skin
//     surface point, its outward normal (label gradient) and the distance to the bone behind it.
//     Publish an immutable Contact (volatile reference → lock-free).
//   HAPTIC THREAD (DeviceStateChanged ~1–8 kHz): cheap penetration force against the cached
//     Contact: progressive (firm) spring along the skin normal, stiffness scaled by bone closeness.
//
// REQUIRES: Haply Inverse for Unity package + a NrrdTissueVolume in the scene.

using System.Threading;
using UnityEngine;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Skin-Bone Palpation")]
    public class SkinBonePalpation : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("If null, searched on this GameObject.")]
        public Inverse3Controller inverse3;
        public NrrdTissueVolume   nrrdVolume;

        [Tooltip("Palpation tool/finger. Its local +Y is the press direction (into the body). " +
                 "Auto-flipped if it points the wrong way.")]
        public Transform tool;

        [Header("Operation mode")]
        [Tooltip("true = sends force to its own device directly.\n" +
                 "false = another controller calls ComputeForce() and sums the result.")]
        public bool standaloneMode = true;

        [Header("Skin elastic limit")]
        [Tooltip("How far the skin yields (mm) before it feels firm. The force reaches the linear " +
                 "value at this depth and ramps hard past it (set by the exponent).")]
        [Min(0.2f)] public float skinGiveMm = 2.5f;
        [Tooltip("Firmness ramp. 1 = soft linear spring. Higher = firmer wall (reaches the force " +
                 "ceiling in a shorter depth).")]
        [Range(1f, 5f)] public float firmnessExponent = 3f;
        [Tooltip("Base skin stiffness (N/m) over soft tissue (no bone near).")]
        [Min(0f)] public float baseStiffness = 300f;
        [Tooltip("Contact damping (Ns/m). Raise with stiffness to keep it from buzzing.")]
        [Min(0f)] public float damping = 1f;

        [Header("Bone projection")]
        [Tooltip("Distance from the skin (mm) beyond which the bone is no longer felt. Within this " +
                 "range, the closer the bone, the firmer the skin.")]
        [Min(1f)] public float boneInfluenceMm = 40f;
        [Tooltip("Extra firmness multiplier when the bone is right under the skin. e.g. 1.5 → up to " +
                 "2.5× the base stiffness over a shallow bony prominence.")]
        [Min(0f)] public float boneFirmnessGain = 1.5f;

        [Header("Ray-march (Unity thread)")]
        [Tooltip("Step of the NRRD ray-march (mm).")]
        [Min(0.1f)] public float marchStepMm = 0.7f;
        [Tooltip("How far ahead of the tool the ray looks for skin/bone (mm).")]
        [Min(10f)] public float maxRangeMm = 120f;
        [Tooltip("How far behind the tip the ray starts, so a slightly-sunk tip still finds the surface (mm).")]
        [Min(0f)] public float backMarginMm = 15f;

        [Header("Surface smoothing (kills voxel-normal jitter)")]
        [Tooltip("Finite-difference baseline for the normal (mm). LARGER = smoother normal (averages " +
                 "the orientation over more voxels). Too small → the normal snaps between voxel faces " +
                 "and the device buzzes. ~3-6mm works well.")]
        [Min(0.5f)] public float normalBaselineMm = 4f;
        [Tooltip("Temporal smoothing (EMA) of the contact point, normal and bone-closeness. " +
                 "0 = none (raw, jittery). Higher = smoother but laggier. The hand moves slowly, so " +
                 "0.7-0.9 removes the buzz without noticeable lag.")]
        [Range(0f, 0.97f)] public float temporalSmoothing = 0.82f;

        [Header("Safety")]
        [Range(0f, 10f)] public float maxOutputForce = 3.5f;

        [Header("Gizmo")]
        public bool drawGizmo = true;

        // ── Immutable contact snapshot (Unity → haptic, lock-free) ────────────
        sealed class Contact
        {
            public bool    valid;
            public Vector3 point;          // skin surface point (world)
            public Vector3 normal;         // outward (toward the finger)
            public float   boneCloseness;  // 0 = bone far/none, 1 = bone right under the skin
        }
        volatile Contact _contact;

        float _uPerMm = 0.003f;

        // EMA state (Unity thread only) — temporal smoothing of the measured contact.
        Vector3 _emaPoint, _emaNormal;
        float   _emaCloseness;
        bool    _emaInit;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (inverse3 == null) inverse3 = GetComponent<Inverse3Controller>();
        }

        void OnEnable()
        {
            if (inverse3 != null && standaloneMode)
                inverse3.DeviceStateChanged += OnDeviceStateChanged;
        }

        void OnDisable()
        {
            if (inverse3 == null) return;
            if (standaloneMode)
            {
                inverse3.DeviceStateChanged -= OnDeviceStateChanged;
                inverse3.Release();
            }
        }

        // ── Unity thread: build the contact snapshot ──────────────────────────
        void FixedUpdate()
        {
            if (inverse3 == null || nrrdVolume == null || !nrrdVolume.IsReady || tool == null)
            { _contact = null; _emaInit = false; return; }

            Vector3 cursor = inverse3.CursorPosition;
            if (float.IsNaN(cursor.x)) return;

            _uPerMm = Mathf.Max(nrrdVolume.unityUnitsPerMm, 1e-6f);
            float h = normalBaselineMm * _uPerMm;

            // Press direction = tool +Y, oriented so it points INTO the body (toward more solid).
            Vector3 dir = tool.up;
            if (dir.sqrMagnitude < 1e-9f) { _contact = null; return; }
            dir.Normalize();
            if (Solid(cursor + dir * h) < Solid(cursor - dir * h)) dir = -dir;

            // March from a bit behind the tip, forward into the body.
            Vector3 start = cursor - dir * (backMarginMm * _uPerMm);
            int   n        = Mathf.CeilToInt((backMarginMm + maxRangeMm) / marchStepMm);
            bool  foundSkin = false;
            Vector3 skinP   = Vector3.zero;
            float skinT = 0f, boneT = -1f;

            for (int i = 0; i <= n; i++)
            {
                float   t  = i * marchStepMm;
                Vector3 wp = start + dir * (t * _uPerMm);
                VoxelTissueType tt = nrrdVolume.SampleTissue(wp);

                if (!foundSkin && tt != VoxelTissueType.Background)
                { foundSkin = true; skinP = wp; skinT = t; }

                if (foundSkin && tt == VoxelTissueType.Bone) { boneT = t; break; }
            }

            if (!foundSkin) { _contact = new Contact { valid = false }; _emaInit = false; return; }

            Vector3 normal      = SurfaceNormal(skinP, h, dir);
            float   boneDistMm  = boneT >= 0f ? (boneT - skinT) : float.PositiveInfinity;
            float   closeness   = float.IsInfinity(boneDistMm)
                                  ? 0f : Mathf.Clamp01(1f - boneDistMm / boneInfluenceMm);

            // Temporal smoothing (EMA): the voxel surface is blocky, so even the averaged normal
            // steps as the contact moves voxel-to-voxel. Blending across frames removes that buzz.
            float s = Mathf.Clamp01(temporalSmoothing);
            if (!_emaInit)
            {
                _emaPoint = skinP; _emaNormal = normal; _emaCloseness = closeness; _emaInit = true;
            }
            else
            {
                _emaPoint     = Vector3.Lerp(skinP, _emaPoint, s);
                _emaNormal    = Vector3.Slerp(normal, _emaNormal, s).normalized;
                _emaCloseness = Mathf.Lerp(closeness, _emaCloseness, s);
            }

            _contact = new Contact
            {
                valid         = true,
                point         = _emaPoint,
                normal        = _emaNormal,
                boneCloseness = _emaCloseness,
            };
        }

        // ── Haptic thread ─────────────────────────────────────────────────────
        void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            var dev = args.DeviceController;
            Vector3 pos = dev.CursorPosition;
            Vector3 vel = dev.CursorVelocity;
            if (float.IsNaN(pos.x)) return;
            dev.SetCursorForce(ComputeForce(pos, vel));
        }

        // Thread-safe (reads the volatile immutable Contact). Call from another haptic loop to sum.
        public Vector3 ComputeForce(Vector3 cursor, Vector3 cursorVel)
        {
            Contact c = _contact;
            if (c == null || !c.valid) return Vector3.zero;

            // Penetration below the local skin plane (positive = pressed in).
            float p = Vector3.Dot(c.point - cursor, c.normal);
            if (p <= 0f) return Vector3.zero;

            float firmDepth = Mathf.Max(skinGiveMm * 0.001f, 1e-4f);
            float kEff       = baseStiffness * (1f + boneFirmnessGain * c.boneCloseness);
            float xn         = p / firmDepth;

            // Progressive spring: F = kEff·firmDepth·(p/firmDepth)^exponent  − damping·vNormal.
            float mag = kEff * firmDepth * Mathf.Pow(xn, firmnessExponent)
                        - Vector3.Dot(cursorVel, c.normal) * damping;
            if (mag <= 0f) return Vector3.zero;

            Vector3 f = c.normal * mag;
            float m = f.magnitude;
            if (m > maxOutputForce) f *= maxOutputForce / m;
            return f;
        }

        // ── NRRD helpers (Unity thread) ───────────────────────────────────────
        float Solid(Vector3 worldPos) =>
            nrrdVolume.SampleTissue(worldPos) != VoxelTissueType.Background ? 1f : 0f;

        // Smoothed occupancy: average of the binary solid field over a 3×3×3 voxel box. Turns the
        // 0/1 label field into a continuous 0..1 field so its gradient is not quantized to voxel faces.
        float Occupancy(Vector3 p)
        {
            float vw  = Mathf.Max(nrrdVolume.voxelSizeMm * _uPerMm, 1e-5f);
            float sum = 0f;
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += Solid(p + new Vector3(x, y, z) * vw);
            return sum * (1f / 27f);
        }

        // Outward skin normal from the SMOOTHED occupancy gradient (points toward background).
        Vector3 SurfaceNormal(Vector3 p, float h, Vector3 intoBody)
        {
            float gx = Occupancy(p + Vector3.right   * h) - Occupancy(p - Vector3.right   * h);
            float gy = Occupancy(p + Vector3.up      * h) - Occupancy(p - Vector3.up      * h);
            float gz = Occupancy(p + Vector3.forward * h) - Occupancy(p - Vector3.forward * h);
            Vector3 g = new Vector3(gx, gy, gz);
            if (g.sqrMagnitude < 1e-9f) return -intoBody.normalized; // flat region → opposite press dir
            return (-g).normalized; // gradient points into solid → outward = -gradient
        }

        // ── Gizmo ─────────────────────────────────────────────────────────────
        void OnDrawGizmos()
        {
            if (!drawGizmo) return;
            Contact c = _contact;
            if (c == null || !c.valid) return;

            Gizmos.color = Color.Lerp(new Color(0.2f, 0.8f, 1f), new Color(1f, 0.3f, 0.2f), c.boneCloseness);
            Gizmos.DrawWireSphere(c.point, 0.004f);
            Gizmos.DrawLine(c.point, c.point + c.normal * 0.02f);
        }
    }
}
