// Loads the segmentation NRRD at runtime and provides thread-safe tissue sampling
// at any Unity world position, using the same coordinate transform as SpineModelBuilder.
//
// SampleTissue(worldPos) is safe to call from the haptic thread (~1–8 kHz):
//   the labels[] array is read-only after Start() completes.
//
// Setup: set the fields to match BuildConfig (nrrdPath, voxelSizeMm, unityUnitsPerMm,
//         centerModel, resampleRule). Loading+resampling happens once in Start().

using System.IO;
using SpineVoxels.Config;
using SpineVoxels.Core;
using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Nrrd Tissue Volume")]
    public class NrrdTissueVolume : MonoBehaviour
    {
        [Header("Must match BuildConfig — set these in the Inspector")]
        public string      nrrdPath        = "Assets/URS04 segmentation-label.nrrd";
        public float       voxelSizeMm     = 1.5f;
        public float       unityUnitsPerMm = 0.003f;
        public bool        centerModel     = true;
        public ResampleRule resampleRule   = ResampleRule.Majority;

        // ── Runtime cache ────────────────────────────────────────────────────
        LabelVolume _vol;
        Vector3     _gridOffset;
        float       _voxelSizeUnity;

        public bool IsReady => _vol != null;

        void Start()
        {
            if (!File.Exists(nrrdPath))
            {
                Debug.LogError($"[NrrdTissueVolume] NRRD not found: {nrrdPath}");
                return;
            }

            Debug.Log("[NrrdTissueVolume] Loading NRRD… (may take a few seconds)");
            var (header, data) = NrrdReader.Read(nrrdPath);
            var native = LabelVolume.FromNrrd(header, data);
            _vol = VolumeResampler.Resample(native, voxelSizeMm, resampleRule);

            _gridOffset = centerModel
                ? new Vector3(_vol.SizeX * 0.5f, _vol.SizeY * 0.5f, _vol.SizeZ * 0.5f)
                : Vector3.zero;
            _voxelSizeUnity = voxelSizeMm * unityUnitsPerMm;

            Debug.Log($"[NrrdTissueVolume] Ready — {_vol.SizeX}×{_vol.SizeY}×{_vol.SizeZ} voxels " +
                      $"({voxelSizeMm}mm, {unityUnitsPerMm} u/mm)");
        }

        // ── Public API (haptic-thread safe after Start) ──────────────────────

        // Maps worldPos → tissue type. Returns Background if not loaded or outside volume.
        public VoxelTissueType SampleTissue(Vector3 worldPos)
        {
            if (_vol == null) return VoxelTissueType.Background;
            return ClassifyLabel(SampleLabel(worldPos));
        }

        // Raw label value (0 = background).
        public short SampleLabel(Vector3 worldPos)
        {
            if (_vol == null) return 0;
            WorldToGrid(worldPos, out int gx, out int gy, out int gz);
            return _vol.GetLabel(gx, gy, gz);
        }

        // ── Coordinate mapping ───────────────────────────────────────────────
        // Inverted CoordinateTransform (same as SpineModelBuilder used during bake):
        //   Unity.x = (gx - offsetX) * vsU  →  gx = Unity.x / vsU + offsetX
        //   Unity.y = (gz - offsetZ) * vsU  →  gz = Unity.y / vsU + offsetZ   (Y↔Z swap)
        //   Unity.z = (gy - offsetY) * vsU  →  gy = Unity.z / vsU + offsetY
        void WorldToGrid(Vector3 world, out int gx, out int gy, out int gz)
        {
            float inv = 1f / _voxelSizeUnity;
            gx = Mathf.FloorToInt(world.x * inv + _gridOffset.x);
            gy = Mathf.FloorToInt(world.z * inv + _gridOffset.y);
            gz = Mathf.FloorToInt(world.y * inv + _gridOffset.z);
        }

        // ── Label classification ─────────────────────────────────────────────
        static VoxelTissueType ClassifyLabel(short label)
        {
            if (label >= 1 && label <= 6)   return VoxelTissueType.Bone;   // vertebrae S1-L1
            if (label == 7)                 return VoxelTissueType.Cord;
            if (label >= 8 && label <= 11)  return VoxelTissueType.Muscle;
            if (label == 12)                return VoxelTissueType.Fat;
            if (label == 13)                return VoxelTissueType.Skin;
            if (label >= 14 && label <= 16) return VoxelTissueType.Bone;   // sacrum + hips
            return VoxelTissueType.Background;
        }
    }
}
