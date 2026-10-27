// Records a puncture session (puncture stage) from needle ENTRY to EXIT:
//   • At entry: validates the entry point (proximity to the ideal insertionPoint) and the entry
//     angle (vs the ideal axis). If off-target it only logs Debug.LogWarning("punción mal hecha…").
//   • From entry to exit: each FixedUpdate it records (time, axial velocity, axial force, tissue).
//   • At exit: logs a summary and (optionally) writes a CSV to Application.persistentDataPath.
//
// "Inside the body" is tracked by penetration past the entry surface (not by tissue != background),
// so internal voids (e.g. the dura↔cord gap) do NOT prematurely end the session.
//
// Velocity/force come from HybridNeedleController.TryGetTelemetry (published by the haptic thread).

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Puncture Session Logger")]
    public class PunctureSessionLogger : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional: only record while this manager is in the Puncture stage.")]
        public SimulationStageManager stageManager;
        public HybridNeedleController needleController;
        public Transform             needleTip;
        public Transform             needleBody;   // local +Y = insertion direction
        public PunctureAccessStack   punctureAccess;
        public NrrdTissueVolume      nrrdVolume;

        [Header("Ideal entry validation")]
        public GameObject insertionPoint;          // ideal entry point
        [Tooltip("Transform whose local +X is the ideal insertion axis (same convention as " +
                 "NeedleAngleIndicator.gizmoOrigin). If null, world +Z is used.")]
        public Transform  idealAxisRef;
        [Tooltip("Max distance from the ideal point to count as a good entry (mm).")]
        public float positionToleranceMm = 8f;
        [Tooltip("Max angle from the ideal axis to count as a good entry (degrees).")]
        public float angleToleranceDeg = 15f;
        [Tooltip("Ideal entry angle in degrees (90 = needle perpendicular). The recorded/report angle " +
                 "is measured so a perfectly-aligned needle reads this value, dropping as it tilts away.")]
        public float idealEntryAngleDeg = 90f;

        [Header("Session")]
        [Tooltip("Withdraw this far back behind the entry surface to end the session (mm).")]
        public float exitMarginMm = 2f;
        [Tooltip("Auto-write a CSV on needle exit. Off by default — saving is normally done from the " +
                 "on-screen panel button (which also writes the .txt report with name + notes).")]
        public bool writeCsvOnExit = false;

        // ── Recorded sample ──────────────────────────────────────────────────
        public struct Sample
        {
            public float   t;           // s since entry
            public Vector3 pos;         // world position of the needle tip
            public float   velocity;    // mm/s, axial (+ inward / − outward)
            public float   force;       // N, axial
            public float   angleDeg;    // needle vs ideal axis (0 = perfectly aligned)
            public string  sentido;     // "insertando" / "retirando" / "quieto"
            public string  tissue;
        }

        public IReadOnlyList<Sample> LastSession => _samples;
        public bool InSession => _inSession;

        readonly List<Sample> _samples = new();
        bool    _inSession;
        Vector3 _entryPos;
        Vector3 _entryAxis;
        float   _startTime;

        // Entry evaluation, kept for the exported report.
        float  _entryAngleDeg;
        bool   _entryOk;
        string _entryWhy = "";

        float UPerMm => nrrdVolume != null ? Mathf.Max(nrrdVolume.unityUnitsPerMm, 1e-6f) : 0.003f;

        void FixedUpdate()
        {
            if (needleTip == null || needleController == null) return;
            if (stageManager != null && stageManager.Current != SimulationStageManager.Stage.Puncture)
            {
                if (_inSession) Finalize();
                return;
            }

            Vector3 tip = needleTip.position;

            if (!_inSession)
            {
                if (TipInTissue(tip, out _)) StartSession(tip);
                return;
            }

            // In session: end it once the tip is withdrawn back behind the entry surface.
            float penMm = Vector3.Dot(tip - _entryPos, _entryAxis) / UPerMm;
            if (penMm < -exitMarginMm) { Finalize(); return; }

            RecordSample(tip);
        }

        void OnDisable() { if (_inSession) Finalize(); }

        // ── Session boundaries ───────────────────────────────────────────────
        void StartSession(Vector3 tip)
        {
            _entryPos  = tip;
            _entryAxis = InwardAxis();
            _samples.Clear();
            _startTime = Time.time;
            _inSession = true;

            ValidateEntry(tip);
            RecordSample(tip);
        }

        void Finalize()
        {
            _inSession = false;
            if (_samples.Count < 2) return;

            float dur = _samples[_samples.Count - 1].t;
            float maxF = 0f;
            foreach (var s in _samples) if (s.force > maxF) maxF = s.force;
            Debug.Log($"[PunctureSessionLogger] Session done — {_samples.Count} samples, " +
                      $"{dur:F2}s, peak force {maxF:F2}N.");

            if (writeCsvOnExit) WriteCsv();
        }

        // ── Entry validation ─────────────────────────────────────────────────
        void ValidateEntry(Vector3 tip)
        {
            bool   ok = true;
            string why = "";

            if (insertionPoint != null)
            {
                float distMm = Vector3.Distance(tip, insertionPoint.transform.position) / UPerMm;
                if (distMm > positionToleranceMm)
                {
                    ok = false;
                    why += $"punto a {distMm:F1}mm del ideal (máx {positionToleranceMm}); ";
                }
            }

            float dev = SampleAngleDeg();      // deviation from the ideal axis (0 = aligned)
            if (dev > angleToleranceDeg)
            {
                ok = false;
                why += $"ángulo {dev:F1}° del ideal (máx {angleToleranceDeg}); ";
            }

            _entryAngleDeg = ReportedAngleDeg();  // 90-based (idealEntryAngleDeg at perfect alignment)
            _entryOk       = ok;
            _entryWhy      = ok ? "" : why;

            if (!ok) Debug.LogWarning($"[PunctureSessionLogger] punción mal hecha: {why}");
            else     Debug.Log("[PunctureSessionLogger] Entrada correcta (punto + ángulo dentro de tolerancia).");
        }

        // Deviation (deg) between the needle axis and the ideal insertion axis (0 = aligned).
        float SampleAngleDeg()
        {
            Vector3 ideal  = idealAxisRef != null ? idealAxisRef.right : Vector3.forward;
            Vector3 needle = needleBody   != null ? needleBody.up      : Vector3.forward;
            float   ang    = Vector3.Angle(needle, ideal);
            return Mathf.Min(ang, 180f - ang);
        }

        // Reported entry angle: idealEntryAngleDeg (e.g. 90) when perfectly aligned, decreasing as
        // the needle tilts away from the ideal axis.
        float ReportedAngleDeg() => idealEntryAngleDeg - SampleAngleDeg();

        static string Sentido(float velMmps) =>
            velMmps >  0.5f ? "insertando" :
            velMmps < -0.5f ? "retirando"  : "quieto";

        // ── Sampling ─────────────────────────────────────────────────────────
        void RecordSample(Vector3 tip)
        {
            needleController.TryGetTelemetry(out _, out float force, out float vel, out _);
            TipInTissue(tip, out string tissue);
            _samples.Add(new Sample
            {
                t        = Time.time - _startTime,
                pos      = tip,
                velocity = vel,
                force    = force,
                angleDeg = ReportedAngleDeg(),
                sentido  = Sentido(vel),
                tissue   = tissue,
            });
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        // "Inside" = in a PunctureAccess layer or a non-background NRRD voxel. Also returns a label.
        bool TipInTissue(Vector3 tip, out string tissue)
        {
            var s = punctureAccess != null ? punctureAccess.FindActiveSnapshot(tip) : null;
            if (s != null)
            {
                float z = Vector3.Dot(tip - s.origin, s.axis) / s.visualScale;
                if (z >= 0f && z < s.totalDepth)
                {
                    for (int i = 0; i < s.layers.Length; i++)
                        if (z >= s.layers[i].dStartM && z < s.layers[i].dEndM)
                        { tissue = s.layers[i].name; return true; }
                    tissue = "ligament-gap";
                    return true;
                }
            }
            if (nrrdVolume != null && nrrdVolume.IsReady)
            {
                var t = nrrdVolume.SampleTissue(tip);
                tissue = t.ToString();
                return t != VoxelTissueType.Background;
            }
            tissue = "none";
            return false;
        }

        Vector3 InwardAxis()
        {
            Vector3 d = needleBody != null ? needleBody.up : Vector3.forward;
            if (d.sqrMagnitude < 1e-12f) return Vector3.forward;
            d.Normalize();
            if (Vector3.Dot(d, Vector3.forward) < 0f) d = -d;
            return d;
        }

        void WriteCsv()
        {
            ExportSession("", "", out _, out _);
        }

        // ── Export (button-driven) ───────────────────────────────────────────
        // Writes two files under Application.persistentDataPath:
        //   • .csv — pure data (no notes/name), for analysis.
        //   • .txt — a general-info paragraph (practitioner, timestamp, duration, notes, …) followed
        //            by the same data table, as a human-readable report.
        // Returns false (and empty paths) if there is nothing recorded yet.
        public bool ExportSession(string practitioner, string notes,
                                  out string csvPath, out string txtPath)
        {
            csvPath = txtPath = "";
            if (_samples.Count < 1)
            {
                Debug.LogWarning("[PunctureSessionLogger] No hay muestras para exportar todavía.");
                return false;
            }

            float dur  = _samples[_samples.Count - 1].t;
            float maxF = 0f, maxV = 0f;
            foreach (var s in _samples)
            {
                if (s.force > maxF) maxF = s.force;
                if (Mathf.Abs(s.velocity) > maxV) maxV = Mathf.Abs(s.velocity);
            }

            var  ci    = CultureInfo.InvariantCulture;
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir   = Application.persistentDataPath;

            // Column header + rows (shared by both files).
            const string header = "time_s,pos_x,pos_y,pos_z,tissue,force_N,velocity_mmps,angle_deg,sentido";
            var rows = new StringBuilder();
            foreach (var s in _samples)
                rows.AppendLine(string.Format(ci,
                    "{0:F4},{1:F4},{2:F4},{3:F4},{4},{5:F4},{6:F3},{7:F2},{8}",
                    s.t, s.pos.x, s.pos.y, s.pos.z, s.tissue, s.force, s.velocity, s.angleDeg, s.sentido));

            // ── CSV: data only, no annotations ──
            csvPath = Path.Combine(dir, $"puncture_{stamp}.csv");
            bool okCsv = TryWrite(csvPath, header + "\n" + rows);

            // ── TXT: general-info paragraph, then the data table ──
            var txt = new StringBuilder();
            txt.AppendLine("INFORME DE SESIÓN — Punción Lumbar");
            txt.AppendLine($"Practicante : {(string.IsNullOrWhiteSpace(practitioner) ? "(sin nombre)" : practitioner.Trim())}");
            txt.AppendLine($"Fecha/hora  : {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            txt.AppendLine(string.Format(ci, "Duración    : {0:F2} s   ({1} muestras)", dur, _samples.Count));
            txt.AppendLine(string.Format(ci, "Fuerza pico : {0:F2} N    Vel. máx: {1:F1} mm/s", maxF, maxV));
            txt.AppendLine(string.Format(ci, "Ángulo de entrada: {0:F1}°   Entrada: {1}",
                _entryAngleDeg, _entryOk ? "correcta" : $"observada ({_entryWhy.Trim()})"));
            txt.AppendLine($"Notas       : {(string.IsNullOrWhiteSpace(notes) ? "(sin notas)" : notes.Trim())}");
            txt.AppendLine();
            txt.AppendLine(header);
            txt.Append(rows);
            txtPath = Path.Combine(dir, $"puncture_{stamp}.txt");
            bool okTxt = TryWrite(txtPath, txt.ToString());

            if (okCsv || okTxt)
                Debug.Log($"[PunctureSessionLogger] Exportado:\n  CSV → {csvPath}\n  TXT → {txtPath}");
            return okCsv || okTxt;
        }

        static bool TryWrite(string path, string contents)
        {
            try { File.WriteAllText(path, contents); return true; }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PunctureSessionLogger] Falló escritura {path}: {e.Message}");
                return false;
            }
        }
    }
}
