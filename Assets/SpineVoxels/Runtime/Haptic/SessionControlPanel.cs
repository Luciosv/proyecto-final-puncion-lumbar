// On-screen control panel (right side) for the puncture simulator.
//
// Real-time toggles (applied the instant you flip them):
//   • Huesos            → HybridNeedleController.boneLowestPriority (off = bones de-prioritised /
//                          "apagados"), plus optional bone visuals hidden.
//   • Punto inserción   → shows/hides the ideal insertion-point marker.
//   • Ángulo guía       → shows/hides the angle guide (GameObject and/or its Behaviour, e.g.
//                          NeedleAngleIndicator).
//
// Plus: practitioner name + free-text notes, and a "Guardar sesión" button that exports the
// recorded session via PunctureSessionLogger.ExportSession → a .csv (data only) and a .txt (an
// info paragraph with name/time/notes then the data table).
//
// Uses IMGUI so it needs no Canvas wiring. It draws on the desktop screen (not inside a VR headset).

using UnityEngine;
using UnityEngine.InputSystem;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Session Control Panel")]
    public class SessionControlPanel : MonoBehaviour
    {
        [Header("References")]
        public PunctureSessionLogger  logger;
        public HybridNeedleController needleController;

        [Header("Toggle targets")]
        [Tooltip("Optional bone visuals to hide when 'Huesos' is turned off (the haptic bone is handled " +
                 "via HybridNeedleController.boneLowestPriority).")]
        public GameObject[] boneVisuals;
        [Tooltip("Ideal insertion-point marker to show/hide.")]
        public GameObject insertionPoint;
        [Tooltip("Angle-guide GameObject to show/hide (e.g. the guide cone / needle-angle visual).")]
        public GameObject angleGuideObject;
        [Tooltip("Angle-guide Behaviour to enable/disable (e.g. NeedleAngleIndicator).")]
        public Behaviour angleGuideBehaviour;

        [Header("Panel")]
        [Tooltip("Panel width in pixels.")]
        public float panelWidth = 320f;
        [Tooltip("Margin from the screen edges in pixels.")]
        public float margin = 12f;
        [Tooltip("Key that shows/hides the whole panel (new Input System).")]
        public Key toggleKey = Key.Tab;
        public bool visible = true;

        // ── UI state ──────────────────────────────────────────────────────────
        bool   _bonesOn = true;
        bool   _insertionOn = true;
        bool   _angleOn = true;
        string _practitioner = "";
        string _notes = "";
        string _lastSaved = "";
        Vector2 _notesScroll;

        void OnEnable()
        {
            // Seed the toggle states from the current scene values.
            if (needleController != null) _bonesOn = !needleController.boneLowestPriority;
            if (insertionPoint   != null) _insertionOn = insertionPoint.activeSelf;
            if (angleGuideObject != null) _angleOn = angleGuideObject.activeSelf;
            else if (angleGuideBehaviour != null) _angleOn = angleGuideBehaviour.enabled;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && toggleKey != Key.None && kb[toggleKey].wasPressedThisFrame)
                visible = !visible;
        }

        void OnGUI()
        {
            if (!visible) return;

            float x = Screen.width - panelWidth - margin;
            var area = new Rect(x, margin, panelWidth, Screen.height - margin * 2);
            GUILayout.BeginArea(area, GUI.skin.box);

            GUILayout.Label("<b>Panel de sesión</b>", RichLabel());

            // ── Real-time toggles ──
            GUILayout.Space(4);
            GUILayout.Label("Visualización (tiempo real)");

            bool bones = GUILayout.Toggle(_bonesOn, "  Huesos");
            if (bones != _bonesOn) { _bonesOn = bones; ApplyBones(); }

            bool ins = GUILayout.Toggle(_insertionOn, "  Punto de inserción");
            if (ins != _insertionOn) { _insertionOn = ins; ApplyInsertion(); }

            bool ang = GUILayout.Toggle(_angleOn, "  Ángulo guía");
            if (ang != _angleOn) { _angleOn = ang; ApplyAngleGuide(); }

            // ── Study info ──
            GUILayout.Space(8);
            GUILayout.Label("Practicante");
            _practitioner = GUILayout.TextField(_practitioner ?? "");

            GUILayout.Label("Notas del estudio");
            _notesScroll = GUILayout.BeginScrollView(_notesScroll, GUILayout.Height(90));
            _notes = GUILayout.TextArea(_notes ?? "", GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            // ── Session status + save ──
            GUILayout.Space(8);
            if (logger != null)
            {
                int n = logger.LastSession != null ? logger.LastSession.Count : 0;
                GUILayout.Label(logger.InSession
                    ? $"Grabando… ({n} muestras)"
                    : (n > 0 ? $"Sesión lista ({n} muestras)" : "Sin datos aún"));
            }
            else
            {
                GUILayout.Label("<color=#e08080>Asigná el PunctureSessionLogger</color>", RichLabel());
            }

            GUI.enabled = logger != null;
            if (GUILayout.Button("Guardar sesión (CSV + TXT)", GUILayout.Height(32)))
                SaveSession();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_lastSaved))
            {
                GUILayout.Space(4);
                GUILayout.Label(_lastSaved, WrapLabel());
                if (GUILayout.Button("Abrir carpeta"))
                    Application.OpenURL("file://" + Application.persistentDataPath);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"<size=10>{toggleKey} para ocultar el panel</size>", RichLabel());

            GUILayout.EndArea();
        }

        // ── Toggle actions ────────────────────────────────────────────────────
        void ApplyBones()
        {
            if (needleController != null) needleController.boneLowestPriority = !_bonesOn;
            if (boneVisuals != null)
                foreach (var go in boneVisuals)
                    if (go != null) go.SetActive(_bonesOn);
        }

        void ApplyInsertion()
        {
            if (insertionPoint != null) insertionPoint.SetActive(_insertionOn);
        }

        void ApplyAngleGuide()
        {
            if (angleGuideObject    != null) angleGuideObject.SetActive(_angleOn);
            if (angleGuideBehaviour != null) angleGuideBehaviour.enabled = _angleOn;
        }

        // ── Save ──────────────────────────────────────────────────────────────
        void SaveSession()
        {
            if (logger == null) return;
            if (logger.ExportSession(_practitioner, _notes, out string csv, out string txt))
                _lastSaved = $"Guardado:\n{System.IO.Path.GetFileName(csv)}\n{System.IO.Path.GetFileName(txt)}";
            else
                _lastSaved = "Nada para guardar todavía.";
        }

        // ── Small label styles ────────────────────────────────────────────────
        static GUIStyle _rich, _wrap;
        static GUIStyle RichLabel()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true };
            return _rich;
        }
        static GUIStyle WrapLabel()
        {
            if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _wrap;
        }
    }
}
