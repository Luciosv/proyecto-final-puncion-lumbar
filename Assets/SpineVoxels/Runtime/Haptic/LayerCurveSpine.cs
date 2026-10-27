using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    public enum CurveFamilySpine
    {
        Constant, Linear, Polynomial2, Polynomial3,
        Exponential, SaturatingExp, Sigmoid, KvRupture, SimonePop
    }

    public struct LayerCurveSpine
    {
        public CurveFamilySpine family;
        public float[]          parameters;

        // dz in mm (already rescaled to JSON fitting domain by dzScale before calling).
        public float Evaluate(float dz_mm)
        {
            float[] p = parameters;
            switch (family)
            {
                case CurveFamilySpine.Constant:
                    return p[0];

                case CurveFamilySpine.Linear:
                    return p[0] + p[1] * dz_mm;

                case CurveFamilySpine.Polynomial2:
                    return p[0] + p[1] * dz_mm + p[2] * dz_mm * dz_mm;

                case CurveFamilySpine.Polynomial3:
                    return p[0] + p[1] * dz_mm + p[2] * dz_mm * dz_mm + p[3] * dz_mm * dz_mm * dz_mm;

                case CurveFamilySpine.Exponential:
                    return p[0] + p[1] * Mathf.Exp(Mathf.Clamp(p[2] * dz_mm, -30f, 30f));

                case CurveFamilySpine.SaturatingExp:
                    // F = Finf - (Finf - F0) * exp(-alpha * dz)
                    return p[1] - (p[1] - p[0]) * Mathf.Exp(Mathf.Clamp(-p[2] * dz_mm, -30f, 30f));

                case CurveFamilySpine.Sigmoid:
                    // F = L / (1 + exp(-k*(dz - x0)))
                    return p[0] / (1f + Mathf.Exp(Mathf.Clamp(-p[1] * (dz_mm - p[2]), -30f, 30f)));

                case CurveFamilySpine.KvRupture:
                {
                    float k = p[0], Fr = p[1], slope = p[2];
                    float rise = k * dz_mm;
                    if (rise < Fr) return rise;
                    float dz_r = Fr / Mathf.Max(k, 1e-6f);
                    return Fr + slope * (dz_mm - dz_r);
                }

                case CurveFamilySpine.SimonePop:
                {
                    float val = p[0] + p[1] * dz_mm;
                    return val >= p[2] ? 0.5f : val;
                }

                default:
                    return 0f;
            }
        }
    }
}
