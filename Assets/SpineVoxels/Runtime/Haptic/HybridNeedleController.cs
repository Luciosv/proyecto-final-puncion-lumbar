// Hybrid haptic needle controller for the Voxels lumbar-puncture simulator.
//
// Tissue detection is a two-source hybrid:
//   • PunctureAccessStack  — BoxCollider cubes for the 6 ligament/membrane layers.
//   • NrrdTissueVolume     — NRRD label sampling for Bone, Cord, Muscle, Fat, Skin.
//
// Force priority (highest to lowest):
//   1. Bone  (NRRD)  → rigid bidirectional penalty (blocks insertion, capped to avoid launch).
//   2. Muscle/Cord   → buzz + audio; no additional positional force.
//   3. Skin  (NRRD)  → exponential skin curve (constant force at JSON midpoint, adjustable).
//   4. PunctureAccess ligament cube → full JSON curve with backlash/blend depth tracking.
//   5. Fat   (NRRD)  → constant force (JSON midpoint value).
//   6. Background    → no force (palpation targets still active).
//
// HAPTIC THREAD (DeviceStateChanged ~1–8 kHz):
//   Reads NrrdTissueVolume, finds active StackSnapshotSpine, evaluates force, sends to device.
//
// UNITY THREAD (FixedUpdate ~50 Hz):
//   Caches needle direction for haptic thread; manages muscle/cord buzz + audio.
//
// REQUIRES: Haply Inverse for Unity package.

