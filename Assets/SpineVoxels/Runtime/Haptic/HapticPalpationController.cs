// Haptic palpation force for bone surface targets (spinous processes, vertebral bodies).
//
// HAPTIC THREAD (~1–8 kHz): ComputePalpationForce() — reads cached snapshots, sum forces.
// UNITY THREAD (Update):    Rebuilds snapshot from current target transforms.
//
// Usage:
//   standaloneMode = false → HybridNeedleController calls ComputePalpationForce() and sums.
//   standaloneMode = true  → subscribes to DeviceStateChanged directly (standalone palpation).
//
// REQUIRES: Haply Inverse for Unity package.

using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Haptic Palpation Controller")]
    public class HapticPalpationController : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("If null, searched on this GameObject.")]
        public Inverse3Controller inverse3;

        [Tooltip("Palpation targets. If empty, auto-detected in Awake.")]
        public List<HapticPalpationTarget> targets = new();

        [Header("Safety")]
        [Range(0f, 10f)] public float maxOutputForce = 3.5f;

        [Header("Operation mode")]
        [Tooltip("true = sends force to device directly.\n" +
                 "false = ComputePalpationForce() is called by HybridNeedleController.")]
        public bool standaloneMode = true;

        [Header("Cursor tip")]
        public Transform tipTransform;
        [Range(0f, 0.05f)] public float cursorRadius = 0.025f;

        void OnDrawGizmos()
        {
            if (tipTransform == null || cursorRadius <= 0f) return;
            Gizmos.color = new Color(0.7f, 0.2f, 1f, 0.6f);
            Gizmos.DrawWireSphere(tipTransform.position, cursorRadius);
        }

        // ── Thread-safe snapshot ─────────────────────────────────────────────
        struct TargetSnapshot
        {
            public PalpationShape shape;
            public Vector3        position, right, up, forward;
            public float          radius, capsuleHeight, stiffness, damping;
            public Vector3        boxHalfSize;
            public float          exponent, firmDepthM;
        }

        TargetSnapshot[]              _snapshots = new TargetSnapshot[0];
        readonly ReaderWriterLockSlim _rwLock    = new();

        void Awake()
        {
            if (inverse3 == null)
                inverse3 = GetComponent<Inverse3Controller>();
            if (targets.Count == 0)
                targets = new List<HapticPalpationTarget>(
                    FindObjectsByType<HapticPalpationTarget>(FindObjectsSortMode.None));
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

        void Update()
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (_snapshots.Length != targets.Count)
                    _snapshots = new TargetSnapshot[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    if (t == null) continue;
                    _snapshots[i] = new TargetSnapshot
                    {
                        shape         = t.shape,
                        position      = t.transform.position,
                        right         = t.transform.right,
                        up            = t.transform.up,
                        forward       = t.transform.forward,
                        radius        = t.radius,
                        capsuleHeight = t.capsuleHeight,
                        boxHalfSize   = t.boxSize * 0.5f,
                        stiffness     = t.stiffness,
                        damping       = t.damping,
                        exponent      = t.firmnessExponent,
                        firmDepthM    = Mathf.Max(t.firmnessDepthMm * 0.001f, 1e-4f),
                    };
                }
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            var     dev = args.DeviceController;
            Vector3 pos = dev.CursorPosition;
            Vector3 vel = dev.CursorVelocity;
            if (float.IsNaN(pos.x)) return;
            dev.SetCursorForce(ComputePalpationForce(pos, vel));
        }

        // Thread-safe public API for HybridNeedleController.
        public Vector3 ComputePalpationForce(Vector3 cursorPos, Vector3 cursorVel)
        {
            Vector3 total = Vector3.zero;
            _rwLock.EnterReadLock();
            try
            {
                for (int i = 0; i < _snapshots.Length; i++)
                    total += ComputeForce(ref _snapshots[i], cursorPos, cursorVel, cursorRadius);
            }
            finally { _rwLock.ExitReadLock(); }

            float mag = total.magnitude;
            if (mag > maxOutputForce) total *= maxOutputForce / mag;
            return total;
        }

        // ── Per-shape force (thread-safe: reads only ref-passed struct) ───────
        static Vector3 ComputeForce(ref TargetSnapshot t, Vector3 pos, Vector3 vel, float r)
        {
            switch (t.shape)
            {
                case PalpationShape.Sphere:  return SphereForce (ref t, pos, vel, r);
                case PalpationShape.Plane:   return PlaneForce  (ref t, pos, vel, r);
                case PalpationShape.Capsule: return CapsuleForce(ref t, pos, vel, r);
                case PalpationShape.Box:     return BoxForce    (ref t, pos, vel, r);
                default: return Vector3.zero;
            }
        }

        // Progressive contact magnitude (penetration must be > 0):
        //   F = stiffness · firmDepth · (penetration / firmDepth)^exponent  −  damping · vNormal
        // exponent = 1 → classic linear spring (penetration · stiffness).
        // exponent > 1 → the deeper you push, the harder it resists, so the device reaches its
        //                force ceiling within a short depth → feels like a firm wall, not a
        //                bottomless soft surface (the pedagogical "skin you palpate against").
        static float SpringMag(float penetration, float vNormal, ref TargetSnapshot t)
        {
            float xn   = penetration / t.firmDepthM;                    // normalized penetration
            float kPen = t.stiffness * t.firmDepthM * Mathf.Pow(Mathf.Max(xn, 0f), t.exponent);
            return kPen - vNormal * t.damping;
        }

        static Vector3 SphereForce(ref TargetSnapshot t, Vector3 pos, Vector3 vel, float r)
        {
            Vector3 d   = pos - t.position;
            float   dist = d.magnitude;
            float   pen  = t.radius + r - dist;
            if (pen <= 0f) return Vector3.zero;
            Vector3 n = dist > 1e-6f ? d / dist : Vector3.up;
            return n * SpringMag(pen, Vector3.Dot(vel, n), ref t);
        }

        static Vector3 PlaneForce(ref TargetSnapshot t, Vector3 pos, Vector3 vel, float r)
        {
            float pen = r - Vector3.Dot(pos - t.position, t.up);
            if (pen <= 0f) return Vector3.zero;
            return t.up * SpringMag(pen, Vector3.Dot(vel, t.up), ref t);
        }

        static Vector3 CapsuleForce(ref TargetSnapshot t, Vector3 pos, Vector3 vel, float r)
        {
            Vector3 local   = pos - t.position;
            float   axisT   = Vector3.Dot(local, t.up);
            float   clamped = Mathf.Clamp(axisT, -t.capsuleHeight * 0.5f, t.capsuleHeight * 0.5f);
            Vector3 closest = t.position + t.up * clamped;
            Vector3 d       = pos - closest;
            float   dist    = d.magnitude;
            float   pen     = t.radius + r - dist;
            if (pen <= 0f) return Vector3.zero;
            Vector3 n = dist > 1e-6f ? d / dist : t.up;
            return n * SpringMag(pen, Vector3.Dot(vel, n), ref t);
        }

        static Vector3 BoxForce(ref TargetSnapshot t, Vector3 pos, Vector3 vel, float r)
        {
            Vector3 d  = pos - t.position;
            float   lx = Vector3.Dot(d, t.right);
            float   ly = Vector3.Dot(d, t.up);
            float   lz = Vector3.Dot(d, t.forward);
            Vector3 h  = t.boxHalfSize;
            bool    inside = Mathf.Abs(lx) <= h.x && Mathf.Abs(ly) <= h.y && Mathf.Abs(lz) <= h.z;

            Vector3 normalLocal;
            float   penetration;

            if (!inside)
            {
                float cx = Mathf.Clamp(lx, -h.x, h.x);
                float cy = Mathf.Clamp(ly, -h.y, h.y);
                float cz = Mathf.Clamp(lz, -h.z, h.z);
                Vector3 dl  = new Vector3(lx - cx, ly - cy, lz - cz);
                float   dist = dl.magnitude;
                penetration  = r - dist;
                if (penetration <= 0f) return Vector3.zero;
                normalLocal = dist > 1e-6f ? dl / dist : Vector3.up;
            }
            else
            {
                float dxF = h.x - Mathf.Abs(lx);
                float dyF = h.y - Mathf.Abs(ly);
                float dzF = h.z - Mathf.Abs(lz);
                if (dxF <= dyF && dxF <= dzF) { normalLocal = new Vector3(Mathf.Sign(lx), 0f, 0f); penetration = dxF + r; }
                else if (dyF <= dzF)           { normalLocal = new Vector3(0f, Mathf.Sign(ly), 0f); penetration = dyF + r; }
                else                           { normalLocal = new Vector3(0f, 0f, Mathf.Sign(lz)); penetration = dzF + r; }
            }

            Vector3 normal = t.right * normalLocal.x + t.up * normalLocal.y + t.forward * normalLocal.z;
            return normal * SpringMag(penetration, Vector3.Dot(vel, normal), ref t);
        }
    }
}
