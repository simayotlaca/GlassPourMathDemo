using System.Reflection;
using BartenderSort.Core;
using LiquidSort.Levels;
using NUnit.Framework;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    public sealed class BartenderAutomaticPauseTests
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private GameObject host;
        private GameObject settingsOverlay;
        private GameObject settingsCard;
        private GameObject exitConfirmationCard;
        private BartenderLevelController controller;
        private BartenderSession session;
        private BartenderPausePresenter presenter;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Bartender automatic-pause test host");
            host.SetActive(false);
            controller = host.AddComponent<BartenderLevelController>();
            controller.DisableAutomaticLoadAtRuntime();
            session = host.AddComponent<BartenderSession>();
            presenter = host.AddComponent<BartenderPausePresenter>();

            settingsOverlay = new GameObject("Settings overlay");
            settingsCard = new GameObject("Settings card");
            settingsCard.transform.SetParent(settingsOverlay.transform, false);
            exitConfirmationCard = new GameObject("Exit confirmation card");
            exitConfirmationCard.transform.SetParent(settingsOverlay.transform, false);
            SetField(session, "controller", controller);
            SetField(presenter, "session", session);
            SetField(presenter, "controller", controller);
            SetField(presenter, "settingsOverlay", settingsOverlay);
            SetField(presenter, "settingsCard", settingsCard);
            SetField(presenter, "exitConfirmationCard", exitConfirmationCard);

            Invoke(session, "Dispatch", BsFlowTrigger.LoadRequested);
            Invoke(session, "Dispatch", BsFlowTrigger.LevelLoaded);
            SetField(session, "mirroredState", BartenderLevelState.Playing);
            SetControllerState(BartenderLevelState.Playing);
            Invoke(session, "Subscribe");
            Invoke(presenter, "Subscribe");
            Invoke(presenter, "Project", BsFlowState.Playing);
        }

        [TearDown]
        public void TearDown()
        {
            if (settingsOverlay != null) Object.DestroyImmediate(settingsOverlay);
            if (host != null) Object.DestroyImmediate(host);
            settingsOverlay = null;
            settingsCard = null;
            exitConfirmationCard = null;
            controller = null;
            session = null;
            presenter = null;
        }

        [Test]
        public void Focus_owned_pause_stays_silent_and_resumes_when_focus_returns()
        {
            SetField(controller, "activeAttemptId", "focus-pause-attempt");
            SetField(controller, "applicationFocusLost", true);

            Invoke(controller, "MaintainApplicationPause");

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(presenter.OverlayState, Is.EqualTo(BsPauseOverlayState.Closed));
            Assert.That(settingsOverlay.activeSelf, Is.False);
            Assert.That(settingsCard.activeSelf, Is.False);

            SetField(controller, "applicationFocusLost", false);
            Invoke(controller, "MaintainApplicationPause");

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Playing));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Playing));
        }

        [Test]
        public void Gear_button_pause_opens_settings_and_focus_return_does_not_resume_it()
        {
            presenter.OpenPauseMenu();

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(presenter.OverlayState, Is.EqualTo(BsPauseOverlayState.Settings));
            Assert.That(settingsOverlay.activeSelf, Is.True);
            Assert.That(settingsCard.activeSelf, Is.True);

            SetField(controller, "applicationFocusLost", true);
            Invoke(controller, "MaintainApplicationPause");
            SetField(controller, "applicationFocusLost", false);
            Invoke(controller, "MaintainApplicationPause");

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(settingsOverlay.activeSelf, Is.True);
        }

        [Test]
        public void Automatic_pause_waits_until_both_suspension_signals_clear()
        {
            SetField(controller, "activeAttemptId", "overlap-pause-attempt");
            SetField(controller, "applicationFocusLost", true);
            SetField(controller, "applicationPaused", true);
            Invoke(controller, "MaintainApplicationPause");

            SetField(controller, "applicationFocusLost", false);
            Invoke(controller, "MaintainApplicationPause");
            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(settingsOverlay.activeSelf, Is.False);

            SetField(controller, "applicationPaused", false);
            Invoke(controller, "MaintainApplicationPause");
            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Playing));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Playing));
        }

        [Test]
        public void Rejected_gear_pause_does_not_arm_the_next_programmatic_pause()
        {
            var barrierOwner = new object();
            Assert.That(controller.AcquirePresentationBarrier(barrierOwner), Is.True);

            presenter.OpenPauseMenu();

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Playing));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Playing));
            Assert.That(presenter.OverlayState, Is.EqualTo(BsPauseOverlayState.Closed));
            Assert.That(settingsOverlay.activeSelf, Is.False);

            Assert.That(controller.ReleasePresentationBarrier(barrierOwner), Is.True);
            Assert.That(controller.Pause(), Is.True);

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(presenter.OverlayState, Is.EqualTo(BsPauseOverlayState.Closed));
            Assert.That(settingsOverlay.activeSelf, Is.False);
            Assert.That(settingsCard.activeSelf, Is.False);
        }

        [Test]
        public void Cancelling_exit_returns_to_settings_and_keeps_gameplay_paused()
        {
            presenter.OpenPauseMenu();
            presenter.RequestExitToMainMenu();

            Assert.That(presenter.OverlayState,
                Is.EqualTo(BsPauseOverlayState.ExitConfirmation));
            Assert.That(settingsCard.activeSelf, Is.False);
            Assert.That(exitConfirmationCard.activeSelf, Is.True);

            presenter.CancelExitToMainMenu();

            Assert.That(controller.State, Is.EqualTo(BartenderLevelState.Paused));
            Assert.That(session.State, Is.EqualTo(BsFlowState.Paused));
            Assert.That(presenter.OverlayState, Is.EqualTo(BsPauseOverlayState.Settings));
            Assert.That(settingsOverlay.activeSelf, Is.True);
            Assert.That(settingsCard.activeSelf, Is.True);
            Assert.That(exitConfirmationCard.activeSelf, Is.False);
        }

        private void SetControllerState(BartenderLevelState state)
        {
            PropertyInfo property = typeof(BartenderLevelController).GetProperty(
                nameof(BartenderLevelController.State), InstanceFlags);
            Assert.That(property, Is.Not.Null);
            MethodInfo setter = property.GetSetMethod(true);
            Assert.That(setter, Is.Not.Null);
            setter.Invoke(controller, new object[] { state });
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, InstanceFlags);
            Assert.That(field, Is.Not.Null, "Missing field: " + fieldName);
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, InstanceFlags);
            Assert.That(method, Is.Not.Null, "Missing method: " + methodName);
            method.Invoke(target, arguments);
        }
    }
}
