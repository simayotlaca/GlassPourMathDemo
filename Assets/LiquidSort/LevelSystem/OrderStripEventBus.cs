using System;
using System.Collections.Generic;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Controller/shelf callback'lerini sipariş şeridinin tek olay diline çevirir.
    /// Bus statik değildir; her presenter kendi kuyruğuna sahiptir. İç içe yayınlar
    /// FIFO işlendiği için DOTween callback'i bir controller callback'inin ortasında
    /// presenter durumunu reentrant biçimde değiştiremez.
    /// </summary>
    internal enum OrderStripSignalKind
    {
        Activate,
        Deactivate,
        Rebind,
        LevelLoaded,
        SnapshotDirty,
        LevelStateChanged,
        Delivered,
        DeliveryPresentationFinished,
        StampHoldElapsed,
        QueueAnimationFinished,
        QueueAnimationAborted,
        DealAnimationFinished,
    }

    internal readonly struct OrderStripSignal
    {
        public OrderStripSignalKind Kind { get; }
        public BsLevel Level { get; }
        public BartenderLevelState LevelState { get; }
        public BartenderDeliveryReceipt Receipt { get; }
        public int PresentationEpoch { get; }

        private OrderStripSignal(OrderStripSignalKind kind, BsLevel level = null,
                                 BartenderLevelState levelState = default,
                                 BartenderDeliveryReceipt receipt = null,
                                 int presentationEpoch = -1)
        {
            Kind = kind;
            Level = level;
            LevelState = levelState;
            Receipt = receipt;
            PresentationEpoch = presentationEpoch;
        }

        public static OrderStripSignal Simple(OrderStripSignalKind kind) =>
            new OrderStripSignal(kind);

        public static OrderStripSignal Loaded(BsLevel level) =>
            new OrderStripSignal(OrderStripSignalKind.LevelLoaded, level);

        public static OrderStripSignal StateChanged(BartenderLevelState state) =>
            new OrderStripSignal(OrderStripSignalKind.LevelStateChanged,
                levelState: state);

        public static OrderStripSignal Delivery(BartenderDeliveryReceipt receipt) =>
            new OrderStripSignal(OrderStripSignalKind.Delivered, receipt: receipt);

        public static OrderStripSignal Epoch(OrderStripSignalKind kind, int epoch) =>
            new OrderStripSignal(kind, presentationEpoch: epoch);

        public static OrderStripSignal DeliveryFinished(
            BartenderDeliveryReceipt receipt) =>
            new OrderStripSignal(OrderStripSignalKind.DeliveryPresentationFinished,
                receipt: receipt);

        public static OrderStripSignal Presentation(OrderStripSignalKind kind, int epoch,
                                                    BartenderDeliveryReceipt receipt) =>
            new OrderStripSignal(kind, receipt: receipt, presentationEpoch: epoch);
    }

    internal sealed class OrderStripEventBus
    {
        private readonly Queue<OrderStripSignal> pending =
            new Queue<OrderStripSignal>();
        private Action<OrderStripSignal> subscribers;
        private bool publishing;

        public void Subscribe(Action<OrderStripSignal> subscriber)
        {
            subscribers -= subscriber;
            subscribers += subscriber;
        }

        public void Unsubscribe(Action<OrderStripSignal> subscriber) =>
            subscribers -= subscriber;

        public void Publish(OrderStripSignal signal)
        {
            pending.Enqueue(signal);
            if (publishing) return;

            publishing = true;
            try
            {
                while (pending.Count > 0)
                {
                    OrderStripSignal next = pending.Dequeue();
                    Action<OrderStripSignal> snapshot = subscribers;
                    if (snapshot == null) continue;

                    Delegate[] handlers = snapshot.GetInvocationList();
                    for (int i = 0; i < handlers.Length; i++)
                        ((Action<OrderStripSignal>)handlers[i])(next);
                }
            }
            catch
            {
                pending.Clear();
                throw;
            }
            finally
            {
                publishing = false;
            }
        }
    }
}
