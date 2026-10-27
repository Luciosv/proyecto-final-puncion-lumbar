// Colors the needle depending on whether its axis falls inside an elliptical cone around the
// ideal insertion axis (local X of gizmoOrigin, or world +Z as fallback).
//
// Ported from LP.V2.NeedleAngleIndicator (PuncionSinHaptico), adapted to the Voxels model:
//   • Entry detection uses PunctureAccessStack / NrrdTissueVolume instead of TissueStackV2.
//   • The fallback cone axis is the model insertion axis (Unity +Z).
//
// toleranceA (°): angular freedom about gizmoOrigin local Y. toleranceB (°): about local Z.
// A == B → circular cone; otherwise elliptical.

using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Needle Angle Indicator")]
    public class NeedleAngleIndicator : MonoBehaviour
    {
        public enum Axis { Up, Forward, Right }

        [Header("References")]
        [Tooltip("Transform whose axis defines the needle direction.")]
        public Transform needleBody;

        [Tooltip("Local axis of needleBody that points along the needle.")]
        public Axis needleAxis = Axis.Up;

        [Tooltip("Needle tip — used to detect when it has entered the tissue.")]
        public Transform needleTip;

        [Tooltip("Optional: stop coloring once the tip is inside a PunctureAccess zone.")]
        public PunctureAccessStack punctureAccess;

        [Tooltip("Optional: also treat any non-background NRRD voxel as 'entered'.")]
        public NrrdTissueVolume nrrdVolume;

        [Tooltip("Needle renderers to tint. If empty, the children of needleBody are used.")]
        public Renderer[] needleRenderers;

        [Header("Elliptical tolerance")]
        [Tooltip("Tolerance (°) on axis A (gizmoOrigin local Y) — cone 'width'.")]
        public float toleranceA = 15f;

        [Tooltip("Tolerance (°) on axis B (gizmoOrigin local Z) — cone 'height'.")]
        public float toleranceB = 15f;

        [Tooltip("Color when the needle is INSIDE the cone (correct angle).")]
        public Color insideColor = Color.red;

        [Tooltip("Color when the needle is OUTSIDE the cone (wrong angle).")]
        public Color outsideColor = Color.green;

        [Header("Behaviour")]
        [Tooltip("On tissue entry, stop coloring and restore the original color.")]
        public bool stopOnEntry = true;

        [Header("Gizmo")]
        public bool drawGizmo = true;

        [Tooltip("Reference transform:\n" +
                 "  local X = ideal entry axis (cone grows along it),\n" +
                 "  local Y = axis A (toleranceA),\n" +
                 "  local Z = axis B (toleranceB).\n" +
                 "If null, world +Z is used as the ideal axis.")]
        public Transform gizmoOrigin;

        [Tooltip("Gizmo cone length (world units).")]
        public float gizmoLength = 0.15f;

        // ── Internals ─────────────────────────────────────────────────────────
        MaterialPropertyBlock _mpb;
        bool _entered;

        static readonly int s_ColorId     = Shader.PropertyToID("_Color");
        static readonly int s_BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if ((needleRenderers == null || needleRenderers.Length == 0) && needleBody != null)
                needleRenderers = needleBody.GetComponentsInChildren<Renderer>();
        }

        // Restore the needle's original color when the guide is turned off, so disabling this
        // component (e.g. from the session control panel) actually removes the tint instead of
        // leaving the last green/red frozen on the mesh.
        void OnDisable() => ClearColor();

        void Update()
        {
            if (needleBody == null) return;

            var (coneAxis, axisA, axisB) = GetConeAxes();

            if (stopOnEntry && needleTip != null && IsEntered(needleTip.position))
            {
                if (!_entered) { _entered = true; ClearColor(); }
                return;
            }
            _entered = false;

            bool inside = IsInsideEllipticalCone(NeedleDir(), coneAxis, axisA, axisB);
            ApplyColor(inside ? insideColor : outsideColor);
        }

        // ── Entry detection (replaces TissueStackV2 depth check) ───────────────
        bool IsEntered(Vector3 tip)
        {
            if (punctureAccess != null && punctureAccess.FindActiveSnapshot(tip) != null)
                return true;
            if (nrrdVolume != null && nrrdVolume.IsReady)
                return nrrdVolume.SampleTissue(tip) != VoxelTissueType.Background;
            return false;
        }

        // ── Angle helpers ──────────────────────────────────────────────────────
        (Vector3, Vector3, Vector3) GetConeAxes()
        {
            if (gizmoOrigin != null)
                return (gizmoOrigin.right, gizmoOrigin.up, gizmoOrigin.forward);

            // Fallback: the model insertion axis (Unity +Z). The cone is bidirectional.
            Vector3 n = Vector3.forward;
            Vector3 perp = Vector3.Cross(n, Vector3.up);
            if (perp.sqrMagnitude < 0.01f) perp = Vector3.Cross(n, Vector3.right);
            perp.Normalize();
            return (n, perp, Vector3.Cross(n, perp).normalized);
        }

        bool IsInsideEllipticalCone(Vector3 dir, Vector3 coneAxis, Vector3 axisA, Vector3 axisB)
        {
            dir.Normalize();
            float dN  = Mathf.Abs(Vector3.Dot(dir, coneAxis));
            float dA  = Vector3.Dot(dir, axisA);
            float dB  = Vector3.Dot(dir, axisB);
            float tanA = Mathf.Tan(Mathf.Max(toleranceA, 0.1f) * Mathf.Deg2Rad);
            float tanB = Mathf.Tan(Mathf.Max(toleranceB, 0.1f) * Mathf.Deg2Rad);
            return (dA * dA) / (tanA * tanA) + (dB * dB) / (tanB * tanB) <= dN * dN;
        }

        Vector3 NeedleDir() => needleAxis switch
        {
            Axis.Forward => needleBody.forward,
            Axis.Right   => needleBody.right,
            _            => needleBody.up,
        };

        void ApplyColor(Color c)
        {
            if (needleRenderers == null) return;
            foreach (var r in needleRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(s_ColorId, c);
                _mpb.SetColor(s_BaseColorId, c);
                r.SetPropertyBlock(_mpb);
            }
        }

        void ClearColor()
        {
            if (needleRenderers == null) return;
            foreach (var r in needleRenderers)
                if (r != null) r.SetPropertyBlock(null);
        }

        // ── Gizmo ─────────────────────────────────────────────────────────────
        void OnDrawGizmos()
        {
            if (!drawGizmo) return;

            float length = Mathf.Max(gizmoLength, 0.001f);
            var (coneAxis, axisA, axisB) = GetConeAxes();

            Vector3 origin = gizmoOrigin != null ? gizmoOrigin.position
                           : needleTip   != null ? needleTip.position
                           : transform.position;

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(origin, length * 0.04f);

            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawLine(origin, origin + coneAxis * length * 1.1f);

            Color dimInside = new Color(insideColor.r, insideColor.g, insideColor.b, 0.45f);
            DrawEllipse(origin, coneAxis, axisA, axisB, toleranceA, toleranceB, length * 0.5f, dimInside);
            DrawEllipse(origin, coneAxis, axisA, axisB, toleranceA, toleranceB, length, insideColor);

            float rA  = length * Mathf.Tan(Mathf.Max(toleranceA, 0.1f) * Mathf.Deg2Rad);
            float rB  = length * Mathf.Tan(Mathf.Max(toleranceB, 0.1f) * Mathf.Deg2Rad);
            Vector3 tip = origin + coneAxis * length;
            Gizmos.color = new Color(insideColor.r, insideColor.g, insideColor.b, 0.55f);
            Gizmos.DrawLine(origin, tip + axisA * rA);
            Gizmos.DrawLine(origin, tip - axisA * rA);
            Gizmos.DrawLine(origin, tip + axisB * rB);
            Gizmos.DrawLine(origin, tip - axisB * rB);

            if (needleBody != null)
            {
                Vector3 dir = NeedleDir();
                bool inside = IsInsideEllipticalCone(dir, coneAxis, axisA, axisB);
                Gizmos.color = inside ? insideColor : outsideColor;
                Gizmos.DrawLine(origin - dir * length * 1.2f, origin + dir * length * 1.2f);
            }
        }

        static void DrawEllipse(Vector3 origin, Vector3 coneAxis,
                                Vector3 axisA, Vector3 axisB,
                                float halfAngleA, float halfAngleB,
                                float dist, Color color, int seg = 48)
        {
            Gizmos.color = color;
            Vector3 center = origin + coneAxis * dist;
            float rA = dist * Mathf.Tan(Mathf.Max(halfAngleA, 0.1f) * Mathf.Deg2Rad);
            float rB = dist * Mathf.Tan(Mathf.Max(halfAngleB, 0.1f) * Mathf.Deg2Rad);

            Vector3 prev = center + axisA * rA;
            for (int i = 1; i <= seg; i++)
            {
                float t = (float)i / seg * Mathf.PI * 2f;
                Vector3 p = center + axisA * (Mathf.Cos(t) * rA) + axisB * (Mathf.Sin(t) * rB);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
