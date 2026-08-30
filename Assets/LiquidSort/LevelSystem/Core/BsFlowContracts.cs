using System;

namespace BartenderSort.Core
{
    // Tur akışı sözleşmeleri. BartenderSort projesinden taşındı — orada da aynı
    // namespace altında duruyor, yani iki taraf ilerde birbirine bakabilir.
    //
    // Bilerek GETİRİLMEYENLER: BsAttemptId, BsCommandId, BsSettlementRequest,
    // BsSettlementReceipt, BsRoundSnapshot. Onlar BsRoundCoordinator'ın attempt /
    // settlement / persistence hattına ait ve tek başlarına bir işe yaramıyor; bu
    // projede komut kapısını BartenderLevelController zaten receipt'lerle yapıyor.
    // İkisini üst üste koymak iki ayrı yetke demek olurdu.

    /// <summary>Üst akışta yalnız birbirini dışlayan tur yaşam döngüsü durumları bulunur.</summary>
    public enum BsFlowState
    {
        Menu = 0,
        Loading = 1,
        Playing = 2,
        // 3 eski Busy değeriydi. Save/telemetry uyumluluğu için diğer sayılar korunur.
        Paused = 4,
        Won = 5,
        Failed = 6,
    }

    /// <summary>Hedef state yerine oyuncu/sistem niyetini tarif eden FSM tetikleri.</summary>
    public enum BsFlowTrigger
    {
        LoadRequested,
        LevelLoaded,
        LoadFailed,
        PauseRequested,
        ResumeRequested,
        FinishRequested,
        ReturnToMenu,
        // Sona eklenir: daha once serialize/telemetry edilmis trigger sayilarini kaydirmaz.
        AbandonConfirmed,
    }

    public enum BsRoundOutcome
    {
        Won,
        Failed,
    }

    public enum BsRoundEndReason
    {
        OrdersCompleted,
        NoLegalMoves,
        Unsolvable,
        OrderTimedOut,
        PlayerAbandoned,
    }

    public enum BsTransitionRejectReason
    {
        None,
        InvalidTransition,
        StaleRound,
        NotPlaying,
    }

    /// <summary>
    /// Bir gameplay neslini tanımlar. RoundId yeni level örneğinde, GameplayEpoch
    /// ise load/terminal iptalinde değişir; eski coroutine böylece yeni tura yazamaz.
    /// </summary>
    public readonly struct BsRoundToken : IEquatable<BsRoundToken>
    {
        public int RoundId { get; }
        public int GameplayEpoch { get; }

        public BsRoundToken(int roundId, int gameplayEpoch)
        {
            RoundId = roundId;
            GameplayEpoch = gameplayEpoch;
        }

        public bool Equals(BsRoundToken other) =>
            RoundId == other.RoundId && GameplayEpoch == other.GameplayEpoch;

        public override bool Equals(object obj) => obj is BsRoundToken other && Equals(other);
        public override int GetHashCode() => (RoundId * 397) ^ GameplayEpoch;
        public static bool operator ==(BsRoundToken left, BsRoundToken right) => left.Equals(right);
        public static bool operator !=(BsRoundToken left, BsRoundToken right) => !left.Equals(right);
        public override string ToString() => $"Round {RoundId} / Epoch {GameplayEpoch}";
    }

    public readonly struct BsTransitionResult
    {
        public bool Accepted { get; }
        public BsFlowState From { get; }
        public BsFlowState To { get; }
        public BsFlowTrigger Trigger { get; }
        public BsRoundToken Token { get; }
        public BsTransitionRejectReason RejectReason { get; }

        BsTransitionResult(bool accepted, BsFlowState from, BsFlowState to,
                           BsFlowTrigger trigger, BsRoundToken token,
                           BsTransitionRejectReason rejectReason)
        {
            Accepted = accepted;
            From = from;
            To = to;
            Trigger = trigger;
            Token = token;
            RejectReason = rejectReason;
        }

        public static BsTransitionResult Accept(BsFlowState from, BsFlowState to,
                                                BsFlowTrigger trigger, BsRoundToken token) =>
            new BsTransitionResult(true, from, to, trigger, token, BsTransitionRejectReason.None);

        public static BsTransitionResult Reject(BsFlowState state, BsFlowTrigger trigger,
                                                BsRoundToken token,
                                                BsTransitionRejectReason reason =
                                                    BsTransitionRejectReason.InvalidTransition) =>
            new BsTransitionResult(false, state, state, trigger, token, reason);
    }
}
