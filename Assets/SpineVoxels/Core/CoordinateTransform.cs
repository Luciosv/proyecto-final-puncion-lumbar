using UnityEngine;

namespace SpineVoxels.Core
{
    // Converts integer grid corners from the resampled LabelVolume to Unity world positions.
    //
    // Coordinate mapping (LPS → Unity):
    //   Grid +X = patient Right  → Unity +X
    //   Grid +Y = patient Anterior (because DirY.y < 0, flipping posterior) → Unity +Z
    //   Grid +Z = patient Superior → Unity +Y
    //
    //   Unity = (local.x, local.z, local.y) * VoxelSizeUnity
    //   where local = (gx, gy, gz) - gridOffset
    //
    // The Y/Z swap changes handedness from right-handed (grid/LPS) to left-handed (Unity),
    // which is what we want. It also means triangle winding must be flipped (FlipWinding=true).
    public sealed class CoordinateTransform
    {
        readonly float   _voxelSizeUnity;
        readonly Vector3 _gridOffset;

        public float VoxelSizeUnity => _voxelSizeUnity;

        // The Y↔Z swap produces a left-handed coordinate system, so triangle winding
        // (relative to CCW-from-outside in the original right-handed grid) is reversed.
        // Toggle this to false if faces appear inside-out after import.
        public bool FlipWinding => true;

        // gridOffset: the grid-space point that maps to Unity origin (for centering).
        // Typically (SizeX/2, SizeY/2, SizeZ/2) when centerModel=true.
        public CoordinateTransform(float voxelSizeMm, float unityUnitsPerMm, Vector3 gridOffset)
        {
            _voxelSizeUnity = voxelSizeMm * unityUnitsPerMm;
            _gridOffset     = gridOffset;
        }

        // Maps an integer grid corner (gx, gy, gz) to a Unity world position.
        public Vector3 GridCornerToUnity(int gx, int gy, int gz)
            => GridCornerToUnity((float)gx, gy, gz);

        // Float overload: lets callers place geometry at sub-voxel positions (e.g.
        // tissue slabs whose thickness is an exact millimetre value).
        public Vector3 GridCornerToUnity(float gx, float gy, float gz)
        {
            float lx = gx - _gridOffset.x;
            float ly = gy - _gridOffset.y;
            float lz = gz - _gridOffset.z;
            // Swap ly and lz so that grid Z (Superior) → Unity Y (up)
            //                       and grid Y (Anterior) → Unity Z (forward)
            return new Vector3(lx, lz, ly) * _voxelSizeUnity;
        }

        // Returns the Unity-space normal for a given grid axis and face sign.
        // d=0(X)→Unity X, d=1(Y)→Unity Z, d=2(Z)→Unity Y  (reflecting the Y/Z swap).
        public static Vector3 GridAxisNormal(int d, int sign)
        {
            Vector3 axis = d == 0 ? Vector3.right
                         : d == 1 ? Vector3.forward
                                  : Vector3.up;
            return axis * sign;
        }
    }
}
