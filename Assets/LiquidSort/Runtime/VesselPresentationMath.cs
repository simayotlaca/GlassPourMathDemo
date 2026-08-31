using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// The proportion bridge between RoyalGlassLab and a gameplay shelf.
    ///
    /// RoyalGlassLab is the canonical presentation: every vessel stands there at exactly
    /// <see cref="VesselProfile.ShelfReferenceScale"/>, in front of a 5.25 orthographic
    /// camera framed 1080x1920. A shelf shows the same vessel smaller, because a board of
    /// three rows has to fit four glasses across. Writing that shrink as one number,
    ///
    ///     rho = s_world / s_royal
    ///
    /// splits every length in the game into exactly three kinds, and the whole file exists
    /// to keep them apart:
    ///
    ///   (A) VESSEL-LOCAL lengths - waterlines, chords, cap depths, the baked tables.
    ///       A uniform scale multiplies the filled and the total cross-section by the same
    ///       s^2, so their ratio never moves. Royal's volume-to-height law is therefore
    ///       already reproduced exactly at any rho, and nothing here has to convert it.
    ///       <see cref="LocalToWorld"/> only takes such a length out to world space.
    ///
    ///   (B) ROYAL-WORLD lengths - shared pour distances authored once, in the Royal
    ///       scene, against no particular vessel: lip drop, minimum fall, selection lift.
    ///       These are Royal proportions only after multiplying by rho.
    ///       <see cref="ReferenceDistance"/>.
    ///
    ///   (C) SCREEN-ANCHORED lengths - authored in pixels. These are the ones that quietly
    ///       break: a pixel is a fixed share of the SCREEN, so as the vessel shrinks the
    ///       same pixel count eats a growing share of the GLASS, and it changes again on a
    ///       different device resolution or camera size. <see cref="RoyalLocalUnitsPerPixel"/>
    ///       freezes the authored pixel intent at Royal's own framing and hands back a
    ///       vessel-local length, which is then class (A) again and survives everything.
    ///
    /// A length that belongs to one class and is scaled like another is the entire bug
    /// class this file replaces.
    /// </summary>
    public static class VesselPresentationMath
    {
        private const float MinimumScale = 0.0001f;

        /// <summary>Orthographic size of the canonical RoyalGlassLab camera.</summary>
        public const float RoyalOrthographicSize = 5.25f;

        /// <summary>Pixel height RoyalGlassLab's presentation was framed against.</summary>
        public const float RoyalFramePixelHeight = 1920f;

        /// <summary>
        /// World units one Royal screen pixel covers. Constant by construction: it is the
        /// authoring framing, never the device the game happens to run on.
        /// </summary>
        public const float RoyalWorldUnitsPerPixel =
            2f * RoyalOrthographicSize / RoyalFramePixelHeight;

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
        /// rho = s_currentWorld / s_profileReference. One at Royal, by definition.
        /// </summary>
        public static float RelativeToRoyalReference(Transform vessel, VesselProfile profile)
        {
            if (vessel == null) return 1f;
            float referenceScale = profile != null ? profile.ShelfReferenceScale : 1f;
            return Mathf.Max(MinimumScale,
                PlanarWorldScale(vessel) / referenceScale);
        }

        /// <summary>Same value, named after what it means rather than how it is built.</summary>
        public static float RoyalShrink(Transform vessel, VesselProfile profile) =>
            RelativeToRoyalReference(vessel, profile);

        /// <summary>
        /// (A) A vessel-local length in current world units. Use this for anything the
        /// vessel asset itself authored - pour pose clearances, stream widths, mouth
        /// offsets - so it shrinks with the glass exactly as Royal shows it.
        /// </summary>
        public static float LocalToWorld(float localLength, Transform vessel) =>
            localLength * PlanarWorldScale(vessel);

        /// <summary>
        /// (A) The same vessel-local length as RoyalGlassLab would draw it, in Royal world
        /// units. <c>LocalToWorld == RoyalWorldLength * rho</c> is the identity the whole
        /// bridge rests on, and the one the tests pin.
        /// </summary>
        public static float LocalToRoyalWorld(float localLength, VesselProfile profile) =>
            localLength * (profile != null ? profile.ShelfReferenceScale : 1f);

        /// <summary>
        /// (B) Scales a distance authored in RoyalGlassLab world units for the current board.
        /// </summary>
        public static float ReferenceDistance(float royalWorldDistance,
                                              float relativeScale) =>
            royalWorldDistance * Mathf.Max(MinimumScale, relativeScale);

        /// <summary>
        /// (C) How much VESSEL-LOCAL length one authored pixel stood for in RoyalGlassLab.
        ///
        /// This is the escape from screen space. A shader that derives its own units per
        /// pixel keeps an authored inset the same size on the SCREEN, so the inset grows
        /// against a shrinking glass and moves again on the next device resolution. The
        /// Royal answer depends on nothing but the profile, so a shelf glass at rho = 0.435
        /// on a 1170x2532 phone erodes its top face by the same share of its own chord as
        /// the approved Royal vessel does.
        /// </summary>
        public static float RoyalLocalUnitsPerPixel(VesselProfile profile) =>
            RoyalWorldUnitsPerPixel
            / Mathf.Max(MinimumScale,
                profile != null ? profile.ShelfReferenceScale : 1f);

        /// <summary>
        /// (C) An authored pixel length as a vessel-local length, Royal-proportional.
        /// </summary>
        public static float RoyalPixelsToLocal(float pixels, VesselProfile profile) =>
            pixels * RoyalLocalUnitsPerPixel(profile);

        /// <summary>
        /// A length authored against one board scale, re-expressed so it stays the same
        /// share of the vessel at another. Layout constants such as how deep a foot sinks
        /// into a plank are authored by eye on one board and then silently reused on a
        /// board whose glasses are half the size; this is what keeps them proportional
        /// without changing the look of the board they were tuned on.
        /// </summary>
        public static float RescaleAuthoredLength(float authoredLength,
                                                  float authoredAtScale,
                                                  float currentScale) =>
            authoredLength * Mathf.Max(MinimumScale, currentScale)
                           / Mathf.Max(MinimumScale, authoredAtScale);
    }
}
