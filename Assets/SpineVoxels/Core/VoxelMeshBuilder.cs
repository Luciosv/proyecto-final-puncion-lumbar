using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpineVoxels.Core
{
    public static class VoxelMeshBuilder
    {
        // Builds a voxel mesh for a single label using face-culling + greedy meshing.
        //
        // Algorithm:
        //   For each of the 6 face directions (3 axes × 2 signs):
        //     For each layer w along the face's axis:
        //       Build a 2D boolean mask: cell (ui,vi) is true when:
        //         volume[...d=w...] == labelId  AND  volume[...d=w+sign...] != labelId
        //       Greedy merge the mask into maximal rectangles and emit quads.
        //
        // A voxel bordering the edge of the volume has GetLabel return 0 (background)
        // for the out-of-bounds neighbor, so those faces are always emitted → closed mesh.
        public static Mesh Build(LabelVolume volume, int labelId,
                                 CoordinateTransform transform, string meshName)
        {
            var verts   = new List<Vector3>(4096);
            var norms   = new List<Vector3>(4096);
            var indices = new List<int>(6144);

            int[] dims = { volume.SizeX, volume.SizeY, volume.SizeZ };

            for (int d = 0; d < 3; d++)
            {
                int u     = (d + 1) % 3;
                int v     = (d + 2) % 3;
                int uSize = dims[u];
                int vSize = dims[v];
                int area  = uSize * vSize;

                // Pre-allocate mask buffers for this axis; reuse across signs and layers.
                var mask     = new bool[area];
                var consumed = new bool[area];

                for (int sign = -1; sign <= 1; sign += 2)
                {
                    // shouldFlip accounts for:
                    //   - sign=-1 faces need reversed winding vs sign=+1 (they face the other way)
                    //   - FlipWinding corrects handedness change from the Y/Z axis swap
                    bool shouldFlip = (sign == -1) ^ transform.FlipWinding;

                    Vector3 faceNormal = CoordinateTransform.GridAxisNormal(d, sign);

                    var pos  = new int[3];
                    var npos = new int[3];

                    for (int w = 0; w < dims[d]; w++)
                    {
                        // ── Build mask ──────────────────────────────────────────────
                        Array.Clear(mask, 0, area);

                        int neighborW = w + sign;

                        for (int vi = 0; vi < vSize; vi++)
                        for (int ui = 0; ui < uSize; ui++)
                        {
                            pos[d]  = w;         pos[u]  = ui; pos[v]  = vi;
                            npos[d] = neighborW; npos[u] = ui; npos[v] = vi;

                            if (volume.GetLabel(pos[0],  pos[1],  pos[2])  == labelId &&
                                volume.GetLabel(npos[0], npos[1], npos[2]) != labelId)
                            {
                                mask[ui + vi * uSize] = true;
                            }
                        }

                        // ── Greedy merge ────────────────────────────────────────────
                        Array.Clear(consumed, 0, area);

                        for (int vi = 0; vi < vSize; vi++)
                        for (int ui = 0; ui < uSize; ui++)
                        {
                            if (!mask[ui + vi * uSize] || consumed[ui + vi * uSize]) continue;

                            // Extend rectangle in u direction
                            int du = 1;
                            while (ui + du < uSize &&
                                   mask[ui + du + vi * uSize] &&
                                   !consumed[ui + du + vi * uSize])
                                du++;

                            // Extend in v direction: entire row [ui..ui+du) must be valid
                            int dv = 1;
                            while (vi + dv < vSize)
                            {
                                bool rowOk = true;
                                for (int k = 0; k < du; k++)
                                {
                                    int cell = (ui + k) + (vi + dv) * uSize;
                                    if (!mask[cell] || consumed[cell]) { rowOk = false; break; }
                                }
                                if (!rowOk) break;
                                dv++;
                            }

                            // Mark the merged rectangle as consumed
                            for (int dvi = 0; dvi < dv; dvi++)
                            for (int dui = 0; dui < du; dui++)
                                consumed[(ui + dui) + (vi + dvi) * uSize] = true;

                            // ── Emit quad ───────────────────────────────────────────
                            // Face sits at the high (+1) or low (0) side of voxel `w`.
                            int faceW = sign == 1 ? w + 1 : w;

                            // Four corner positions in grid space (integer, for GridCornerToUnity)
                            // p00=(ui,vi), p10=(ui+du,vi), p01=(ui,vi+dv), p11=(ui+du,vi+dv)
                            int[] c00 = new int[3]; c00[d] = faceW; c00[u] = ui;      c00[v] = vi;
                            int[] c10 = new int[3]; c10[d] = faceW; c10[u] = ui + du; c10[v] = vi;
                            int[] c01 = new int[3]; c01[d] = faceW; c01[u] = ui;      c01[v] = vi + dv;
                            int[] c11 = new int[3]; c11[d] = faceW; c11[u] = ui + du; c11[v] = vi + dv;

                            int baseIdx = verts.Count;
                            verts.Add(transform.GridCornerToUnity(c00[0], c00[1], c00[2]));
                            verts.Add(transform.GridCornerToUnity(c10[0], c10[1], c10[2]));
                            verts.Add(transform.GridCornerToUnity(c01[0], c01[1], c01[2]));
                            verts.Add(transform.GridCornerToUnity(c11[0], c11[1], c11[2]));

                            norms.Add(faceNormal);
                            norms.Add(faceNormal);
                            norms.Add(faceNormal);
                            norms.Add(faceNormal);

                            // CCW winding from outside (sign=+1 looking from +d, sign=-1 from -d).
                            // After the Y/Z swap the handedness flips, so shouldFlip reverses winding.
                            // Indices: p00=base+0, p10=base+1, p01=base+2, p11=base+3
                            if (!shouldFlip)
                            {
                                indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
                                indices.Add(baseIdx + 0); indices.Add(baseIdx + 3); indices.Add(baseIdx + 1);
                            }
                            else
                            {
                                indices.Add(baseIdx + 0); indices.Add(baseIdx + 3); indices.Add(baseIdx + 2);
                                indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 3);
                            }
                        }
                    }
                }
            }

            if (verts.Count == 0) return null;

            var mesh = new Mesh { name = meshName };
            if (verts.Count > 65535)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
