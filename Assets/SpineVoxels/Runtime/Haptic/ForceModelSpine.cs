namespace SpineVoxels.Runtime.Haptic
{
    public static class ForceModelSpine
    {
        // Returns resistance force (N) at a given physical depth along the insertion axis.
        // layerIdx: index into layers[] for the layer containing depth_m.
        // Returns 0 if cursor is outside all layers or index is invalid.
        public static float Evaluate(float depth_m, int layerIdx, LayerDataSpine[] layers,
                                     bool inserting, float withdrawalScale)
        {
            if (layerIdx < 0 || layerIdx >= layers.Length) return 0f;

            ref LayerDataSpine lay = ref layers[layerIdx];
            float dz_scene_mm = (depth_m - lay.dStartM) * 1000f;
            float dz_curve_mm = dz_scene_mm * lay.dzScale;

            float F = lay.curve.Evaluate(dz_curve_mm);
            return inserting ? F : F * withdrawalScale;
        }

        // Returns the layer index containing depth_m, or -1 if none.
        public static int GetLayerIndex(float depth_m, LayerDataSpine[] layers)
        {
            for (int i = 0; i < layers.Length; i++)
                if (depth_m >= layers[i].dStartM && depth_m < layers[i].dEndM)
                    return i;
            return -1;
        }
    }
}
