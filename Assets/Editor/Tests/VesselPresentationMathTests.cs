using NUnit.Framework;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    /// <summary>
    /// Pins the three length classes of <see cref="VesselPresentationMath"/> against each
    /// other. A length that belongs to one class and is scaled like another is the whole
    /// bug class these tests exist to catch, and none of them needs a scene.
    /// </summary>
    public sealed class VesselPresentationMathTests
    {
        // The two boards the shipped showcase actually seats glasses at
        // (SortingShelfShowcase.unity, twoRow/threeRowSpaciousGlassScale).
        private const float TwoRowBoard = 0.78791803f;
        private const float ThreeRowBoard = 0.43527383f;

        [Test]
        public void Planar_world_scale_tracks_nested_uniform_scales_after_rotation()
        {
            var safeArea = new GameObject("Safe Area");
            var vessel = new GameObject("Vessel");
            try
            {
                safeArea.transform.localScale = Vector3.one * 0.8f;
                safeArea.transform.rotation = Quaternion.Euler(0f, 0f, 17f);
                vessel.transform.SetParent(safeArea.transform, false);
                vessel.transform.localScale = Vector3.one * 0.45f;
                vessel.transform.localRotation = Quaternion.Euler(0f, 0f, -11f);

                Assert.That(
                    VesselPresentationMath.PlanarWorldScale(vessel.transform),
                    Is.EqualTo(0.8f * 0.45f).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(safeArea);
            }
        }

        [Test]
        public void Royal_relative_scale_keeps_only_board_and_safe_area_factors()
        {
            const float royalReference = 0.654f;
            const float safeAreaFit = 0.82f;

            var safeArea = new GameObject("Safe Area");
            var seatRoot = new GameObject("SeatRoot");
            var vessel = new GameObject("Vessel");
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = royalReference;
                profile.shelfReferenceRotationDegrees = 7f;
                safeArea.transform.localScale = Vector3.one * safeAreaFit;
                seatRoot.transform.SetParent(safeArea.transform, false);
                seatRoot.transform.localScale = Vector3.one * ThreeRowBoard;
                vessel.transform.SetParent(seatRoot.transform, false);
                vessel.transform.localPosition = Vector3.zero;
                vessel.transform.localRotation = profile.ShelfReferenceLocalRotation;
                vessel.transform.localScale = profile.ShelfReferenceLocalScale;

                float relative = VesselPresentationMath.RelativeToRoyalReference(
                    vessel.transform, profile);
                Assert.That(relative,
                    Is.EqualTo(ThreeRowBoard * safeAreaFit).Within(0.00001f));
                Assert.That(
                    VesselPresentationMath.ReferenceDistance(0.16f, relative),
                    Is.EqualTo(0.16f * ThreeRowBoard * safeAreaFit).Within(0.00001f));
                Assert.That(vessel.transform.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(Quaternion.Angle(vessel.transform.localRotation,
                    profile.ShelfReferenceLocalRotation), Is.LessThan(0.0001f));
                Assert.That(vessel.transform.localScale,
                    Is.EqualTo(profile.ShelfReferenceLocalScale));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(safeArea);
            }
        }

        [TestCase(TwoRowBoard)]
        [TestCase(ThreeRowBoard)]
        public void Seat_root_places_support_without_rewriting_the_royal_local_pose(
            float boardScale)
        {
            var safeArea = new GameObject("Safe Area");
            var seatRoot = new GameObject("SeatRoot");
            var vessel = new GameObject("Vessel");
            var frontGlass = new GameObject("FrontGlass");
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = 0.539f;
                profile.shelfReferenceRotationDegrees = -4f;
                safeArea.transform.localScale = Vector3.one * 0.82f;
                safeArea.transform.rotation = Quaternion.Euler(0f, 0f, 13f);
                seatRoot.transform.SetParent(safeArea.transform, false);
                seatRoot.transform.localScale = Vector3.one * boardScale;
                vessel.transform.SetParent(seatRoot.transform, false);
                vessel.transform.localPosition = Vector3.zero;
                vessel.transform.localRotation = profile.ShelfReferenceLocalRotation;
                vessel.transform.localScale = profile.ShelfReferenceLocalScale;
                frontGlass.transform.SetParent(vessel.transform, false);
                frontGlass.transform.localPosition = new Vector3(0.08f, -0.03f, 0f);
                frontGlass.transform.localRotation = Quaternion.Euler(0f, 0f, 2f);
                frontGlass.transform.localScale = new Vector3(0.96f, 1.03f, 1f);

                Vector3 supportLocal = new Vector3(0.21f, -1.17f, 0f);
                Vector3 desiredWorld = new Vector3(1.34f, -2.08f, 0.4f);
                seatRoot.transform.position =
                    VesselPresentationMath.RootPositionForAnchoredPoint(
                        seatRoot.transform, frontGlass.transform, supportLocal,
                        desiredWorld);

                Vector3 actualWorld = frontGlass.transform.TransformPoint(supportLocal);
                Assert.That((actualWorld - desiredWorld).magnitude, Is.LessThan(0.00001f));
                Assert.That(vessel.transform.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(Quaternion.Angle(vessel.transform.localRotation,
                    profile.ShelfReferenceLocalRotation), Is.LessThan(0.0001f));
                Assert.That(vessel.transform.localScale,
                    Is.EqualTo(profile.ShelfReferenceLocalScale));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(safeArea);
            }
        }

        [Test]
        public void A_vessel_standing_in_royal_glass_lab_reports_no_shrink_at_all()
        {
            var vessel = new GameObject("Vessel");
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                // Exactly what RoyalGlassLabBuilder does: the vessel root wears the
                // profile's own reference scale under an unscaled parent.
                profile.shelfReferenceScale = 0.539f;
                vessel.transform.localScale = profile.ShelfReferenceLocalScale;

                Assert.That(
                    VesselPresentationMath.RoyalShrink(vessel.transform, profile),
                    Is.EqualTo(1f).Within(0.00001f),
                    "rho must be exactly one in the scene it is defined against, or every "
                    + "Royal-authored distance is wrong in its own reference scene.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(vessel);
            }
        }

        [Test]
        public void Local_length_reaches_the_same_world_length_through_either_route()
        {
            const float royalReference = 0.783f;
            const float localLength = 0.27f;

            var vessel = new GameObject("Vessel");
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = royalReference;
                vessel.transform.localScale =
                    Vector3.one * (royalReference * ThreeRowBoard);

                // The identity the whole bridge rests on: taking a vessel-local length
                // straight out to world must equal drawing it at Royal and then shrinking.
                float direct = VesselPresentationMath.LocalToWorld(
                    localLength, vessel.transform);
                float viaRoyal = VesselPresentationMath.ReferenceDistance(
                    VesselPresentationMath.LocalToRoyalWorld(localLength, profile),
                    VesselPresentationMath.RoyalShrink(vessel.transform, profile));

                Assert.That(direct, Is.EqualTo(viaRoyal).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(vessel);
            }
        }

        [Test]
        public void Royal_pixel_lengths_are_a_fixed_share_of_the_glass_at_every_board()
        {
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = 0.6f;   // BeerRoyal

                // The point of routing pixels through the profile rather than through a
                // screen derivative: the answer is the same number on a two-row board, on
                // a three-row board, and on a device nobody has tested on yet.
                float local = VesselPresentationMath.RoyalPixelsToLocal(1.25f, profile);
                Assert.That(local, Is.EqualTo(1.25f * (2f * 5.25f / 1920f) / 0.6f)
                    .Within(0.000001f));

                // Beer's baked top chord is about 2.3 local units wide, so the approved
                // inset is roughly 1% of it. The old screen-derived value reached almost
                // 4% of the same chord on a three-row shelf.
                Assert.That(local, Is.LessThan(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Royal_liquid_insets_keep_the_same_share_at_both_shipped_glass_scales()
        {
            const float referenceScale = 0.539f; // CocktailRoyal
            const float localChord = 2.24145f;

            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = referenceScale;
                float localInset = VesselPresentationMath.RoyalPixelsToLocal(1.25f, profile);

                float twoRowInset = localInset * referenceScale * TwoRowBoard;
                float twoRowChord = localChord * referenceScale * TwoRowBoard;
                float threeRowInset = localInset * referenceScale * ThreeRowBoard;
                float threeRowChord = localChord * referenceScale * ThreeRowBoard;

                Assert.That(twoRowInset / twoRowChord,
                    Is.EqualTo(threeRowInset / threeRowChord).Within(0.000001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void An_authored_layout_length_stays_the_same_share_of_a_smaller_glass()
        {
            const float authoredInset = 0.02f;

            float twoRow = VesselPresentationMath.RescaleAuthoredLength(
                authoredInset, TwoRowBoard, TwoRowBoard);
            float threeRow = VesselPresentationMath.RescaleAuthoredLength(
                authoredInset, TwoRowBoard, ThreeRowBoard);

            // The board it was eyeballed on is untouched, to the last float.
            Assert.That(twoRow, Is.EqualTo(authoredInset).Within(1e-7f));

            // And every other board buries the foot by the same share of the glass.
            Assert.That(threeRow / ThreeRowBoard,
                Is.EqualTo(twoRow / TwoRowBoard).Within(1e-6f));

            // Guarding the actual regression: left absolute, the sink is a constant
            // world depth, so a smaller glass swallows proportionally more of it.
            Assert.That(authoredInset / ThreeRowBoard,
                Is.GreaterThan(1.3f * (authoredInset / TwoRowBoard)),
                "the un-normalised inset must be measurably deeper on the smaller board, "
                + "or this test is no longer guarding anything.");
        }
    }
}
