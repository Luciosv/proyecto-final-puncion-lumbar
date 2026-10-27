using System;
using System.Collections.Generic;
using UnityEngine;
using SpineVoxels.Config;

namespace SpineVoxels.Core
{
    public sealed class BuiltStructure
    {
        public int           labelId;
        public Mesh          mesh;
        public StructureInfo info;
    }

    // Orchestrates the full pipeline:
    //   NrrdReader → LabelVolume → VolumeResampler → VoxelMeshBuilder (per label)
    // Returns geometry only — no GameObjects, no AssetDatabase calls.
    // Safe to call from a background thread (Mesh construction itself is NOT thread-safe
    // in Unity; see SpineModelRuntimeLoader for the threading split).
    public static class SpineModelBuilder
    {
        public static List<BuiltStructure> Build(BuildConfig config,
                                                 Action<float, string> progress = null)
        {
            progress?.Invoke(0.00f, "Reading NRRD…");
            var (header, data) = NrrdReader.Read(config.nrrdPath);

            progress?.Invoke(0.10f, "Building label volume…");
            var native = LabelVolume.FromNrrd(header, data);

            progress?.Invoke(0.15f, $"Resampling to {config.voxelSizeMm} mm isotropic…");
            var resampled = VolumeResampler.Resample(native, config.voxelSizeMm, config.resampleRule);

            Vector3 gridOffset = config.centerModel
                ? new Vector3(resampled.SizeX * 0.5f, resampled.SizeY * 0.5f, resampled.SizeZ * 0.5f)
                : Vector3.zero;

            var xform = new CoordinateTransform(config.voxelSizeMm, config.unityUnitsPerMm, gridOffset);

            var results = new List<BuiltStructure>();
            var labelIds = new List<int>(config.palette.LabelIds);

            for (int i = 0; i < labelIds.Count; i++)
            {
                int id = labelIds[i];
                if (!config.palette.TryGet(id, out var info)) continue;

                float p = 0.20f + 0.80f * (i / (float)labelIds.Count);
                progress?.Invoke(p, $"Meshing {info.name} ({i + 1}/{labelIds.Count})…");

                var mesh = VoxelMeshBuilder.Build(resampled, id, xform, info.name);
                if (mesh == null) continue;

                results.Add(new BuiltStructure { labelId = id, mesh = mesh, info = info });
            }

            return results;
        }
    }
}
