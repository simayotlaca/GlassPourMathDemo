using System;
using BartenderSort.Core;
using NUnit.Framework;

namespace LiquidSort.Tests.EditMode
{
    // The fixture stays internal because its public test cases use internal runtime
    // state types exposed to Assembly-CSharp-Editor through InternalsVisibleTo.
    internal sealed class BsOrderPresentationStateMachinesTests
    {
        [Test]
        public void Strip_dispatch_matches_the_transition_table_for_every_pair()
        {
            Array states = Enum.GetValues(typeof(BsOrderStripState));
            Array triggers = Enum.GetValues(typeof(BsOrderStripTrigger));

            foreach (BsOrderStripState from in states)
            foreach (BsOrderStripTrigger trigger in triggers)
            {
                BsOrderStripStateMachine machine = CreateStripMachineIn(from);
                bool expectedAccepted = ExpectedStripTransition(
                    from, trigger, out BsOrderStripState expectedState);

                bool accepted = machine.Dispatch(trigger);

                Assert.That(accepted, Is.EqualTo(expectedAccepted),
                    $"Unexpected acceptance for {from} + {trigger}.");
                Assert.That(machine.State, Is.EqualTo(expectedState),
                    $"Unexpected target for {from} + {trigger}.");
            }
        }

        [TestCase(BsOrderStripState.Detached, false)]
        [TestCase(BsOrderStripState.Hidden, false)]
        [TestCase(BsOrderStripState.Dealing, true)]
        [TestCase(BsOrderStripState.Ready, false)]
        [TestCase(BsOrderStripState.StampHold, true)]
        [TestCase(BsOrderStripState.WaitingForDelivery, true)]
        [TestCase(BsOrderStripState.QueueAnimating, true)]
        [TestCase(BsOrderStripState.Faulted, true)]
        public void Strip_transition_playing_is_derived_only_from_busy_states(
            BsOrderStripState state, bool expected)
        {
            BsOrderStripStateMachine machine = CreateStripMachineIn(state);

            Assert.That(machine.TransitionPlaying, Is.EqualTo(expected));
        }

        [Test]
        public void Strip_complete_delivery_lifecycle_returns_to_ready()
        {
            var machine = new BsOrderStripStateMachine();

            AssertTransition(machine, BsOrderStripTrigger.Attach,
                BsOrderStripState.Hidden);
            AssertTransition(machine, BsOrderStripTrigger.LevelLoaded,
                BsOrderStripState.Hidden);
            AssertTransition(machine, BsOrderStripTrigger.BeginDeal,
                BsOrderStripState.Dealing);
            AssertTransition(machine, BsOrderStripTrigger.DealCompleted,
                BsOrderStripState.Ready);
            AssertTransition(machine, BsOrderStripTrigger.DeliveryCommitted,
                BsOrderStripState.StampHold);
            AssertTransition(machine, BsOrderStripTrigger.StampHoldElapsed,
                BsOrderStripState.WaitingForDelivery);
            AssertTransition(machine,
                BsOrderStripTrigger.DeliveryPresentationFinished,
                BsOrderStripState.QueueAnimating);
            AssertTransition(machine, BsOrderStripTrigger.QueueCompleted,
                BsOrderStripState.Ready);
        }

        [Test]
        public void Strip_delivery_finish_before_stamp_hold_does_not_mutate_state()
        {
            BsOrderStripStateMachine machine =
                CreateStripMachineIn(BsOrderStripState.StampHold);

            bool accepted = machine.Dispatch(
                BsOrderStripTrigger.DeliveryPresentationFinished);

            Assert.That(accepted, Is.False);
            Assert.That(machine.State, Is.EqualTo(BsOrderStripState.StampHold));
            Assert.That(machine.TransitionPlaying, Is.True);
        }

