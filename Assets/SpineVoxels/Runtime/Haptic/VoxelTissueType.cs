namespace SpineVoxels.Runtime.Haptic
{
    // Tissue type as detected from the NRRD segmentation labels at runtime.
    // Label mapping for this scan: 1-6=vertebrae (S1-L1), 7=spinal cord,
    // 8-11=back/iliopsoas muscle, 12=fat, 13=skin, 14=sacrum, 15-16=hips.
    public enum VoxelTissueType
    {
        Background = 0,
        Bone       = 1,   // labels 1-6, 14-16
        Cord       = 2,   // label 7
        Muscle     = 3,   // labels 8-11
        Fat        = 4,   // label 12
        Skin       = 5,   // label 13
    }
}
