using System.Collections.Generic;
using UnityEngine;

namespace SpineVoxels.Core
{
    // Computes a "tissue stack" of simple stretched cubes (like PuncionSinHaptico's
    // TissueStack), placed in the anatomy: the 6 layers in needle order from the
    // spinous-process tips to the spinal cord, with thickness PROPORTIONAL to the
    // z_width_mm values of AnatomicalNoMuscle2.json.
    //
    // The puncture region (the cord's vertical extent, L1-L5) is divided into up to
    // `segments` vertical bands; in each band the 6 tissues are laid out between the
    // local spinous-tip depth and the local cord surface, so the stack follows the
    // spinal curve in ≤3 steps per tissue. No voxels — one cube per (tissue, band).
    //
    // Coordinate assumptions (resampled grid, see CoordinateTransform):
    //   grid +Y = anterior (needle direction), maps to Unity +Z
    //   grid +Z = superior (vertical),         maps to Unity +Y
    public static class PunctureStackLayout
    {
        public enum Tissue { Supraspinous, Interspinous, Flavum, Epidural, Dura, Subarachnoid }

        readonly struct Def
        {
            public readonly Tissue tissue;
            public readonly float  proportion;   // z_width_mm from the JSON
            public Def(Tissue t, float p) { tissue = t; proportion = p; }
        }

        // Needle order, tips → cord. Proportions sum to 35.
        static readonly Def[] Defs =
        {
            new Def(Tissue.Supraspinous,  3f),
            new Def(Tissue.Interspinous, 17f),
            new Def(Tissue.Flavum,        4f),
            new Def(Tissue.Epidural,      5f),
            new Def(Tissue.Dura,          2f),
            new Def(Tissue.Subarachnoid,  4f),
        };
        const float Total = 35f;

        public struct Column
        {
            public Tissue  tissue;
            public int     order;        // crossing order (skin=0, fat=1, these=2..7)
            public int     segment;      // vertical band index
            public float   thicknessMm;  // nominal z_width_mm
            public Vector3 unityCenter;
            public Vector3 unitySize;
        }

        const short Cord = 7;
        static bool IsVertebra(short l) => l >= 1 && l <= 6;

        public static List<Column> Build(LabelVolume vol, CoordinateTransform xform,
                                         float voxelSizeMm, int segments, float sideWidthMm)
        {
            var result   = new List<Column>();
            int midX     = ComputeMidlineX(vol);
            int sideHalf = Mathf.Max(1, Mathf.RoundToInt(sideWidthMm / voxelSizeMm * 0.5f));
            int band     = Mathf.Max(2, sideHalf);

            if (!CordZExtent(vol, midX, band, out int zMin, out int zMax))
                throw new System.InvalidOperationException(
                    "No spinal cord (label 7) found near the midline.");

            segments = Mathf.Max(1, segments);
            float segH = (zMax - zMin + 1) / (float)segments;

            for (int s = 0; s < segments; s++)
            {
                int zs = Mathf.RoundToInt(zMin + s * segH);
                int ze = Mathf.RoundToInt(zMin + (s + 1) * segH) - 1;
                if (ze < zs) ze = zs;

                if (!Anchors(vol, midX, band, zs, ze, out int tipY, out int cordPostY))
                    continue;
                float span = cordPostY - tipY;
                if (span <= 0f) continue;

                float cum = 0f;
                for (int i = 0; i < Defs.Length; i++)
                {
                    float yA = tipY + span * (cum / Total);
                    cum += Defs[i].proportion;
                    float yB = tipY + span * (cum / Total);

                    Vector3 c0 = xform.GridCornerToUnity(midX - sideHalf, yA, zs);
                    Vector3 c1 = xform.GridCornerToUnity(midX + sideHalf, yB, ze + 1);

                    result.Add(new Column
                    {
                        tissue      = Defs[i].tissue,
                        order       = 2 + i,
                        segment     = s,
                        thicknessMm = Defs[i].proportion,
                        unityCenter = (c0 + c1) * 0.5f,
                        unitySize   = new Vector3(Mathf.Abs(c1.x - c0.x),
                                                  Mathf.Abs(c1.y - c0.y),
                                                  Mathf.Abs(c1.z - c0.z)),
                    });
                }
            }

            if (result.Count == 0)
                throw new System.InvalidOperationException(
                    "Could not place tissue columns (missing tip/cord landmarks).");
            return result;
        }

        // Most posterior bone (spinous tip) and most posterior cord, over a segment,
        // near the midline. Both as grid-Y (posterior = smaller).
        static bool Anchors(LabelVolume v, int midX, int band, int zs, int ze,
                            out int tipY, out int cordPostY)
        {
            tipY = int.MaxValue; cordPostY = int.MaxValue;
            int x0 = Mathf.Max(0, midX - band), x1 = Mathf.Min(v.SizeX - 1, midX + band);

            for (int z = zs; z <= ze; z++)
            for (int x = x0; x <= x1; x++)
            {
                int b = PosteriorVertebraY(v, x, z);
                if (b >= 0 && b < tipY) tipY = b;
                int c = PosteriorLabelY(v, x, z, Cord);
                if (c >= 0 && c < cordPostY) cordPostY = c;
            }
            return tipY != int.MaxValue && cordPostY != int.MaxValue;
        }

        static int PosteriorVertebraY(LabelVolume v, int x, int z)
        {
            for (int y = 0; y < v.SizeY; y++)
                if (IsVertebra(v.GetLabel(x, y, z))) return y;
            return -1;
        }

        static int PosteriorLabelY(LabelVolume v, int x, int z, short label)
        {
            for (int y = 0; y < v.SizeY; y++)
                if (v.GetLabel(x, y, z) == label) return y;
            return -1;
        }

        static bool CordZExtent(LabelVolume v, int midX, int band, out int zMin, out int zMax)
        {
            zMin = int.MaxValue; zMax = int.MinValue;
            int x0 = Mathf.Max(0, midX - band), x1 = Mathf.Min(v.SizeX - 1, midX + band);

            for (int z = 0; z < v.SizeZ; z++)
            {
                bool has = false;
                for (int x = x0; x <= x1 && !has; x++)
                    for (int y = 0; y < v.SizeY; y++)
                        if (v.GetLabel(x, y, z) == Cord) { has = true; break; }
                if (has) { if (z < zMin) zMin = z; if (z > zMax) zMax = z; }
            }
            return zMax >= zMin;
        }

        static int ComputeMidlineX(LabelVolume v)
        {
            long sum = 0, cnt = 0;
            for (int z = 0; z < v.SizeZ; z++)
            for (int y = 0; y < v.SizeY; y++)
            for (int x = 0; x < v.SizeX; x++)
                if (v.GetLabel(x, y, z) == Cord) { sum += x; cnt++; }

            if (cnt == 0)
            {
                for (int z = 0; z < v.SizeZ; z++)
                for (int y = 0; y < v.SizeY; y++)
                for (int x = 0; x < v.SizeX; x++)
                    if (IsVertebra(v.GetLabel(x, y, z))) { sum += x; cnt++; }
            }

            return cnt > 0 ? (int)(sum / cnt) : v.SizeX / 2;
        }
    }
}