        [TestCase(BsOrderStripState.Hidden)]
        [TestCase(BsOrderStripState.Dealing)]
        [TestCase(BsOrderStripState.Ready)]
        [TestCase(BsOrderStripState.StampHold)]
        [TestCase(BsOrderStripState.WaitingForDelivery)]
        [TestCase(BsOrderStripState.QueueAnimating)]
        [TestCase(BsOrderStripState.Faulted)]
        public void Strip_level_boundary_preempts_every_attached_state(
            BsOrderStripState from)
        {
            BsOrderStripStateMachine loaded = CreateStripMachineIn(from);
            BsOrderStripStateMachine deactivated = CreateStripMachineIn(from);

            Assert.That(loaded.Dispatch(BsOrderStripTrigger.LevelLoaded), Is.True);
            Assert.That(loaded.State, Is.EqualTo(BsOrderStripState.Hidden));
            Assert.That(deactivated.Dispatch(
                BsOrderStripTrigger.LevelDeactivated), Is.True);
            Assert.That(deactivated.State, Is.EqualTo(BsOrderStripState.Hidden));
        }

        [Test]
        public void Card_dispatch_matches_the_transition_table_for_every_pair()
        {
            Array states = Enum.GetValues(typeof(BsOrderCardState));
            Array triggers = Enum.GetValues(typeof(BsOrderCardTrigger));

            foreach (BsOrderCardState from in states)
            foreach (BsOrderCardTrigger trigger in triggers)
            {
                BsOrderCardStateMachine machine = CreateCardMachineIn(from);
                bool expectedAccepted = ExpectedCardTransition(
                    from, trigger, out BsOrderCardState expectedState);

                bool accepted = machine.Dispatch(trigger);

                Assert.That(accepted, Is.EqualTo(expectedAccepted),
                    $"Unexpected acceptance for {from} + {trigger}.");
                Assert.That(machine.State, Is.EqualTo(expectedState),
                    $"Unexpected target for {from} + {trigger}.");
            }
        }

        [TestCase(BsOrderCardState.Uninitialized, false)]
        [TestCase(BsOrderCardState.Hidden, false)]
        [TestCase(BsOrderCardState.Dealing, true)]
        [TestCase(BsOrderCardState.Visible, false)]
        [TestCase(BsOrderCardState.Shifting, true)]
        [TestCase(BsOrderCardState.Exiting, true)]
        [TestCase(BsOrderCardState.Disabled, false)]
        public void Card_is_animating_is_derived_only_from_motion_states(
            BsOrderCardState state, bool expected)
        {
            BsOrderCardStateMachine machine = CreateCardMachineIn(state);

            Assert.That(machine.IsAnimating, Is.EqualTo(expected));
        }

        [TestCase(BsOrderCardState.Dealing, BsOrderCardState.Visible)]
        [TestCase(BsOrderCardState.Shifting, BsOrderCardState.Visible)]
        [TestCase(BsOrderCardState.Exiting, BsOrderCardState.Hidden)]
        public void Card_animation_completion_reaches_the_expected_rest_state(
            BsOrderCardState from, BsOrderCardState expected)
        {
            BsOrderCardStateMachine machine = CreateCardMachineIn(from);

            Assert.That(machine.Dispatch(BsOrderCardTrigger.AnimationCompleted),
                Is.True);
            Assert.That(machine.State, Is.EqualTo(expected));
            Assert.That(machine.IsAnimating, Is.False);
        }

        [TestCase(BsOrderCardState.Dealing)]
        [TestCase(BsOrderCardState.Shifting)]
        [TestCase(BsOrderCardState.Exiting)]
        public void Card_reset_visible_rejects_stale_animation_completion(
            BsOrderCardState from)
        {
            BsOrderCardStateMachine machine = CreateCardMachineIn(from);

            Assert.That(machine.Dispatch(BsOrderCardTrigger.ResetVisible), Is.True);
            Assert.That(machine.State, Is.EqualTo(BsOrderCardState.Visible));
            Assert.That(machine.Dispatch(BsOrderCardTrigger.AnimationCompleted),
                Is.False);
            Assert.That(machine.State, Is.EqualTo(BsOrderCardState.Visible));
            Assert.That(machine.IsAnimating, Is.False);
        }

