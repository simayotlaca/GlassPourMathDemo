using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public enum BartenderLevelState
    {
        Unloaded,
        Playing,
        Paused,
        Won,
        Failed,
        CampaignComplete
    }

    public enum BartenderFailureReason
    {
        None,
        NoLegalMoves,
        OrderTimedOut
    }

    /// <summary>Terminal navigation has non-boolean success outcomes.</summary>
    public enum BartenderTerminalCommandResult
    {
        Rejected,
        NextLevelLoaded,
        CurrentLevelReloaded,
        CampaignCompleted,
        ReturnedToMainMenu
    }

    /// <summary>
    /// Detached description of one committed rule move. A later view layer can animate
    /// these snapshots without owning or mutating the live board.
    /// </summary>
    public sealed class BartenderPourReceipt
    {
        public int Revision { get; }
        public int Amount { get; }
        public int ColorIndex { get; }
        public RtGlass SourceBefore { get; }
        public RtGlass SourceAfter { get; }
        public RtGlass TargetBefore { get; }
        public RtGlass TargetAfter { get; }

        internal BartenderPourReceipt(int revision, PourResult result,
                                      RtGlass sourceBefore, RtGlass sourceAfter,
                                      RtGlass targetBefore, RtGlass targetAfter)
        {
            Revision = revision;
            Amount = result.Amount;
            ColorIndex = result.Color;
            SourceBefore = sourceBefore;
            SourceAfter = sourceAfter;
            TargetBefore = targetBefore;
            TargetAfter = targetAfter;
        }
    }

    /// <summary>Detached data needed to present one committed delivery.</summary>
    public sealed class BartenderDeliveryReceipt
    {
        public int Revision { get; }
        public int SlotIndex { get; }
        public RtGlass DeliveredGlass { get; }
        public OrderDef DeliveredOrder { get; }
        public OrderDef ReplacementOrder { get; }

        internal BartenderDeliveryReceipt(int revision, int slotIndex,
                                           RtGlass deliveredGlass,
                                           OrderDef deliveredOrder,
                                           OrderDef replacementOrder)
        {
            Revision = revision;
            SlotIndex = slotIndex;
            DeliveredGlass = deliveredGlass;
            DeliveredOrder = deliveredOrder;
            ReplacementOrder = replacementOrder;
        }
    }

    /// <summary>
    /// Scene-independent campaign host for the imported BartenderSort levels.
    ///
    /// It owns level loading, the authoritative BsBoard, rule commands, order clocks and
    /// saved campaign progress. It deliberately does not create GameObjects, read pointer
    /// input, lay out glasses or run animations. Final artwork can be connected later via
    /// a small view adapter and the detached command receipts.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderLevelController : MonoBehaviour
    {
        private const string DefaultPaletteResource = "BsPalette";

        private enum OrderExpiryResult
        {
            NotExpired,
            Settled,
            SettlementRejected,
        }

        /// <summary>
        /// Undo restores rule state and the exact order deadlines that belonged to it.
        /// Deadlines are absolute points on <see cref="activeGameplayTime"/>; restoring a
        /// memento therefore never gives back time spent after the captured move.
        /// </summary>
        private sealed class BoardMemento
        {
            public BsBoard Board;
            public double?[] SlotDeadlines;
        }

        [Header("Campaign")]
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool resumeSavedProgress = true;
        [SerializeField, Min(1)] private int startingLevelNumber = 1;
        [SerializeField] private string progressKey = "LiquidSort.Bartender.NextLevelSlot";

        [Header("Campaign data")]
        [SerializeField] private BsPalette palette;

        [Header("Booster kapasitesi")]
        [Tooltip("Geri al yığınının derinliği. Level başına ayrılan bellek bu kadar "
               + "board klonu; 0 geri almayı tamamen kapatır.")]
        [SerializeField, Min(0)] private int undoHistoryDepth = 32;

        private static List<BsLevel> cachedCampaign;

        private readonly Dictionary<OrderDef, double> orderDeadlines =
            new Dictionary<OrderDef, double>(ReferenceComparer<OrderDef>.Instance);
        private readonly List<OrderDef> timerRemovalScratch = new List<OrderDef>();
        private readonly List<int> timeBoostOrderScratch = new List<int>(4);
        /// <summary>Committed board/deadline mementos, oldest first. Undo pops the last one.</summary>
        private readonly List<BoardMemento> undoHistory = new List<BoardMemento>();
        private readonly List<Layer> shuffleScratch = new List<Layer>(48);

        private BsBoard board;
        private double activeGameplayTime;
        private double[] timeBonusByOrderIndex = Array.Empty<double>();
        private bool commandInProgress;
        private bool notificationInProgress;
        private object presentationLockOwner;
        private int presentationLockRevision = -1;
        private readonly HashSet<object> presentationBarrierOwners =
            new HashSet<object>(ReferenceComparer<object>.Instance);
        private bool hasPendingStateNotification;
        private BartenderLevelState pendingStateNotification;
        private string activeAttemptId;
        private bool settlementInProgress;
        private bool automaticLoadDisabledAtRuntime;
        private bool startHasRun;
        private bool applicationPaused;
        private bool applicationFocusLost;
        private bool suppressNextGameplayTick;
        private bool ownsAutomaticPause;
        private string automaticPauseAttemptId;
        private int automaticPauseStateGeneration = -1;
        private int stateGeneration;
        private bool terminalSettlementPending;
        private BartenderFailureReason pendingFailureReason;
        private int pendingTimedOutOrderSlot = -1;
        private float nextSettlementRetryTime;

        public BsLevel CurrentLevel { get; private set; }
        public int CurrentCampaignSlot { get; private set; } = -1;
        public int BoardRevision { get; private set; }
        public BartenderLevelState State { get; private set; } = BartenderLevelState.Unloaded;
        public BartenderFailureReason FailureReason { get; private set; }
        public int TimedOutOrderSlot { get; private set; } = -1;
        public int CampaignCount => Campaign.Count;
        public BsPalette Palette => palette;

        /// <summary>Kalan booster stokları. Level yüklenirken asset'ten kopyalanır.</summary>
        public int UndoRemaining { get; private set; }
        public int TimeBoostRemaining { get; private set; }
        public int ShuffleRemaining { get; private set; }
        /// <summary>Geri alınacak bir hamle var mı — stok ayrıca sayılır.</summary>
        public bool HasUndoableMove => undoHistory.Count > 0;

        /// <summary>
        /// A detached board clone. UI, tests and future presentation adapters cannot mutate
        /// the live rule state by editing this value.
        /// </summary>
        public BsBoard Board => board?.Clone();

        /// <summary>
        /// Zero-based slot of the next unlocked level. CampaignCount is the sentinel that
        /// means the campaign has been completed.
        /// </summary>
        public int NextUnlockedCampaignSlot => Mathf.Clamp(
            BartenderProgressService.NextUnlockedCampaignSlot, 0, Campaign.Count);

        /// <summary>
        /// Human-facing level number stored in the next unlocked campaign asset. This is
        /// intentionally not slot+1 because imported campaigns may use sparse indices.
        /// </summary>
        public int NextUnlockedLevelNumber
        {
            get
            {
                int slot = NextUnlockedCampaignSlot;
                if (Campaign.Count == 0) return 1;
                if (slot >= Campaign.Count)
                {
                    BsLevel finalLevel = Campaign[Campaign.Count - 1];
                    return finalLevel != null ? finalLevel.Index : Campaign.Count;
                }
                BsLevel level = Campaign[slot];
                return level != null ? level.Index : slot + 1;
            }
        }

        /// <summary>
        /// True while a view is animating an already committed board revision. The domain
        /// remains authoritative, but timers and additional commands wait until that visual
        /// transaction has reconciled.
        /// </summary>
        public bool PresentationLocked => presentationLockOwner != null
                                          || presentationBarrierOwners.Count > 0;

        /// <summary>
        /// Read-only ownership check for presentation adapters. A view may start an
        /// animated reconciliation only when the exact owner holds the exact board
        /// revision; a direct domain caller therefore cannot accidentally start an
        /// unlocked portal flight.
        /// </summary>
        public bool IsPresentationLockOwnedBy(object owner, int committedRevision) =>
            owner != null && ReferenceEquals(presentationLockOwner, owner)
            && presentationLockRevision == committedRevision;

        /// <summary>
        /// Registers a view-owned presentation barrier that is independent from the exact
        /// revision lock above. Multiple presenters may overlap; gameplay clocks and public
        /// mutations stay frozen until every owner has released its barrier.
        /// </summary>
        public bool AcquirePresentationBarrier(object owner)
        {
            if (owner == null) return false;
            presentationBarrierOwners.Add(owner);
            return true;
        }

        /// <summary>Releases a barrier previously registered by the same owner token.</summary>
        public bool ReleasePresentationBarrier(object owner) =>
            owner != null && presentationBarrierOwners.Remove(owner);

        /// <summary>Read-only ownership probe used by presentation adapters and tests.</summary>
        public bool IsPresentationBarrierOwnedBy(object owner) =>
            owner != null && presentationBarrierOwners.Contains(owner);

        public event Action<BsLevel> LevelLoaded;
        public event Action BoardChanged;
        public event Action OrdersChanged;
        public event Action<BartenderLevelState> StateChanged;
        public event Action<BartenderPourReceipt> Poured;
        public event Action<BartenderDeliveryReceipt> Delivered;
        /// <summary>Stok veya geri-al yığını değişti; alt şerit sayaçlarını tazeler.</summary>
        public event Action BoostersChanged;
        /// <summary>Kabul edilen +süre miktarı; sunum isterse kartları pulse ettirir.</summary>
        public event Action<float> TimeBoosted;

        private static List<BsLevel> Campaign
        {
            get
            {
                if (cachedCampaign != null) return cachedCampaign;

                BsLevel[] found = Resources.LoadAll<BsLevel>("Levels");
                cachedCampaign = new List<BsLevel>(found);
                cachedCampaign.Sort((a, b) =>
                {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    return a.Index.CompareTo(b.Index);
                });
                return cachedCampaign;
            }
        }

        private void Start()
        {
            startHasRun = true;
            ResolveDependencies();
            if (!loadOnStart || automaticLoadDisabledAtRuntime)
            {
                if (NextUnlockedCampaignSlot >= Campaign.Count && Campaign.Count > 0)
                    SetState(BartenderLevelState.CampaignComplete);
                return;
            }

            if (resumeSavedProgress)
            {
                ResumeSavedCampaign();
                return;
            }

            int slot = FindCampaignSlot(startingLevelNumber);

            if (slot >= Campaign.Count)
            {
                SetState(BartenderLevelState.CampaignComplete);
                return;
            }

            if (slot < 0) slot = 0;
            LoadCampaignSlot(slot);
        }

        private void Update()
        {
            MaintainApplicationPause();
            Tick(Time.unscaledDeltaTime);
        }

        private void OnApplicationPause(bool paused)
        {
            applicationPaused = paused;
            suppressNextGameplayTick = true;
            BartenderProgressService.Refresh();
            MaintainApplicationPause();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationFocusLost = !hasFocus;
            suppressNextGameplayTick = true;
            BartenderProgressService.Refresh();
            MaintainApplicationPause();
        }

        /// <summary>
        /// Called by a same-scene menu bootstrap before Start. Serialized authoring data
        /// stays untouched while runtime auto-load is suppressed.
        /// </summary>
        public void DisableAutomaticLoadAtRuntime()
        {
            if (startHasRun || State != BartenderLevelState.Unloaded) return;
            automaticLoadDisabledAtRuntime = true;
        }

        /// <summary>Advances order clocks; public for deterministic tests and hosts.</summary>
        public void Tick(float unscaledDeltaTime)
        {
            if (terminalSettlementPending)
            {
                RetryPendingTerminalSettlement();
                return;
            }
            if (applicationPaused || applicationFocusLost) return;
            if (suppressNextGameplayTick)
            {
                suppressNextGameplayTick = false;
                return;
            }
            if (MutationBlocked || State != BartenderLevelState.Playing
                || unscaledDeltaTime <= 0f) return;
            activeGameplayTime += unscaledDeltaTime;
            OrderExpiryResult expiry = ExpireOrderIfNeeded(out string rejectionReason);
            if (expiry == OrderExpiryResult.SettlementRejected)
                ArmPendingTerminalSettlement(BartenderFailureReason.OrderTimedOut,
                    FindExpiredOrderSlot(), rejectionReason);
        }

        public bool LoadLevelNumber(int oneBasedLevelNumber)
        {
            int slot = FindCampaignSlot(oneBasedLevelNumber);
            return slot >= 0 && LoadCampaignSlot(slot);
        }

#if UNITY_EDITOR
        internal bool EditorLevelJumpReady => startHasRun;

        /// <summary>
        /// Level Jumper'ın Editor-only kapısı. Hedefi ve canı mevcut turu değiştirmeden
        /// doğrular; etkin makbuzu hedef slota taşır, eski sunumu normal Unloaded olayıyla
        /// temizler ve üretimde kullanılan level yükleme/event zincirini aynen çalıştırır.
        /// </summary>
        internal bool EditorTryJumpToLevelNumber(
            int oneBasedLevelNumber, out bool ownershipTouched,
            out string ownedAttemptId, out int ownedAttemptSlot,
            out string rejectionReason)
        {
            ownershipTouched = false;
            ownedAttemptId = null;
            ownedAttemptSlot = -1;
            rejectionReason = null;
            if (!startHasRun)
            {
                rejectionReason = "Level sunumu henüz hazırlanıyor";
                return false;
            }
            if (MutationBlocked)
            {
                rejectionReason = "Sunum veya başka bir level işlemi sürüyor";
                return false;
            }

            int targetSlot = FindCampaignSlot(oneBasedLevelNumber);
            if (targetSlot < 0 || targetSlot >= Campaign.Count)
            {
                rejectionReason = $"Level {oneBasedLevelNumber} kampanyada yok";
                return false;
            }
            if (BartenderProgressService.Lives <= 0)
            {
                rejectionReason = "Can 0; Level Jumper'dan canı doldur";
                return false;
            }

            BsLevel targetLevel = Campaign[targetSlot];
            if (!TryValidateLevel(targetLevel, out string validationError))
            {
                rejectionReason = $"Level {oneBasedLevelNumber} geçersiz: {validationError}";
                return false;
            }
            try { BsBoard.FromLevel(targetLevel); }
            catch (Exception exception)
            {
                rejectionReason = "Level kuralları oluşturulamadı: " + exception.Message;
                return false;
            }

            bool hasActiveAttempt = !string.IsNullOrEmpty(activeAttemptId)
                                 && CurrentCampaignSlot >= 0;
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !hasActiveAttempt)
            {
                rejectionReason = "Etkin turun Editor makbuzu bulunamadı";
                return false;
            }
            if (hasActiveAttempt
                && !BartenderProgressService.EditorTryRetargetActiveAttempt(
                    activeAttemptId, CurrentCampaignSlot, targetSlot,
                    out rejectionReason))
                return false;
            if (hasActiveAttempt)
            {
                ownershipTouched = true;
                ownedAttemptId = activeAttemptId;
                ownedAttemptSlot = targetSlot;
            }

            if (State != BartenderLevelState.Unloaded || board != null
                || CurrentLevel != null || CurrentCampaignSlot >= 0)
                UnloadInternal(BartenderLevelState.Unloaded);

            bool loaded = TryLoadCampaignSlot(targetSlot, out rejectionReason);
            if (loaded)
            {
                ownershipTouched = true;
                ownedAttemptId = activeAttemptId;
                ownedAttemptSlot = CurrentCampaignSlot;
                return true;
            }

            // Retarget commit'inden veya yeni attempt açılışından sonra presentation/save
            // adımı başarısız olursa durable makbuzu sızdırma. Aksi halde sonraki domain
            // load bunu gerçek abandon sanıp bir can tüketir.
            if (string.IsNullOrEmpty(ownedAttemptId)
                && BartenderProgressService.EditorTryGetActiveAttempt(
                    out string openedAttemptId, out int openedAttemptSlot)
                && openedAttemptSlot == targetSlot)
            {
                ownershipTouched = true;
                ownedAttemptId = openedAttemptId;
                ownedAttemptSlot = openedAttemptSlot;
            }
            if (string.IsNullOrEmpty(ownedAttemptId)) return false;

            string loadReason = rejectionReason;
            if (BartenderProgressService.EditorTryDiscardActiveAttempt(
                    ownedAttemptId, ownedAttemptSlot, out string cleanupReason))
            {
                ownedAttemptId = null;
                ownedAttemptSlot = -1;
                return false;
            }

            rejectionReason = loadReason + ". Editor turu da kapatılamadı: "
                            + cleanupReason;
            return false;
        }
