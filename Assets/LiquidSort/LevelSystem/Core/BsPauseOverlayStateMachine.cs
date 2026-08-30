namespace BartenderSort.Core
{
    /// <summary>
    /// Round FSM'inden ayri, yalniz pause overlay icinde hangi kartin gorundugunu
    /// belirleyen kucuk sunum FSM'i. Gameplay her iki acik durumda da Paused kalir.
    ///
    /// BartenderSort projesinden taşındı. Orada BartenderSort.Game altındaydı; burada
    /// Core'a alındı çünkü içinde tek bir Unity tipi yok ve bu projede Game namespace'i
    /// yok. Davranış birebir aynı.
    /// </summary>
    public enum BsPauseOverlayState
    {
        Closed = 0,
        Settings = 1,
        ExitConfirmation = 2,
    }

    public enum BsPauseOverlayTrigger
    {
        PauseAccepted,
        ExitRequested,
        ExitCancelled,
        PauseEnded,
    }

    public sealed class BsPauseOverlayStateMachine
    {
        public BsPauseOverlayState State { get; private set; } =
            BsPauseOverlayState.Closed;

        public bool Can(BsPauseOverlayTrigger trigger) =>
            TryResolve(State, trigger, out _);

        public bool Dispatch(BsPauseOverlayTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsPauseOverlayState next))
                return false;

            State = next;
            return true;
        }

        public static bool TryResolve(BsPauseOverlayState from,
                                      BsPauseOverlayTrigger trigger,
                                      out BsPauseOverlayState next)
        {
            next = from;
            switch (trigger)
            {
                case BsPauseOverlayTrigger.PauseAccepted:
                    if (from != BsPauseOverlayState.Closed) return false;
                    next = BsPauseOverlayState.Settings;
                    return true;

                case BsPauseOverlayTrigger.ExitRequested:
                    if (from != BsPauseOverlayState.Settings) return false;
                    next = BsPauseOverlayState.ExitConfirmation;
                    return true;

                case BsPauseOverlayTrigger.ExitCancelled:
                    if (from != BsPauseOverlayState.ExitConfirmation) return false;
                    next = BsPauseOverlayState.Settings;
                    return true;

                case BsPauseOverlayTrigger.PauseEnded:
                    if (from == BsPauseOverlayState.Closed) return false;
                    next = BsPauseOverlayState.Closed;
                    return true;

                default:
                    return false;
            }
        }
    }
}
