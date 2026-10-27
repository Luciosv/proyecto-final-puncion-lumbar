// Dedicated Inspector control for the "punción lograda" feature of HapticForceProfiler.
//
// The profiler exposes enableSuccessDetection + holdSeconds directly, but they sit among many
// other fields. This tiny component surfaces JUST those two, and pushes them to the profiler
// whenever they change (Inspector edit, play mode, or via the runtime setters below).
//
// SETUP: add to the same GameObject as the HapticForceProfiler (it auto-wires on Reset), or drag
// the profiler reference in manually. Then use `Success Detection Enabled` and `Hold Seconds` here.

using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Puncture Success Control")]
    public class PunctureSuccessControl : MonoBehaviour
    {
        [Tooltip("The Haptic Force Profiler that owns the puncture-success detection. " +
                 "Auto-filled with the one on this GameObject when the component is added.")]
        public HapticForceProfiler profiler;

        [Tooltip("Enable/disable the 'punción lograda' detection (subarachnoid hold → banner + CSF drops).")]
        public bool successDetectionEnabled = true;

        [Tooltip("Seconds the needle tip must stay in the subarachnoid space (cord/bone-free) to succeed.")]
        [Min(0.1f)] public float holdSeconds = 1.5f;

        // Auto-wire the profiler on the same GameObject when the component is first added.
        void Reset() => profiler = GetComponent<HapticForceProfiler>();

        void OnEnable()   => Apply();
        void OnValidate() => Apply(); // live-apply while editing in the Inspector (edit or play mode)

        // Push the current settings to the profiler. Safe to call any time.
        public void Apply()
        {
            if (profiler == null) return;
            profiler.enableSuccessDetection = successDetectionEnabled;
            profiler.holdSeconds            = Mathf.Max(0.1f, holdSeconds);
        }

        // Runtime setters — wire these to a UI Toggle / Slider if you want in-app controls.
        public void SetEnabled(bool on)     { successDetectionEnabled = on; Apply(); }
        public void SetHoldSeconds(float s) { holdSeconds = s;              Apply(); }
        public void ToggleEnabled()         { SetEnabled(!successDetectionEnabled); }
    }
}
