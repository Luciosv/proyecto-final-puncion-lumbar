// Drives the two-stage lumbar-puncture simulation:
//
//   PALPATION (optional, RIGHT device):
//     • VerseGrip markButton  → mark the planned palpation point (tentative, at needle tip).
//     • VerseGrip checkButton → 1st press: show the marked→ideal distance.
//                                2nd press: advance to the puncture stage.
//     • Mouse (no haptic device needed):
//         mouseMarkButton (default left)  → raycast from the cursor onto the body,
//                                            mark the tentative point at the hit.
//         mouseCheckButton (default right) → same as checkButton (show distance / advance).
//     If palpationEnabled is OFF, the sim starts directly in the puncture stage.
//
//   PUNCTURE (LEFT device, needle):
//     • Needle controller + force profiler become active; the ideal insertionPoint stays visible.
//     • Entry validation / time-series logging are handled by separate components that read
//       Current and StageChanged.
//
// Stage switching is done by toggling the relevant components' `enabled` flag, so each haptic
// controller subscribes/unsubscribes from its device (OnEnable/OnDisable) only while its stage runs.
//
// REQUIRES: Haply Inverse for Unity package.

using UnityEngine;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Simulation Stage Manager")]
    public class SimulationStageManager : MonoBehaviour
    {
        public enum Stage { Palpation, Puncture }

        [Header("Stages")]
        [Tooltip("If OFF, the simulation starts directly in the puncture stage.")]
        public bool palpationEnabled = true;

        [Header("Palpation stage (right device)")]
        public HapticPalpationController palpationController;
        public EntryPointComparator      entryComparator;
        [Tooltip("VerseGrip of the RIGHT device that drives the palpation buttons.")]
        public VerseGripController rightVerseGrip;
        [Tooltip("Button to MARK the planned palpation point.")]
        public VerseGripButton markButton  = VerseGripButton.Button1;
        [Tooltip("Button to CHECK (1st press = show distance) then ADVANCE to puncture (2nd press).")]
        public VerseGripButton checkButton = VerseGripButton.Button0;

        [Header("Mouse input (palpation, no haptic device needed)")]
        [Tooltip("If ON, mouse clicks also drive mark/check during the palpation stage.")]
        public bool enableMouseInput = true;
        [Tooltip("Mouse button for MARK (0 = left, 1 = right, 2 = middle).")]
        public int mouseMarkButton = 0;
        [Tooltip("Mouse button for CHECK / ADVANCE (0 = left, 1 = right, 2 = middle).")]
        public int mouseCheckButton = 1;
        [Tooltip("Camera used for the mouse→body raycast. If null, Camera.main is used.")]
        public Camera raycastCamera;
        [Tooltip("Which layers the mouse ray can hit when looking for the body.")]
        public LayerMask mouseRaycastMask = ~0;
        [Tooltip("Max raycast distance (world units).")]
        public float mouseMaxDistance = 100f;
        [Tooltip("If ON, clicks that miss every collider are ignored. If OFF, a missed MARK " +
                 "falls back to the needle-tip position.")]
        public bool mouseRequireBodyHit = true;
        [Tooltip("Lift the marker above the hit surface along the normal (world units) " +
                 "so it never z-fights with the skin.")]
        public float markSurfaceOffset = 0.0015f;
        [Tooltip("If ON, every mouse click / raycast step logs to the Console " +
                 "(useful to diagnose why a mark is ignored).")]
        public bool enableMouseLogs = true;

        [Header("Mouse raycast fallback (no collider needed)")]
        [Tooltip("Optional. When the mouse ray hits no collider, march the NRRD volume along " +
                 "the ray to find the skin entry. If null, it is auto-detected in the scene.")]
        public NrrdTissueVolume nrrdVolume;
        [Tooltip("NRRD march step (mm) used by the mouse fallback.")]
        public float nrrdMarchStepMm = 1f;
        [Tooltip("How far along the mouse ray to look for the body (mm).")]
        public float nrrdMaxDistanceMm = 300f;

        [Tooltip("Optional: the palpation hand/finger model. Shown in palpation, hidden in puncture.")]
        public GameObject palpationHandModel;

        [Header("Puncture stage (left device)")]
        public HybridNeedleController needleController;
        public HapticForceProfiler   profiler;
        [Tooltip("Ideal entry-point GameObject — kept active during the puncture stage.")]
        public GameObject insertionPoint;

        [Tooltip("Optional: the needle model. Hidden in palpation, shown in puncture.")]
        public GameObject needleModel;

        // ── Public state (read by validator / logger) ────────────────────────
        public Stage Current { get; private set; }
        public event System.Action<Stage> StageChanged;

        bool _subscribed;
        bool _checked;   // palpation: has the comparison been shown at least once?

        // ── Lifecycle ────────────────────────────────────────────────────────
        void Awake()
        {
            // The stage manager is the single button authority during palpation.
            if (entryComparator != null) entryComparator.externalControl = true;

            if (nrrdVolume == null)
                nrrdVolume = FindFirstObjectByType<NrrdTissueVolume>();

            // Make sure no device controller fires before its stage starts (their OnEnable
            // subscribes to DeviceStateChanged — we gate that with the enabled flag).
            SetEnabled(needleController,     false);
            SetEnabled(profiler,            false);
            SetEnabled(palpationController, false);
            SetEnabled(entryComparator,     false);
        }

        void Start()
        {
            if (palpationEnabled) EnterPalpation();
            else                  EnterPuncture();
            if (enableMouseLogs)
                Debug.Log($"[StageManager] Mouse config — enabled={enableMouseInput} " +
                          $"markBtn={mouseMarkButton} checkBtn={mouseCheckButton} " +
                          $"stage={Current} comparator={(entryComparator != null ? "ok" : "NULL")} " +
                          $"nrrd={(nrrdVolume != null ? (nrrdVolume.IsReady ? "ready" : "loading") : "NULL")}");
        }

        void OnDisable() => Unsubscribe();

        // ── Mouse input (palpation only) ─────────────────────────────────────
        // NOTE: the project uses the NEW Input System (activeInputHandler = Input System),
        // so legacy Input.GetMouseButtonDown never fires — read Mouse.current instead,
        // keeping the legacy call as a fallback when both systems are enabled.
        void Update()
        {
            if (!enableMouseInput || Current != Stage.Palpation || entryComparator == null)
                return;
            // Don't steal clicks meant for UI buttons/sliders.
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
            {
                if (enableMouseLogs) Debug.Log("[StageManager] Mouse click eaten by UI — ignored.");
                return;
            }

            if (MouseButtonDown(mouseMarkButton))  HandleMouseMark();
            if (mouseCheckButton != mouseMarkButton &&
                MouseButtonDown(mouseCheckButton)) HandleMouseCheck();
        }

        // New Input System first, legacy Input Manager as fallback.
        static bool MouseButtonDown(int button)
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                switch (button)
                {
                    case 0: if (mouse.leftButton.wasPressedThisFrame)   return true; break;
                    case 1: if (mouse.rightButton.wasPressedThisFrame)  return true; break;
                    case 2: if (mouse.middleButton.wasPressedThisFrame) return true; break;
                }
            }