        [TestCase(BsOrderCardTrigger.ResetVisible, BsOrderCardState.Visible)]
        [TestCase(BsOrderCardTrigger.ResetHidden, BsOrderCardState.Hidden)]
        public void Card_disabled_accepts_explicit_reset_intent(
            BsOrderCardTrigger reset, BsOrderCardState expected)
        {
            BsOrderCardStateMachine machine =
                CreateCardMachineIn(BsOrderCardState.Disabled);

            Assert.That(machine.Dispatch(reset), Is.True);
            Assert.That(machine.State, Is.EqualTo(expected));
            Assert.That(machine.IsAnimating, Is.False);
        }

        private static BsOrderStripStateMachine CreateStripMachineIn(
            BsOrderStripState state)
        {
            var machine = new BsOrderStripStateMachine();
            switch (state)
            {
                case BsOrderStripState.Detached:
                    break;
                case BsOrderStripState.Hidden:
                    Assert.That(machine.Dispatch(BsOrderStripTrigger.Attach), Is.True);
                    break;
                case BsOrderStripState.Dealing:
                    Assert.That(machine.Dispatch(BsOrderStripTrigger.Attach), Is.True);
                    Assert.That(machine.Dispatch(BsOrderStripTrigger.BeginDeal), Is.True);
                    break;
                case BsOrderStripState.Ready:
                    Assert.That(machine.Dispatch(BsOrderStripTrigger.Attach), Is.True);
                    Assert.That(machine.Dispatch(
                        BsOrderStripTrigger.ActivateLiveLevel), Is.True);
                    break;
                case BsOrderStripState.StampHold:
                    machine = CreateStripMachineIn(BsOrderStripState.Ready);
                    Assert.That(machine.Dispatch(
                        BsOrderStripTrigger.DeliveryCommitted), Is.True);
                    break;
                case BsOrderStripState.WaitingForDelivery:
                    machine = CreateStripMachineIn(BsOrderStripState.StampHold);
                    Assert.That(machine.Dispatch(
                        BsOrderStripTrigger.StampHoldElapsed), Is.True);
                    break;
                case BsOrderStripState.QueueAnimating:
                    machine = CreateStripMachineIn(
                        BsOrderStripState.WaitingForDelivery);
                    Assert.That(machine.Dispatch(
                        BsOrderStripTrigger.DeliveryPresentationFinished), Is.True);
                    break;
                case BsOrderStripState.Faulted:
                    Assert.That(machine.Dispatch(BsOrderStripTrigger.Attach), Is.True);
                    Assert.That(machine.Dispatch(
                        BsOrderStripTrigger.BindingRejected), Is.True);
                    break;
                default:
                    Assert.Fail($"No setup path for strip state {state}.");
                    break;
            }

            Assert.That(machine.State, Is.EqualTo(state));
            return machine;
        }

