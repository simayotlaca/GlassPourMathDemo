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
        /// Royal's exposed top face keeps its authored full ellipse at every non-empty
        /// volume. The unit waterline still rises with volume; only the perspective depth
        /// remains stable, matching the approved one-unit source appearance.
        /// </summary>
        public static float ExposedSurfaceScale(float displayVolume, int capacity) => 1f;

        /// <summary>
        /// Fails closed when a replacement material no longer honours the runtime
        /// contract. Keeping the property in the handshake prevents replacement shaders
        /// from silently interpreting the approved full-depth value differently.
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
