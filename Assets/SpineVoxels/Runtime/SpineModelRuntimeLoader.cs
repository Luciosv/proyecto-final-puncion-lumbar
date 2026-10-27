using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using SpineVoxels.Config;
using SpineVoxels.Core;

namespace SpineVoxels.Runtime
{
    // Plan B: generates the voxel model at runtime (Play mode only).
    // Use this if the editor-time bake (SpineVoxelBaker) is too slow to iterate.
    //
    // Heavy work (NRRD parsing, resampling, array building) runs on a background thread.
    // Mesh object creation and GameObject assembly happen on the main thread.
    public sealed class SpineModelRuntimeLoader : MonoBehaviour
    {
        public BuildConfig config;
        public bool        buildOnStart = true;

        void Start()
        {
            if (buildOnStart) BuildNow();
        }

        public void BuildNow()
        {
            if (config == null)
            {
                Debug.LogError("[SpineVoxels] No BuildConfig assigned to SpineModelRuntimeLoader.");
                return;
            }
            StartCoroutine(BuildAsync());
        }

        IEnumerator BuildAsync()
        {
            Debug.Log("[SpineVoxels] Starting runtime build…");
            var startTime = Time.realtimeSinceStartup;

            // The background task computes vertex/index arrays but does NOT create Mesh objects
            // (Unity Mesh API is main-thread only). We pass raw data out via MeshData.
            List<MeshData> meshDataList = null;
            string         error        = null;
            var cfg = config; // capture for lambda

            var task = Task.Run(() =>
            {
                try
                {
                    var structures = SpineModelBuilder.Build(cfg);
                    meshDataList = new List<MeshData>(structures.Count);
                    foreach (var s in structures)
                        meshDataList.Add(MeshData.FromBuiltStructure(s));
                }
                catch (System.Exception e)
                {
                    error = e.ToString();
                }
            });

            while (!task.IsCompleted)
                yield return null;

            if (error != null)
            {
                Debug.LogError($"[SpineVoxels] Runtime build failed:\n{error}");
                yield break;
            }

            // Upload meshes and instantiate GameObjects on the main thread.
            foreach (var md in meshDataList)
            {
                var mesh = md.UploadToMesh();

                var go = new GameObject(md.info.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().mesh             = mesh;
                go.AddComponent<MeshRenderer>().material       = MaterialFactory.Create(md.info);
                go.SetActive(md.info.defaultVisible);

                yield return null; // spread over frames to avoid a spike
            }

            float elapsed = Time.realtimeSinceStartup - startTime;
            Debug.Log($"[SpineVoxels] Runtime build complete — " +
                      $"{meshDataList.Count} structures in {elapsed:F1}s.");
        }
    }

    // Intermediate data produced on the background thread; safe to pass to the main thread.
    sealed class MeshData
    {
        public Vector3[]     vertices;
        public Vector3[]     normals;
        public int[]         indices;
        public string        name;
        public bool          useUInt32;
        public StructureInfo info;

        public static MeshData FromBuiltStructure(BuiltStructure s)
        {
            var m = s.mesh;
            return new MeshData
            {
                vertices  = m.vertices,
                normals   = m.normals,
                indices   = m.triangles,
                name      = m.name,
                useUInt32 = m.vertexCount > 65535,
                info      = s.info,
            };
        }

        public Mesh UploadToMesh()
        {
            var mesh = new Mesh { name = name };
            if (useUInt32)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices  = vertices;
            mesh.normals   = normals;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
