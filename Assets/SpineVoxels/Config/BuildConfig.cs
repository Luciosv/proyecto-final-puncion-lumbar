using UnityEngine;

namespace SpineVoxels.Config
{
    public enum ResampleRule { Majority, AnyPresent }

    [CreateAssetMenu(menuName = "SpineVoxels/Build Config", fileName = "BuildConfig")]
    public class BuildConfig : ScriptableObject
    {
        [Tooltip("Absolute path to the .nrrd file (or relative to project root)")]
        public string nrrdPath = "D:\\Unity Projects\\FacuFiles\\Voxels\\Assets\\URS04 segmentation-label.nrrd";

        [Tooltip("Target isotropic voxel size in mm (1.5 = good balance between detail and performance)")]
        public float voxelSizeMm = 1.5f;

        [Tooltip("Unity units per mm. 1 = 1u per mm (model ~163x230u). Use 0.001 to work in meters.")]
        public float unityUnitsPerMm = 0.003f;

        [Tooltip("Center the model bounds at the world origin")]
        public bool centerModel = true;

        [Tooltip("Majority: most frequent label wins. AnyPresent: any non-zero label wins (fills thin structures better).")]
        public ResampleRule resampleRule = ResampleRule.Majority;

        public StructurePalette palette;

#if UNITY_EDITOR
        private void OnValidate()
        {
            nrrdPath = "D:\\Unity Projects\\FacuFiles\\Voxels\\Assets\\URS04 segmentation-label.nrrd";
            voxelSizeMm = 1.5f;
            unityUnitsPerMm = 0.003f;
            centerModel = true;
            resampleRule = ResampleRule.Majority;
        }
#endif
    }
}
