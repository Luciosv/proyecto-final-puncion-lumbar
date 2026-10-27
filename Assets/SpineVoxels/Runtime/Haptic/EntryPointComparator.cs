// Compares a TENTATIVE entry point (marked by the user with the VerseGrip) against the TRUE
// insertion point (insertionPoint, assigned in the Inspector).
//
// Ported from LP.V2.EntryPointComparator (PuncionSinHaptico), namespace adapted to Voxels.
//   Button 1 (toggleButton): shows/hides both points together.
//   Button 2 (markButton):   destroys the previous tentative instance and creates a new one at
//                            the needle tip (tip position + axis-mapped rotation), visible even
//                            if the global toggle is off.
// When both points are visible, a line + distance label (cm) is drawn between them.

using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using TMPro;
using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Entry Point Comparator")]
    public class EntryPointComparator : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("If null, the first one in the scene is used.")]
        public VerseGripController verseGrip;

        [Tooltip("Needle tip transform. The tentative marker is created exactly here.")]
        public Transform needleTip;

        [Tooltip("TRUE entry point. Assign the GameObject; toggled with button 1.")]
        public GameObject insertionPoint;

        [Tooltip("PREFAB of the tentative point. Button 2 destroys the previous instance and " +
                 "creates a new one at the tip (tip position + axis-mapped rotation).")]
        public GameObject tentativePrefab;

        [Tooltip("Fine rotation offset (Euler, degrees) applied ON TOP of the axis mapping.")]
        public Vector3 tentativeEulerOffset = Vector3.zero;

        [Header("VerseGrip buttons")]
        [Tooltip("Button 1: global toggle (shows/hides both).")]
        public VerseGripButton toggleButton = VerseGripButton.Button0;

        [Tooltip("Button 2: destroys and recreates the tentative marker at the tip.")]
        public VerseGripButton markButton = VerseGripButton.Button1;

        [Tooltip("If ON, this component does NOT listen to the VerseGrip itself — an external " +
                 "driver (SimulationStageManager) calls MarkTentative()/SetComparisonActive().")]
        public bool externalControl = false;

        [Header("Line")]
        [Tooltip("LineRenderer joining the points. If null, one is created automatically at Awake. " +
                 "Assign a scene object instead to see and tweak it without entering Play mode.")]
        public LineRenderer line;

        [Tooltip("OFF = the script never touches colours/sizes; style the LineRenderer and the " +
                 "TextMeshPro from their own Inspectors instead.")]
        public bool applyStyleFromScript = true;

        [Tooltip("Line width (world units).")]
        public float lineWidth = 0.005f;

        [Tooltip("Line colour along its length: green at the ideal point, red at the marked one. " +
                 "Perceptual blend, so the midpoint reads orange with only two keys.")]
        public Gradient lineGradient = new Gradient
        {
            mode = GradientMode.PerceptualBlend,
            colorKeys = new[]
            {
                new GradientColorKey(new Color(0.42f, 0.72f, 0.24f), 0f),
                new GradientColorKey(new Color(0.79f, 0.19f, 0.17f), 1f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            }
        };

        [Header("Distance label")]
        [Tooltip("TextMeshPro showing the distance. If null, one is created automatically at Awake. " +
                 "Assign a scene object instead to style it from the TMP Inspector.")]
        public TextMeshPro distanceLabel;

        [Tooltip("Text color.")]
        public Color labelColor = Color.green;

        [Tooltip("Label font size (TextMeshPro fontSize).")]
        public float fontSize = 0.2f;

        [Tooltip("Bold glyphs — noticeably easier to read through a headset.")]
        public bool bold = true;

        [Tooltip("Outline drawn around the glyphs, so they read over any background.")]
        public Color outlineColor = Color.black;

        [Tooltip("Outline thickness (TMP units, 0..1). ~0.2 reads well in VR.")]
        [Range(0f, 1f)] public float outlineWidth = 0.2f;

        public enum LabelFacing
        {
            Billboard,           // glyphs run horizontally on screen
            PerpendicularToLine  // glyphs run across the line
        }

        [Tooltip("Glyph direction. Both modes keep the text plane facing the camera.")]
        public LabelFacing labelFacing = LabelFacing.PerpendicularToLine;

        [Tooltip("Alignment inside the text box below. Right = the text hugs the box's right edge.")]
        public TextAlignmentOptions labelAlignment = TextAlignmentOptions.Right;

        [Tooltip("Text box size (world units). Alignment is meaningless without it: the box is " +
                 "centred on the label transform and the text is placed inside it.")]
        public Vector2 labelBoxSize = new Vector2(0.6f, 0.15f);

        [Tooltip("Offset from the line midpoint, along the perpendicular, in world units. " +
                 "Positive/negative picks the side.")]
        public float labelOffset = 0.02f;

        [Tooltip("Distance format string. {0} is the value in centimetres.")]
        public string labelFormat = "{0:F1} cm";

        [Tooltip("World-units → centimetres. Voxels: 1/unityUnitsPerMm/10 = 1/0.003/10 ≈ 33.33.")]
        public float worldToCm = 33.33f;

        // ── State ─────────────────────────────────────────────────────────────
        bool _globalOn;
        bool _pendingTentative;

        GameObject _tentativeInstance;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (verseGrip == null)
                verseGrip = FindFirstObjectByType<VerseGripController>();

            EnsureLine();
            EnsureLabel();
            ApplyStyle();

            if (insertionPoint != null) insertionPoint.SetActive(false);
            ApplyVisibility();
        }

