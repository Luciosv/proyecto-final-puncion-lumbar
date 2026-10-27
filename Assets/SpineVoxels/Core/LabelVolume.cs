using UnityEngine;

namespace SpineVoxels.Core
{
    public sealed class LabelVolume
    {
        readonly short[] _data;

        public int     SizeX     { get; }
        public int     SizeY     { get; }
        public int     SizeZ     { get; }
        public Vector3 DirX      { get; }   // mm per voxel, LPS
        public Vector3 DirY      { get; }
        public Vector3 DirZ      { get; }
        public Vector3 OriginLps { get; }   // mm, LPS

        public LabelVolume(short[] data, int sx, int sy, int sz,
                           Vector3 dirX, Vector3 dirY, Vector3 dirZ, Vector3 originLps)
        {
            _data     = data;
            SizeX     = sx;
            SizeY     = sy;
            SizeZ     = sz;
            DirX      = dirX;
            DirY      = dirY;
            DirZ      = dirZ;
            OriginLps = originLps;
        }

        public static LabelVolume FromNrrd(NrrdHeader h, short[] data) =>
            new LabelVolume(data,
                            h.Sizes[0], h.Sizes[1], h.Sizes[2],
                            h.SpaceDirections[0],
                            h.SpaceDirections[1],
                            h.SpaceDirections[2],
                            h.SpaceOrigin);

        // Returns 0 for any out-of-bounds coordinate → treated as background by culling.
        public short GetLabel(int x, int y, int z)
        {
            if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ)
                return 0;
            return _data[x + y * SizeX + z * SizeX * SizeY];
        }
    }
}