#endif
            try
            {
                if (Input.GetMouseButtonDown(button)) return true;
            }
            catch (System.Exception) { /* legacy input disabled — ignore */ }
            return false;
        }

        static Vector2 MouseScreenPosition()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null) return mouse.position.ReadValue();
#endif
            return Input.mousePosition;
        }

        void HandleMouseMark()
        {
            Vector2 mousePos = MouseScreenPosition();
            if (enableMouseLogs) Debug.Log($"[StageManager] Mouse MARK pressed at screen {mousePos}.");
            if (TryMouseBodyHit(mousePos, out Vector3 point, out Vector3 normal, out string hitSource))
            {
                if (enableMouseLogs)
                    Debug.Log($"[StageManager] Mouse MARK hit via {hitSource} at {point} (normal {normal}).");
                entryComparator.MarkTentativeAt(point + normal * markSurfaceOffset, normal);
                _checked = false; // a fresh mark must be checked again before advancing
            }
            else if (!mouseRequireBodyHit)
            {
                if (enableMouseLogs) Debug.Log("[StageManager] Mouse MARK missed body — fallback to needle tip.");
                entryComparator.MarkTentative(); // fallback: needle-tip position
                _checked = false;
            }
            else
            {
                Debug.LogWarning("[StageManager] Mouse MARK ignored — cursor is not over the body " +
                                 "(no collider hit and NRRD fallback missed/not ready).");
            }
        }

        void HandleMouseCheck()
        {
            if (enableMouseLogs) Debug.Log("[StageManager] Mouse CHECK pressed.");
            HandleCheckButton();
        }

        bool TryMouseBodyHit(Vector2 mousePos, out Vector3 point, out Vector3 normal, out string hitSource)
        {
            point     = Vector3.zero;
            normal    = Vector3.up;
            hitSource = "none";
            Camera cam = raycastCamera != null ? raycastCamera : Camera.main;
            if (cam == null)
            {
                if (enableMouseLogs) Debug.LogWarning("[StageManager] Mouse raycast: no camera (raycastCamera null + Camera.main null).");
                return false;
            }
            Ray ray = cam.ScreenPointToRay(mousePos);
            // 1) Physics colliders (fast path — works if the body has colliders).
            if (Physics.Raycast(ray, out RaycastHit hit, mouseMaxDistance, mouseRaycastMask))
            {
                point     = hit.point;
                normal    = hit.normal;
                hitSource = $"collider '{hit.collider.name}'";
                return true;
            }
            // 2) NRRD fallback (no collider needed — march the voxel volume).
            if (TryNrrdBodyHit(ray, out point, out normal))
            {
                hitSource = "nrrd";
                return true;
            }
            if (enableMouseLogs)
                Debug.Log("[StageManager] Mouse raycast: physics missed + " +
                          (nrrdVolume != null
                              ? (nrrdVolume.IsReady ? "NRRD marched, no tissue found." : "NRRD still loading.")
                              : "no NrrdTissueVolume in scene."));
            return false;
        }

        bool TryNrrdBodyHit(Ray ray, out Vector3 point, out Vector3 normal)
        {
            point  = Vector3.zero;
            normal = -ray.direction;
            if (nrrdVolume == null || !nrrdVolume.IsReady) return false;

            float stepMm = Mathf.Max(nrrdMarchStepMm, 0.1f);
            float uPerMm = needleController != null && needleController.nrrdVolume != null
                ? Mathf.Max(needleController.nrrdVolume.unityUnitsPerMm, 1e-6f)
                : Mathf.Max(nrrdVolume.unityUnitsPerMm, 1e-6f);
            int n = Mathf.CeilToInt(Mathf.Max(nrrdMaxDistanceMm, 1f) / stepMm);

            for (int i = 0; i <= n; i++)
            {
                Vector3 wp = ray.origin + ray.direction * (i * stepMm * uPerMm);
                if (nrrdVolume.SampleTissue(wp) != VoxelTissueType.Background)
                {
                    point  = wp;
                    normal = NrrdSurfaceNormal(wp, uPerMm * 2f, ray.direction);
                    return true;
                }
            }
            return false;
        }

        Vector3 NrrdSurfaceNormal(Vector3 p, float h, Vector3 rayDir)
        {
            float SolidAt(Vector3 w) =>
                nrrdVolume.SampleTissue(w) != VoxelTissueType.Background ? 1f : 0f;
            float gx = SolidAt(p + Vector3.right   * h) - SolidAt(p - Vector3.right   * h);
            float gy = SolidAt(p + Vector3.up      * h) - SolidAt(p - Vector3.up      * h);
            float gz = SolidAt(p + Vector3.forward * h) - SolidAt(p - Vector3.forward * h);
            Vector3 g = new Vector3(gx, gy, gz);
            if (g.sqrMagnitude < 1e-9f) return (-rayDir).normalized; // flat → face the camera
            return (-g).normalized; // gradient points into solid → outward = -gradient
        }

        // ── Stage transitions ────────────────────────────────────────────────
        public void EnterPalpation()
        {
            Current  = Stage.Palpation;
            _checked = false;

            SetEnabled(needleController,     false);
            SetEnabled(profiler,            false);
            SetEnabled(palpationController, true);
            SetEnabled(entryComparator,     true);

            if (palpationHandModel != null) palpationHandModel.SetActive(true);
            if (needleModel        != null) needleModel.SetActive(false);

            Subscribe();
            StageChanged?.Invoke(Current);
            Debug.Log("[StageManager] Palpation — VerseGrip mark/check + mouse " +
                      $"mark({mouseMarkButton})/check({mouseCheckButton}) ready.");
        }

        public void EnterPuncture()
        {
            Current = Stage.Puncture;
            Unsubscribe();

            // Hide the comparison visuals (tentative marker + line + distance label) while the
            // comparator is still enabled, THEN disable it. The ideal insertionPoint is re-shown after.
            if (entryComparator != null) entryComparator.SetComparisonActive(false);
            SetEnabled(palpationController, false);
            SetEnabled(entryComparator,     false);

            if (palpationHandModel != null) palpationHandModel.SetActive(false);
            if (needleModel        != null) needleModel.SetActive(true);
            if (insertionPoint     != null) insertionPoint.SetActive(true); // ideal point stays visible

            SetEnabled(needleController, true);
            SetEnabled(profiler,        true);

            StageChanged?.Invoke(Current);
            Debug.Log("[StageManager] Puncture — needle active on the left device.");
        }

        // ── VerseGrip buttons (palpation only) ───────────────────────────────
        void Subscribe()
        {
            if (_subscribed || rightVerseGrip == null) return;
            rightVerseGrip.ButtonDown.AddListener(OnButtonDown);
            _subscribed = true;
        }

        void Unsubscribe()
        {
            if (!_subscribed || rightVerseGrip == null) return;
            rightVerseGrip.ButtonDown.RemoveListener(OnButtonDown);
            _subscribed = false;
        }

        void OnButtonDown(VerseGripController grip, VerseGripEventArgs args)
        {
            if (Current != Stage.Palpation) return;

            if (args.Button == markButton)
            {
                entryComparator?.MarkTentative();
                _checked = false; // a fresh mark must be checked again before advancing
            }
            else if (args.Button == checkButton)
            {
                HandleCheckButton();
            }
        }

        void HandleCheckButton()
        {
            if (!_checked)
            {
                entryComparator?.SetComparisonActive(true); // show marked→ideal distance
                _checked = true;
                if (entryComparator != null)
                    Debug.Log($"[StageManager] Check — distance {entryComparator.LastDistanceCm:F1} cm. " +
                              "Press button 1 again to start the puncture.");
            }
            else
            {
                EnterPuncture();
            }
        }

        // ── Helper ───────────────────────────────────────────────────────────
        static void SetEnabled(MonoBehaviour c, bool on)
        {
            if (c != null && c.enabled != on) c.enabled = on;
        }
    }
}
