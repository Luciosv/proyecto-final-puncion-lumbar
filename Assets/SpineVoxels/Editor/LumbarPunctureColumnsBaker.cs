#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using SpineVoxels.Config;
using SpineVoxels.Core;
using SpineVoxels.Runtime;

namespace SpineVoxels.Editor
{
    // Builds the lumbar-puncture tissue stack as simple stretched CUBES (no voxels),
    // like PuncionSinHaptico's TissueStack: the 6 layers in needle order from the
    // spinous tips to the cord, thickness ∝ z_width_mm, split into up to 3 vertical
    // bands so the stack follows the spinal curve.
    //
    // Output: a "PunctureAccess" prefab aligned with SpineModel at world origin.
    // Each cube has a BoxCollider and a PunctureLayer (order + thickness).
    // Menu: Tools > SpineVoxels > Build Puncture Columns
    public static class LumbarPunctureColumnsBaker
    {
        const string OutputRoot   = "Assets/SpineVoxels/Generated";
        const int    Segments     = 3;     // vertical bands per tissue (max columns)
        const float  SideWidthMm  = 12f;   // left-right width of each column
        const float  Alpha        = 0.6f;  // material transparency

        // Colours match PuncionSinHaptico's TissueStackV2.
        static readonly Dictionary<PunctureStackLayout.Tissue, (string name, Color color)> Info =
            new()
            {
                [PunctureStackLayout.Tissue.Supraspinous] = ("supraspinous_ligament", new Color(0.565f, 0.933f, 0.565f)),
                [PunctureStackLayout.Tissue.Interspinous] = ("interspinous_ligament", new Color(0.400f, 0.800f, 0.400f)),
                [PunctureStackLayout.Tissue.Flavum]       = ("ligamentum_flavum",     new Color(0.678f, 1.000f, 0.184f)),
                [PunctureStackLayout.Tissue.Epidural]     = ("epidural_space",         new Color(0.529f, 0.808f, 0.922f)),
                [PunctureStackLayout.Tissue.Dura]         = ("dura_mater",             new Color(0.867f, 0.627f, 0.867f)),
                [PunctureStackLayout.Tissue.Subarachnoid] = ("subarachnoid_space",     new Color(0.698f, 0.933f, 0.933f)),
            };

        [MenuItem("Tools/SpineVoxels/Build Puncture Columns")]
        static void Build()
        {
            var config = FindConfig();
            if (config == null) return;

            EditorUtility.DisplayProgressBar("SpineVoxels — Tissue Stack", "Reading NRRD…", 0f);
            try   { RunBuild(config); }
            catch (System.Exception ex) { Debug.LogError($"[SpineVoxels] Stack build failed: {ex}"); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static void RunBuild(BuildConfig config)
        {
            Progress(0.05f, "Reading NRRD…");
            var (header, data) = NrrdReader.Read(config.nrrdPath);

            Progress(0.25f, "Building label volume…");
            var native = LabelVolume.FromNrrd(header, data);

            Progress(0.45f, "Resampling…");
            var resampled = VolumeResampler.Resample(native, config.voxelSizeMm, config.resampleRule);

            // Same transform as SpineModelBuilder so the stack aligns at world origin.
            Vector3 gridOffset = config.centerModel
                ? new Vector3(resampled.SizeX * 0.5f, resampled.SizeY * 0.5f, resampled.SizeZ * 0.5f)
                : Vector3.zero;
            var xform = new CoordinateTransform(config.voxelSizeMm, config.unityUnitsPerMm, gridOffset);

            Progress(0.65f, "Placing tissue stack…");
            var columns = PunctureStackLayout.Build(resampled, xform, config.voxelSizeMm,
                                                    Segments, SideWidthMm);

            EnsureFolder(OutputRoot);

            var existingRoot = GameObject.Find("PunctureAccess");
            if (existingRoot != null) Object.DestroyImmediate(existingRoot);
            var root = new GameObject("PunctureAccess");

            // One shared material per tissue (reused across vertical bands).
            var materials = new Dictionary<PunctureStackLayout.Tissue, Material>();

            Progress(0.8f, "Creating cubes…");
            foreach (var col in columns)
            {
                var (name, color) = Info[col.tissue];

                if (!materials.TryGetValue(col.tissue, out var mat))
                {
                    mat = MaterialFactory.Create(new StructureInfo
                    {
                        labelId        = col.order,
                        name           = name,
                        color          = new Color(color.r, color.g, color.b, Alpha),
                        defaultVisible = true,
                        transparent    = true,
                    });
                    AssetDatabase.CreateAsset(mat, $"{OutputRoot}/{name}.mat");
                    materials[col.tissue] = mat;
                }

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"{name}_s{col.segment}";
                go.transform.SetParent(root.transform, false);
                go.transform.position   = col.unityCenter;
                go.transform.localScale  = col.unitySize;
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;

                var layer = go.AddComponent<PunctureLayer>();
                layer.order       = col.order;
                layer.layerName   = name;
                layer.thicknessMm = col.thicknessMm;
            }

            AssetDatabase.SaveAssets();

            string prefabPath = $"{OutputRoot}/PunctureAccess.prefab";
            PrefabUtility.SaveAsPrefabAssetAndConnect(root, prefabPath, InteractionMode.AutomatedAction);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SpineVoxels] Tissue stack built ({columns.Count} cubes) → {prefabPath}. " +
                      "Place PunctureAccess at (0,0,0) alongside SpineModel.");
        }

        // ── helpers ─────────────────────────────────────────────────────────

        static void Progress(float p, string msg) =>
            EditorUtility.DisplayProgressBar("SpineVoxels — Tissue Stack", msg, p);

        static BuildConfig FindConfig()
        {
            if (Selection.activeObject is BuildConfig sel) return sel;
            var guids = AssetDatabase.FindAssets("t:BuildConfig");
            if (guids.Length == 0)
            {
                EditorUtility.DisplayDialog("SpineVoxels", "No BuildConfig found.", "OK");
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<BuildConfig>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
            string child  = Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, child);
        }
    }
}
#endif
