using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// The small, shared contract between the liquid layout code and BottleLiquid.shader.
    /// Keeping the property name and the volume rule here prevents a visual refactor from
    /// silently dropping one half of the C# -> shader handshake again.
    /// </summary>
    public static class LiquidSurfaceContract
    {
        public const string ShaderName = "LiquidSort/BottleLiquid";
        public const string BulgeProperty = "_Bulge";
        public const string BulgeMaxProperty = "_BulgeMax";
        public const string InnerCurveProperty = "_InnerCurve";
        public const string InnerBulgeProperty = "_InnerBulge";
        public const string InnerMaxProperty = "_InnerMax";
        public const string SurfaceScaleProperty = "_SurfaceScale";

        public static readonly int BulgeId = Shader.PropertyToID(BulgeProperty);
        public static readonly int BulgeMaxId = Shader.PropertyToID(BulgeMaxProperty);
        public static readonly int InnerCurveId = Shader.PropertyToID(InnerCurveProperty);
        public static readonly int InnerBulgeId = Shader.PropertyToID(InnerBulgeProperty);
        public static readonly int BandInfoId = Shader.PropertyToID("_BandInfo");
        public static readonly int BandCountId = Shader.PropertyToID("_BandCount");
        public static readonly int SurfaceScaleId =
            Shader.PropertyToID(SurfaceScaleProperty);

        private static readonly string[] RequiredMaterialProperties =
        {
            "_MaskTex",
            BulgeProperty,
            BulgeMaxProperty,
            InnerCurveProperty,
            InnerBulgeProperty,
            InnerMaxProperty,
            SurfaceScaleProperty,
            "_Wave",
            "_SplashAmp",
            "_SplashX",
            "_SplashLife",
            "_CapFlash",
            "_QuadSize"
        };

        /// <summary>
        /// Share of the exposed top-face depth owned by the currently displayed volume.
        /// One unit therefore uses 1/capacity on every vessel, while a full vessel uses 1.
        /// </summary>
        public static float ExposedSurfaceScale(float displayVolume, int capacity) =>
            Mathf.Clamp01(displayVolume / Mathf.Max(1, capacity));

        /// <summary>
        /// Fails closed when a replacement material no longer honours the runtime
        /// contract. A missing property must never degrade silently into a full-depth
        /// surface on every partially filled vessel.
        /// </summary>
        public static bool TryValidate(Material material, out string reason)
        {
            if (material == null)
            {
                reason = "liquid material is missing";
                return false;
            }

            if (material.shader == null)
            {
                reason = $"material '{material.name}' has no shader";
                return false;
            }

            for (int i = 0; i < RequiredMaterialProperties.Length; i++)
            {
                string property = RequiredMaterialProperties[i];
                if (material.HasProperty(Shader.PropertyToID(property))) continue;

                reason = $"material '{material.name}' using shader "
                       + $"'{material.shader.name}' does not expose {property}";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
