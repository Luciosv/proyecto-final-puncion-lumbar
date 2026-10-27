using System;
using UnityEngine;

namespace SpineVoxels.Config
{
    [Serializable]
    public struct StructureInfo
    {
        public int    labelId;
        public string name;
        public Color  color;
        public bool   defaultVisible;
        public bool   transparent;
    }
}
