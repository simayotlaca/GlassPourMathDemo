namespace BartenderSort.Core
{
    /// <summary>
    /// Sipariş şeridinin tek yetkili sunum durumu. Gameplay FSM'inden ayrıdır: yalnız
    /// kartların snapshot, teslim damgası ve kuyruk animasyonu arasındaki sırasını
    /// korur. Unity veya DOTween tipi bilmediği için geçiş tablosu doğrudan test
    /// edilebilir.
    /// </summary>
    internal enum BsOrderStripState
    {
        Detached = 0,
        Hidden = 1,
        Dealing = 2,
        Ready = 3,
        StampHold = 4,
        WaitingForDelivery = 5,
        QueueAnimating = 6,
        Faulted = 7,
    }

    internal enum BsOrderStripTrigger
    {
        Attach,
        Detach,
        LevelLoaded,
        LevelDeactivated,
        ActivateLiveLevel,
        BeginDeal,
        DealCompleted,
        DeliveryCommitted,
        StampHoldElapsed,
        DeliveryPresentationFinished,
        QueueCompleted,
        BindingRejected,
    }

    internal sealed class BsOrderStripStateMachine
    {
        public BsOrderStripState State { get; private set; } =
            BsOrderStripState.Detached;

        public bool TransitionPlaying =>
            State == BsOrderStripState.Dealing
            || State == BsOrderStripState.StampHold
            || State == BsOrderStripState.WaitingForDelivery
            || State == BsOrderStripState.QueueAnimating
            || State == BsOrderStripState.Faulted;

        public bool Dispatch(BsOrderStripTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsOrderStripState next)) return false;
            State = next;
            return true;
        }

        internal static bool TryResolve(BsOrderStripState from,
                                        BsOrderStripTrigger trigger,
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
                        && from != BsOrderStripState.Ready)
                        return false;
                    next = BsOrderStripState.Dealing;
                    return true;

                case BsOrderStripTrigger.DealCompleted:
                    if (from != BsOrderStripState.Dealing) return false;
                    next = BsOrderStripState.Ready;
                    return true;

                case BsOrderStripTrigger.DeliveryCommitted:
                    // Normal giriş Ready'dir. Dealing de kabul edilir; dışarıdan gelen
                    // geçerli bir teslimi sırf sunum bariyeri gecikti diye kaybetmeyiz.
                    if (from != BsOrderStripState.Ready
                        && from != BsOrderStripState.Dealing)
                        return false;
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
    }

    /// <summary>
    /// Tek kartın görünürlük/poz yaşam döngüsü. Canvas alpha ve RectTransform bu
    /// durumların projeksiyonudur; ayrı bool kombinasyonları karar vermez.
    /// </summary>
    internal enum BsOrderCardState
    {
        Uninitialized = 0,
        Hidden = 1,
        Dealing = 2,
        Visible = 3,
        Shifting = 4,
        Exiting = 5,
        Disabled = 6,
    }

    internal enum BsOrderCardTrigger
    {
        InitializeHidden,
        ShowImmediate,
        HideImmediate,
        BeginDeal,
        BeginShift,
        BeginExit,
        AnimationCompleted,
        ResetVisible,
        ResetHidden,
        Disable,
    }

    internal sealed class BsOrderCardStateMachine
    {
        public BsOrderCardState State { get; private set; } =
            BsOrderCardState.Uninitialized;

        public bool IsAnimating => State == BsOrderCardState.Dealing
                                   || State == BsOrderCardState.Shifting
                                   || State == BsOrderCardState.Exiting;

        public bool Dispatch(BsOrderCardTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsOrderCardState next)) return false;
            State = next;
            return true;
        }

        internal static bool TryResolve(BsOrderCardState from,
                                        BsOrderCardTrigger trigger,
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
                        && from != BsOrderCardState.Dealing)
                        return false;
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
    }
}
