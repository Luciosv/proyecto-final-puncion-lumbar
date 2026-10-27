// Tissue-stack + force-curve profiler for the lumbar-puncture simulator.
//
// PLANNING PREVIEW (before entering):
//   While the needle tip is OUTSIDE all tissue, the profiler re-projects a ray along the
//   current insertion direction (needleBody.up) from the tip to the end of the body, every
//   few FixedUpdates. It marches the NRRD volume + PunctureAccess cubes along that ray and
//   redraws the background — colored tissue bands + the dashed reference force curve — so you
//   can see WHICH tissues and WHICH force curve the needle would meet on that trajectory.
//
//   Because the ray is marched in the real insertion direction and the PA force is evaluated
//   at the Z-projected depth (exactly as HybridNeedleController feels it), the preview adapts
//   to non-parallel entry: a steeper angle stretches the curve by 1/cos(theta), matching the
//   haptic device.
//
// INSERTION (after entering):
//   On entry the background is FROZEN (the projection captured at the entry point/direction).
//   A solid green trace records the ACTUAL axial force vs depth as the needle advances; the
//   yellow dot + white line mark the live (depth, force). Withdrawing past the entry ends the
//   run and pushes the trace to history (faded-green overlays).
//
// Rendering follows LP.V2.PlotV2: a Texture2D on a UI RawImage, fixed X axis = depth (mm).
//
// TEACHING OVERLAY:
//   A self-contained 5x7 bitmap font labels the graph directly so it explains the movement without
//   a separate legend: each tissue band is named (PIEL, GRASA, L.AMARILLO, DURAMADRE, ...) with its
//   peak force, and the force (N) / depth (mm) axes carry tick numbers. While inserting, a live
//   read-out shows the current tissue, depth and force. All of this is toggleable in the Inspector.
//
// RESETTING THE DRAWN CURVE:
//   R (resetKey)  → archive the current trace as a faded overlay and re-aim the preview.
//   C (clearKey)  → wipe the drawn curve completely (live trace + all history).
//   ResetChart()/ClearTraces() are public — wire ResetChart to a UI Button's OnClick for a
//   clickable reset next to the plot.
//
// PUNCTURE SUCCESS:
//   When the tip reaches the SUBARACHNOID space — or the empty CSF cistern beyond it, up to the
//   cord — and stays there for `holdSeconds` without touching cord or bone, the puncture counts as
//   achieved: a "PUNCION LOGRADA" banner is drawn, a couple of CSF drops fall from the configured
//   dropSpawnPoint (built-in, or an assigned ParticleSystem), and onPunctureAchieved fires. Success
//   clears when the needle withdraws or the chart is reset.
//
// SETUP:
//   1. Create a UI RawImage (e.g. 512 x 256) and add this component to it.
//   2. Wire needleController, punctureAccess, nrrdVolume in the Inspector.
//   3. (Optional) Add a UI Button and hook its OnClick to this component's ResetChart().

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SpineVoxels.Runtime.Haptic
{
    [RequireComponent(typeof(RawImage))]
    [AddComponentMenu("SpineVoxels/Haptic Force Profiler")]
    public class HapticForceProfiler : MonoBehaviour
    {
        // ── References ────────────────────────────────────────────────────────
        [Header("References")]
        public HybridNeedleController needleController;
        public PunctureAccessStack    punctureAccess;
        public NrrdTissueVolume       nrrdVolume;

        // ── Scan settings ─────────────────────────────────────────────────────
        [Header("Scan")]
        [Tooltip("Total ray length projected ahead of the tip (mm, physical). Should reach the far side of the body.")]
        public float maxScanMm = 180f;
        [Tooltip("Left margin (mm, physical) drawn before the contact point so the skin is not glued " +
                 "to the left edge. The scan starts this far behind the tip.")]
        [Min(0f)] public float contactOffsetMm = 8f;
        [Tooltip("Ray-march step for tissue detection and reference curve (mm, physical).")]
        [Min(0.1f)] public float stepMm = 0.7f;
        [Tooltip("Re-project the planning preview every N FixedUpdates while not inserting.")]
        [Min(1)] public int previewPeriod = 4;

        // ── Texture settings ──────────────────────────────────────────────────
        [Header("Texture")]
        [Tooltip("Plot width in pixels (1.3x the previous 1024). Widen the RawImage RectTransform to match.")]
        public int   texWidth  = 1331;
        public int   texHeight = 256;
        [Range(1f, 12f)]  public float maxForceN = 8f;
        [Range(-4f, 0f)]  public float minForceN = -1f;
        [Tooltip("Half-size of the filled yellow dot drawn at the current position.")]
        public int dotHalf = 3;

        // ── Withdrawal (suction) reference ────────────────────────────────────
        [Header("Withdrawal (suction) reference")]
        [Tooltip("Draw the exit/withdrawal force as a constant reference line across the tissue span.")]
        public bool  showWithdrawalCurve = true;
        [Tooltip("Constant withdrawal force (N). A negative value sits below zero to suggest suction.")]
        public float withdrawalConstantN = -0.5f;

        // ── History ───────────────────────────────────────────────────────────
        [Header("History")]
        [Tooltip("Number of previous runs kept as faded-green overlays.")]
        public int maxHistory = 3;
        [Tooltip("Press to archive the current trace as a faded overlay and re-aim the preview.")]
        public Key resetKey = Key.R;
        [Tooltip("Press to fully clear the drawn curve (live trace + all faded history), leaving only " +
                 "the tissue bands and reference curve. Also callable from a UI Button via ResetChart().")]
        public Key clearKey = Key.C;

        // ── Labels (teaching overlay) ─────────────────────────────────────────
        [Header("Labels (teaching)")]
        [Tooltip("Draw the tissue name on top of each band so the graph is self-explanatory.")]
        public bool showTissueLabels = true;
        [Tooltip("Draw the peak force (N) of each tissue under its name.")]
        public bool showPeakForce = true;
        [Tooltip("Draw force (N) and depth (mm) axis tick numbers.")]
        public bool showAxisLabels = true;
        [Tooltip("While inserting, draw a live read-out (current tissue, depth, force) in the corner.")]
        public bool showLiveReadout = true;
        [Tooltip("Text size multiplier for the labels (1 = crisp 5x7 font, 2 = double).")]
        [Range(1, 3)] public int labelScale = 1;

        // ── Puncture success (reaching the subarachnoid space) ────────────────
        [Header("Puncture success")]
        [Tooltip("Detect a successful puncture: needle tip held inside the SUBARACHNOID layer " +
                 "for holdSeconds without touching cord or bone.")]
        public bool enableSuccessDetection = true;
        [Tooltip("Seconds the tip must stay in the subarachnoid space (cord/bone-free) to count as success.")]
        [Min(0.1f)] public float holdSeconds = 1.5f;
        [Tooltip("Draw a 'PUNCION LOGRADA' banner on the graph when the puncture succeeds.")]
        public bool showSuccessSign = true;
        public string successText = "PUNCION LOGRADA";

        [Header("Puncture success — CSF drops")]
        [Tooltip("Spawn a couple of falling CSF drops from the defined spawn point on success.")]
        public bool spawnCsfDrops = true;
        [Tooltip("The point the drops fall from — assign an empty placed where you want them (e.g. the " +
                 "needle hub / rear end). If left empty, the needle body is used as a fallback.")]
        public Transform dropSpawnPoint;
        [Range(1, 8)] public int dropCount = 3;
        [Tooltip("Absolute drop size in world units. Leave 0 to auto-size from the needle's thickness " +
                 "(recommended — a real 2mm drop is tiny on this ~0.003 units/mm model and hard to see).")]
        [Min(0f)] public float dropSizeUnits = 0f;
        [Tooltip("Fallback physical drop diameter (mm) used only when dropSizeUnits is 0 AND no needle " +
                 "renderer is found to auto-size from.")]
        [Min(0.1f)] public float dropDiameterMm = 2f;
        [Tooltip("Log to the Console when the puncture succeeds and drops spawn (helps confirm setup).")]
        public bool logSuccess = true;
        public Color dropColor = new Color(0.75f, 0.92f, 1f, 1f);
        [Tooltip("Optional particle system played on success (in addition to / instead of the built-in drops).")]
        public ParticleSystem csfParticles;
        [Tooltip("Optional hook fired once when the puncture succeeds (wire your own VFX/SFX/UI here).")]
        public UnityEngine.Events.UnityEvent onPunctureAchieved;

        // ── Palette (matches PlotV2 dark theme) ───────────────────────────────
        static readonly Color32 C_BG     = new Color32( 13,  16,  24, 255);
        static readonly Color32 C_GRID   = new Color32( 32,  37,  52, 255);
        static readonly Color32 C_ZERO   = new Color32( 75,  82, 108, 255);
        static readonly Color32 C_REF    = new Color32(160, 160, 160, 255); // dashed grey
        static readonly Color32 C_TRACE  = new Color32( 80, 200,  80, 255); // green live
        static readonly Color32 C_CURSOR = new Color32(255, 255, 255,  80); // translucent white
        static readonly Color32 C_DOT    = new Color32(255, 220,  40, 255); // yellow dot
        static readonly Color32 C_WITHDRAW = new Color32(230, 120, 40, 255); // orange dashed (suction)
        static readonly Color32 C_LABEL  = new Color32(210, 216, 228, 255); // near-white tissue names
        static readonly Color32 C_AXIS   = new Color32(120, 128, 150, 255); // dim axis numbers
        static readonly Color32 C_OK     = new Color32(120, 240, 150, 255); // success banner text

        // ── Internal profile data ─────────────────────────────────────────────

        struct Seg
        {
            public float  startMm, endMm;
            public Color  color;
            public string label;   // tissue name drawn on the band
            public float  peakF;   // peak reference force (N) within the band
        }

        readonly List<Seg> _segs = new();

        // The PunctureAccess snapshot captured for the current projection (null if none ahead).
        StackSnapshotSpine _projSnap;

        // Projection geometry (frozen on entry; live while previewing).
        Vector3 _scanStartPos;   // ray origin = tip at projection time
        Vector3 _scanDir;        // insertion direction (needle d0)
        float   _uPerMm;         // Unity units per physical mm

        // Pre-sampled reference curve.
        readonly List<(float mm, float F)> _ref = new();

        // Live trace: recorded (depthMm, fOutput) during the current insertion.
        readonly List<(float mm, float F)> _trace = new();
        readonly List<List<(float mm, float F)>> _history = new();

        // Live cursor (current depth + force), updated every FixedUpdate.
        float _curMm, _curF;

        // Success tracking: time held inside the subarachnoid space (cord/bone-free) and whether the
        // puncture has been achieved this run (latched until the needle withdraws / the chart resets).
        float    _successHold;
        bool     _success;
        Material _dropMat;
        // Last PunctureAccess snapshot the tip was inside — reused as a depth reference while the
        // tip sits in the empty CSF cistern beyond the stack bounds.
        StackSnapshotSpine _lastSnap;

        // _frozen: an insertion is in progress — the projection is locked at the entry point so
        // withdrawing THROUGH the tissue does NOT drag the background backward. When the needle is
        // fully withdrawn back out to the air, it returns to the live preview (which follows the aim).
        bool _frozen;
        int  _frame;

        // Set by InvalidateProjection() — forces a re-projection on the next FixedUpdate regardless
        // of _frozen state, so calibration updates are reflected immediately.
        bool _forceReproject;

        // ── Texture ───────────────────────────────────────────────────────────
        Texture2D _tex;
        Color32[] _pixels;     // working buffer
        Color32[] _base;       // BG + bands + grid + ref curve (rebuilt on each projection)
        bool      _dirty;
        RawImage  _img;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Awake()
        {
            _img       = GetComponent<RawImage>();
            _img.color = Color.white;
            EnsureTexture();
        }

        void FixedUpdate()
        {
            if (needleController?.inverse3 == null) return;

            Vector3 cursor = needleController.inverse3.CursorPosition;
            if (float.IsNaN(cursor.x)) return;

            Vector3 dir = needleController.needleBody != null
                ? needleController.needleBody.up.normalized
                : Vector3.forward;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;

            bool tipInTissue = TipInTissue(cursor);

            // Calibration update requested: re-project with new curves, keeping the frozen state.
            if (_forceReproject)
            {
                _forceReproject = false;
                Project(cursor, dir);
                _dirty = true;
            }
            else if (!_frozen)
            {
                if (tipInTissue)
                {
                    // Entry — freeze the projection at this point/direction, begin recording.
                    Project(cursor, dir);
                    _trace.Clear();
                    _frozen = true;
                }
                else if (++_frame % previewPeriod == 0)
                {
                    // Live planning preview — follows the aim while OUTSIDE the tissue.
                    Project(cursor, dir);
                    _dirty = true;
                }
            }

            if (_frozen)
            {
                // Background stays locked — withdrawing through the tissue never re-projects it back.
                _curMm = Vector3.Dot(cursor - _scanStartPos, _scanDir) / _uPerMm;

                // Fully withdrawn back out to the entry air → archive (fade) and resume the preview.
                if (_curMm < contactOffsetMm - 2f) { ArchiveAndReaim(); return; }

                if (needleController.TryGetHapticOutput(out _, out float forceN))
                    _curF = forceN;

                // Trace grows forward only; on withdrawal it (and the background) stay in place.
                float prevMm = _trace.Count > 0 ? _trace[_trace.Count - 1].mm : float.NegativeInfinity;
                if (_curMm > prevMm + 0.15f)
                    _trace.Add((_curMm, _curF));

                _dirty = true;
            }

            UpdateSuccess(cursor);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            // R (configurable): archive the current trace as a faded overlay and re-aim.
            if (kb[resetKey].wasPressedThisFrame) ArchiveAndReaim();
            // C (configurable): wipe the drawn curve entirely (live trace + history).
            if (kb[clearKey].wasPressedThisFrame) ClearTraces();
        }

        void LateUpdate()
        {
            if (!_dirty || _base == null) return;
            _dirty = false;
            Redraw();
        }

        void OnDestroy()
        {
            if (_tex != null)     Destroy(_tex);
            if (_dropMat != null) Destroy(_dropMat);
        }

        // ── Public API ────────────────────────────────────────────────────────

        // Force an immediate re-projection on the next FixedUpdate, even during a frozen insertion.
        // Call this after live calibration data changes so the reference curve updates without
        // requiring the user to withdraw the needle first.
        public void InvalidateProjection() => _forceReproject = true;

        // Archive the current trace to history (faded overlay) and return to live-aiming preview.
        public void ArchiveAndReaim()
        {
            if (_trace.Count > 1)
            {
                _history.Add(new List<(float, float)>(_trace));
                while (_history.Count > maxHistory) _history.RemoveAt(0);
            }
            _trace.Clear();
            _frozen = false;
            _curMm  = _curF = 0f;
            _success = false; _successHold = 0f; _lastSnap = null;
            _dirty  = true;
        }

        // Fully clear the drawn curve: the live green trace AND every faded history overlay, leaving
        // only the tissue bands + reference curve. Hook this to a UI Button's OnClick for a reset
        // button next to the plot.
        public void ResetChart() => ClearTraces();

        public void ClearTraces()
        {
            _trace.Clear();
            _history.Clear();
            _curMm = _curF = 0f;
            _success = false; _successHold = 0f; _lastSnap = null;
            _dirty = true;
        }

        // ── Entry detection ───────────────────────────────────────────────────

        bool TipInTissue(Vector3 cursor)
        {
            var s = punctureAccess?.FindActiveSnapshot(cursor);
            if (s != null)
            {
                float z = Vector3.Dot(cursor - s.origin, s.axis) / s.visualScale;
                if (FindLayer(s, z) >= 0) return true;
            }
            if (nrrdVolume != null && nrrdVolume.IsReady)
                return nrrdVolume.SampleTissue(cursor) != VoxelTissueType.Background;
            return false;
        }

        // ── Projection: march the ray and rebuild bands + reference curve ──────

        void Project(Vector3 origin, Vector3 dir)
        {
            _scanDir = dir;

            // Capture the PA snapshot this ray passes through (active or ahead).
            _projSnap = punctureAccess?.FindActiveSnapshot(origin);
            if (_projSnap == null && punctureAccess != null)
                _projSnap = punctureAccess.FindSnapshotAhead(origin, dir);

            // Unity-units-per-mm: from the snapshot's visualScale if available, else NRRD config.
            _uPerMm = _projSnap != null ? _projSnap.visualScale * 0.001f
                    : nrrdVolume != null ? nrrdVolume.unityUnitsPerMm
                    : 0.003f;

            // Start the scan contactOffsetMm BEHIND the tip → left margin before the skin.
            // The tip then sits at mm = contactOffsetMm in scan coordinates.
            _scanStartPos = origin - dir * (contactOffsetMm * _uPerMm);

            float scale = needleController != null ? needleController.forceScale : 1f;

            _segs.Clear();
            _ref.Clear();

            int n = Mathf.CeilToInt(maxScanMm / stepMm);

            // Pass 1 — sample raw tissue, color and force at every step.
            var kind  = new int[n + 1];     // -1 = background; >=0 = drawable tissue key
            var cols  = new Color[n + 1];
            var fcs   = new float[n + 1];
            var isFat = new bool[n + 1];
            var isSkn = new bool[n + 1];
            var names = new string[n + 1];  // tissue name at each step (for band labels)
            for (int i = 0; i <= n; i++)
            {
                float mm = Mathf.Min(i * stepMm, maxScanMm);
                Vector3 p = _scanStartPos + _scanDir * (mm * _uPerMm);
                SampleAt(p, scale, out kind[i], out cols[i], out fcs[i], out isFat[i], out isSkn[i], out names[i]);
            }

            // Pass 2 — latch: hold the fat band/force across empty gaps BETWEEN tissues.
            // Mirrors HybridNeedleController: a void is filled with fat only while the running
            // latch == Fat. Skin resets the latch (so exit air reads zero) and we never fill
            // beyond the last real tissue (so the far side past the body stays empty).
            int lastReal = -1;
            for (int i = 0; i <= n; i++) if (kind[i] != -1) lastReal = i;

            bool latchFat = false;
            Color fatCol  = NrrdColor(VoxelTissueType.Fat);
            float fatF    = Mathf.Max(0f, (needleController?.fatForceN ?? 0.5f) * scale);
            for (int i = 0; i <= n; i++)
            {
                if (isFat[i] || isSkn[i])
                {
                    latchFat = true; // skin AND fat arm the fat-hold (fill skin↔fat and fat↔deep gaps)
                }
                else if (kind[i] == -1)
                {
                    // Empty gap: fill with fat only while the fat-hold is armed and we are still
                    // before the last real tissue. Deep gaps (e.g. dura↔cord) stay empty; the
                    // trailing air past the last tissue (i >= lastReal) also stays empty.
                    if (latchFat && i < lastReal)
                    {
                        kind[i]  = (int)VoxelTissueType.Fat;
                        cols[i]  = fatCol;
                        fcs[i]   = fatF;
                        names[i] = TissueName(VoxelTissueType.Fat);
                    }
                }
                else
                {
                    latchFat = false; // any deep structure clears the fat-hold
                }
            }

            // Pass 2b — skin: stretch the skin curve to each contiguous skin run so the rupture
            // lands at the skin/fat boundary, mirroring HybridNeedleController.SkinForce.
            if (needleController != null && needleController.TryGetSkinCurve(out var skinCurve, out float skinW))
            {
                int i = 0;
                while (i <= n)
                {
                    if (!isSkn[i]) { i++; continue; }
                    int a = i;
                    while (i <= n && isSkn[i]) i++;
                    int   b       = i - 1;                       // skin run [a, b]
                    float thickMm = Mathf.Max((b - a) * stepMm, 1e-3f);
                    float dz      = skinW / thickMm;
                    for (int k = a; k <= b; k++)
                        fcs[k] = Mathf.Max(0f, skinCurve.Evaluate((k - a) * stepMm * dz) * scale);
                }
            }

            // Pass 2c — fat: stretch the fat curve to each contiguous ACTUAL fat run so the graph
            // shows the depth-dependent shape (ramp, exponential, etc.) instead of a flat line.
            // Uses isFat[] (set by SampleAt) so gap-fill pixels remain at the constant fatForceN.
            if (needleController != null && needleController.TryGetFatCurve(out var fatCurve, out float fatW))
            {
                int i = 0;
                while (i <= n)
                {
                    if (!isFat[i]) { i++; continue; }
                    int a = i;
                    while (i <= n && isFat[i]) i++;
                    int   b       = i - 1;
                    float thickMm = Mathf.Max((b - a) * stepMm, 1e-3f);
                    float dz      = fatW / thickMm;
                    for (int k = a; k <= b; k++)
                        fcs[k] = Mathf.Max(0f, fatCurve.Evaluate((k - a) * stepMm * dz) * scale);
                }
            }

            // Pass 3 — build reference curve + bands from the resolved arrays.
            int    prevKey = int.MinValue;
            float  segS    = 0f;
            Color  segCol  = Color.clear;
            string segName = null;
            float  segPeak = 0f;
            for (int i = 0; i <= n; i++)
            {
                float mm  = Mathf.Min(i * stepMm, maxScanMm);
                bool  end = i == n;

                _ref.Add((mm, fcs[i]));

                bool changed = kind[i] != prevKey;
                if ((changed || end) && prevKey >= 0 && mm > segS + 0.05f)
                    _segs.Add(new Seg { startMm = segS, endMm = mm, color = segCol,
                                        label = segName, peakF = segPeak });

                if (changed) { prevKey = kind[i]; segS = mm; segCol = cols[i]; segName = names[i]; segPeak = 0f; }
                if (kind[i] >= 0) segPeak = Mathf.Max(segPeak, fcs[i]);
            }

            DrawBase();
        }

        // Classify a world point: key (band identity), band color, force (N), and which surface
        // tissue it is (for the fat latch).  key: -1 = background; >=0 = a drawable tissue.
        void SampleAt(Vector3 p, float scale, out int key, out Color col, out float force,
                      out bool fat, out bool skin, out string name)
        {
            fat = skin = false;
            name = null;

            VoxelTissueType t = nrrdVolume != null && nrrdVolume.IsReady
                ? nrrdVolume.SampleTissue(p)
                : VoxelTissueType.Background;

            bool  boneLast = needleController != null && needleController.boneLowestPriority;
            float boneF    = needleController != null ? needleController.maxBoneForce : 3.0f;

            // Bone priority mirrors HybridNeedleController: by default bone wins over the ligament;
            // with boneLowestPriority the ligament takes precedence (bone falls through to the switch).
            if (t == VoxelTissueType.Bone && !boneLast)
            {
                key = (int)t; col = NrrdColor(t); force = boneF; name = TissueName(t); return;
            }

            // PunctureAccess ligament.
            if (_projSnap != null && _projSnap.bounds.Contains(p))
            {
                float zDepthM = Vector3.Dot(p - _projSnap.origin, _projSnap.axis) / _projSnap.visualScale;
                int li = FindLayer(_projSnap, zDepthM);
                if (li >= 0)
                {
                    var L = _projSnap.layers[li];
                    key   = 1000 + li;
                    col   = LayerColor(L.type);
                    force = Mathf.Max(0f, ForceModelSpine.Evaluate(zDepthM, li, _projSnap.layers, true, 1f) * scale);
                    name  = LayerName(L.type);
                    return;
                }
            }

            switch (t)
            {
                case VoxelTissueType.Skin:
                    skin = true;
                    key = (int)t; col = NrrdColor(t); name = TissueName(t);
                    force = Mathf.Max(0f, (needleController?.skinForceN ?? 1.0f) * scale); return;
                case VoxelTissueType.Fat:
                    fat = true;
                    key = (int)t; col = NrrdColor(t); name = TissueName(t);
                    force = Mathf.Max(0f, (needleController?.fatForceN ?? 0.5f) * scale); return;
                case VoxelTissueType.Bone:
                    // Reached only with boneLowestPriority + no ligament above → bone felt last.
                    key = (int)t; col = NrrdColor(t); force = boneF; name = TissueName(t); return;
                case VoxelTissueType.Muscle:
                case VoxelTissueType.Cord:
                    // Buzz tissues: dimmed band, no positional force.
                    key = (int)t; name = TissueName(t);
                    col = Color.Lerp(NrrdColor(t), new Color(0.15f, 0.15f, 0.15f), 0.45f);
                    force = 0f; return;
                default:
                    key = -1; col = Color.clear; force = 0f; return;
            }
        }

        static int FindLayer(StackSnapshotSpine snap, float zDepthM)
        {
            var ls = snap.layers;
            for (int i = 0; i < ls.Length; i++)
                if (zDepthM >= ls[i].dStartM && zDepthM < ls[i].dEndM) return i;
            return -1;
        }

        // ── Texture management ────────────────────────────────────────────────

        void EnsureTexture()
        {
            if (_tex != null && _tex.width == texWidth && _tex.height == texHeight) return;
            if (_tex != null) Destroy(_tex);
            _tex            = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
            _tex.filterMode = FilterMode.Point;
            _pixels         = new Color32[texWidth * texHeight];
            _base           = new Color32[texWidth * texHeight];
            _img.texture    = _tex;
        }

        // Pre-draw static elements into _base (rebuilt on each projection).
        void DrawBase()
        {
            for (int i = 0; i < _base.Length; i++) _base[i] = C_BG;

            // Tissue bands (18% tint over BG, same as PlotV2).
            foreach (var seg in _segs)
            {
                int x0 = DX(seg.startMm), x1 = DX(seg.endMm);
                var tint = new Color32(
                    (byte)(seg.color.r * 0.18f * 255f + C_BG.r * 0.82f),
                    (byte)(seg.color.g * 0.18f * 255f + C_BG.g * 0.82f),
                    (byte)(seg.color.b * 0.18f * 255f + C_BG.b * 0.82f), 255);
                for (int x = Mathf.Max(x0, 0); x < Mathf.Min(x1, texWidth); x++)
                    for (int y = 0; y < texHeight; y++)
                        _base[y * texWidth + x] = tint;
            }

            // Horizontal grid lines every 1 N (from minForceN up to maxForceN).
            for (float fN = Mathf.Ceil(minForceN); fN <= maxForceN; fN += 1f)
            {
                int y = FY(fN);
                if (y < 0 || y >= texHeight) continue;
                Color32 c = Mathf.Approximately(fN, 0f) ? C_ZERO : C_GRID;
                for (int x = 0; x < texWidth; x++) _base[y * texWidth + x] = c;
            }

            // Withdrawal (suction) reference: a flat constant line across the tissue span.
            if (showWithdrawalCurve && _segs.Count > 0)
            {
                float minMm = float.MaxValue, maxMm = float.MinValue;
                foreach (var seg in _segs) { minMm = Mathf.Min(minMm, seg.startMm); maxMm = Mathf.Max(maxMm, seg.endMm); }
                int yW = FY(withdrawalConstantN);
                Line(_base, DX(minMm), yW, DX(maxMm), yW, C_WITHDRAW, dashed: true);
            }

            // Insertion reference curve (dashed grey).
            for (int i = 1; i < _ref.Count; i++)
                Line(_base, DX(_ref[i-1].mm), FY(_ref[i-1].F),
                            DX(_ref[i].mm),   FY(_ref[i].F), C_REF, dashed: true);

            DrawAxisLabels();
            DrawBandLabels();
        }

        // Force (N) ticks on the left, depth (mm) ticks along the bottom.
        void DrawAxisLabels()
        {
            if (!showAxisLabels) return;
            int s = labelScale;

            // Y axis: even N values, plus the zero line.
            for (int fN = Mathf.CeilToInt(minForceN); fN <= Mathf.FloorToInt(maxForceN); fN++)
            {
                if (fN != 0 && (fN & 1) != 0) continue; // even only, always keep 0
                int y = FY(fN);
                DrawText(_base, fN.ToString(), 2, y + 3 * s, C_AXIS, s);
            }
            DrawText(_base, "N", 2, texHeight - 2, C_AXIS, s);

            // X axis: depth every 40 mm.
            for (int mm = 0; mm <= Mathf.RoundToInt(maxScanMm); mm += 40)
            {
                int x = DX(mm);
                Line(_base, x, 0, x, 3 * s, C_AXIS, dashed: false);
                string t = mm.ToString();
                int tx = Clamp(x - TextWidth(t, s) / 2, 0, texWidth - 1 - TextWidth(t, s));
                DrawText(_base, t, tx, 5 * s + 6, C_AXIS, s);
            }
            string unit = "MM";
            DrawText(_base, unit, texWidth - TextWidth(unit, s) - 2, 5 * s + 6, C_AXIS, s);
        }

        // Tissue name (and peak force) centered on each band, near the top of the plot.
        void DrawBandLabels()
        {
            if (!showTissueLabels) return;
            int s     = labelScale;
            int yName = texHeight - 4;               // top row of the name text
            int yPeak = yName - (8 * s);             // one line below the name

            foreach (var seg in _segs)
            {
                if (string.IsNullOrEmpty(seg.label)) continue;
                int x0 = DX(seg.startMm), x1 = DX(seg.endMm);
                int w  = x1 - x0;

                // Prefer the full name; if the band is too narrow, fall back to a short code so
                // something still fits (e.g. L.AMARILLO → LIG.A). If even that doesn't fit, skip it.
                string name  = seg.label;
                int    nameW = TextWidth(name, s);
                if (w < nameW + 2 && _shortName.TryGetValue(seg.label, out var abbr))
                {
                    name  = abbr;
                    nameW = TextWidth(name, s);
                }
                if (w >= nameW + 2)
                    DrawText(_base, name, x0 + (w - nameW) / 2, yName, C_LABEL, s);

                if (showPeakForce && seg.peakF > 0.01f)
                {
                    string pf = seg.peakF.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "N";
                    int pfW = TextWidth(pf, s);
                    if (w >= pfW + 2)
                        DrawText(_base, pf, x0 + (w - pfW) / 2, yPeak, C_REF, s);
                }
            }
        }

        // ── Per-frame redraw ──────────────────────────────────────────────────

        void Redraw()
        {
            System.Array.Copy(_base, _pixels, _pixels.Length);

            for (int h = 0; h < _history.Count; h++)
            {
                byte a = (byte)(255 * (h + 1f) / (_history.Count + 1f));
                DrawTrace(_history[h], new Color32(C_TRACE.r, C_TRACE.g, C_TRACE.b, a));
            }

            DrawTrace(_trace, C_TRACE);

            if (_frozen)
            {
                DrawCursor();
                if (showLiveReadout) DrawReadout();
            }

            if (_success && showSuccessSign) DrawSuccessSign();

            _tex.SetPixels32(_pixels);
            _tex.Apply(false);
        }

        // Corner read-out shown while inserting: current tissue name + live depth and force.
        // Drawn top-right (usually empty air past the body) over a dark backing for legibility.
        void DrawReadout()
        {
            int s = Mathf.Max(1, labelScale);
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            string tissue = CurrentBandLabel(_curMm) ?? "-";
            string stats  = "P " + _curMm.ToString("0.0", ci) + "MM  F " + _curF.ToString("0.0", ci) + "N";

            int w = Mathf.Max(TextWidth(tissue, s), TextWidth(stats, s));
            int lineH = 8 * s;
            int pad = 3;

            int x0 = texWidth - w - pad * 2 - 2;
            int yTop = texHeight - 4;                 // top text row
            int boxTop = yTop + 2;
            int boxBot = yTop - lineH - 6 * s - 1;    // below the second line

            // Dark translucent backing.
            for (int y = Mathf.Max(boxBot, 0); y <= Mathf.Min(boxTop, texHeight - 1); y++)
                for (int x = Mathf.Max(x0 - pad, 0); x < texWidth; x++)
                    Blend(x, y, new Color32(13, 16, 24, 200));

            DrawText(_pixels, tissue, x0, yTop, C_DOT, s);
            DrawText(_pixels, stats,  x0, yTop - lineH, C_LABEL, s);
        }

        // Name of the band the given depth falls in (null if none / in a gap).
        string CurrentBandLabel(float mm)
        {
            foreach (var seg in _segs)
                if (mm >= seg.startMm && mm < seg.endMm) return seg.label;
            return null;
        }

        // ── Puncture success ──────────────────────────────────────────────────

        // Detect a good puncture: the tip has reached the SUBARACHNOID space (or the CSF void beyond
        // it, up to the cord) and stays there — clear of cord and bone — for `holdSeconds`. Reaching
        // the cord or bone aborts the hold. Success latches until the needle is withdrawn
        // (ArchiveAndReaim) or the chart is reset.
        //
        // The CSF cistern between the subarachnoid cube and the cord is empty (no PunctureAccess
        // layer and outside the stack bounds), so we keep the last snapshot's geometry to measure
        // depth there: any depth at or beyond the subarachnoid start counts as the success zone,
        // while shallower depths / air (z below the subarachnoid start) do not.
        void UpdateSuccess(Vector3 cursor)
        {
            if (!enableSuccessDetection || _success) return;

            // Cord / bone contact aborts the hold.
            bool danger = false;
            if (nrrdVolume != null && nrrdVolume.IsReady)
            {
                var t = nrrdVolume.SampleTissue(cursor);
                danger = t == VoxelTissueType.Bone || t == VoxelTissueType.Cord;
            }

            // Use the active snapshot if we're inside one; otherwise reuse the last one seen so we
            // still have a depth reference in the empty CSF cistern beyond the stack bounds.
            var live = punctureAccess?.FindActiveSnapshot(cursor);
            if (live != null) _lastSnap = live;
            var s = live ?? _lastSnap;

            bool inZone = false;
            if (s != null)
            {
                float z        = Vector3.Dot(cursor - s.origin, s.axis) / s.visualScale;
                float subStart = SubarachnoidStartM(s);
                int   li       = FindLayer(s, z);
                // Inside the subarachnoid cube, OR in the void at/beyond where it starts (the cistern
                // down to the cord). Depths shallower than that (other layers / air) are not the zone.
                if (li >= 0)
                    inZone = s.layers[li].type == LayerTypeSpine.Subarachnoid;
                else
                    inZone = !float.IsPositiveInfinity(subStart) && z >= subStart;
            }

            if (inZone && !danger)
            {
                _successHold += Time.fixedDeltaTime;
                if (_successHold >= holdSeconds) TriggerSuccess();
            }
            else
            {
                _successHold = 0f;
            }
        }

        // Depth (m) at which the subarachnoid layer begins in this snapshot; +Infinity if absent.
        static float SubarachnoidStartM(StackSnapshotSpine s)
        {
            foreach (var L in s.layers)
                if (L.type == LayerTypeSpine.Subarachnoid) return L.dStartM;
            return float.PositiveInfinity;
        }

        void TriggerSuccess()
        {
            _success = true;
            _dirty   = true;
            if (logSuccess)
                Debug.Log("[HapticForceProfiler] Punción lograda — aguja estable en el espacio subaracnoideo.");

            onPunctureAchieved?.Invoke();
            if (csfParticles != null) csfParticles.Play();
            if (spawnCsfDrops)        SpawnDrops();
        }

        // Spawn a few small opaque drops at the needle hub that fall and shrink, then self-destroy.
        // Opaque + shrink avoids depending on a transparent shader across render pipelines.
        void SpawnDrops()
        {
            Transform spawn = dropSpawnPoint != null ? dropSpawnPoint
                            : needleController != null ? needleController.needleBody
                            : null;
            Vector3 origin = spawn != null ? spawn.position : transform.position;

            float size = DropWorldSize();
            var   mat  = DropMaterial();

            if (logSuccess)
                Debug.Log($"[HapticForceProfiler] Gotas: {dropCount} en {origin} (tamaño {size:F4} u). " +
                          (dropSpawnPoint == null
                              ? "Consejo: asigná 'Drop Spawn Point' al punto desde donde querés que salgan."
                              : ""));

            for (int i = 0; i < dropCount; i++)
            {
                var go  = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "CSFDrop";
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);

                go.transform.position   = origin + Random.insideUnitSphere * (size * 0.5f);
                go.transform.localScale = Vector3.one * size;

                var rend = go.GetComponent<Renderer>();
                if (rend != null) rend.sharedMaterial = mat;

                var drop      = go.AddComponent<CsfDrop>();
                drop.delay    = i * 0.35f;
                drop.life     = 1.8f;
                drop.dir      = Vector3.down;
                drop.speed    = size * 6f;        // fall ~6 drop-sizes/s, accelerating
                drop.baseSize = Vector3.one * size;
            }
        }

        // World-space drop diameter. Prefers the explicit override, then ~3x the needle's thickness
        // (so it scales with whatever the scene scale is and stays visible), then the mm fallback.
        float DropWorldSize()
        {
            if (dropSizeUnits > 0f) return dropSizeUnits;

            Renderer nr = null;
            if (needleController != null && needleController.needleBody != null)
                nr = needleController.needleBody.GetComponentInChildren<Renderer>();
            if (nr != null)
            {
                Vector3 s = nr.bounds.size;
                float thin = Mathf.Min(s.x, Mathf.Min(s.y, s.z)); // needle diameter-ish
                if (thin > 1e-5f) return Mathf.Clamp(thin * 3f, 1e-4f, 1f);
            }

            float uPerMm = nrrdVolume != null ? nrrdVolume.unityUnitsPerMm : 0.003f;
            return Mathf.Max(dropDiameterMm * uPerMm, 1e-4f);
        }

        Material DropMaterial()
        {
            if (_dropMat != null) return _dropMat;
            Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Sprites/Default");
            _dropMat = new Material(sh);
            if (_dropMat.HasProperty("_BaseColor")) _dropMat.SetColor("_BaseColor", dropColor);
            if (_dropMat.HasProperty("_Color"))     _dropMat.SetColor("_Color",     dropColor);
            // Make the drops self-lit so they read clearly against dark tissue / in VR.
            _dropMat.EnableKeyword("_EMISSION");
            if (_dropMat.HasProperty("_EmissionColor"))
                _dropMat.SetColor("_EmissionColor", (Color)dropColor * 0.6f);
            return _dropMat;
        }

        // "PUNCION LOGRADA" banner, centered in the upper-middle over a dark backing.
        void DrawSuccessSign()
        {
            int    scl = Mathf.Max(2, labelScale + 1);
            string t   = (successText ?? "").ToUpperInvariant();
            if (t.Length == 0) return;

            int w    = TextWidth(t, scl);
            int h    = 7 * scl;
            int x0   = (texWidth - w) / 2;
            int yTop = texHeight / 2 + h;

            for (int y = yTop - h - 4; y <= yTop + 4; y++)
                for (int x = x0 - 6; x < x0 + w + 6; x++)
                    Blend(x, y, new Color32(10, 40, 16, 210));

            DrawText(_pixels, t, x0, yTop, C_OK, scl);
        }

        // A single CSF drop: waits `delay`, then falls (accelerating) and shrinks over `life`.
        sealed class CsfDrop : MonoBehaviour
        {
            public float   delay, life, speed;
            public Vector3 dir, baseSize;
            float _t;

            void Update()
            {
                _t += Time.deltaTime;
                if (_t < delay) { transform.localScale = Vector3.zero; return; }

                float u = _t - delay;
                transform.position   += dir * (speed * (0.3f + u) * Time.deltaTime);
                transform.localScale  = baseSize * (0.6f + 0.4f * Mathf.Clamp01(1f - u / life));
                if (u >= life) Destroy(gameObject);
            }
        }

        void DrawTrace(List<(float mm, float F)> pts, Color32 col)
        {
            if (pts == null || pts.Count < 2) return;
            for (int i = 1; i < pts.Count; i++)
                Line(_pixels, DX(pts[i-1].mm), FY(pts[i-1].F),
                              DX(pts[i].mm),   FY(pts[i].F), col, dashed: false);
        }

        void DrawCursor()
        {
            int xc = DX(_curMm), yc = FY(_curF);

            for (int y = 0; y < texHeight; y++)
                Blend(xc, y, C_CURSOR);

            for (int dy = -dotHalf; dy <= dotHalf; dy++)
            for (int dx = -dotHalf; dx <= dotHalf; dx++)
            {
                int px = Mathf.Clamp(xc + dx, 0, texWidth  - 1);
                int py = Mathf.Clamp(yc + dy, 0, texHeight - 1);
                _pixels[py * texWidth + px] = C_DOT;
            }
        }

        // ── Pixel primitives (identical to PlotV2) ────────────────────────────

        void Line(Color32[] buf, int x0, int y0, int x1, int y1, Color32 col, bool dashed)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx - dy, step = 0;
            for (int g = 0; g < 4096; g++)
            {
                if (!dashed || (step & 3) < 2)
                {
                    int px = Clamp(x0, 0, texWidth  - 1);
                    int py = Clamp(y0, 0, texHeight - 1);
                    buf[py * texWidth + px] = col;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 <  dx) { err += dx; y0 += sy; }
                step++;
            }
        }

        void Blend(int x, int y, Color32 src)
        {
            x = Clamp(x, 0, texWidth  - 1);
            y = Clamp(y, 0, texHeight - 1);
            int idx = y * texWidth + x;
            Color32 dst = _pixels[idx];
            float a = src.a / 255f, ia = 1f - a;
            _pixels[idx] = new Color32(
                (byte)(src.r * a + dst.r * ia),
                (byte)(src.g * a + dst.g * ia),
                (byte)(src.b * a + dst.b * ia), 255);
        }

        // ── Coordinate transforms ─────────────────────────────────────────────

        int DX(float mm) =>
            Clamp(Mathf.RoundToInt(mm / maxScanMm * (texWidth - 1)), 0, texWidth - 1);

        int FY(float fN) =>
            Clamp(Mathf.RoundToInt((fN - minForceN) / (maxForceN - minForceN) * (texHeight - 1)), 0, texHeight - 1);

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        // ── Tissue colors ─────────────────────────────────────────────────────

        static Color NrrdColor(VoxelTissueType t) => t switch
        {
            VoxelTissueType.Skin   => new Color(0.93f, 0.72f, 0.60f),
            VoxelTissueType.Fat    => new Color(0.96f, 0.88f, 0.38f),
            VoxelTissueType.Muscle => new Color(0.80f, 0.20f, 0.20f),
            VoxelTissueType.Bone   => new Color(0.95f, 0.93f, 0.72f),
            VoxelTissueType.Cord   => new Color(0.72f, 0.32f, 0.80f),
            _                      => new Color(0.20f, 0.20f, 0.22f),
        };

        static Color LayerColor(LayerTypeSpine lt) => lt switch
        {
            LayerTypeSpine.SupraspinousLig  => new Color(0.42f, 0.80f, 0.42f),
            LayerTypeSpine.InterspinousLig  => new Color(0.28f, 0.66f, 0.28f),
            LayerTypeSpine.LigamentumFlavum => new Color(0.16f, 0.54f, 0.16f),
            LayerTypeSpine.EpiduralSpace    => new Color(0.36f, 0.66f, 0.88f),
            LayerTypeSpine.Dura             => new Color(0.18f, 0.46f, 0.80f),
            LayerTypeSpine.Subarachnoid     => new Color(0.10f, 0.34f, 0.70f),
            _                               => new Color(0.50f, 0.50f, 0.50f),
        };

        // ── Tissue display names (Spanish, uppercase, ASCII to fit the 5x7 font) ──

        static string TissueName(VoxelTissueType t) => t switch
        {
            VoxelTissueType.Skin   => "PIEL",
            VoxelTissueType.Fat    => "GRASA",
            VoxelTissueType.Muscle => "MUSCULO",
            VoxelTissueType.Bone   => "HUESO",
            VoxelTissueType.Cord   => "MEDULA",
            _                      => "",
        };

        static string LayerName(LayerTypeSpine lt) => lt switch
        {
            LayerTypeSpine.Skin             => "PIEL",
            LayerTypeSpine.Fat              => "GRASA",
            LayerTypeSpine.Muscle           => "MUSCULO",
            LayerTypeSpine.SupraspinousLig  => "SUPRAESP",
            LayerTypeSpine.InterspinousLig  => "INTERESP",
            LayerTypeSpine.LigamentumFlavum => "L.AMARILLO",
            LayerTypeSpine.EpiduralSpace    => "EPIDURAL",
            LayerTypeSpine.Dura             => "DURAMADRE",
            LayerTypeSpine.Subarachnoid     => "SUBARACN",
            _                               => "LIG",
        };

        // Short codes used when a band is too narrow for the full name.
        static readonly Dictionary<string, string> _shortName = new()
        {
            { "PIEL", "PIEL" }, { "GRASA", "GRA" }, { "MUSCULO", "MUSC" },
            { "HUESO", "HUE" }, { "MEDULA", "MED" }, { "SUPRAESP", "SUP" },
            { "INTERESP", "INT" }, { "L.AMARILLO", "LIG.A" }, { "EPIDURAL", "EPI" },
            { "DURAMADRE", "DURA" }, { "SUBARACN", "SUB" },
        };

        // ── Bitmap text (self-contained 5x7 font; no scene setup required) ────────

        // Each glyph is 7 rows; the low 5 bits of each byte are the pixels (bit 4 = leftmost).
        static readonly Dictionary<char, byte[]> _font = BuildFont();

        // Advance width of a string in pixels (5px glyph + 1px gap), scaled.
        static int TextWidth(string s, int scale) =>
            string.IsNullOrEmpty(s) ? 0 : (s.Length * 6 - 1) * scale;

        // Draw `s` with its top-left glyph corner at (x, yTop). y grows upward (texture space),
        // so each glyph row steps downward in y. Out-of-range pixels are skipped (no wrap).
        void DrawText(Color32[] buf, string s, int x, int yTop, Color32 col, int scale)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.ToUpperInvariant();
            int cx = x;
            foreach (char ch in s)
            {
                if (_font.TryGetValue(ch, out var g))
                {
                    for (int r = 0; r < 7; r++)
                    {
                        int row = g[r];
                        if (row == 0) continue;
                        for (int c = 0; c < 5; c++)
                            if ((row & (1 << (4 - c))) != 0)
                                FillBlock(buf, cx + c * scale, yTop - r * scale, scale, col);
                    }
                }
                cx += 6 * scale;
            }
        }

        // Fill a scale x scale block whose top-left is (x, y) (y downward), clipped to the texture.
        void FillBlock(Color32[] buf, int x, int y, int s, Color32 col)
        {
            for (int dy = 0; dy < s; dy++)
            for (int dx = 0; dx < s; dx++)
            {
                int px = x + dx, py = y - dy;
                if (px < 0 || px >= texWidth || py < 0 || py >= texHeight) continue;
                buf[py * texWidth + px] = col;
            }
        }

        static Dictionary<char, byte[]> BuildFont()
        {
            var f = new Dictionary<char, byte[]>();
            void G(char c, params string[] rows)
            {
                var b = new byte[7];
                for (int r = 0; r < 7; r++)
                {
                    byte v = 0;
                    string row = rows[r];
                    for (int col = 0; col < 5 && col < row.Length; col++)
                        if (row[col] == 'X') v |= (byte)(1 << (4 - col));
                    b[r] = v;
                }
                f[c] = b;
            }

            G(' ', "     ", "     ", "     ", "     ", "     ", "     ", "     ");
            G('.', "     ", "     ", "     ", "     ", "     ", " XX  ", " XX  ");
            G('-', "     ", "     ", "     ", "XXXXX", "     ", "     ", "     ");
            G('A', " XXX ", "X   X", "X   X", "XXXXX", "X   X", "X   X", "X   X");
            G('B', "XXXX ", "X   X", "X   X", "XXXX ", "X   X", "X   X", "XXXX ");
            G('C', " XXXX", "X    ", "X    ", "X    ", "X    ", "X    ", " XXXX");
            G('D', "XXXX ", "X   X", "X   X", "X   X", "X   X", "X   X", "XXXX ");
            G('E', "XXXXX", "X    ", "X    ", "XXXX ", "X    ", "X    ", "XXXXX");
            G('F', "XXXXX", "X    ", "X    ", "XXXX ", "X    ", "X    ", "X    ");
            G('G', " XXXX", "X    ", "X    ", "X  XX", "X   X", "X   X", " XXXX");
            G('H', "X   X", "X   X", "X   X", "XXXXX", "X   X", "X   X", "X   X");
            G('I', "XXXXX", "  X  ", "  X  ", "  X  ", "  X  ", "  X  ", "XXXXX");
            G('J', "  XXX", "   X ", "   X ", "   X ", "X  X ", "X  X ", " XX  ");
            G('K', "X   X", "X  X ", "X X  ", "XX   ", "X X  ", "X  X ", "X   X");
            G('L', "X    ", "X    ", "X    ", "X    ", "X    ", "X    ", "XXXXX");
            G('M', "X   X", "XX XX", "X X X", "X   X", "X   X", "X   X", "X   X");
            G('N', "X   X", "XX  X", "X X X", "X  XX", "X   X", "X   X", "X   X");
            G('O', " XXX ", "X   X", "X   X", "X   X", "X   X", "X   X", " XXX ");
            G('P', "XXXX ", "X   X", "X   X", "XXXX ", "X    ", "X    ", "X    ");
            G('Q', " XXX ", "X   X", "X   X", "X   X", "X X X", "X  X ", " XX X");
            G('R', "XXXX ", "X   X", "X   X", "XXXX ", "X X  ", "X  X ", "X   X");
            G('S', " XXXX", "X    ", "X    ", " XXX ", "    X", "    X", "XXXX ");
            G('T', "XXXXX", "  X  ", "  X  ", "  X  ", "  X  ", "  X  ", "  X  ");
            G('U', "X   X", "X   X", "X   X", "X   X", "X   X", "X   X", " XXX ");
            G('V', "X   X", "X   X", "X   X", "X   X", "X   X", " X X ", "  X  ");
            G('W', "X   X", "X   X", "X   X", "X   X", "X X X", "XX XX", "X   X");
            G('X', "X   X", "X   X", " X X ", "  X  ", " X X ", "X   X", "X   X");
            G('Y', "X   X", "X   X", " X X ", "  X  ", "  X  ", "  X  ", "  X  ");
            G('Z', "XXXXX", "    X", "   X ", "  X  ", " X   ", "X    ", "XXXXX");
            G('0', " XXX ", "X   X", "X  XX", "X X X", "XX  X", "X   X", " XXX ");
            G('1', "  X  ", " XX  ", "  X  ", "  X  ", "  X  ", "  X  ", " XXX ");
            G('2', " XXX ", "X   X", "    X", "   X ", "  X  ", " X   ", "XXXXX");
            G('3', "XXXXX", "   X ", "  X  ", "   X ", "    X", "X   X", " XXX ");
            G('4', "   X ", "  XX ", " X X ", "X  X ", "XXXXX", "   X ", "   X ");
            G('5', "XXXXX", "X    ", "XXXX ", "    X", "    X", "X   X", " XXX ");
            G('6', "  XX ", " X   ", "X    ", "XXXX ", "X   X", "X   X", " XXX ");
            G('7', "XXXXX", "    X", "   X ", "  X  ", " X   ", " X   ", " X   ");
            G('8', " XXX ", "X   X", "X   X", " XXX ", "X   X", "X   X", " XXX ");
            G('9', " XXX ", "X   X", "X   X", " XXXX", "    X", "   X ", " XX  ");
            return f;
        }
    }
}