using System.IO;
using System.Threading;
using UnityEngine;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Hybrid Needle Controller")]
    public class HybridNeedleController : MonoBehaviour
    {
        // ── Scene references ──────────────────────────────────────────────────
        [Header("Scene references")]
        [Tooltip("If null, searched on this GameObject.")]
        public Inverse3Controller        inverse3;
        public PunctureAccessStack       punctureAccess;
        public NrrdTissueVolume          nrrdVolume;
        public HapticPalpationController palpationController;

        [Tooltip("Transform whose local +Y defines the needle insertion direction (d0).")]
        public Transform needleBody;

        [Header("Audio — muscle/cord wrong-hit")]
        [Tooltip("AudioSource for muscle/cord buzz (voxel-detected, played from FixedUpdate).")]
        public AudioSource voxelBuzzAudio;

        [Header("Force JSON")]
        [Tooltip("Filename inside StreamingAssets. Resolved to Application.streamingAssetsPath at runtime.")]
        public string jsonFileName = "AnatomicalNoMuscle2.json";

        [Header("Calibration WebSocket (Python)")]
        [Tooltip("Optional. If assigned, receives live calibration JSON from the Python CalibrationApp " +
                 "and updates the force curves on the fly. Requires the NativeWebSocket package.")]
        public CalibrationWebSocketClient calibrationClient;
        [Tooltip("Optional. If assigned, the force profiler graph is refreshed immediately after each " +
                 "calibration update so the reference curve reflects the new parameters without " +
                 "needing to withdraw the needle first.")]
        public HapticForceProfiler hapticProfiler;

        // ── Safety ────────────────────────────────────────────────────────────
        [Header("Safety")]
        public Vector3 maxOutputForce  = new Vector3(3.5f, 3.5f, 3.5f);
        public bool    enableForceClamp = true;

        [Range(0f, 5f)]
        [Tooltip("Global multiplier on the ligament/skin/fat axial force.")]
        public float forceScale = 1f;

        // ── Bone penalty ──────────────────────────────────────────────────────
        [Header("Bone penalty")]
        [Tooltip("If ON, bone drops to LOWEST priority: when the cursor is inside a PunctureAccess " +
                 "ligament you feel the ligament, and bone is felt only where no other structure is " +
                 "above it. If OFF (default), bone has top priority (felt even inside a ligament cube).")]
        public bool boneLowestPriority = false;
        [Tooltip("Constant bone wall force (N) applied along the insertion axis when bone is the " +
                 "active tissue. Cap keeps it from expelling the cursor.")]
        [Min(0f)] public float maxBoneForce = 3.0f;

        // ── Withdrawal (position-based backlash, same as HapticNeedleControllerV2) ──
        [Header("Withdrawal (ligament layers)")]
        public float withdrawalScale    = 0.3f;
        [Min(0f)] public float withdrawalBandMm  = 0.5f;
        [Min(0.01f)] public float withdrawalBlendMm = 2f;
        public bool  constantWithdrawal  = false;
        [Range(0f, 9f)] public float withdrawalForce = 1f;

        // ── Lateral rail constraint ───────────────────────────────────────────
        [Header("Lateral constraint")]
        public bool  enableLateralConstraint = true;
        [Min(0f)] public float lateralStiffness   = 300f;
        [Min(0f)] public float lateralDamping     = 5f;
        [Min(0f)] public float lateralDeadbandMm  = 0.5f;

        // ── Skin / Fat constant forces (precomputed from JSON in Start) ───────
        [Header("Skin/Fat forces (override if JSON absent)")]
        [Tooltip("Skin axial resistance (N). Auto-set from JSON curve midpoint at Start if JSON loaded.")]
        public float skinForceN = 1.0f;
        [Tooltip("Fat axial resistance (N). Auto-set from JSON curve midpoint at Start if JSON loaded.")]
        public float fatForceN  = 0.5f;

        // ── Buzz ──────────────────────────────────────────────────────────────
        [Header("Buzz (muscle/cord/wrong-hit)")]
        [Range(1f, 500f)] public float buzzFrequency = 30f;
        [Range(0f, 9f)]   public float buzzFMin      = 0f;
        [Range(0f, 9f)]   public float buzzFMax      = 3f;

        // ── Shared state: haptic thread → Unity thread ────────────────────────
        struct HapticState
        {
            public float depth, fTotal, fOutput, velocity; // velocity = axial mm/s
            public int   layerIdx;
        }

        HapticState                   _shared;
        readonly ReaderWriterLockSlim _rwLock = new();
        bool                          _hasEvent;

        // ── Haptic-thread state ───────────────────────────────────────────────
        float _dirRef;
        float _prevDepth;
        float _blend;
        bool  _withdrawing;
        bool  _dirInit;

        Vector3 _railOrigin;
        Vector3 _railDir;

        // Last active snapshot (stable during an insertion; null when outside all PA zones).
        StackSnapshotSpine _activeSnap;

        // Surface-tissue latch: the last NRRD surface tissue (Skin/Fat) the cursor passed
        // through. Used to hold the fat force across empty gaps between tissues. Set to Fat
        // while in fat, to Skin while in skin (so exit air past the skin reads zero), and is
        // left untouched by muscle/cord/bone/ligament so it survives for the return trip.
        VoxelTissueType _surfaceLatch = VoxelTissueType.Background;

        // Skin curve (e.g. kv_rupture): stretched/compressed to the ACTUAL skin thickness the
        // needle traverses, so the rupture always lands at the skin/fat boundary regardless of
        // local thickness or entry angle. Thickness is ray-marched on the NRRD at first contact.
        LayerCurveSpine _skinCurve;
        float           _skinJsonWidthMm = 2f;
        bool            _hasSkinCurve;
        Vector3         _skinEntryPos;
        float           _skinThicknessMm;
        bool            _skinActive;
        float           _uPerMm = 0.003f;   // Unity units per physical mm (from NRRD config)

        // Fat curve: stored from JSON and evaluated depth-dependently (like skin).
        // fatForceN is kept as the midpoint fallback for gap-fill and the graph constant display.
        LayerCurveSpine _fatCurve;
        float           _fatJsonWidthMm = 14f;
        bool            _hasFatCurve;
        Vector3         _fatEntryPos;
        bool            _fatActive;

        // Body envelope: armed at the first skin/fat contact, cleared once the cursor withdraws
        // back behind that surface. Gates the fat gap-fill so entry/exit air stays force-free.
        Vector3 _bodyEntryPos;
        bool    _inBody;

        // ── Unity-thread caches (written in FixedUpdate, read by haptic thread) ─
        // needleBody.up (d0) is a Transform field → not thread-safe from haptic callback.
        readonly object _needleDirLock = new();
        Vector3 _needleDirUnity;
        bool    _needleDirValid;

        // ── Buzz state (volatile: written/read across threads) ────────────────
        volatile bool  _buzzActive;
        volatile float _buzzFMin, _buzzFMax, _buzzFreqHz;
        long           _buzzStartTick;

        // ── Voxel tissue (haptic thread writes, FixedUpdate reads) ───────────
        volatile int _lastVoxelTissue; // VoxelTissueType cast to int
        bool         _wasBuzzing;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (inverse3 == null)
                inverse3 = GetComponent<Inverse3Controller>();
        }

        void Start()
        {
            string resolvedJson = System.IO.Path.Combine(Application.streamingAssetsPath, jsonFileName);
            // Load JSON force curves.
            if (ForceJsonLoaderSpine.TryLoad(resolvedJson, out var fits))
            {
                // Skin: keep the full curve (it is depth-dependent, e.g. kv_rupture). The constant
                // skinForceN stays as a fallback (JSON-curve midpoint) if the curve is unavailable.
                if (fits.TryGetValue(LayerTypeSpine.Skin, out var skinFit))
                {
                    _skinCurve       = skinFit.curve;
                    _skinJsonWidthMm = Mathf.Max(skinFit.jsonWidthMm, 1e-3f);
                    _hasSkinCurve    = true;
                    skinForceN       = Mathf.Max(0f, skinFit.curve.Evaluate(skinFit.jsonWidthMm * 0.5f));
                }
                // Fat: store the full curve for depth-dependent evaluation; also precompute midpoint
                // for gap-fill and graph fallback.
                if (fits.TryGetValue(LayerTypeSpine.Fat, out var fatFit))
                {
                    _fatCurve       = fatFit.curve;
                    _fatJsonWidthMm = Mathf.Max(fatFit.jsonWidthMm, 1e-3f);
                    _hasFatCurve    = true;
                    fatForceN       = Mathf.Max(0f, fatFit.curve.Evaluate(fatFit.jsonWidthMm * 0.5f));
                }

                _uPerMm = nrrdVolume != null ? Mathf.Max(nrrdVolume.unityUnitsPerMm, 1e-6f) : 0.003f;

                // Initialize the PunctureAccess snapshots.
                if (punctureAccess != null)
                    punctureAccess.Initialize(fits);

                Debug.Log($"[HybridNeedleController] JSON loaded. skinF={skinForceN:F2}N fatF={fatForceN:F2}N");
            }
            else
            {
                Debug.LogWarning("[HybridNeedleController] JSON not loaded — skin/fat use inspector values.");
                punctureAccess?.Initialize(new System.Collections.Generic.Dictionary<LayerTypeSpine, ForceJsonLoaderSpine.LayerFitSpine>());
            }

            // Connect to the Python calibration app for live parameter updates.
            if (calibrationClient != null)
            {
                calibrationClient.OnCalibrationJsonReceived += HandleCalibrationJson;
                calibrationClient.Connect();
            }
        }

        void OnEnable()
        {
            if (inverse3 != null)
                inverse3.DeviceStateChanged += OnDeviceStateChanged;
        }

        void OnDisable()
        {
            if (calibrationClient != null)
                calibrationClient.OnCalibrationJsonReceived -= HandleCalibrationJson;

            if (inverse3 == null) return;
            inverse3.DeviceStateChanged -= OnDeviceStateChanged;
            inverse3.Release();
        }

        // ── Calibration WebSocket (Python) ───────────────────────────────────
        // Called on the Unity thread when new calibration JSON arrives. Atomically replaces the
        // force parameters (skin curve, fat constant, PA snapshots) without stopping the haptic loop.
        void HandleCalibrationJson(string json)
        {
            Debug.Log($"[CalibrationWS] JSON received ({json.Length} chars):\n" +
                      json.Substring(0, Mathf.Min(json.Length, 600)));

            if (!ForceJsonLoaderSpine.TryParse(json, out var fits))
            {
                Debug.LogWarning("[CalibrationWS] Parse failed — no layers recognized. JSON may have wrong key names or format.");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"[CalibrationWS] Parsed {fits.Count} layer(s): ");
            foreach (var kvp in fits)
                sb.Append($"{kvp.Key}(family={kvp.Value.curve.family} w={kvp.Value.jsonWidthMm:F2}mm) ");
            Debug.Log(sb.ToString());

            if (fits.TryGetValue(LayerTypeSpine.Skin, out var skinFit))
            {
                _skinCurve       = skinFit.curve;
                _skinJsonWidthMm = Mathf.Max(skinFit.jsonWidthMm, 1e-3f);
                _hasSkinCurve    = true;
                skinForceN       = Mathf.Max(0f, skinFit.curve.Evaluate(skinFit.jsonWidthMm * 0.5f));
            }
            if (fits.TryGetValue(LayerTypeSpine.Fat, out var fatFit))
            {
                _fatCurve       = fatFit.curve;
                _fatJsonWidthMm = Mathf.Max(fatFit.jsonWidthMm, 1e-3f);
                _hasFatCurve    = true;
                fatForceN       = Mathf.Max(0f, fatFit.curve.Evaluate(fatFit.jsonWidthMm * 0.5f));
            }

            if (punctureAccess != null)
                punctureAccess.Initialize(fits); // rebuild ligament snapshots with the new curves

            ResetTracking(); // next haptic callback starts fresh with the new parameters

            hapticProfiler?.InvalidateProjection(); // redraw the force graph with the new curves

            Debug.Log($"[CalibrationWS] Applied — skinF={skinForceN:F2}N skinW={_skinJsonWidthMm:F2}mm fatF={fatForceN:F2}N");
        }

        // ── Public API ────────────────────────────────────────────────────────

        // Called from NeedleColliderAudio or any component that wants to trigger buzz.
        public void StartBuzz(float freqHz, float fMin, float fMax)
        {
            _buzzFMin   = Mathf.Clamp(fMin, 0f, 9f);
            _buzzFMax   = Mathf.Clamp(fMax, 0f, 9f);
            _buzzFreqHz = Mathf.Max(freqHz, 0.1f);
            if (!_buzzActive)
                System.Threading.Interlocked.Exchange(ref _buzzStartTick,
                    System.Diagnostics.Stopwatch.GetTimestamp());
            _buzzActive = true;
        }

        public void StopBuzz() => _buzzActive = false;

        public void ResetTracking()
        {
            _hasEvent     = false;
            _dirInit      = false;
            _withdrawing  = false;
            _blend        = 0f;
            _activeSnap   = null;
            _surfaceLatch = VoxelTissueType.Background;
            _inBody       = false;
            _skinActive   = false;
            _fatActive    = false;
        }

        // Full telemetry for PunctureSessionLogger: PA depth (m), axial force (N), axial velocity
        // (mm/s) and the current NRRD tissue at the cursor. Thread-safe.
        public bool TryGetTelemetry(out float paDepthM, out float forceN, out float velocityMmps,
                                    out VoxelTissueType tissue)
        {
            tissue = (VoxelTissueType)_lastVoxelTissue; // volatile, safe to read directly
            _rwLock.EnterReadLock();
            try
            {
                if (!_hasEvent) { paDepthM = forceN = velocityMmps = 0f; return false; }
                paDepthM     = _shared.depth;
                forceN       = _shared.fOutput;
                velocityMmps = _shared.velocity;
                return true;
            }
            finally { _rwLock.ExitReadLock(); }
        }

        // Skin force curve (for HapticForceProfiler to mirror the stretched skin morphology).
        public bool TryGetSkinCurve(out LayerCurveSpine curve, out float jsonWidthMm)
        {
            curve = _skinCurve; jsonWidthMm = _skinJsonWidthMm;
            return _hasSkinCurve;
        }

        // Fat force curve (for HapticForceProfiler to show the depth-dependent fat profile).
        public bool TryGetFatCurve(out LayerCurveSpine curve, out float jsonWidthMm)
        {
            curve = _fatCurve; jsonWidthMm = _fatJsonWidthMm;
            return _hasFatCurve;
        }

        // Thread-safe read for HapticForceProfiler (FixedUpdate).
        // paDepthM: depth inside the active PA zone (m); 0 when outside.
        // forceOutputN: axial force projected onto the insertion axis, sent to device.
        public bool TryGetHapticOutput(out float paDepthM, out float forceOutputN)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (!_hasEvent) { paDepthM = forceOutputN = 0f; return false; }
                paDepthM     = _shared.depth;
                forceOutputN = _shared.fOutput;
                return true;
            }
            finally { _rwLock.ExitReadLock(); }
        }

        // ── Haptic thread ─────────────────────────────────────────────────────
        void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            Vector3 cursor    = inverse3.CursorPosition;
            Vector3 cursorVel = inverse3.CursorVelocity;
            if (float.IsNaN(cursor.x)) return;

            // ── 1. Voxel tissue at cursor position ───────────────────────────
            VoxelTissueType tissue = nrrdVolume != null && nrrdVolume.IsReady
                ? nrrdVolume.SampleTissue(cursor)
                : VoxelTissueType.Background;
            _lastVoxelTissue = (int)tissue;
            if (tissue != VoxelTissueType.Skin) _skinActive = false; // re-measure on next contact
            if (tissue != VoxelTissueType.Fat)  _fatActive  = false; // re-lock entry on next contact

            // ── 2. Active PunctureAccess snapshot ────────────────────────────
            StackSnapshotSpine snap = punctureAccess?.FindActiveSnapshot(cursor);

            // ── 3. Force computation ─────────────────────────────────────────
            // ALL tissues deliver their force along the insertion axis (never world -Z).
            // The MAGNITUDE for ligaments is still computed from the depth projected on the
            // cube normal (snap.axis), inside LigamentForce, so the curve morphology is intact.
            Vector3 axis         = LiveInsertionAxis();
            Vector3 forceAxial   = Vector3.zero;
            Vector3 forceLateral = Vector3.zero;
            float   paDepthM     = 0f; // PA-relative depth (m); published for profiler

            // Are we inside a PunctureAccess ligament layer (past the entry face)?
            bool  inLigament = false;
            float worldDepth = 0f;
            if (snap != null)
            {
                worldDepth = Vector3.Dot(cursor - snap.origin, snap.axis);
                inLigament = worldDepth >= 0f;
            }
            bool boneHere = tissue == VoxelTissueType.Bone;

            // Bone priority: by default bone wins over everything; with boneLowestPriority it is
            // felt ONLY when there is no ligament above it (ligament takes precedence).
            if (boneHere && (!boneLowestPriority || !inLigament))
            {
                // Constant capped bone wall along the insertion axis (a constant, not velocity-
                // scaled, avoids accumulation in the haptic thread and the launcher effect).
                _dirInit      = false;
                _surfaceLatch = VoxelTissueType.Background; // deep structure → stop fat-hold
                forceAxial    = -axis * maxBoneForce;
            }
            else if (inLigament)
            {
                // PunctureAccess ligament — wins over bone when boneLowestPriority is on.
                paDepthM = Mathf.Clamp(worldDepth / snap.visualScale, 0f, snap.totalDepth);
                LigamentForce(cursor, cursorVel, paDepthM, snap, out forceAxial, out forceLateral);
                _surfaceLatch = VoxelTissueType.Background; // in the PA stack → stop fat-hold
            }
            else
            {
                _dirInit = false; // outside ligament & not bone: reset depth tracking

                switch (tissue)
                {
                    case VoxelTissueType.Skin:
                        // Skin curve stretched to the traversed skin thickness (rupture at the
                        // skin/fat boundary). Delivered along the insertion axis. Skin ALSO arms
                        // the fat-hold so the skin↔fat gap is filled with fat.
                        if (!_inBody) { _bodyEntryPos = cursor; _inBody = true; }
                        _surfaceLatch = VoxelTissueType.Fat;
                        forceAxial = -axis * (SkinForce(cursor, axis) * forceScale);
                        break;
                    case VoxelTissueType.Fat:
                        if (!_inBody) { _bodyEntryPos = cursor; _inBody = true; }
                        _surfaceLatch = VoxelTissueType.Fat;
                        if (_hasFatCurve)
                        {
                            if (!_fatActive) { _fatEntryPos = cursor; _fatActive = true; }
                            float fatDepthMm = Vector3.Dot(cursor - _fatEntryPos, axis) / _uPerMm;
                            float fFat = Mathf.Max(0f, _fatCurve.Evaluate(
                                Mathf.Clamp(fatDepthMm, 0f, _fatJsonWidthMm)));
                            forceAxial = -axis * (fFat * forceScale);
                        }
                        else
                            forceAxial = -axis * (fatForceN * forceScale);
                        break;
                    case VoxelTissueType.Background:
                        // Empty gap: hold the fat force across a SUPERFICIAL void (skin↔fat or
                        // fat↔first deep structure) while still inside the body envelope. Deep
                        // gaps clear the latch; entry/exit air clears _inBody → both read zero.
                        if (_inBody)
                        {
                            float bodyDepthMm = Vector3.Dot(cursor - _bodyEntryPos, axis) / _uPerMm;
                            if (bodyDepthMm < -2f) { _inBody = false; _surfaceLatch = VoxelTissueType.Background; }
                        }
                        if (_inBody && _surfaceLatch == VoxelTissueType.Fat)
                            forceAxial = -axis * (fatForceN * forceScale);
                        break;
                    case VoxelTissueType.Muscle:
                    case VoxelTissueType.Cord:
                        // Deep structures: buzz handled in FixedUpdate; no positional force.
                        // Clear the fat-hold so any empty space beyond them (e.g. the
                        // subarachnoid/CSF gap between dura and cord) reads ZERO, not fat.
                        _surfaceLatch = VoxelTissueType.Background;
                        break;
                }
            }

            // ── 4. Palpation force (bone surface targets) ────────────────────
            Vector3 forcePalpation = palpationController != null
                ? palpationController.ComputePalpationForce(cursor, cursorVel)
                : Vector3.zero;

            // ── 5. Buzz ──────────────────────────────────────────────────────
            Vector3 forceBuzz = ComputeBuzz();

            // ── 6. Combine, clamp, send ──────────────────────────────────────
            Vector3 total = ClampForce(forceAxial + forceLateral + forcePalpation + forceBuzz);
            inverse3.SetCursorForce(total);

            // ── 7. Publish for Unity thread ──────────────────────────────────
            // Project onto the insertion axis actually used for delivery.
            Vector3 deliv = _dirInit && _railDir.sqrMagnitude > 1e-12f ? _railDir : axis;
            float fOutput = Vector3.Dot(total, -deliv);
            float axialVelMmps = Vector3.Dot(cursorVel, deliv) / _uPerMm; // mm/s along insertion axis
            _rwLock.EnterWriteLock();
            try
            {
                _shared   = new HapticState { depth = paDepthM, fTotal = forceAxial.magnitude,
                                             fOutput = fOutput, velocity = axialVelMmps };
                _hasEvent = true;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        // Full backlash + blend ligament depth model (adapted from HapticNeedleControllerV2).
        void LigamentForce(Vector3 cursor, Vector3 cursorVel, float depth, StackSnapshotSpine snap,
                           out Vector3 axial, out Vector3 lateral)
        {
            float bandM  = withdrawalBandMm  * 0.001f;
            float blendM = Mathf.Max(withdrawalBlendMm * 0.001f, 1e-5f);

            if (!_dirInit)
            {
                _dirRef = depth; _prevDepth = depth;
                _blend = 0f; _withdrawing = false; _dirInit = true;

                // Lock rail to needle direction at entry.
                Vector3 d0; bool valid;
                lock (_needleDirLock) { d0 = _needleDirUnity; valid = _needleDirValid; }
                if (!valid || d0.sqrMagnitude < 1e-12f) d0 = snap.axis;
                d0.Normalize();
                if (Vector3.Dot(d0, snap.axis) < 0f) d0 = -d0;
                _railDir    = d0;
                _railOrigin = cursor;

                _activeSnap = snap;
            }

            // Direction detection via backlash hysteresis.
            if      (depth > _dirRef + bandM) { _withdrawing = false; _dirRef = depth - bandM; }
            else if (depth < _dirRef - bandM) { _withdrawing = true;  _dirRef = depth + bandM; }

            _blend = Mathf.MoveTowards(_blend, _withdrawing ? 1f : 0f,
                                       Mathf.Abs(depth - _prevDepth) / blendM);
            _prevDepth = depth;
            float dirScale = Mathf.Lerp(1f, withdrawalScale, _blend);

            int   layerIdx = ForceModelSpine.GetLayerIndex(depth, snap.layers);
            float fInsert  = ForceModelSpine.Evaluate(depth, layerIdx, snap.layers, true, 1f);
            float fMag     = constantWithdrawal
                ? Mathf.Lerp(fInsert, withdrawalForce, _blend)
                : fInsert * dirScale;
            fMag = Mathf.Max(fMag * forceScale, 0f);

            axial = -_railDir * fMag;

            // Lateral rail spring ⊥ _railDir.
            lateral = Vector3.zero;
            if (enableLateralConstraint)
            {
                Vector3 rel    = cursor - _railOrigin;
                Vector3 offset = rel - Vector3.Dot(rel, _railDir) * _railDir;
                float   dist   = offset.magnitude;
                float   db     = lateralDeadbandMm * 0.001f;
                if (dist > db)
                {
                    Vector3 n      = offset / dist;
                    float   eff    = dist - db;
                    Vector3 latVel = cursorVel - Vector3.Dot(cursorVel, _railDir) * _railDir;
                    lateral = -lateralStiffness * eff * n - lateralDamping * latVel;
                    lateral -= Vector3.Dot(lateral, _railDir) * _railDir;
                }
            }
        }

        // Current insertion axis (unit, inward) from the cached needle direction. Used for
        // delivering NRRD-tissue forces (skin/fat/bone/gap). Oriented toward +Z (anterior).
        Vector3 LiveInsertionAxis()
        {
            Vector3 d0; bool valid;
            lock (_needleDirLock) { d0 = _needleDirUnity; valid = _needleDirValid; }
            if (!valid || d0.sqrMagnitude < 1e-12f) return Vector3.forward;
            d0.Normalize();
            if (Vector3.Dot(d0, Vector3.forward) < 0f) d0 = -d0;
            return d0;
        }

        // Skin resistance: the JSON skin curve stretched/compressed so its domain [0, z_width_mm]
        // maps onto the ACTUAL skin thickness traversed → the rupture lands at the skin/fat border.
        float SkinForce(Vector3 cursor, Vector3 axis)
        {
            if (!_hasSkinCurve) return Mathf.Max(0f, skinForceN); // fallback to constant

            // On first contact, lock the entry point and measure the thickness ahead.
            if (!_skinActive)
            {
                _skinEntryPos    = cursor;
                _skinThicknessMm = MeasureSkinThicknessMm(cursor, axis);
                _skinActive      = true;
            }

            float depthMm = Vector3.Dot(cursor - _skinEntryPos, axis) / _uPerMm;
            depthMm       = Mathf.Clamp(depthMm, 0f, _skinThicknessMm);
            float dzScale = _skinJsonWidthMm / Mathf.Max(_skinThicknessMm, 1e-3f);
            return Mathf.Max(0f, _skinCurve.Evaluate(depthMm * dzScale));
        }

        // Ray-marches the NRRD from `start` along `axis` until skin ends → thickness in mm.
        // Runs once per skin entry. SampleTissue is read-only after Start (haptic-thread safe).
        float MeasureSkinThicknessMm(Vector3 start, Vector3 axis)
        {
            const float step = 0.2f, maxMm = 12f;
            if (nrrdVolume == null || !nrrdVolume.IsReady) return _skinJsonWidthMm;
            for (float mm = step; mm <= maxMm; mm += step)
            {
                Vector3 p = start + axis * (mm * _uPerMm);
                if (nrrdVolume.SampleTissue(p) != VoxelTissueType.Skin)
                    return mm;
            }
            return maxMm;
        }

        // ── Unity thread ──────────────────────────────────────────────────────
        void FixedUpdate()
        {
            // Cache needleBody.up for the haptic thread (Transform.* is not thread-safe).
            if (needleBody != null)
            {
                Vector3 up = needleBody.up;
                lock (_needleDirLock) { _needleDirUnity = up; _needleDirValid = true; }
            }

            // Manage muscle/cord buzz + audio from the voxel tissue.
            VoxelTissueType tissue = (VoxelTissueType)_lastVoxelTissue;
            bool needsBuzz = tissue == VoxelTissueType.Muscle || tissue == VoxelTissueType.Cord;

            if (needsBuzz && !_wasBuzzing)
            {
                StartBuzz(buzzFrequency, buzzFMin, buzzFMax);
                if (voxelBuzzAudio != null && !voxelBuzzAudio.isPlaying)
                    voxelBuzzAudio.Play();
                _wasBuzzing = true;
            }
            else if (!needsBuzz && _wasBuzzing)
            {
                StopBuzz();
                voxelBuzzAudio?.Stop();
                _wasBuzzing = false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Square-wave buzz along the insertion/rail direction.
        Vector3 ComputeBuzz()
        {
            if (!_buzzActive) return Vector3.zero;
            long  freqT   = System.Diagnostics.Stopwatch.Frequency;
            long  now     = System.Diagnostics.Stopwatch.GetTimestamp();
            float elapsed = (float)(now - System.Threading.Interlocked.Read(ref _buzzStartTick)) / freqT;
            float fBuzz   = ((elapsed * _buzzFreqHz) % 1f < 0.5f) ? _buzzFMax : _buzzFMin;
            // Buzz along the insertion axis: locked rail if in a ligament, else the live needle dir.
            Vector3 dir   = (_dirInit && _railDir.sqrMagnitude > 1e-12f) ? _railDir.normalized
                                                                         : LiveInsertionAxis();
            return dir * fBuzz;
        }

        Vector3 ClampForce(Vector3 f)
        {
            if (!enableForceClamp) return f;
            return new Vector3(
                Mathf.Clamp(f.x, -maxOutputForce.x, maxOutputForce.x),
                Mathf.Clamp(f.y, -maxOutputForce.y, maxOutputForce.y),
                Mathf.Clamp(f.z, -maxOutputForce.z, maxOutputForce.z));
        }
    }
}
