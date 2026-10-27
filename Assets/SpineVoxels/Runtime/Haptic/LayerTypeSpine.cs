namespace SpineVoxels.Runtime.Haptic
{
    // JSON-defined tissue layers for the force model.
    // Values match LP.V2.LayerTypeV2 from PuncionSinHaptico so serialized data is compatible.
    public enum LayerTypeSpine
    {
        Skin             = 0,
        Fat              = 1,
        Muscle           = 2,
        SupraspinousLig  = 3,
        LigamentumFlavum = 4,
        EpiduralSpace    = 5,
        Dura             = 6,
        Subarachnoid     = 7,
        InterspinousLig  = 8,
    }
}
