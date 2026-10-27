#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using SpineVoxels.Config;
using SpineVoxels.Core;

namespace SpineVoxels.Editor
{
    public static class SpineVoxelBaker
    {
        const string OutputRoot = "Assets/SpineVoxels/Generated";

        [MenuItem("Tools/SpineVoxels/Build From Config")]
        static void Build()
        {
            var config = ResolveConfig();
            if (config == null) return;

            if (!ValidateConfig(config)) return;

            EditorUtility.DisplayProgressBar("SpineVoxels", "Starting…", 0f);
            System.Collections.Generic.List<BuiltStructure> structures = null;
            try
            {
                structures = SpineModelBuilder.Build(config, (p, msg) =>
                    EditorUtility.DisplayProgressBar("SpineVoxels — Building voxel model", msg, p));
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[SpineVoxels] Build failed: {ex}");
                EditorUtility.DisplayDialog("SpineVoxels — Error", ex.Message, "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.DisplayProgressBar("SpineVoxels", "Saving assets…", 0.95f);
            try
            {
                SaveAll(structures);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ── Config resolution ────────────────────────────────────────────────

        static BuildConfig ResolveConfig()
        {
            // Prefer the selected asset in the Project window.
            if (Selection.activeObject is BuildConfig selected)
                return selected;

            var guids = AssetDatabase.FindAssets("t:BuildConfig");
            if (guids.Length == 0)
            {
                EditorUtility.DisplayDialog("SpineVoxels",
                    "No BuildConfig found.\n\n" +
                    "Create one via:\n  Assets > Create > SpineVoxels > Build Config\n\n" +
                    "Then set the .nrrd path and assign a StructurePalette.", "OK");
                return null;
            }

            var config = AssetDatabase.LoadAssetAtPath<BuildConfig>(
                AssetDatabase.GUIDToAssetPath(guids[0]));

            if (guids.Length > 1)
                Debug.LogWarning("[SpineVoxels] Multiple BuildConfigs found. " +
                                 $"Using: {AssetDatabase.GetAssetPath(config)}");
            return config;
        }

        static bool ValidateConfig(BuildConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.nrrdPath) || !File.Exists(config.nrrdPath))
            {
                EditorUtility.DisplayDialog("SpineVoxels",
                    $"NRRD file not found:\n{config.nrrdPath}\n\n" +
                    "Set nrrdPath in your BuildConfig asset.", "OK");
                return false;
            }
            if (config.palette == null)
            {
                EditorUtility.DisplayDialog("SpineVoxels",
                    "No StructurePalette assigned in BuildConfig.\n\n" +
                    "Create one via:\n  Assets > Create > SpineVoxels > Structure Palette\n\n" +
                    "then assign it to the BuildConfig.", "OK");
                return false;
            }
            return true;
        }

        // ── Asset saving ─────────────────────────────────────────────────────

        static void SaveAll(System.Collections.Generic.List<BuiltStructure> structures)
        {
            EnsureFolder(OutputRoot);
            EnsureFolder(OutputRoot + "/Meshes");
            EnsureFolder(OutputRoot + "/Materials");

            // Save individual mesh and material assets.
            foreach (var s in structures)
            {
                string meshPath = $"{OutputRoot}/Meshes/{s.info.name}.asset";
                string matPath  = $"{OutputRoot}/Materials/{s.info.name}.mat";

                // Overwrite existing assets.
                var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (existingMesh != null)
                    AssetDatabase.DeleteAsset(meshPath);

                AssetDatabase.CreateAsset(s.mesh, meshPath);

                var mat = MaterialFactory.Create(s.info);
                var existingMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (existingMat != null)
                    AssetDatabase.DeleteAsset(matPath);

                AssetDatabase.CreateAsset(mat, matPath);
            }

            AssetDatabase.SaveAssets();

            // Build scene hierarchy.
            var existing = GameObject.Find("SpineModel");
            if (existing != null)
                Object.DestroyImmediate(existing);

            var root = new GameObject("SpineModel");

            foreach (var s in structures)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                    $"{OutputRoot}/Meshes/{s.info.name}.asset");
                var mat = AssetDatabase.LoadAssetAtPath<Material>(
                    $"{OutputRoot}/Materials/{s.info.name}.mat");

                var go = new GameObject(s.info.name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh      = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.SetActive(s.info.defaultVisible);
            }

            // Save as prefab.
            string prefabPath = $"{OutputRoot}/SpineModel.prefab";
            PrefabUtility.SaveAsPrefabAssetAndConnect(
                root, prefabPath, InteractionMode.AutomatedAction);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SpineVoxels] Build complete — {structures.Count} structures. " +
                      $"Prefab: {prefabPath}");

            EditorUtility.DisplayDialog("SpineVoxels",
                $"Build complete!\n\n" +
                $"Structures: {structures.Count}\n" +
                $"Prefab: {prefabPath}\n\n" +
                "The SpineModel prefab is now in the scene and visible in the Editor.",
                "OK");
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
