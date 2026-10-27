using System.Collections.Generic;
using UnityEngine;
using SpineVoxels.Config;

namespace SpineVoxels.Core
{
    public static class VolumeResampler
    {
        // Resamples `src` to an isotropic voxel grid of `targetVoxelMm` per side.
        // The resampling factor per axis = targetVoxelMm / native_spacing.
        // Output size = ceil(nativeSize / factor) per axis.
        public static LabelVolume Resample(LabelVolume src, float targetVoxelMm, ResampleRule rule)
        {
            float spacingX = src.DirX.magnitude;
            float spacingY = src.DirY.magnitude;
            float spacingZ = src.DirZ.magnitude;

            float factorX = targetVoxelMm / spacingX;
            float factorY = targetVoxelMm / spacingY;
            float factorZ = targetVoxelMm / spacingZ;

            int outX = Mathf.Max(1, Mathf.CeilToInt(src.SizeX / factorX));
            int outY = Mathf.Max(1, Mathf.CeilToInt(src.SizeY / factorY));
            int outZ = Mathf.Max(1, Mathf.CeilToInt(src.SizeZ / factorZ));

            var outData = new short[outX * outY * outZ];

            // Reuse the count dict across all output cells to reduce GC pressure.
            var counts = new Dictionary<short, int>(32);

            for (int oz = 0; oz < outZ; oz++)
            for (int oy = 0; oy < outY; oy++)
            for (int ox = 0; ox < outX; ox++)
            {
                int srcX0 = Mathf.RoundToInt(ox * factorX);
                int srcY0 = Mathf.RoundToInt(oy * factorY);
                int srcZ0 = Mathf.RoundToInt(oz * factorZ);
                int srcX1 = Mathf.Min(src.SizeX, Mathf.RoundToInt((ox + 1) * factorX));
                int srcY1 = Mathf.Min(src.SizeY, Mathf.RoundToInt((oy + 1) * factorY));
                int srcZ1 = Mathf.Min(src.SizeZ, Mathf.RoundToInt((oz + 1) * factorZ));

                // Guarantee at least 1 source voxel per output cell.
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;
                if (srcY1 <= srcY0) srcY1 = srcY0 + 1;
                if (srcZ1 <= srcZ0) srcZ1 = srcZ0 + 1;

                outData[ox + oy * outX + oz * outX * outY] =
                    PickLabel(src, srcX0, srcY0, srcZ0, srcX1, srcY1, srcZ1, rule, counts);
            }

            // Recalculate affine: same direction but normalized × targetVoxelMm.
            Vector3 newDirX = src.DirX.normalized * targetVoxelMm;
            Vector3 newDirY = src.DirY.normalized * targetVoxelMm;
            Vector3 newDirZ = src.DirZ.normalized * targetVoxelMm;

            return new LabelVolume(outData, outX, outY, outZ,
                                   newDirX, newDirY, newDirZ, src.OriginLps);
        }

        static short PickLabel(LabelVolume src,
                               int x0, int y0, int z0,
                               int x1, int y1, int z1,
                               ResampleRule rule,
                               Dictionary<short, int> counts)
        {
            counts.Clear();

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                short lbl = src.GetLabel(x, y, z);
                counts.TryGetValue(lbl, out int c);
                counts[lbl] = c + 1;
            }

            if (rule == ResampleRule.AnyPresent)
            {
                // Most frequent non-zero label; fall back to 0 if block is pure background.
                short best = 0;
                int   bestCount = 0;
                foreach (var kv in counts)
                {
                    if (kv.Key != 0 && kv.Value > bestCount)
                    {
                        bestCount = kv.Value;
                        best      = kv.Key;
                    }
                }
                return best;
            }
            else // Majority
            {
                // Most frequent label overall.
                // Tie-break: higher label id wins (favors fine structures: spinal cord > bone > fat).
                short best = 0;
                int   bestCount = 0;
                foreach (var kv in counts)
                {
                    if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key > best))
                    {
                        bestCount = kv.Value;
                        best      = kv.Key;
                    }
                }
                return best;
            }
        }
    }
}
