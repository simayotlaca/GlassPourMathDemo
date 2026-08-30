namespace BartenderSort.Core
{
    /// <summary>
    /// Tur yaşam döngüsünün tek geçiş tablosu. Busy burada bulunmaz; bardak bazlı
    /// animasyon çakışmaları sunum katmanının kendi kilitleriyle yönetilir.
    /// </summary>
    public static class BsFlowRules
    {
        public static bool TryResolve(BsFlowState from, BsFlowTrigger trigger,
                                      out BsFlowState next)
        {
            next = from;

            switch (trigger)
            {
                case BsFlowTrigger.LoadRequested:
                    if (from == BsFlowState.Menu
                        || from == BsFlowState.Playing
                        || from == BsFlowState.Paused
                        || from == BsFlowState.Won
                        || from == BsFlowState.Failed)
                    {
                        next = BsFlowState.Loading;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.LevelLoaded:
                    if (from == BsFlowState.Loading)
                    {
                        next = BsFlowState.Playing;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.LoadFailed:
                    if (from == BsFlowState.Loading)
                    {
                        next = BsFlowState.Menu;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.PauseRequested:
                    if (from == BsFlowState.Playing)
                    {
                        next = BsFlowState.Paused;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.ResumeRequested:
                    if (from == BsFlowState.Paused)
                    {
                        next = BsFlowState.Playing;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.AbandonConfirmed:
                    if (from == BsFlowState.Paused)
                    {
                        next = BsFlowState.Menu;
                        return true;
                    }
                    return false;

                case BsFlowTrigger.ReturnToMenu:
                    if (from != BsFlowState.Menu)
                    {
                        next = BsFlowState.Menu;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        public static bool CanHandle(BsFlowState state, BsFlowTrigger trigger) =>
            trigger == BsFlowTrigger.FinishRequested
                ? CanFinish(state)
                : TryResolve(state, trigger, out _);

        public static bool CanFinish(BsFlowState state) => state == BsFlowState.Playing;
        public static bool AcceptsInput(BsFlowState state) => state == BsFlowState.Playing;
        public static bool CanPause(BsFlowState state) =>
            CanHandle(state, BsFlowTrigger.PauseRequested);
        public static bool IsLevelOver(BsFlowState state) =>
            state == BsFlowState.Won || state == BsFlowState.Failed;
        public static bool TimersRun(BsFlowState state) => state == BsFlowState.Playing;
    }
}
