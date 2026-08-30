namespace BartenderSort.Core
{
    /// <summary>
    /// Unity/panel/save bilmeyen saf tur FSM'i. Normal akışta Reset kapısı yoktur;
    /// bütün değişiklikler niyet belirten trigger veya atomik TryFinish üzerinden geçer.
    ///
    /// internal bırakıldı: bu projede de tek bir sahip tarafından sürülmesi gerekiyor.
    /// Assembly bölünmediği için LiquidSort.Levels tarafı erişebilir.
    /// </summary>
    internal sealed class BsRoundStateMachine
    {
        int _roundId;
        int _gameplayEpoch;

        public BsFlowState State { get; private set; }
        public BsRoundToken CurrentToken => new BsRoundToken(_roundId, _gameplayEpoch);

        public BsRoundStateMachine(BsFlowState initial = BsFlowState.Menu)
        {
            State = initial;
        }

        public bool Can(BsFlowTrigger trigger) => BsFlowRules.CanHandle(State, trigger);

        public BsTransitionResult Dispatch(BsFlowTrigger trigger)
        {
            BsFlowState from = State;
            if (!BsFlowRules.TryResolve(from, trigger, out BsFlowState next))
                return BsTransitionResult.Reject(from, trigger, CurrentToken);

            if (trigger == BsFlowTrigger.LoadRequested)
            {
                _roundId++;
                _gameplayEpoch++;
            }
            else if (trigger == BsFlowTrigger.LoadFailed
                     || trigger == BsFlowTrigger.AbandonConfirmed
                     || trigger == BsFlowTrigger.ReturnToMenu)
            {
                _gameplayEpoch++;
            }

            State = next;
            return BsTransitionResult.Accept(from, next, trigger, CurrentToken);
        }

        public bool TryGetPlayingToken(out BsRoundToken token)
        {
            token = CurrentToken;
            return State == BsFlowState.Playing;
        }

        public bool IsCurrentRound(int roundId) => roundId > 0 && roundId == _roundId;

        /// <summary>
        /// Token ayni gameplay nesline mi ait? Pause tokeni gecersiz kilmaz;
        /// yalniz load, terminal settlement ve menuye donus kilitler.
        /// </summary>
        public bool IsTokenCurrent(BsRoundToken token) => token == CurrentToken;

        public bool IsGameplayTokenValid(BsRoundToken token) =>
            State == BsFlowState.Playing && token == CurrentToken;

        public bool CanFinish(BsRoundToken token, out BsTransitionRejectReason rejectReason)
        {
            if (token != CurrentToken)
            {
                rejectReason = BsTransitionRejectReason.StaleRound;
                return false;
            }

            if (!BsFlowRules.CanFinish(State))
            {
                rejectReason = BsTransitionRejectReason.NotPlaying;
                return false;
            }

            rejectReason = BsTransitionRejectReason.None;
            return true;
        }

        public bool TryFinish(BsRoundToken token, BsRoundOutcome outcome,
                              out BsTransitionResult transition)
        {
            BsFlowState from = State;
            if (!CanFinish(token, out BsTransitionRejectReason rejectReason))
            {
                transition = BsTransitionResult.Reject(from, BsFlowTrigger.FinishRequested,
                    CurrentToken, rejectReason);
                return false;
            }

            BsFlowState next = outcome == BsRoundOutcome.Won
                ? BsFlowState.Won
                : BsFlowState.Failed;

            State = next;             // Önce terminal sonucu kilitle.
            _gameplayEpoch++;         // Sonra bütün gameplay callback'lerini geçersiz kıl.

            transition = BsTransitionResult.Accept(from, next,
                BsFlowTrigger.FinishRequested, CurrentToken);
            return true;
        }
    }
}