        private static BsOrderCardStateMachine CreateCardMachineIn(
            BsOrderCardState state)
        {
            var machine = new BsOrderCardStateMachine();
            switch (state)
            {
                case BsOrderCardState.Uninitialized:
                    break;
                case BsOrderCardState.Hidden:
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.InitializeHidden), Is.True);
                    break;
                case BsOrderCardState.Dealing:
                    Assert.That(machine.Dispatch(BsOrderCardTrigger.BeginDeal), Is.True);
                    break;
                case BsOrderCardState.Visible:
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.ShowImmediate), Is.True);
                    break;
                case BsOrderCardState.Shifting:
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.ShowImmediate), Is.True);
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.BeginShift), Is.True);
                    break;
                case BsOrderCardState.Exiting:
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.ShowImmediate), Is.True);
                    Assert.That(machine.Dispatch(
                        BsOrderCardTrigger.BeginExit), Is.True);
                    break;
                case BsOrderCardState.Disabled:
                    Assert.That(machine.Dispatch(BsOrderCardTrigger.Disable), Is.True);
                    break;
                default:
                    Assert.Fail($"No setup path for card state {state}.");
                    break;
            }

            Assert.That(machine.State, Is.EqualTo(state));
            return machine;
        }

        private static bool ExpectedStripTransition(
            BsOrderStripState from, BsOrderStripTrigger trigger,
            out BsOrderStripState next)
        {
            next = from;
            switch (trigger)
            {
                case BsOrderStripTrigger.Attach:
                    if (from != BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Hidden;
                    return true;
                case BsOrderStripTrigger.Detach:
                    next = BsOrderStripState.Detached;
                    return true;
                case BsOrderStripTrigger.LevelLoaded:
                case BsOrderStripTrigger.LevelDeactivated:
                    if (from == BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Hidden;
                    return true;
                case BsOrderStripTrigger.ActivateLiveLevel:
                    if (from != BsOrderStripState.Hidden) return false;
                    next = BsOrderStripState.Ready;
                    return true;
                case BsOrderStripTrigger.BeginDeal:
                    if (from != BsOrderStripState.Hidden
                        && from != BsOrderStripState.Ready) return false;
                    next = BsOrderStripState.Dealing;
                    return true;
                case BsOrderStripTrigger.DealCompleted:
                    if (from != BsOrderStripState.Dealing) return false;
                    next = BsOrderStripState.Ready;
                    return true;
                case BsOrderStripTrigger.DeliveryCommitted:
                    if (from != BsOrderStripState.Ready
                        && from != BsOrderStripState.Dealing) return false;
                    next = BsOrderStripState.StampHold;
                    return true;
                case BsOrderStripTrigger.StampHoldElapsed:
                    if (from != BsOrderStripState.StampHold) return false;
                    next = BsOrderStripState.WaitingForDelivery;
                    return true;
                case BsOrderStripTrigger.DeliveryPresentationFinished:
                    if (from != BsOrderStripState.WaitingForDelivery) return false;
                    next = BsOrderStripState.QueueAnimating;
                    return true;
                case BsOrderStripTrigger.QueueCompleted:
                    if (from != BsOrderStripState.QueueAnimating) return false;
                    next = BsOrderStripState.Ready;
                    return true;
                case BsOrderStripTrigger.BindingRejected:
                    if (from == BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Faulted;
                    return true;
                default:
                    return false;
            }
        }

        private static bool ExpectedCardTransition(
            BsOrderCardState from, BsOrderCardTrigger trigger,
            out BsOrderCardState next)
        {
            next = from;
            switch (trigger)
            {
                case BsOrderCardTrigger.InitializeHidden:
                case BsOrderCardTrigger.HideImmediate:
                case BsOrderCardTrigger.ResetHidden:
                    next = BsOrderCardState.Hidden;
                    return true;
                case BsOrderCardTrigger.ShowImmediate:
                case BsOrderCardTrigger.ResetVisible:
                    next = BsOrderCardState.Visible;
                    return true;
                case BsOrderCardTrigger.BeginDeal:
                    next = BsOrderCardState.Dealing;
                    return true;
                case BsOrderCardTrigger.BeginShift:
                    if (from != BsOrderCardState.Visible) return false;
                    next = BsOrderCardState.Shifting;
                    return true;
                case BsOrderCardTrigger.BeginExit:
                    if (from != BsOrderCardState.Visible
                        && from != BsOrderCardState.Shifting
                        && from != BsOrderCardState.Dealing) return false;
                    next = BsOrderCardState.Exiting;
                    return true;
                case BsOrderCardTrigger.AnimationCompleted:
                    if (from == BsOrderCardState.Dealing
                        || from == BsOrderCardState.Shifting)
                    {
                        next = BsOrderCardState.Visible;
                        return true;
                    }
                    if (from == BsOrderCardState.Exiting)
                    {
                        next = BsOrderCardState.Hidden;
                        return true;
                    }
                    return false;
                case BsOrderCardTrigger.Disable:
                    next = BsOrderCardState.Disabled;
                    return true;
                default:
                    return false;
            }
        }

        private static void AssertTransition(BsOrderStripStateMachine machine,
                                             BsOrderStripTrigger trigger,
                                             BsOrderStripState expected)
        {
            Assert.That(machine.Dispatch(trigger), Is.True,
                $"Expected {trigger} to be accepted from {machine.State}.");
            Assert.That(machine.State, Is.EqualTo(expected));
        }
    }
}
