using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Converts RoyalGlassLab-authored presentation distances into the current shelf's
    /// world scale. Liquid fill remains profile-local: uniform scaling multiplies both
    /// the filled and total cross-section by the same s^2, so their ratio is unchanged.
    /// Only world-space animation distances need the scale relative to the Royal pose.
    /// </summary>
    public static class VesselPresentationMath
    {
        private const float MinimumScale = 0.0001f;

        /// <summary>
        /// Effective two-dimensional world scale of one vessel-local unit. The square root
        /// of the transformed XY area remains correct under rotation and nested uniform
        /// parents, unlike reading one lossyScale axis in isolation.
        /// </summary>
        public static float PlanarWorldScale(Transform vessel)
        {
            if (vessel == null) return 1f;

            Vector3 worldRight = vessel.TransformVector(Vector3.right);
            Vector3 worldUp = vessel.TransformVector(Vector3.up);
            float worldArea = Vector3.Cross(worldRight, worldUp).magnitude;
            return Mathf.Max(MinimumScale, Mathf.Sqrt(worldArea));
        }

        /// <summary>
        /// Board and safe-area multiplier on top of the canonical RoyalGlassLab scale:
        /// s_relative = s_currentWorld / s_profileReference.
        /// </summary>
        public static float RelativeToRoyalReference(Transform vessel, VesselProfile profile)
        {
            if (vessel == null) return 1f;
            float referenceScale = profile != null ? profile.ShelfReferenceScale : 1f;
            return Mathf.Max(MinimumScale,
                PlanarWorldScale(vessel) / referenceScale);
        }

        /// <summary>
        /// Scales a distance authored in RoyalGlassLab world units for the current board.
        /// </summary>
        public static float ReferenceDistance(float royalWorldDistance,
                                              float relativeScale) =>
            royalWorldDistance * Mathf.Max(MinimumScale, relativeScale);
    }
}