#if UNITY_EDITOR
        // Live restyle whenever a field changes in the Inspector, in Play mode or not.
        // Deferred because touching TMP materials during OnValidate trips Unity's
        // "SendMessage cannot be called during OnValidate" warning.
        void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                ApplyStyle();
            };
        }
#endif

        void OnEnable()
        {
            if (verseGrip != null && !externalControl)
                verseGrip.ButtonDown.AddListener(OnButtonDown);
        }

        void OnDisable()
        {
            // Always remove (no-op if not added) so toggling externalControl can't leave a dangling listener.
            if (verseGrip != null)
                verseGrip.ButtonDown.RemoveListener(OnButtonDown);
        }

        // ── Input ─────────────────────────────────────────────────────────────
        void OnButtonDown(VerseGripController grip, VerseGripEventArgs args)
        {
            if (args.Button == markButton)        MarkTentative();
            else if (args.Button == toggleButton) ToggleGlobal();
        }

        // Public API (callable by SimulationStageManager when externalControl is on).
        public void MarkTentative()
        {
            if (tentativePrefab == null || needleTip == null) return;

            // Unparented → keeps the prefab's raw scale. Position = tip. Rotation = axis mapping:
            //   GO.X → tip.Z, GO.Y → -tip.X, GO.Z → -tip.Y.
            // (tentativeEulerOffset is applied inside MarkTentativeAt.)
            Quaternion mapped = Quaternion.LookRotation(-needleTip.up, -needleTip.right);
            MarkTentativeAt(needleTip.position, mapped);
        }

        // Mouse / raycast variant: places the tentative marker at an arbitrary world pose
        // (e.g. the mouse cursor hit point on the body). Applies tentativeEulerOffset on top.
        public void MarkTentativeAt(Vector3 position, Quaternion rotation)
        {
            if (tentativePrefab == null) return;

            if (_tentativeInstance != null) Destroy(_tentativeInstance);

            _tentativeInstance = Instantiate(tentativePrefab);
            _tentativeInstance.transform.SetPositionAndRotation(
                position, rotation * Quaternion.Euler(tentativeEulerOffset));

            _pendingTentative = true;
            ApplyVisibility();
        }

        // Convenience: orient the marker like the ideal insertion point when available,
        // otherwise face the surface normal.
        public void MarkTentativeAt(Vector3 position, Vector3 surfaceNormal)
        {
            Quaternion rotation;
            if (insertionPoint != null)
                rotation = insertionPoint.transform.rotation;
            else if (surfaceNormal.sqrMagnitude > 1e-10f)
                rotation = Quaternion.LookRotation(surfaceNormal.normalized);
            else
                rotation = Quaternion.identity;
            MarkTentativeAt(position, rotation);
        }

        void ToggleGlobal()
        {
            _globalOn = !_globalOn;
            if (!_globalOn) _pendingTentative = false;
            ApplyVisibility();
        }

        // Explicit show/hide of the comparison (ideal + tentative + line/distance).
        public void SetComparisonActive(bool on)
        {
            _globalOn = on;
            if (!on) _pendingTentative = false;
            ApplyVisibility();
        }

        // Last measured marked→ideal distance (cm). Valid while the comparison is visible.
        public float LastDistanceCm { get; private set; }

        // ── Visibility + line ──────────────────────────────────────────────────
        void ApplyVisibility()
        {
            if (insertionPoint != null)
                insertionPoint.SetActive(_globalOn);

            bool tentativeVisible = _globalOn || _pendingTentative;
            if (_tentativeInstance != null)
                _tentativeInstance.SetActive(tentativeVisible);

            bool bothVisible = _globalOn && tentativeVisible
                               && insertionPoint != null && _tentativeInstance != null;
            ShowLine(bothVisible);
            if (bothVisible) UpdateLine();
        }

        void Update()
        {
            if (line != null && line.enabled) UpdateLine();
        }

        void UpdateLine()
        {
            if (insertionPoint == null || _tentativeInstance == null) return;

            Vector3 a = insertionPoint.transform.position;
            Vector3 b = _tentativeInstance.transform.position;

            line.positionCount = 2;
            line.SetPosition(0, a);
            line.SetPosition(1, b);

            float distCm = Vector3.Distance(a, b) * worldToCm;
            LastDistanceCm = distCm;

            if (distanceLabel == null) return;

            distanceLabel.text = string.Format(labelFormat, distCm);
            PlaceLabel(a, b);
        }

        // Positions and orients the label. The text plane always faces the camera so it stays
        // readable in a headset; labelFacing only decides which way the glyphs run inside it.
        void PlaceLabel(Vector3 a, Vector3 b)
        {
            Vector3 mid = (a + b) * 0.5f;

            var cam = Camera.main;
            if (cam == null)
            {
                distanceLabel.transform.position = mid;
                return;
            }

            Vector3 forward = (mid - cam.transform.position).normalized;  // TMP reads along +X, faces +Z
            Vector3 dir     = b - a;
            Vector3 right;

            if (labelFacing == LabelFacing.PerpendicularToLine && dir.sqrMagnitude > 1e-10f)
            {
                // Perpendicular to the segment, inside the plane that faces the camera.
                right = Vector3.Cross(dir.normalized, forward);
                if (right.sqrMagnitude < 1e-10f) right = cam.transform.right;  // segment points at the camera
                right.Normalize();
                if (Vector3.Dot(right, cam.transform.right) < 0f) right = -right;  // never read backwards
            }
            else
            {
                right = Vector3.Cross(cam.transform.up, forward).normalized;
            }

            Vector3 up = Vector3.Cross(forward, right);
            distanceLabel.transform.SetPositionAndRotation(mid + right * labelOffset,
                                                           Quaternion.LookRotation(forward, up));
        }

        void ShowLine(bool on)
        {
            if (line != null)          line.enabled = on;
            if (distanceLabel != null) distanceLabel.gameObject.SetActive(on);
        }

        // ── Visuals ─────────────────────────────────────────────────────────────
        // Creation only. Anything cosmetic lives in ApplyStyle so it can be re-applied live.
        void EnsureLine()
        {
            if (line == null)
            {
                var go = new GameObject("EntryComparatorLine");
                go.transform.SetParent(transform, false);
                line = go.AddComponent<LineRenderer>();
                line.material = new Material(Shader.Find("Sprites/Default"));
            }
            line.useWorldSpace = true;
            line.enabled       = false;
        }

        void EnsureLabel()
        {
            if (distanceLabel == null)
            {
                var go = new GameObject("EntryComparatorDistance");
                go.transform.SetParent(transform, false);
                distanceLabel = go.AddComponent<TextMeshPro>();
            }
            distanceLabel.gameObject.SetActive(false);
        }

        void ApplyStyle()
        {
            if (!applyStyleFromScript) return;

            if (line != null)
            {
                line.startWidth     = line.endWidth = lineWidth;
                line.colorGradient  = lineGradient;
                line.numCapVertices = 4;
                line.alignment      = LineAlignment.View;   // ribbon always faces the camera
            }

            if (distanceLabel != null)
            {
                distanceLabel.fontSize  = fontSize;
                distanceLabel.color     = labelColor;
                distanceLabel.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                distanceLabel.alignment = labelAlignment;
                distanceLabel.rectTransform.sizeDelta = labelBoxSize;

                // Both setters instance the font material behind the scenes; TMP caches it.
                distanceLabel.outlineColor = outlineColor;
                distanceLabel.outlineWidth = outlineWidth;
            }
        }
    }
}
