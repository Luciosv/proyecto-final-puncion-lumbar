using UnityEngine;

namespace SpineVoxels.Core
{
    public sealed class NrrdHeader
    {
        public int[]     Sizes;            // [sX, sY, sZ]
        public Vector3[] SpaceDirections;  // 3 row-vectors, mm, LPS space
        public Vector3   SpaceOrigin;      // mm, LPS
        public string    Type;             // "short"
        public bool      LittleEndian;
        public string    Encoding;         // "gzip" | "gz" | "raw"
        public long      DataByteOffset;   // byte where the data block starts

        public int VoxelCount => Sizes[0] * Sizes[1] * Sizes[2];
    }
}
