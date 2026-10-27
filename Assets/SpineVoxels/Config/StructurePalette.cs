using System.Collections.Generic;
using UnityEngine;

namespace SpineVoxels.Config
{
    [CreateAssetMenu(menuName = "SpineVoxels/Structure Palette", fileName = "StructurePalette")]
    public class StructurePalette : ScriptableObject
    {
        public List<StructureInfo> structures = new List<StructureInfo>();

        public bool TryGet(int labelId, out StructureInfo info)
        {
            foreach (var s in structures)
            {
                if (s.labelId == labelId) { info = s; return true; }
            }
            info = default;
            return false;
        }

        public IEnumerable<int> LabelIds
        {
            get
            {
                var ids = new List<int>(structures.Count);
                foreach (var s in structures) ids.Add(s.labelId);
                ids.Sort();
                return ids;
            }
        }

        void Reset() => structures = BuildDefaultPalette();

        public static List<StructureInfo> BuildDefaultPalette() => new List<StructureInfo>
        {
            new StructureInfo { labelId=1,  name="S1",                     color=C(230,225,200),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=2,  name="L5",                     color=C(225,215,180),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=3,  name="L4",                     color=C(220,205,165),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=4,  name="L3",                     color=C(215,200,160),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=5,  name="L2",                     color=C(210,195,150),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=6,  name="L1",                     color=C(205,190,140),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=7,  name="spinal_cord",             color=C(240,220,60),    defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=8,  name="left_deep_back_muscle",   color=C(200,60,60),     defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=9,  name="right_deep_back_muscle",  color=C(150,40,40),     defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=10, name="left_iliopsoas",          color=C(210,70,170),    defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=11, name="right_iliopsoas",         color=C(140,50,160),    defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=12, name="Fat",                     color=CA(240,225,120,90), defaultVisible=false, transparent=true  },
            new StructureInfo { labelId=13, name="skin",                    color=CA(235,205,170,70), defaultVisible=false, transparent=true  },
            new StructureInfo { labelId=14, name="Sacrum",                  color=C(200,185,135),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=15, name="left_hip",                color=C(195,180,150),   defaultVisible=true,  transparent=false },
            new StructureInfo { labelId=16, name="right_hip",               color=C(180,165,135),   defaultVisible=true,  transparent=false },
        };

        static Color C(int r, int g, int b)            => new Color(r/255f, g/255f, b/255f);
        static Color CA(int r, int g, int b, int a)    => new Color(r/255f, g/255f, b/255f, a/255f);
    }
}
