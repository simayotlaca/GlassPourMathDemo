using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    /// <summary>
    /// Guards the C# -> shader handshake for every authored vessel. These tests exist
    /// because the approved Royal one-unit top face is part of the C# -> shader contract,
    /// not a material default that can silently change between the Lab and gameplay.
    /// </summary>
    public sealed class LiquidSurfaceContractTests
    {
        [TestCase(1, 1f)]
        [TestCase(2, 1f)]
        [TestCase(3, 1f)]
        [TestCase(4, 1f)]
        [TestCase(5, 1f)]
        public void One_unit_keeps_the_approved_full_surface_depth(
            int capacity, float expected)
        {
            Assert.That(
                LiquidSurfaceContract.ExposedSurfaceScale(1f, capacity),
                Is.EqualTo(expected).Within(0.00001f));
        }

        [Test]
        public void Every_authored_profile_uses_the_complete_liquid_contract()
        {
            List<VesselProfile> profiles = LoadProfiles();
            Assert.That(profiles, Is.Not.Empty,
                "At least one authored VesselProfile must exist.");

            for (int i = 0; i < profiles.Count; i++)
            {
                VesselProfile profile = profiles[i];
                Assert.That(profile.IsBaked, Is.True,
                    $"{profile.name} must have valid baked geometry tables.");
                Assert.That(profile.capacity, Is.InRange(1, LiquidBottle.MaxBands),
                    $"{profile.name} has an invalid capacity.");
                Assert.That(profile.interiorMask, Is.Not.Null,
                    $"{profile.name} is missing its baked interior mask.");
                Assert.That(profile.surfaceBulge, Is.InRange(0.02f, 0.20f),
                    $"{profile.name} has an invalid surfaceBulge.");
                Assert.That(profile.maxCapDepth, Is.InRange(0.01f, 0.30f),
                    $"{profile.name} has an invalid maxCapDepth.");
                Assert.That(profile.maxFillFraction, Is.InRange(0.50f, 1f),
                    $"{profile.name} has an invalid maxFillFraction.");
                Assert.That(profile.evenBandHeights, Is.InRange(0f, 1f),
                    $"{profile.name} has an invalid evenBandHeights.");
                Assert.That(profile.innerJunctionCurve, Is.InRange(0f, 1f),
                    $"{profile.name} has an invalid innerJunctionCurve.");
                Assert.That(profile.innerJunctionDepth, Is.InRange(0f, 0.25f),
                    $"{profile.name} has an invalid innerJunctionDepth.");
                Assert.That(
                    LiquidSurfaceContract.TryValidate(
                        profile.liquidMaterial, out string reason),
                    Is.True,
                    $"{profile.name}: {reason}");
            }
        }

        [Test]
        public void Every_authored_profile_publishes_approved_surface_to_renderer()
        {
            List<VesselProfile> profiles = LoadProfiles();
            var colors = new List<Color>(LiquidBottle.MaxBands);
            var propertyBlock = new MaterialPropertyBlock();

            for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            {
                VesselProfile profile = profiles[profileIndex];
                var root = new GameObject($"{profile.name} Contract Test");
                try
                {
                    LiquidBottle bottle = root.AddComponent<LiquidBottle>();
                    bottle.profile = profile;
                    bottle.capacity = profile.capacity;
                    float previousLevel = float.NegativeInfinity;

                    for (int units = 1; units <= profile.capacity; units++)
                    {
                        colors.Clear();
                        for (int i = 0; i < units; i++) colors.Add(Color.red);

                        bottle.SetUnits(colors);
                        bottle.Refresh();

                        Transform liquid = root.transform.Find("Liquid");
                        Assert.That(liquid, Is.Not.Null,
                            $"{profile.name} did not create its liquid renderer.");
                        MeshRenderer renderer = liquid.GetComponent<MeshRenderer>();
                        Assert.That(renderer, Is.Not.Null,
                            $"{profile.name} has no liquid MeshRenderer.");

                        renderer.GetPropertyBlock(propertyBlock);
                        float actual = propertyBlock.GetFloat(
                            LiquidSurfaceContract.SurfaceScaleId);
                        float expected = 1f;
                        Assert.That(actual, Is.EqualTo(expected).Within(0.00001f),
                            $"{profile.name}, {units}/{profile.capacity} units");

                        Assert.That(propertyBlock.GetFloat(
                                LiquidSurfaceContract.BulgeId),
                            Is.EqualTo(profile.surfaceBulge).Within(0.00001f),
                            $"{profile.name} surfaceBulge was not published.");
                        Assert.That(propertyBlock.GetFloat(
                                LiquidSurfaceContract.InnerCurveId),
                            Is.EqualTo(profile.innerJunctionCurve).Within(0.00001f),
                            $"{profile.name} innerJunctionCurve was not published.");
                        Assert.That(propertyBlock.GetFloat(
                                LiquidSurfaceContract.InnerBulgeId),
                            Is.EqualTo(profile.innerJunctionDepth).Within(0.00001f),
                            $"{profile.name} innerJunctionDepth was not published.");

                        Assert.That(propertyBlock.GetFloat(
                                LiquidSurfaceContract.BandCountId),
                            Is.EqualTo(1f).Within(0.00001f),
                            $"{profile.name} should merge identical test units.");
                        Vector4[] bandInfo = propertyBlock.GetVectorArray(
                            LiquidSurfaceContract.BandInfoId);
                        Assert.That(bandInfo, Is.Not.Empty,
                            $"{profile.name} did not publish band geometry.");

                        float level = bandInfo[0].x;
                        Assert.That(level, Is.GreaterThan(previousLevel),
                            $"{profile.name} waterline did not rise from "
                            + $"{units - 1} to {units} units.");
                        previousLevel = level;

                    }

                    Assert.That(previousLevel,
                        Is.LessThanOrEqualTo(profile.upright.ceilingY + 0.0001f),
                        $"{profile.name} full waterline exceeded its baked ceiling.");
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
        }

        private static List<VesselProfile> LoadProfiles()
        {
            string[] guids = AssetDatabase.FindAssets(
                "t:VesselProfile", new[] { "Assets/LiquidSort" });
            var paths = new List<string>(guids.Length);
            for (int i = 0; i < guids.Length; i++)
                paths.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
            paths.Sort(System.StringComparer.Ordinal);

            var profiles = new List<VesselProfile>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                VesselProfile profile =
                    AssetDatabase.LoadAssetAtPath<VesselProfile>(paths[i]);
                if (profile != null) profiles.Add(profile);
            }
            return profiles;
        }
    }
}
