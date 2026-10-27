using UnityEditor;
using UnityEngine;
using SpineVoxels.Runtime.Haptic;

namespace SpineVoxels.Editor
{
    // Shows the computed Colgate damping minimum (a C# property, so it never appears in the
    // default Inspector) and warns when the target's damping is below the stable threshold.
    [CustomEditor(typeof(HapticPalpationTarget))]
    public class HapticPalpationTargetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);

            var t = (HapticPalpationTarget)target;
            float bMin        = t.ColgateDampingMin;
            float recommended = bMin * 1.25f; // 25% margin over the minimum

            EditorGUILayout.LabelField("Colgate stability", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"b_min = {bMin:F4}   |   recommended (×1.25) = {recommended:F4}");
            EditorGUILayout.LabelField($"[K = {t.stiffness} N/m   T = 1/{t.hapticLoopHz} s]",
                                       EditorStyles.miniLabel);

            if (t.damping < bMin)
                EditorGUILayout.HelpBox(
                    $"damping ({t.damping:F3}) is BELOW b_min ({bMin:F4}) — the contact may buzz/oscillate. " +
                    $"Raise damping to at least {recommended:F4}.",
                    MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    $"damping ({t.damping:F3}) ≥ b_min — stable.",
                    MessageType.Info);

            if (GUILayout.Button($"Set damping = recommended ({recommended:F4})"))
            {
                Undo.RecordObject(t, "Set Colgate damping");
                t.damping = recommended;
                EditorUtility.SetDirty(t);
            }
        }
    }
}
