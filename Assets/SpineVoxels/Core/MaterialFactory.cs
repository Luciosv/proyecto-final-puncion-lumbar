using UnityEngine;
using UnityEngine.Rendering;
using SpineVoxels.Config;

namespace SpineVoxels.Core
{
    public static class MaterialFactory
    {
        const string ShaderName = "Universal Render Pipeline/Lit";

        public static Material Create(StructureInfo info)
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SpineVoxels] Shader '{ShaderName}' not found. " +
                                 "Ensure URP is installed and the project uses Universal Render Pipeline.");
                shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
            }

            var mat = new Material(shader) { name = info.name };
            mat.SetColor("_BaseColor", info.color);

            if (info.transparent)
                ConfigureTransparent(mat, info.color.a);
            else
                ConfigureOpaque(mat);

            return mat;
        }

        static void ConfigureOpaque(Material mat)
        {
            mat.SetFloat("_Surface", 0f);           // Opaque
            mat.SetFloat("_Blend",   0f);           // Alpha mode (irrelevant for opaque)
            mat.SetFloat("_ZWrite",  1f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.One);
            mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)RenderQueue.Geometry;
        }

        static void ConfigureTransparent(Material mat, float alpha)
        {
            mat.SetFloat("_Surface", 1f);           // Transparent
            mat.SetFloat("_Blend",   0f);           // Alpha blending
            mat.SetFloat("_ZWrite",  0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }
}
