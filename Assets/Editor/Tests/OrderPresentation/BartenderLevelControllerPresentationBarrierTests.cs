using System.Reflection;
using LiquidSort.Levels;
using NUnit.Framework;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    public sealed class BartenderLevelControllerPresentationBarrierTests
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private GameObject host;
        private BartenderLevelController controller;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("BartenderLevelController barrier test host");
            controller = host.AddComponent<BartenderLevelController>();
            controller.DisableAutomaticLoadAtRuntime();
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.DestroyImmediate(host);
            host = null;
            controller = null;
        }

        [Test]
        public void Soft_barriers_overlap_and_release_independently()
        {
            var firstOwner = new object();
            var secondOwner = new object();

            Assert.That(controller.PresentationLocked, Is.False);
            Assert.That(controller.AcquirePresentationBarrier(firstOwner), Is.True);
            Assert.That(controller.AcquirePresentationBarrier(secondOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.True);
            Assert.That(controller.IsPresentationBarrierOwnedBy(firstOwner), Is.True);
            Assert.That(controller.IsPresentationBarrierOwnedBy(secondOwner), Is.True);

            Assert.That(controller.ReleasePresentationBarrier(firstOwner), Is.True);
            Assert.That(controller.IsPresentationBarrierOwnedBy(firstOwner), Is.False);
            Assert.That(controller.IsPresentationBarrierOwnedBy(secondOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.True);

            Assert.That(controller.ReleasePresentationBarrier(secondOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.False);
        }

        [Test]
        public void Exact_revision_lock_coexists_with_soft_barrier_and_aggregates()
        {
            var barrierOwner = new object();
            var exactOwner = new object();
            int revision = controller.BoardRevision;

            Assert.That(controller.TryAcquirePresentationLock(
                exactOwner, revision + 1), Is.False);
            Assert.That(controller.AcquirePresentationBarrier(barrierOwner), Is.True);
            Assert.That(controller.TryAcquirePresentationLock(
                exactOwner, revision), Is.True);
            Assert.That(controller.IsPresentationLockOwnedBy(
                exactOwner, revision), Is.True);
            Assert.That(controller.PresentationLocked, Is.True);

            Assert.That(controller.ReleasePresentationBarrier(barrierOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.True,
                "The exact revision owner must keep the aggregate lock closed.");
            Assert.That(controller.AcquirePresentationBarrier(barrierOwner), Is.True);
            Assert.That(controller.ReleasePresentationLock(
                exactOwner, revision), Is.True);
            Assert.That(controller.PresentationLocked, Is.True,
                "The soft barrier must keep the aggregate lock closed.");

            Assert.That(controller.ReleasePresentationBarrier(barrierOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.False);
        }

        [Test]
        public void Exact_revision_lock_rejects_wrong_owner_and_revision()
        {
            var owner = new object();
            var stranger = new object();
            int revision = controller.BoardRevision;

            Assert.That(controller.TryAcquirePresentationLock(owner, revision), Is.True);
            Assert.That(controller.IsPresentationLockOwnedBy(owner, revision), Is.True);
            Assert.That(controller.IsPresentationLockOwnedBy(
                owner, revision + 1), Is.False);
            Assert.That(controller.IsPresentationLockOwnedBy(
                stranger, revision), Is.False);
            Assert.That(controller.ReleasePresentationLock(
                stranger, revision), Is.False);
            Assert.That(controller.ReleasePresentationLock(
                owner, revision + 1), Is.False);
            Assert.That(controller.PresentationLocked, Is.True);

            Assert.That(controller.ReleasePresentationLock(owner, revision), Is.True);
            Assert.That(controller.PresentationLocked, Is.False);
        }

        [Test]
        public void Null_owners_are_rejected_without_mutating_lock_state()
        {
            Assert.That(controller.AcquirePresentationBarrier(null), Is.False);
            Assert.That(controller.ReleasePresentationBarrier(null), Is.False);
            Assert.That(controller.IsPresentationBarrierOwnedBy(null), Is.False);
            Assert.That(controller.TryAcquirePresentationLock(
                null, controller.BoardRevision), Is.False);
            Assert.That(controller.TryAcquireLoadPresentationLock(
                null, controller.BoardRevision), Is.False);
            Assert.That(controller.IsPresentationLockOwnedBy(
                null, controller.BoardRevision), Is.False);
            Assert.That(controller.ReleasePresentationLock(
                null, controller.BoardRevision), Is.False);
            Assert.That(controller.PresentationLocked, Is.False);

            var validOwner = new object();
            Assert.That(controller.AcquirePresentationBarrier(validOwner), Is.True);
            Assert.That(controller.ReleasePresentationBarrier(null), Is.False);
            Assert.That(controller.PresentationLocked, Is.True);
            Assert.That(controller.ReleasePresentationBarrier(validOwner), Is.True);
            Assert.That(controller.PresentationLocked, Is.False);
        }

        [Test]
        public void Tick_stays_frozen_until_the_last_soft_barrier_is_released()
        {
            var firstOwner = new object();
            var secondOwner = new object();
            SetState(BartenderLevelState.Playing);
            SetActiveGameplayTime(12.5d);
            Assert.That(controller.AcquirePresentationBarrier(firstOwner), Is.True);
            Assert.That(controller.AcquirePresentationBarrier(secondOwner), Is.True);

            controller.Tick(0.25f);
            Assert.That(ReadActiveGameplayTime(), Is.EqualTo(12.5d));
            Assert.That(controller.ReleasePresentationBarrier(firstOwner), Is.True);
            controller.Tick(0.25f);
            Assert.That(ReadActiveGameplayTime(), Is.EqualTo(12.5d));

            Assert.That(controller.ReleasePresentationBarrier(secondOwner), Is.True);
            controller.Tick(0.25f);
            Assert.That(ReadActiveGameplayTime(),
                Is.EqualTo(12.75d).Within(0.0000001d));
        }

        private void SetState(BartenderLevelState state)
        {
            PropertyInfo property = typeof(BartenderLevelController).GetProperty(
                nameof(BartenderLevelController.State), InstanceFlags);
            Assert.That(property, Is.Not.Null);
            MethodInfo setter = property.GetSetMethod(true);
            Assert.That(setter, Is.Not.Null);
            setter.Invoke(controller, new object[] { state });
        }

        private void SetActiveGameplayTime(double value)
        {
            FieldInfo field = ActiveGameplayTimeField();
            field.SetValue(controller, value);
        }

        private double ReadActiveGameplayTime()
        {
            FieldInfo field = ActiveGameplayTimeField();
            return (double)field.GetValue(controller);
        }

        private static FieldInfo ActiveGameplayTimeField()
        {
            FieldInfo field = typeof(BartenderLevelController).GetField(
                "activeGameplayTime", InstanceFlags);
            Assert.That(field, Is.Not.Null);
            return field;
        }
    }
}