#endif

        /// <summary>
        /// Ana menü aynı sahne/rig üzerinde yaşıyorsa geri dönüşün açık yükleme kapısı.
        /// Rozet ve diğer sunumlar LevelLoaded event'inden kendiliğinden yenilenir.
        /// Bunu genel OnEnable akışına bağlamıyoruz; sıradan UI/rig toggle'ı aktif
        /// leveli yanlışlıkla baştan başlatmamalı.
        /// </summary>
        public bool ResumeSavedCampaign()
        {
            bool started = TryStartSavedCampaign(out string rejectionReason);
            if (!started && !string.IsNullOrEmpty(rejectionReason))
                Debug.LogWarning(rejectionReason, this);
            return started;
        }

        /// <summary>Play button contract: saved slot + positive life + durable attempt.</summary>
        public bool TryStartSavedCampaign(out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Başka bir level işlemi sürüyor";
                return false;
            }
            if (State != BartenderLevelState.Unloaded
                && State != BartenderLevelState.CampaignComplete)
            {
                rejectionReason = "Oyun zaten açık";
                return false;
            }

            int slot = NextUnlockedCampaignSlot;
            if (slot >= Campaign.Count)
            {
                UnloadInternal(BartenderLevelState.CampaignComplete);
                rejectionReason = "Tüm bölümler tamamlandı";
                return false;
            }
            if (BartenderProgressService.Lives <= 0)
            {
                rejectionReason = "Canın dolmasını bekle";
                return false;
            }

            return TryLoadCampaignSlot(Mathf.Max(0, slot), out rejectionReason);
        }

        public bool LoadCampaignSlot(int zeroBasedSlot)
        {
            if (State == BartenderLevelState.Failed
                && zeroBasedSlot != CurrentCampaignSlot)
            {
                Debug.LogWarning("Failed tur yalnız aynı bölümle yeniden denenebilir.", this);
                return false;
            }
            if (State == BartenderLevelState.Won
                && zeroBasedSlot != CurrentCampaignSlot + 1)
            {
                Debug.LogWarning("Won tur yalnız sıradaki bölüme ilerleyebilir.", this);
                return false;
            }
            if ((State == BartenderLevelState.Unloaded
                 || State == BartenderLevelState.CampaignComplete)
                && resumeSavedProgress
                && zeroBasedSlot != NextUnlockedCampaignSlot)
            {
                Debug.LogWarning("Kampanya yalnız kayıtlı açık bölümden başlatılabilir.", this);
                return false;
            }
            return TryLoadCampaignSlot(zeroBasedSlot, out _);
        }

        private bool TryLoadCampaignSlot(int zeroBasedSlot, out string rejectionReason) =>
            TryLoadCampaignSlot(zeroBasedSlot, 0, out rejectionReason);

        private bool TryLoadCampaignSlot(int zeroBasedSlot, int paidLifeCoinCost,
                                         out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Başka bir level işlemi sürüyor";
                Debug.LogWarning(rejectionReason, this);
                return false;
            }
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !string.IsNullOrEmpty(activeAttemptId))
            {
                rejectionReason = "Etkin tur sonuçlanmadan başka bölüm yüklenemez";
                Debug.LogWarning(rejectionReason, this);
                return false;
            }
            ResolveDependencies();
            if (zeroBasedSlot < 0 || zeroBasedSlot >= Campaign.Count)
            {
                rejectionReason = $"LiquidSort level slotu bulunamadı: {zeroBasedSlot}.";
                Debug.LogError(rejectionReason, this);
                return false;
            }

            BsLevel level = Campaign[zeroBasedSlot];
            if (!TryValidateLevel(level, out string error))
            {
                string levelName = level != null
                    ? level.Index.ToString()
                    : zeroBasedSlot.ToString();
                rejectionReason = $"Level {levelName} yüklenmedi: {error}";
                Debug.LogError(rejectionReason, this);
                return false;
            }

            BsBoard loadedBoard;
            try { loadedBoard = BsBoard.FromLevel(level); }
            catch (Exception exception)
            {
                rejectionReason = "Level kuralları oluşturulamadı";
                Debug.LogException(exception, this);
                return false;
            }

            string attemptId;
            bool attemptOpened = paidLifeCoinCost > 0
                ? BartenderProgressService.TryPurchaseLifeAndBeginAttempt(
                    zeroBasedSlot, paidLifeCoinCost, out attemptId,
                    out rejectionReason)
                : BartenderProgressService.TryBeginAttempt(
                    zeroBasedSlot, out attemptId, out rejectionReason);
            if (!attemptOpened)
                return false;

            commandInProgress = true;
            try
            {
                activeAttemptId = attemptId;
                terminalSettlementPending = false;
                pendingFailureReason = BartenderFailureReason.None;
                pendingTimedOutOrderSlot = -1;
                nextSettlementRetryTime = 0f;
                CurrentCampaignSlot = zeroBasedSlot;
                CurrentLevel = level;
                board = loadedBoard;
                BoardRevision = 0;
                FailureReason = BartenderFailureReason.None;
                TimedOutOrderSlot = -1;
                activeGameplayTime = 0d;
                ResetTimeBonuses(level);
                ResetOrderDeadlines();
                ResetBoosters(level);

                SetState(BartenderLevelState.Playing);
                if (!EvaluateTerminalState(out rejectionReason))
                {
                    UnloadInternal(BartenderLevelState.Unloaded);
                    return false;
                }
                InvokeSafely(LevelLoaded, level);
                InvokeSafely(BoardChanged);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        public bool ReloadCurrentLevel()
        {
            return CurrentCampaignSlot >= 0 && LoadCampaignSlot(CurrentCampaignSlot);
        }

        /// <summary>
        /// Failure ekranının tek retry kapısı. Playing/Paused/Won veya boş bir session
        /// aynı leveli bu niyet üzerinden yeniden başlatamaz.
        /// </summary>
        public BartenderTerminalCommandResult TryRetryAfterFailure()
        {
            if (MutationBlocked || State != BartenderLevelState.Failed
                || CurrentCampaignSlot < 0)
                return BartenderTerminalCommandResult.Rejected;

            return LoadCampaignSlot(CurrentCampaignSlot)
                ? BartenderTerminalCommandResult.CurrentLevelReloaded
                : BartenderTerminalCommandResult.Rejected;
        }

        /// <summary>
        /// Failure kartındaki ücretli devam kapısı. Level doğrulandıktan sonra jeton
        /// harcaması, bir canın iadesi ve yeni tur makbuzu progress servisinde atomik
        /// yapılır; bu nedenle sıfır canda da aynı bölüm güvenle yeniden açılabilir.
        /// </summary>
        public BartenderTerminalCommandResult TryPaidRetryAfterFailure(int coinCost)
        {
            if (MutationBlocked || State != BartenderLevelState.Failed
                || CurrentCampaignSlot < 0 || coinCost <= 0)
                return BartenderTerminalCommandResult.Rejected;

            return TryLoadCampaignSlot(CurrentCampaignSlot, coinCost, out _)
                ? BartenderTerminalCommandResult.CurrentLevelReloaded
                : BartenderTerminalCommandResult.Rejected;
        }

        /// <summary>
        /// Win ekranının tek ilerleme kapısı. Son levelde CampaignComplete'e geçmek de
        /// kabul edilmiş bir navigation sonucudur; false/retry döngüsü üretmez.
        /// </summary>
        public BartenderTerminalCommandResult TryContinueAfterWin()
        {
            if (MutationBlocked || State != BartenderLevelState.Won
                || CurrentCampaignSlot < 0)
                return BartenderTerminalCommandResult.Rejected;

            int next = CurrentCampaignSlot + 1;
            if (next >= Campaign.Count)
            {
                UnloadInternal(BartenderLevelState.CampaignComplete);
                return BartenderTerminalCommandResult.CampaignCompleted;
            }

            return LoadCampaignSlot(next)
                ? BartenderTerminalCommandResult.NextLevelLoaded
                : BartenderTerminalCommandResult.Rejected;
        }

        /// <summary>
        /// Sonuç ekranının kapatma kapısı. Won/Failed'a gelmeden önce tur makbuzu
        /// zaten kalıcı olarak settle edilmiştir; burada yeniden settlement yapılmaz,
        /// yalnızca terminal board boşaltılıp aynı sahnedeki ana menüye dönülür.
        /// </summary>
        public BartenderTerminalCommandResult TryReturnToMainMenuFromTerminal()
        {
            bool terminalState = State == BartenderLevelState.Won
                              || State == BartenderLevelState.Failed;
            if (MutationBlocked || !terminalState || CurrentCampaignSlot < 0
                || !string.IsNullOrEmpty(activeAttemptId))
                return BartenderTerminalCommandResult.Rejected;

            UnloadInternal(BartenderLevelState.Unloaded);
            return BartenderTerminalCommandResult.ReturnedToMainMenu;
        }

        public bool LoadNextLevel()
        {
            return TryContinueAfterWin() != BartenderTerminalCommandResult.Rejected;
        }

        public void UnloadLevel()
        {
            if (MutationBlocked) return;
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !string.IsNullOrEmpty(activeAttemptId))
            {
                Debug.LogWarning(
                    "Etkin tur doğrudan boşaltılamaz; TryAbandonToMainMenu kullanın.", this);
                return;
            }
            UnloadInternal(BartenderLevelState.Unloaded);
        }

        /// <summary>
        /// Confirmed pause-menu exit. Its durable abandon receipt consumes one life once;
        /// a rejected save keeps both the paused round and confirmation UI intact.
        /// </summary>
        public bool TryAbandonToMainMenu(out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Başka bir level işlemi sürüyor";
                return false;
            }
            if (State != BartenderLevelState.Paused || CurrentCampaignSlot < 0
                || string.IsNullOrEmpty(activeAttemptId))
            {
                rejectionReason = "Yalnız duraklatılmış etkin tur terk edilebilir";
                return false;
            }

            if (!TrySettleActiveAttempt(BartenderSettlementKind.Abandoned,
                    NextUnlockedCampaignSlot, out rejectionReason))
                return false;

            UnloadInternal(BartenderLevelState.Unloaded);
            return true;
        }

        public bool Pause()
        {
            if (MutationBlocked || State != BartenderLevelState.Playing) return false;
            SetState(BartenderLevelState.Paused);
            return true;
        }

        public bool Resume()
        {
            if (MutationBlocked || State != BartenderLevelState.Paused) return false;
            SetState(BartenderLevelState.Playing);
            return true;
        }

        private void MaintainApplicationPause()
        {
            bool suspended = applicationPaused || applicationFocusLost;
            if (suspended)
            {
                if (ownsAutomaticPause || State != BartenderLevelState.Playing) return;
                ownsAutomaticPause = true;
                automaticPauseAttemptId = activeAttemptId;
                automaticPauseStateGeneration = -1;
                if (Pause() && ownsAutomaticPause
                    && State == BartenderLevelState.Paused)
                {
                    automaticPauseStateGeneration = stateGeneration;
                    return;
                }
                ClearAutomaticPauseOwnership();
                return;
            }

            if (!ownsAutomaticPause) return;
            bool shouldResume = State == BartenderLevelState.Paused
                             && stateGeneration == automaticPauseStateGeneration
                             && string.Equals(activeAttemptId, automaticPauseAttemptId,
                                 StringComparison.Ordinal);
            ClearAutomaticPauseOwnership();
            if (shouldResume) Resume();
        }

        private void ClearAutomaticPauseOwnership()
        {
            ownsAutomaticPause = false;
            automaticPauseAttemptId = null;
            automaticPauseStateGeneration = -1;
        }

        /// <summary>
        /// Holds the exact committed revision while its presentation animation runs. The
        /// caller is an ownership token and must release the same revision. Acquiring this
        /// lock never mutates board data.
        /// </summary>
        public bool TryAcquirePresentationLock(object owner, int committedRevision)
        {
            if (owner == null || presentationLockOwner != null
                || commandInProgress || notificationInProgress
                || committedRevision != BoardRevision)
                return false;

            presentationLockOwner = owner;
            presentationLockRevision = committedRevision;
            return true;
        }

        /// <summary>
        /// LevelLoaded listeners run while the load command is still publishing its
        /// snapshot, so the normal acquisition gate is intentionally closed there. The
        /// level view may use this narrow entry point to keep timers and follow-up commands
        /// frozen until its entrance presentation has seated the exact loaded revision.
        /// </summary>
        public bool TryAcquireLoadPresentationLock(object owner, int loadedRevision)
        {
            if (owner == null || presentationLockOwner != null
                || !commandInProgress || !notificationInProgress
                || loadedRevision != BoardRevision)
                return false;

            presentationLockOwner = owner;
            presentationLockRevision = loadedRevision;
            return true;
        }

        /// <summary>Releases a presentation lock owned by <paramref name="owner"/>.</summary>
        public bool ReleasePresentationLock(object owner, int committedRevision)
        {
            if (owner == null || !ReferenceEquals(presentationLockOwner, owner)
                || presentationLockRevision != committedRevision)
                return false;

            presentationLockOwner = null;
            presentationLockRevision = -1;
            return true;
        }

        public PourResult CanPour(int sourceGlassId, int targetGlassId)
        {
            if (board == null)
                return PourResult.Fail("Level yüklü değil");
            return board.CanPour(board.GlassById(sourceGlassId), board.GlassById(targetGlassId));
        }

        /// <summary>
        /// Commits a rule move immediately. Presentation is downstream: it receives
        /// detached before/after data and cannot delay or roll back domain state.
        /// </summary>
        public bool TryPour(int sourceGlassId, int targetGlassId,
                            out BartenderPourReceipt receipt,
                            out string rejectionReason)
        {
            receipt = null;
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;

            RtGlass source = board.GlassById(sourceGlassId);
            RtGlass target = board.GlassById(targetGlassId);
            PourResult rule = board.CanPour(source, target);
            if (!rule.Success)
            {
                rejectionReason = rule.Reason;
                return false;
            }

            commandInProgress = true;
            try
            {
                RtGlass sourceBefore = source.Clone();
                RtGlass targetBefore = target.Clone();
                // Settlement is part of the rule transaction. Keep an unconditional
                // rollback even when player-facing undo history is disabled.
                BoardMemento rollback = CaptureCurrentMemento();
                BoardMemento undoSnapshot = undoHistoryDepth > 0
                    ? CloneMemento(rollback)
                    : null;
                PourResult committed = board.Pour(source, target);
                if (!committed.Success)
                {
                    rejectionReason = committed.Reason;
                    return false;
                }

                BoardRevision++;
                receipt = new BartenderPourReceipt(
                    BoardRevision, committed, sourceBefore, source.Clone(),
                    targetBefore, target.Clone());

                // Settle all domain invariants before calling code owned by a future view.
                if (!EvaluateTerminalState(out rejectionReason))
                {
                    RestoreUndoSnapshot(rollback);
                    BoardRevision--;
                    receipt = null;
                    return false;
                }
                CommitUndoSnapshot(undoSnapshot);
                InvokeSafely(Poured, receipt);
                InvokeSafely(BoardChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        public int MatchedOrderSlot(int glassId)
        {
            return board == null ? -1 : board.MatchedSlot(board.GlassById(glassId));
        }

        /// <summary>Detached bir receipt karesini mevcut siparişlere göre değerlendirir.</summary>
        public int MatchedOrderSlot(RtGlass glass)
        {
            return board == null ? -1 : board.MatchedSlot(glass);
        }

        /// <summary>Pointer'ın pickup sesi/seçimi için kaynak oyundaki aynı domain kapısı.</summary>
        public bool CanSelectAsPourSource(int glassId)
        {
            if (board == null) return false;
            RtGlass glass = board.GlassById(glassId);
            return glass != null && !glass.IsEmpty && !glass.IsChained(board.Delivered)
                   && glass.TopChainLength(board.Delivered) > 0;
        }

        public bool TryDeliver(int glassId, out BartenderDeliveryReceipt receipt,
                               out string rejectionReason)
        {
            receipt = null;
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;

            RtGlass glass = board.GlassById(glassId);
            if (glass == null)
            {
                rejectionReason = "Bardak bu levelda yok";
                return false;
            }

            int matchedSlot = board.MatchedSlot(glass);
            if (matchedSlot < 0)
            {
                rejectionReason = "Bardak açık bir siparişi karşılamıyor";
                return false;
            }

            commandInProgress = true;
            try
            {
                RtGlass deliveredGlass = glass.Clone();
                OrderDef deliveredOrder = LiveOrderAtSlot(matchedSlot)?.Clone();
                BoardMemento rollback = CaptureCurrentMemento();
                BoardMemento undoSnapshot = undoHistoryDepth > 0
                    ? CloneMemento(rollback)
                    : null;
                if (!board.Deliver(glass, out int committedSlot) || committedSlot != matchedSlot)
                {
                    rejectionReason = "Teslim kuralı işlemi reddetti";
                    return false;
                }

                BoardRevision++;
                RefreshOrderDeadlinesAfterDelivery();
                receipt = new BartenderDeliveryReceipt(
                    BoardRevision, committedSlot, deliveredGlass, deliveredOrder,
                    LiveOrderAtSlot(committedSlot)?.Clone());

                if (!EvaluateTerminalState(out rejectionReason))
                {
                    RestoreUndoSnapshot(rollback);
                    BoardRevision--;
                    receipt = null;
                    return false;
                }
                CommitUndoSnapshot(undoSnapshot);
                InvokeSafely(Delivered, receipt);
                InvokeSafely(BoardChanged);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        // ---------------------------------------------------------------
        //  BOOSTER KOMUTLARI
        //
        //  Üçü de aynı kapıdan geçer: yalnız Playing durumunda, yalnız komut/sunum
        //  kilidi yokken. Undo yalnız normal gameplay hamlelerini geri alır; satın alınan
        //  +süre ve kabul edilen karıştırma kalıcı booster harcamalarıdır. Failed'dan
        //  kurtarma bilerek YOK — terminal turdan çıkışın doğru yeri retry akışıdır.
        // ---------------------------------------------------------------

        /// <summary>
        /// Son kabul edilen hamleyi geri alır. Board'un tamamı geri yüklenir: dökme de,
        /// teslim de, teslimle birlikte gelen slot/deste hareketi de.
        /// </summary>
        public bool TryUndo(out string rejectionReason)
        {
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (undoHistory.Count == 0)
            {
                rejectionReason = "Geri alınacak hamle yok";
                return false;
            }
            if (UndoRemaining <= 0)
            {
                rejectionReason = "Geri al hakkı kalmadı";
                return false;
            }

            commandInProgress = true;
            try
            {
                BoardMemento rollback = CaptureCurrentMemento();
                int last = undoHistory.Count - 1;
                BoardMemento memento = undoHistory[last];
                undoHistory.RemoveAt(last);
                UndoRemaining--;
                BoardRevision++;

                RestoreUndoSnapshot(memento);

                // Geri alınan kartın deadline'ı geçen sürede dolmuş olabilir. Board
                // yine de geri alınır, fakat aynı Undo komutu turu Failed'a kilitler.
                OrderExpiryResult expiry = ExpireOrderIfNeeded(out rejectionReason);
                bool settlementAccepted = expiry != OrderExpiryResult.SettlementRejected;
                if (expiry == OrderExpiryResult.NotExpired)
                    settlementAccepted = EvaluateTerminalState(out rejectionReason);
                if (!settlementAccepted)
                {
                    RestoreUndoSnapshot(rollback);
                    undoHistory.Add(memento);
                    UndoRemaining++;
                    BoardRevision--;
                    return false;
                }
                InvokeSafely(BoardChanged);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// Ücretli +süre teklifinin o anda kabul edilip edilemeyeceğini doğrular.
        /// Süresi dolmuş sipariş CanAcceptCommand içinde önce Failed olur;
        /// booster terminal sonucu geriye çeviremez.
        /// </summary>
        public bool CanPurchaseTimeBoost(float seconds, int coinCost,
                                         out string rejectionReason)
        {
            if (!TryCollectTimeBoostTargets(seconds, out rejectionReason)) return false;
            if (TimeBoostRemaining <= 0)
            {
                rejectionReason = "Süre booster hakkı kalmadı";
                return false;
            }
            if (coinCost <= 0)
            {
                rejectionReason = "Süre booster fiyatı geçersiz";
                return false;
            }
            if (!BartenderEconomy.CanAfford(coinCost))
            {
                rejectionReason = $"Yetersiz altın: {BartenderEconomy.Coins}/{coinCost}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// O anda açık olan bütün süreli siparişlere aynı bonusu ekler. Gelecekte açılan
        /// kartlar hedef değildir. Satın alınan bonus logical order kimliğiyle ayrıca
        /// tutulur ve bütün eski Undo mementolarındaki ilgili deadline'lara işlenir;
        /// böylece Undo ne altını ne satın alınmış süreyi geri verir.
        /// </summary>
        public bool TryPurchaseTimeBoost(float seconds, int coinCost,
                                         out string rejectionReason)
        {
            if (!CanPurchaseTimeBoost(seconds, coinCost, out rejectionReason)) return false;
            int[] targetOrders = timeBoostOrderScratch.ToArray();

            commandInProgress = true;
            try
            {
                // Hedef kümesi yukarıdaki doğrulamadan bu yana değişemez: Unity main
                // thread'deyiz ve commandInProgress reentrant gameplay komutunu kapatır.
                for (int target = 0; target < targetOrders.Length; target++)
                {
                    int orderIndex = targetOrders[target];
                    timeBonusByOrderIndex[orderIndex] += seconds;
                    ExtendLiveDeadline(orderIndex, seconds);
                    ExtendUndoDeadlines(orderIndex, seconds);
                }
                TimeBoostRemaining--;

                // Avantajı ekonomi event'inden önce tamamla: CoinsChanged dinleyicileri
                // yeni bakiye ile eski timer/stoğu aynı karede asla gözlemlemez. Kalıcı
                // kayıt kabul edilmezse hiçbir controller eventi yayınlamadan geri al.
                if (!BartenderEconomy.TrySpendCoins(coinCost, out rejectionReason))
                {
                    for (int target = 0; target < targetOrders.Length; target++)
                    {
                        int orderIndex = targetOrders[target];
                        timeBonusByOrderIndex[orderIndex] -= seconds;
                        ExtendLiveDeadline(orderIndex, -seconds);
                        ExtendUndoDeadlines(orderIndex, -seconds);
                    }
                    TimeBoostRemaining++;
                    return false;
                }

                InvokeSafely(TimeBoosted, seconds);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        private bool TryCollectTimeBoostTargets(float seconds, out string rejectionReason)
        {
            timeBoostOrderScratch.Clear();
            rejectionReason = null;
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
            {
                rejectionReason = "Eklenecek süre pozitif ve sonlu olmalı";
                return false;
            }
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (!board.TimedOrdersEnabled)
            {
                rejectionReason = "Bu levelda süreli sipariş yok";
                return false;
            }

            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order == null || order.TimeLimit <= 0f
                    || !orderDeadlines.ContainsKey(order))
                    continue;

                int orderIndex = order.RuntimeOrderIndex;
                if (orderIndex < 0 || orderIndex >= timeBonusByOrderIndex.Length)
                {
                    rejectionReason = "Süreli sipariş kimliği geçersiz";
                    timeBoostOrderScratch.Clear();
                    return false;
                }
                if (!timeBoostOrderScratch.Contains(orderIndex))
                    timeBoostOrderScratch.Add(orderIndex);
            }

            if (timeBoostOrderScratch.Count > 0) return true;
            rejectionReason = "Açık süreli sipariş yok";
            return false;
        }

        private void ExtendLiveDeadline(int orderIndex, double seconds)
        {
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order == null || order.RuntimeOrderIndex != orderIndex
                    || !orderDeadlines.TryGetValue(order, out double deadline))
                    continue;
                orderDeadlines[order] = deadline + seconds;
            }
        }

        private void ExtendUndoDeadlines(int orderIndex, double seconds)
        {
            for (int history = 0; history < undoHistory.Count; history++)
            {
                BoardMemento memento = undoHistory[history];
                if (memento?.Board?.Slots == null || memento.SlotDeadlines == null)
                    continue;

                int count = Math.Min(memento.Board.Slots.Length,
                                     memento.SlotDeadlines.Length);
                for (int slot = 0; slot < count; slot++)
                {
                    OrderDef order = memento.Board.Slots[slot];
                    if (order == null || order.RuntimeOrderIndex != orderIndex
                        || !memento.SlotDeadlines[slot].HasValue)
                        continue;
                    memento.SlotDeadlines[slot] =
                        memento.SlotDeadlines[slot].Value + seconds;
                }
            }
        }

        /// <summary>
        /// Sıvıyı bardaklar arasında yeniden dağıtır.
        ///
        /// KORUNAN: her bardağın birim sayısı, her rengin toplamı, her bardağın
        /// kapasitesi. Yani hamle sayısı ve renk bütçesi değişmez — karıştırma bir
        /// "yeniden deneme", bir hile değil.
        ///
        /// DOKUNULMAYAN: zincirli bardaklar, kilitli veya gizli katman taşıyan
        /// bardaklar. Onların içeriği yerinde kalır; aksi hâlde karıştırma bir kilidi
        /// sessizce açardı.
        ///
        /// Tam çözülebilirlik solver'ı gameplay ana thread'inde koşturulmaz; bunun yerine
        /// sınırlı sayıda aday denenir ve IsFail olan (anında yasal hamlesiz) aday asla
        /// commit edilmez. Kabul edilen karıştırma kalıcıdır ve eski undo geçmişini
        /// kapatır; Undo booster harcamasını geri çeviremez.
        /// </summary>
        public bool TryShuffle(out string rejectionReason)
        {
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (ShuffleRemaining <= 0)
            {
                rejectionReason = "Karıştırma hakkı kalmadı";
                return false;
            }

            commandInProgress = true;
            try
            {
                const int maxAttempts = 12;
                BoardMemento rollback = CaptureCurrentMemento();
                bool acceptedCandidate = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    RestoreUndoSnapshot(CloneMemento(rollback));
                    if (!ShuffleMovableLayers() || board.IsFail()) continue;
                    acceptedCandidate = true;
                    break;
                }

                if (!acceptedCandidate)
                {
                    RestoreUndoSnapshot(rollback);
                    rejectionReason = "Yasal hamlesi kalan bir karıştırma bulunamadı";
                    return false;
                }

                ShuffleRemaining--;
                BoardRevision++;
                if (!EvaluateTerminalState(out rejectionReason))
                {
                    RestoreUndoSnapshot(rollback);
                    ShuffleRemaining++;
                    BoardRevision--;
                    return false;
                }
                undoHistory.Clear();
                InvokeSafely(BoardChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// Serbest bardakların bütün katmanlarını tek havuzda toplar, karıştırır ve aynı
        /// bardaklara aynı adetlerle geri koyar. Dizilim gerçekten değiştiyse true döner.
        /// </summary>
        private bool ShuffleMovableLayers()
        {
            shuffleScratch.Clear();
            var participants = new List<RtGlass>(board.Glasses.Count);
            for (int i = 0; i < board.Glasses.Count; i++)
            {
                RtGlass glass = board.Glasses[i];
                if (glass.Layers.Count == 0) continue;
                if (glass.IsChained(board.Delivered)) continue;
                if (glass.HasLocked(board.Delivered) || glass.HasHidden()) continue;
                participants.Add(glass);
                shuffleScratch.AddRange(glass.Layers);
            }
            if (participants.Count < 2 || shuffleScratch.Count < 2) return false;

            var before = new List<Layer>(shuffleScratch);
            for (int i = shuffleScratch.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (shuffleScratch[i], shuffleScratch[j]) = (shuffleScratch[j], shuffleScratch[i]);
            }

            bool changed = false;
            for (int i = 0; i < before.Count; i++)
                if (!before[i].Equals(shuffleScratch[i])) { changed = true; break; }
            if (!changed) return false;

            int read = 0;
            for (int i = 0; i < participants.Count; i++)
            {
                RtGlass glass = participants[i];
                for (int layer = 0; layer < glass.Layers.Count; layer++)
                    glass.Layers[layer] = shuffleScratch[read++];
            }
            return true;
        }

        private void ResetBoosters(BsLevel level)
        {
            undoHistory.Clear();
            UndoRemaining = Mathf.Max(0, level != null ? level.UndoCount : 0);
            TimeBoostRemaining = Mathf.Max(0, level != null ? level.TimeBoostCount : 0);
            ShuffleRemaining = Mathf.Max(0, level != null ? level.ShuffleCount : 0);
        }

        private BoardMemento CaptureUndoSnapshot() =>
            undoHistoryDepth > 0 && board != null ? CaptureCurrentMemento() : null;

        private BoardMemento CaptureCurrentMemento()
        {
            if (board == null) return null;

            var deadlines = new double?[board.Slots.Length];
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order != null
                    && orderDeadlines.TryGetValue(order, out double deadline))
                    deadlines[slot] = deadline;
            }

            return new BoardMemento
            {
                Board = board.Clone(),
                SlotDeadlines = deadlines,
            };
        }

        private static BoardMemento CloneMemento(BoardMemento source)
        {
            if (source == null || source.Board == null) return null;
            return new BoardMemento
            {
                Board = source.Board.Clone(),
                SlotDeadlines = source.SlotDeadlines == null
                    ? null
                    : (double?[])source.SlotDeadlines.Clone(),
            };
        }

        private void RestoreUndoSnapshot(BoardMemento snapshot)
        {
            if (snapshot == null || snapshot.Board == null)
                throw new ArgumentNullException(nameof(snapshot));

            board = snapshot.Board;
            orderDeadlines.Clear();
            double?[] deadlines = snapshot.SlotDeadlines;
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order == null || deadlines == null || slot >= deadlines.Length
                    || !deadlines[slot].HasValue)
                    continue;
                orderDeadlines[order] = deadlines[slot].Value;
            }
        }

        private void CommitUndoSnapshot(BoardMemento snapshot)
        {
            if (snapshot == null) return;
            undoHistory.Add(snapshot);
            // Oldest first: dropping index 0 keeps the most recent moves reachable.
            while (undoHistory.Count > undoHistoryDepth) undoHistory.RemoveAt(0);
        }

        public RtGlass GlassById(int glassId)
        {
            return board?.GlassById(glassId)?.Clone();
        }

        public OrderDef OrderAtSlot(int slotIndex)
        {
            return LiveOrderAtSlot(slotIndex)?.Clone();
        }

        public bool TryGetOrderTimeRemaining(int slotIndex, out float remaining,
                                             out float duration)
        {
            OrderDef order = LiveOrderAtSlot(slotIndex);
            duration = order != null
                ? order.TimeLimit + (float)TimeBonusFor(order)
                : 0f;
            if (order != null
                && orderDeadlines.TryGetValue(order, out double deadline))
            {
                remaining = Mathf.Max(0f, (float)(deadline - activeGameplayTime));
                return true;
            }
            remaining = 0f;
            return false;
        }

        public bool TryValidateLevel(BsLevel level, out string error)
        {
            ResolveDependencies();
            if (level == null)
            {
                error = "Level asseti boş.";
                return false;
            }
            if (palette == null || palette.Count == 0)
            {
                error = "BsPalette bulunamadı veya boş.";
                return false;
            }
            if (level.ColumnsPerRow <= 0 || level.OrderSlots <= 0)
            {
                error = "Sütun veya sipariş slotu sayısı geçersiz.";
                return false;
            }
            if (level.Glasses == null || level.Glasses.Count == 0)
            {
                error = "Levelda bardak yok.";
                return false;
            }

            for (int i = 0; i < level.Glasses.Count; i++)
            {
                GlassDef glass = level.Glasses[i];
                if (glass == null || glass.Layers == null)
                {
                    error = $"Bardak {i} boş veya katman listesi yok.";
                    return false;
                }
                if (!IsKnownGlassType(glass.Type))
                {
                    error = $"Bardak {i}: tip değeri {(int)glass.Type} geçersiz.";
                    return false;
                }
                if (glass.Layers.Count > glass.Capacity)
                {
                    error = $"Bardak {i}, kapasitesinden fazla katman taşıyor.";
                    return false;
                }
                for (int layer = 0; layer < glass.Layers.Count; layer++)
                {
                    int color = glass.Layers[layer].Color;
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Bardak {i}, katman {layer}: renk indeksi {color} geçersiz.";
                    return false;
                }
            }

            if (level.Orders == null || level.Orders.Count == 0)
            {
                error = "Levelda sipariş yok.";
                return false;
            }

            for (int i = 0; i < level.Orders.Count; i++)
            {
                OrderDef order = level.Orders[i];
                if (order == null || order.Contents == null)
                {
                    error = $"Sipariş {i} boş veya içerik listesi yok.";
                    return false;
                }
                if (!IsKnownGlassType(order.Glass))
                {
                    error = $"Sipariş {i}: bardak tip değeri {(int)order.Glass} geçersiz.";
                    return false;
                }
                if (order.Contents.Count != order.Capacity)
                {
                    error = $"Sipariş {i}, {order.Capacity} yerine "
                          + $"{order.Contents.Count} birim içeriyor.";
                    return false;
                }
                for (int content = 0; content < order.Contents.Count; content++)
                {
                    int color = order.Contents[content];
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Sipariş {i}, içerik {content}: renk indeksi {color} geçersiz.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private void ResolveDependencies()
        {
            if (palette == null)
                palette = Resources.Load<BsPalette>(DefaultPaletteResource);
        }

        private static bool IsKnownGlassType(GlassType type)
        {
            int index = (int)type;
            return index >= 0 && index < BsRules.CapacityTable.Length;
        }

        private bool MutationBlocked => commandInProgress || notificationInProgress
                                     || settlementInProgress
                                     || terminalSettlementPending
                                     || presentationLockOwner != null
                                     || presentationBarrierOwners.Count > 0;

        private bool CanAcceptCommand(out string reason)
        {
            if (MutationBlocked)
            {
                reason = "Başka bir level komutu işleniyor";
                return false;
            }
            if (State != BartenderLevelState.Playing || board == null)
            {
                reason = "Level oynanır durumda değil";
                return false;
            }
            OrderExpiryResult expiry = ExpireOrderIfNeeded(out string expiryReason);
            if (expiry != OrderExpiryResult.NotExpired)
            {
                if (expiry == OrderExpiryResult.SettlementRejected)
                    ArmPendingTerminalSettlement(BartenderFailureReason.OrderTimedOut,
                        FindExpiredOrderSlot(), expiryReason);
                reason = string.IsNullOrEmpty(expiryReason)
                    ? "Sipariş süresi doldu"
                    : expiryReason;
                return false;
            }
            reason = null;
            return true;
        }

        private void ResetOrderDeadlines()
        {
            orderDeadlines.Clear();
            RefreshOrderDeadlinesAfterDelivery();
        }

        private void ResetTimeBonuses(BsLevel level)
        {
            int count = level != null && level.Orders != null ? level.Orders.Count : 0;
            timeBonusByOrderIndex = count > 0 ? new double[count] : Array.Empty<double>();
        }

        private double TimeBonusFor(OrderDef order)
        {
            if (order == null) return 0d;
            int index = order.RuntimeOrderIndex;
            return index >= 0 && index < timeBonusByOrderIndex.Length
                ? timeBonusByOrderIndex[index]
                : 0d;
        }

        private void RefreshOrderDeadlinesAfterDelivery()
        {
            if (board == null || board.Slots == null)
            {
                orderDeadlines.Clear();
                return;
            }

            timerRemovalScratch.Clear();
            foreach (KeyValuePair<OrderDef, double> pair in orderDeadlines)
            {
                if (!ContainsOrderReference(board.Slots, pair.Key))
                    timerRemovalScratch.Add(pair.Key);
            }
            for (int i = 0; i < timerRemovalScratch.Count; i++)
                orderDeadlines.Remove(timerRemovalScratch[i]);

            if (!board.TimedOrdersEnabled) return;
            for (int i = 0; i < board.Slots.Length; i++)
            {
                OrderDef order = board.Slots[i];
                if (order == null || order.TimeLimit <= 0f
                    || orderDeadlines.ContainsKey(order))
                    continue;
                orderDeadlines.Add(order,
                    activeGameplayTime + order.TimeLimit + TimeBonusFor(order));
            }
        }

        private OrderExpiryResult ExpireOrderIfNeeded(out string rejectionReason)
        {
            rejectionReason = null;
            if (board == null || !board.TimedOrdersEnabled)
                return OrderExpiryResult.NotExpired;
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order != null
                    && orderDeadlines.TryGetValue(order, out double deadline)
                    && deadline <= activeGameplayTime)
                {
                    return Fail(BartenderFailureReason.OrderTimedOut, slot,
                            out rejectionReason)
                        ? OrderExpiryResult.Settled
                        : OrderExpiryResult.SettlementRejected;
                }
            }
            return OrderExpiryResult.NotExpired;
        }

        private int FindExpiredOrderSlot()
        {
            if (board == null || board.Slots == null) return -1;
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order != null
                    && orderDeadlines.TryGetValue(order, out double deadline)
                    && deadline <= activeGameplayTime)
                    return slot;
            }
            return -1;
        }

        private void ArmPendingTerminalSettlement(BartenderFailureReason reason,
                                                  int timedOutSlot,
                                                  string rejectionReason)
        {
            terminalSettlementPending = true;
            pendingFailureReason = reason;
            pendingTimedOutOrderSlot = timedOutSlot;
            nextSettlementRetryTime = Time.unscaledTime + 1f;
            if (!string.IsNullOrEmpty(rejectionReason))
                Debug.LogWarning("Tur sonucu kaydedilemedi; yeniden denenecek: "
                               + rejectionReason, this);
        }

        private void RetryPendingTerminalSettlement()
        {
            if (!terminalSettlementPending || State != BartenderLevelState.Playing) return;
            if (applicationPaused || applicationFocusLost
                || Time.unscaledTime < nextSettlementRetryTime) return;

            if (!Fail(pendingFailureReason, pendingTimedOutOrderSlot, out _))
            {
                nextSettlementRetryTime = Time.unscaledTime + 1f;
                return;
            }

            terminalSettlementPending = false;
            pendingFailureReason = BartenderFailureReason.None;
            pendingTimedOutOrderSlot = -1;
            nextSettlementRetryTime = 0f;
        }

        private bool EvaluateTerminalState() => EvaluateTerminalState(out _);

        /// <summary>
        /// Returns false only when a terminal result was detected but its durable receipt
        /// could not be committed. Callers that mutated the board can then roll back before
        /// publishing any presentation event.
        /// </summary>
        private bool EvaluateTerminalState(out string rejectionReason)
        {
            rejectionReason = null;
            if (State != BartenderLevelState.Playing || board == null) return true;
            if (board.IsWin())
            {
                int unlocked = Mathf.Min(CurrentCampaignSlot + 1, Campaign.Count);
                if (!TrySettleActiveAttempt(BartenderSettlementKind.Won, unlocked,
                        out rejectionReason))
                    return false;
                SetState(BartenderLevelState.Won);
            }
            else if (board.IsFail())
            {
                return Fail(BartenderFailureReason.NoLegalMoves, -1,
                    out rejectionReason);
            }
            return true;
        }

        private bool Fail(BartenderFailureReason reason, int timedOutSlot,
                          out string rejectionReason)
        {
            rejectionReason = null;
            if (State != BartenderLevelState.Playing) return true;
            if (!TrySettleActiveAttempt(BartenderSettlementKind.Failed,
                    NextUnlockedCampaignSlot, out rejectionReason))
                return false;
            FailureReason = reason;
            TimedOutOrderSlot = timedOutSlot;
            SetState(BartenderLevelState.Failed);
            return true;
        }

        private bool TrySettleActiveAttempt(BartenderSettlementKind kind,
                                            int nextUnlockedOnWin,
                                            out string rejectionReason)
        {
            rejectionReason = null;
            if (settlementInProgress)
            {
                rejectionReason = "Tur sonucu zaten işleniyor";
                return false;
            }
            if (string.IsNullOrEmpty(activeAttemptId) || CurrentCampaignSlot < 0)
            {
                rejectionReason = "Etkin tur makbuzu bulunamadı";
                return false;
            }

            settlementInProgress = true;
            try
            {
                BartenderProgressCommitResult result =
                    BartenderProgressService.TrySettleAttempt(
                        activeAttemptId, kind, CurrentCampaignSlot,
                        nextUnlockedOnWin, out rejectionReason);
                if (result == BartenderProgressCommitResult.Rejected) return false;
                activeAttemptId = null;
                return true;
            }
            finally
            {
                settlementInProgress = false;
            }
        }

        private void SetState(BartenderLevelState next)
        {
            if (State == next) return;
            State = next;
            stateGeneration++;
            if (commandInProgress)
            {
                pendingStateNotification = next;
                hasPendingStateNotification = true;
                return;
            }
            InvokeSafely(StateChanged, next);
        }

        private void FlushPendingStateChanged()
        {
            if (!hasPendingStateNotification) return;
            BartenderLevelState pending = pendingStateNotification;
            hasPendingStateNotification = false;
            InvokeSafely(StateChanged, pending);
        }

        private void UnloadInternal(BartenderLevelState finalState)
        {
            presentationLockOwner = null;
            presentationLockRevision = -1;
            presentationBarrierOwners.Clear();
            activeAttemptId = null;
            ClearAutomaticPauseOwnership();
            terminalSettlementPending = false;
            pendingFailureReason = BartenderFailureReason.None;
            pendingTimedOutOrderSlot = -1;
            nextSettlementRetryTime = 0f;
            board = null;
            CurrentLevel = null;
            CurrentCampaignSlot = -1;
            BoardRevision = 0;
            activeGameplayTime = 0d;
            timeBonusByOrderIndex = Array.Empty<double>();
            orderDeadlines.Clear();
            ResetBoosters(null);
            FailureReason = BartenderFailureReason.None;
            TimedOutOrderSlot = -1;
            SetState(finalState);
            InvokeSafely(BoostersChanged);
        }

        private OrderDef LiveOrderAtSlot(int slotIndex)
        {
            return board != null && board.Slots != null
                && slotIndex >= 0 && slotIndex < board.Slots.Length
                ? board.Slots[slotIndex]
                : null;
        }

        private int FindCampaignSlot(int oneBasedLevelNumber)
        {
            for (int i = 0; i < Campaign.Count; i++)
                if (Campaign[i] != null && Campaign[i].Index == oneBasedLevelNumber)
                    return i;
            return -1;
        }

        private static bool ContainsOrderReference(OrderDef[] slots, OrderDef wanted)
        {
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i], wanted)) return true;
            return false;
        }

        private void InvokeSafely(Action handlers)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            bool previousNotificationState = notificationInProgress;
            notificationInProgress = true;
            try
            {
                for (int i = 0; i < invocationList.Length; i++)
                {
                    try { ((Action)invocationList[i])(); }
                    catch (Exception exception) { Debug.LogException(exception, this); }
                }
            }
            finally
            {
                notificationInProgress = previousNotificationState;
            }
        }

        private void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            bool previousNotificationState = notificationInProgress;
            notificationInProgress = true;
            try
            {
                for (int i = 0; i < invocationList.Length; i++)
                {
                    try { ((Action<T>)invocationList[i])(value); }
                    catch (Exception exception) { Debug.LogException(exception, this); }
                }
            }
            finally
            {
                notificationInProgress = previousNotificationState;
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
