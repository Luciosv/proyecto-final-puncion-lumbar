// Parses AnatomicalNoMuscle2.json into LayerFitSpine entries.
// Supports both V2 (hardcoded widths) and V3 (embedded z_width_mm) JSON formats.
// Partial loads are valid — missing layers produce warnings only.
//
// TryParse(json) parses a JSON STRING (used for live calibration over WebSocket);
// TryLoad(path) reads a file and delegates to TryParse.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    public static class ForceJsonLoaderSpine
    {
        static readonly Dictionary<string, LayerTypeSpine> s_KeyToType = new()
        {
            { "Skin",              LayerTypeSpine.Skin            },
            { "Fat",               LayerTypeSpine.Fat             },
            { "Muscle",            LayerTypeSpine.Muscle          },
            { "Supraspinous Lig.", LayerTypeSpine.SupraspinousLig },
            { "Interspinous Lig.", LayerTypeSpine.InterspinousLig },
            { "Ligamentum Flavum", LayerTypeSpine.LigamentumFlavum},
            { "Epidural Space",    LayerTypeSpine.EpiduralSpace   },
            { "Dura Mater",        LayerTypeSpine.Dura            },
            { "Subarachnoid",      LayerTypeSpine.Subarachnoid    },
        };

        static readonly Dictionary<string, (CurveFamilySpine family, int paramCount)> s_FamilyMap = new()
        {
            { "constant",       (CurveFamilySpine.Constant,     1) },
            { "linear",         (CurveFamilySpine.Linear,       2) },
            { "polynomial2",    (CurveFamilySpine.Polynomial2,  3) },
            { "polynomial3",    (CurveFamilySpine.Polynomial3,  4) },
            { "exponential",    (CurveFamilySpine.Exponential,  3) },
            { "saturating_exp", (CurveFamilySpine.SaturatingExp,3) },
            { "sigmoid",        (CurveFamilySpine.Sigmoid,      3) },
            { "kv_rupture",     (CurveFamilySpine.KvRupture,    3) },
            { "simone_pop",     (CurveFamilySpine.SimonePop,    3) },
        };

        public struct LayerFitSpine
        {
            public LayerTypeSpine  type;
            public float           jsonWidthMm;
            public LayerCurveSpine curve;
        }

        // Parses a JSON string (file contents or a live calibration payload).
        public static bool TryParse(string json, out Dictionary<LayerTypeSpine, LayerFitSpine> result)
        {
            result = new Dictionary<LayerTypeSpine, LayerFitSpine>();
            if (string.IsNullOrEmpty(json)) return false;

            // Pass 0: extract per-layer z_width_mm
            var widthMap = new Dictionary<string, float>();
            var rxW = new Regex(
                @"""([^""]+)""\s*:\s*\{[^{}]*?""z_width_mm""\s*:\s*([0-9]*\.?[0-9]+(?:[eE][+\-]?[0-9]+)?)",
                RegexOptions.Singleline);
            foreach (Match wm in rxW.Matches(json))
                if (float.TryParse(wm.Groups[2].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out float w))
                    widthMap[wm.Groups[1].Value] = w;

            // Pass 1: parse family + params
            var rx = new Regex(
                @"""([^""]+)""\s*:\s*\{\s*""family""\s*:\s*""([^""]+)""\s*,\s*""params""\s*:\s*\[([^\]]+)\]",
                RegexOptions.Singleline);
            foreach (Match m in rx.Matches(json))
            {
                string key    = m.Groups[1].Value;
                string family = m.Groups[2].Value;
                string pStr   = m.Groups[3].Value;

                if (key == "Outside") continue;
                if (!s_KeyToType.TryGetValue(key, out var layerType))
                {
                    Debug.LogWarning($"[ForceJsonLoaderSpine] Unknown key '{key}' — skipped.");
                    continue;
                }
                if (!s_FamilyMap.TryGetValue(family, out var fam))
                {
                    Debug.LogWarning($"[ForceJsonLoaderSpine] Unsupported family '{family}' on '{key}' — skipped.");
                    continue;
                }

                string[] tokens = pStr.Split(',');
                var parameters = new float[tokens.Length];
                bool ok = true;
                for (int i = 0; i < tokens.Length; i++)
                    if (!float.TryParse(tokens[i].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out parameters[i]))
                    { ok = false; break; }
                if (!ok)
                {
                    Debug.LogWarning($"[ForceJsonLoaderSpine] Could not parse params for '{key}' — skipped.");
                    continue;
                }

                float jsonWidthMm = widthMap.TryGetValue(key, out float w2) ? w2 : 1f;

                result[layerType] = new LayerFitSpine
                {
                    type        = layerType,
                    jsonWidthMm = jsonWidthMm,
                    curve       = new LayerCurveSpine { family = fam.family, parameters = parameters },
                };
            }

            return result.Count > 0;
        }

        // Reads a JSON file from disk and parses it.
        public static bool TryLoad(string path, out Dictionary<LayerTypeSpine, LayerFitSpine> result)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[ForceJsonLoaderSpine] File not found: {path}");
                result = null;
                return false;
            }

            string json = File.ReadAllText(path);
            return TryParse(json, out result);
        }
    }
}
