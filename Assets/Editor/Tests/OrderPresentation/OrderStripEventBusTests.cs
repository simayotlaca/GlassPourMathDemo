using System;
using System.Collections.Generic;
using LiquidSort.Levels;
using NUnit.Framework;

namespace LiquidSort.Tests.EditMode
{
    public sealed class OrderStripEventBusTests
    {
        [Test]
        public void Nested_publish_is_fifo_and_never_reenters_subscribers()
        {
            var bus = new OrderStripEventBus();
            var trace = new List<string>();
            int callbackDepth = 0;
            int maximumDepth = 0;

            bus.Subscribe(signal =>
            {
                callbackDepth++;
                maximumDepth = Math.Max(maximumDepth, callbackDepth);
                trace.Add("first:" + signal.Kind);
                if (signal.Kind == OrderStripSignalKind.Activate)
                    bus.Publish(OrderStripSignal.Simple(
                        OrderStripSignalKind.SnapshotDirty));
                callbackDepth--;
            });
            bus.Subscribe(signal =>
                trace.Add("second:" + signal.Kind));

            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));

            CollectionAssert.AreEqual(new[]
            {
                "first:Activate",
                "second:Activate",
                "first:SnapshotDirty",
                "second:SnapshotDirty",
            }, trace);
            Assert.That(maximumDepth, Is.EqualTo(1));
        }

        [Test]
        public void Subscribing_the_same_handler_twice_delivers_once()
        {
            var bus = new OrderStripEventBus();
            int calls = 0;
            Action<OrderStripSignal> handler = _ => calls++;

            bus.Subscribe(handler);
            bus.Subscribe(handler);
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));

            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void Unsubscribe_stops_future_delivery()
        {
            var bus = new OrderStripEventBus();
            int calls = 0;
            Action<OrderStripSignal> handler = _ => calls++;

            bus.Subscribe(handler);
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));
            bus.Unsubscribe(handler);
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Deactivate));

            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void Subscriber_changes_affect_the_next_signal_not_the_current_snapshot()
        {
            var bus = new OrderStripEventBus();
            var trace = new List<string>();
            Action<OrderStripSignal> second = signal =>
                trace.Add("second:" + signal.Kind);
            Action<OrderStripSignal> third = signal =>
                trace.Add("third:" + signal.Kind);
            Action<OrderStripSignal> first = signal =>
            {
                trace.Add("first:" + signal.Kind);
                if (signal.Kind != OrderStripSignalKind.Activate) return;
                bus.Unsubscribe(second);
                bus.Subscribe(third);
            };

            bus.Subscribe(first);
            bus.Subscribe(second);
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.SnapshotDirty));

            CollectionAssert.AreEqual(new[]
            {
                "first:Activate",
                "second:Activate",
                "first:SnapshotDirty",
                "third:SnapshotDirty",
            }, trace);
        }

        [Test]
        public void Subscriber_exception_clears_nested_work_and_bus_recovers()
        {
            var bus = new OrderStripEventBus();
            var observed = new List<OrderStripSignalKind>();
            Action<OrderStripSignal> throwing = signal =>
            {
                bus.Publish(OrderStripSignal.Simple(
                    OrderStripSignalKind.SnapshotDirty));
                throw new InvalidOperationException("expected test failure");
            };

            bus.Subscribe(throwing);
            Assert.Throws<InvalidOperationException>(() =>
                bus.Publish(OrderStripSignal.Simple(
                    OrderStripSignalKind.Activate)));

            bus.Unsubscribe(throwing);
            bus.Subscribe(signal => observed.Add(signal.Kind));
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Deactivate));

            CollectionAssert.AreEqual(
                new[] { OrderStripSignalKind.Deactivate }, observed);
        }

        [Test]
        public void Signal_payload_is_preserved_through_fifo_delivery()
        {
            var bus = new OrderStripEventBus();
            var received = new List<OrderStripSignal>();
            var receipt = new BartenderDeliveryReceipt(
                19, 2, null, null, null);
            bus.Subscribe(signal => received.Add(signal));

            bus.Publish(OrderStripSignal.Epoch(
                OrderStripSignalKind.QueueAnimationFinished, 37));
            bus.Publish(OrderStripSignal.DeliveryFinished(receipt));
            bus.Publish(OrderStripSignal.Presentation(
                OrderStripSignalKind.QueueAnimationAborted, 41, receipt));

            Assert.That(received, Has.Count.EqualTo(3));
            Assert.That(received[0].Kind,
                Is.EqualTo(OrderStripSignalKind.QueueAnimationFinished));
            Assert.That(received[0].PresentationEpoch, Is.EqualTo(37));
            Assert.That(received[0].Level, Is.Null);
            Assert.That(received[0].Receipt, Is.Null);

            Assert.That(received[1].Kind,
                Is.EqualTo(OrderStripSignalKind.DeliveryPresentationFinished));
            Assert.That(received[1].Receipt, Is.SameAs(receipt));
            Assert.That(received[1].PresentationEpoch, Is.EqualTo(-1));

            Assert.That(received[2].Kind,
                Is.EqualTo(OrderStripSignalKind.QueueAnimationAborted));
            Assert.That(received[2].PresentationEpoch, Is.EqualTo(41));
            Assert.That(received[2].Receipt, Is.SameAs(receipt));
        }

        [Test]
        public void Event_bus_instances_do_not_share_subscribers_or_pending_signals()
        {
            var firstBus = new OrderStripEventBus();
            var secondBus = new OrderStripEventBus();
            int firstCalls = 0;
            int secondCalls = 0;
            firstBus.Subscribe(_ => firstCalls++);
            secondBus.Subscribe(_ => secondCalls++);

            firstBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));

            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.Zero);
        }

        [Test]
        public void Publishing_without_subscribers_is_not_replayed_later()
        {
            var bus = new OrderStripEventBus();
            int calls = 0;

            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));
            bus.Subscribe(_ => calls++);
            bus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Deactivate));

            Assert.That(calls, Is.EqualTo(1));
        }
    }
}
