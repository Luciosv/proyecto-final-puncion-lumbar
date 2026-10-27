// Discovers PunctureLayer cubes from PunctureAccess root GameObjects, groups them by
// vertical segment, and builds one StackSnapshotSpine per group.
//
// Each PunctureAccess root (e.g. PunctureAccess1, PunctureAccess2) is a full tissue stack
// for one interspace. Within a root, cubes are named "tissueName_s{seg}" (e.g. "_s0", "_s1").
// Each segment group → one StackSnapshotSpine with 6 layers (supra→subarachnoid).
//
// Call Initialize(fits) from HybridNeedleController.Start() after loading the JSON.
// FindActiveSnapshot(cursor) is called from the haptic thread — read-only after Initialize().

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SpineVoxels.Runtime; // PunctureLayer

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Puncture Access Stack")]
    public class PunctureAccessStack : MonoBehaviour
    {
        [Header("PunctureAccess roots — assign in Inspector")]
        public Transform punctureAccess1;
        public Transform punctureAccess2;

        [Header("Scale (must match BuildConfig: 1000 * unityUnitsPerMm)")]
        [Tooltip("e.g. unityUnitsPerMm=0.003 → visualScale=3.0")]
        public float visualScale = 3.0f;

        // Populated by Initialize() — read-only afterwards (haptic-thread safe).
        StackSnapshotSpine[] _snapshots;

        // ── Layer name → type mapping (matches LumbarPunctureColumnsBaker) ───
        static readonly Dictionary<string, LayerTypeSpine> s_NameToType = new()
        {
            { "supraspinous_ligament", LayerTypeSpine.SupraspinousLig  },
            { "interspinous_ligament", LayerTypeSpine.InterspinousLig  },
            { "ligamentum_flavum",     LayerTypeSpine.LigamentumFlavum },
            { "epidural_space",        LayerTypeSpine.EpiduralSpace    },
            { "dura_mater",            LayerTypeSpine.Dura             },
            { "subarachnoid_space",    LayerTypeSpine.Subarachnoid     },
        };

        // ── Initialization (Unity thread, once at Start) ──────────────────────

        public void Initialize(Dictionary<LayerTypeSpine, ForceJsonLoaderSpine.LayerFitSpine> fits)
        {
            var snapshots = new List<StackSnapshotSpine>();
            foreach (var root in new[] { punctureAccess1, punctureAccess2 })
            {
                if (root == null) continue;
                BuildFromRoot(root, fits, snapshots);
            }
            _snapshots = snapshots.ToArray();
            Debug.Log($"[PunctureAccessStack] Built {_snapshots.Length} snapshot(s) " +
                      $"(visualScale={visualScale}).");
        }

        // ── Public API (haptic-thread safe after Initialize) ─────────────────

        // Returns the first snapshot whose bounds contain cursorPos, or null.
        public StackSnapshotSpine FindActiveSnapshot(Vector3 cursorPos)
        {
            if (_snapshots == null) return null;
            foreach (var s in _snapshots)
                if (s.bounds.Contains(cursorPos)) return s;
            return null;
        }

        // Returns the nearest snapshot intersected by the ray (origin + t*dir, t >= 0), or null.
        // Used by HapticForceProfiler when the cursor has not yet entered any PA zone.
        public StackSnapshotSpine FindSnapshotAhead(Vector3 origin, Vector3 direction)
        {
            if (_snapshots == null) return null;
            var ray = new Ray(origin, direction.normalized);
            float bestDist = float.MaxValue;
            StackSnapshotSpine best = null;
            foreach (var snap in _snapshots)
            {
                if (snap.bounds.IntersectRay(ray, out float dist) && dist >= 0f && dist < bestDist)
                {
                    bestDist = dist;
                    best = snap;
                }
            }
            return best;
        }

        // ── Build helpers ─────────────────────────────────────────────────────

        void BuildFromRoot(Transform root,
                           Dictionary<LayerTypeSpine, ForceJsonLoaderSpine.LayerFitSpine> fits,
                           List<StackSnapshotSpine> snapshots)
        {
            // Collect all (PunctureLayer + BoxCollider) pairs
            var cubes = new List<(PunctureLayer pl, BoxCollider col)>();
            foreach (var pl in root.GetComponentsInChildren<PunctureLayer>(true))
            {
                var col = pl.GetComponent<BoxCollider>();
                if (col != null) cubes.Add((pl, col));
            }
            if (cubes.Count == 0) return;

            // Group by segment index extracted from the GO name (_s0, _s1, _s2)
            var bySegment = new Dictionary<int, List<(PunctureLayer, BoxCollider)>>();
            foreach (var c in cubes)
            {
                int seg = ExtractSegment(c.pl.gameObject.name);
                if (!bySegment.ContainsKey(seg))
                    bySegment[seg] = new List<(PunctureLayer, BoxCollider)>();
                bySegment[seg].Add(c);
            }

            foreach (var kv in bySegment.OrderBy(x => x.Key))
            {
                var snap = BuildSnapshot(kv.Value, fits);
                if (snap != null) snapshots.Add(snap);
            }
        }

        StackSnapshotSpine BuildSnapshot(List<(PunctureLayer pl, BoxCollider col)> group,
                                          Dictionary<LayerTypeSpine, ForceJsonLoaderSpine.LayerFitSpine> fits)
        {
            // Sort by Z center: posterior (small Z) → anterior (large Z) = insertion order.
            var sorted = group.OrderBy(x => x.col.bounds.center.z).ToList();

            // Insertion axis = Unity +Z (anterior direction, same as grid +Y mapped by CoordinateTransform).
            var axis = Vector3.forward;

            // Stack origin = min-Z face of the first (most posterior) cube.
            float originZ = sorted[0].col.bounds.min.z;
            var origin = new Vector3(sorted[0].col.bounds.center.x,
                                     sorted[0].col.bounds.center.y,
                                     originZ);

            var layerList = new List<LayerDataSpine>();
            foreach (var (pl, col) in sorted)
            {
                // Resolve the tissue name prefix (strip trailing "_s{digit}")
                string tissueName = StripSegmentSuffix(pl.gameObject.name);
                if (!s_NameToType.TryGetValue(tissueName, out var ltype))
                {
                    // Try layerName field as fallback
                    if (!s_NameToType.TryGetValue(pl.layerName, out ltype))
                    {
                        Debug.LogWarning($"[PunctureAccessStack] Unknown layer name '{tissueName}' — skipped.");
                        continue;
                    }
                }
                if (!fits.TryGetValue(ltype, out var fit))
                {
                    Debug.LogWarning($"[PunctureAccessStack] No JSON curve for {ltype} — skipped.");
                    continue;
                }

                float dStartWorld = col.bounds.min.z - originZ;
                float dEndWorld   = col.bounds.max.z - originZ;
                float dStartM     = dStartWorld / visualScale;
                float dEndM       = dEndWorld   / visualScale;
                float widthM      = Mathf.Max(dEndM - dStartM, 1e-6f);
                float dzScale     = fit.jsonWidthMm / (widthM * 1000f);

                layerList.Add(new LayerDataSpine
                {
                    type        = ltype,
                    name        = pl.layerName,
                    dStartM     = dStartM,
                    dEndM       = dEndM,
                    widthM      = widthM,
                    jsonWidthMm = fit.jsonWidthMm,
                    dzScale     = dzScale,
                    curve       = fit.curve,
                    bandColor   = Color.white,
                });
            }

            if (layerList.Count == 0) return null;

            // Combined world-space AABB of all cubes in this segment group.
            Bounds bounds = sorted[0].col.bounds;
            for (int i = 1; i < sorted.Count; i++)
                bounds.Encapsulate(sorted[i].col.bounds);

            return new StackSnapshotSpine
            {
                layers      = layerList.ToArray(),
                axis        = axis,
                origin      = origin,
                visualScale = visualScale,
                totalDepth  = layerList[layerList.Count - 1].dEndM,
                bounds      = bounds,
            };
        }

        // ── Name helpers ─────────────────────────────────────────────────────

        // "supraspinous_ligament_s0" → segment 0
        static int ExtractSegment(string name)
        {
            for (int i = name.Length - 2; i >= 0; i--)
            {
                if (name[i] == '_' && name[i + 1] == 's' &&
                    i + 2 < name.Length && char.IsDigit(name[i + 2]))
                {
                    if (int.TryParse(name.Substring(i + 2), out int seg))
                        return seg;
                }
            }
            return 0;
        }

        // "supraspinous_ligament_s0" → "supraspinous_ligament"
        static string StripSegmentSuffix(string name)
        {
            for (int i = name.Length - 2; i >= 0; i--)
            {
                if (name[i] == '_' && name[i + 1] == 's' &&
                    i + 2 < name.Length && char.IsDigit(name[i + 2]))
                    return name.Substring(0, i);
            }
            return name;
        }
    }
}
