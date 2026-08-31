using NUnit.Framework;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    public sealed class VesselPresentationMathTests
    {
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
            const float boardFit = 0.43527383f;
            const float safeAreaFit = 0.82f;

            var safeArea = new GameObject("Safe Area");
            var vessel = new GameObject("Vessel");
            VesselProfile profile = ScriptableObject.CreateInstance<VesselProfile>();
            try
            {
                profile.shelfReferenceScale = royalReference;
                safeArea.transform.localScale = Vector3.one * safeAreaFit;
                vessel.transform.SetParent(safeArea.transform, false);
                vessel.transform.localScale =
                    Vector3.one * (royalReference * boardFit);

                float relative = VesselPresentationMath.RelativeToRoyalReference(
                    vessel.transform, profile);
                Assert.That(relative,
                    Is.EqualTo(boardFit * safeAreaFit).Within(0.00001f));
                Assert.That(
                    VesselPresentationMath.ReferenceDistance(0.16f, relative),
                    Is.EqualTo(0.16f * boardFit * safeAreaFit).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(safeArea);
            }
        }
    }
}
