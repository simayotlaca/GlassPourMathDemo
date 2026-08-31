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

        [Header("Campaign")]
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool resumeSavedProgress = true;
        [SerializeField, Min(1)] private int startingLevelNumber = 1;
        [SerializeField] private string progressKey = "LiquidSort.Bartender.NextLevelSlot";

        [Header("Campaign data")]
        [SerializeField] private BsPalette palette;

        [Header("Booster kapasitesi")]
        [Tooltip("Sahnedeki statik bardak slotu sınırı. +bardak bu sayıyı aşamaz; "
               + "aştığı an sunum havuzu tükenir ve bütün level reddedilirdi.")]
        [SerializeField, Min(1)] private int maxActiveGlasses = 12;
        [Tooltip("Geri al yığınının derinliği. Level başına ayrılan bellek bu kadar "
               + "board klonu; 0 geri almayı tamamen kapatır.")]
        [SerializeField, Min(0)] private int undoHistoryDepth = 32;

        private static List<BsLevel> cachedCampaign;

        private readonly Dictionary<OrderDef, float> orderRemaining =
            new Dictionary<OrderDef, float>(ReferenceComparer<OrderDef>.Instance);
        private readonly List<OrderDef> timerRemovalScratch = new List<OrderDef>();
        /// <summary>Committed board snapshots, oldest first. Undo pops the last one.</summary>
        private readonly List<BsBoard> undoHistory = new List<BsBoard>();
        private readonly List<Layer> shuffleScratch = new List<Layer>(48);

        private BsBoard board;
        private bool commandInProgress;
        private bool notificationInProgress;
        private object presentationLockOwner;
        private int presentationLockRevision = -1;
        private bool hasPendingStateNotification;
        private BartenderLevelState pendingStateNotification;

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
        public int ExtraGlassRemaining { get; private set; }
        public int ShuffleRemaining { get; private set; }
        /// <summary>Geri alınacak bir hamle var mı — stok ayrıca sayılır.</summary>
        public bool HasUndoableMove => undoHistory.Count > 0;
        /// <summary>Bu levelda sahnede aynı anda kaç bardak bulunabilir.</summary>
        public int MaxActiveGlasses => maxActiveGlasses;
        /// <summary>Board'da şu an duran bardak sayısı; +bardak kapısı bunu okur.</summary>
        public int ActiveGlassCount => board != null ? board.Glasses.Count : 0;

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
            PlayerPrefs.GetInt(progressKey, 0), 0, Campaign.Count);

        /// <summary>
        /// True while a view is animating an already committed board revision. The domain
        /// remains authoritative, but timers and additional commands wait until that visual
        /// transaction has reconciled.
        /// </summary>
        public bool PresentationLocked => presentationLockOwner != null;

        /// <summary>
        /// Read-only ownership check for presentation adapters. A view may start an
        /// animated reconciliation only when the exact owner holds the exact board
        /// revision; a direct domain caller therefore cannot accidentally start an
        /// unlocked portal flight.
        /// </summary>
        public bool IsPresentationLockOwnedBy(object owner, int committedRevision) =>
            owner != null && ReferenceEquals(presentationLockOwner, owner)
            && presentationLockRevision == committedRevision;

        public event Action<BsLevel> LevelLoaded;
        public event Action BoardChanged;
        public event Action OrdersChanged;
        public event Action<BartenderLevelState> StateChanged;
        public event Action<BartenderPourReceipt> Poured;
        public event Action<BartenderDeliveryReceipt> Delivered;
        /// <summary>Stok veya geri-al yığını değişti; alt şerit sayaçlarını tazeler.</summary>
        public event Action BoostersChanged;

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
            ResolveDependencies();
            if (!loadOnStart) return;

            int slot = resumeSavedProgress
                ? NextUnlockedCampaignSlot
                : FindCampaignSlot(startingLevelNumber);

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
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>Advances order clocks; public for deterministic tests and hosts.</summary>
        public void Tick(float unscaledDeltaTime)
        {
            if (MutationBlocked || State != BartenderLevelState.Playing
                || unscaledDeltaTime <= 0f) return;
            TickOrderDeadlines(unscaledDeltaTime);
        }

        public bool LoadLevelNumber(int oneBasedLevelNumber)
        {
            int slot = FindCampaignSlot(oneBasedLevelNumber);
            return slot >= 0 && LoadCampaignSlot(slot);
        }

        public bool LoadCampaignSlot(int zeroBasedSlot)
        {
            if (MutationBlocked)
            {
                Debug.LogWarning("Başka bir level işlemi sürerken level yüklenemez.", this);
                return false;
            }
            ResolveDependencies();
            if (zeroBasedSlot < 0 || zeroBasedSlot >= Campaign.Count)
            {
                Debug.LogError($"LiquidSort level slotu bulunamadı: {zeroBasedSlot}.", this);
                return false;
            }

            BsLevel level = Campaign[zeroBasedSlot];
            if (!TryValidateLevel(level, out string error))
            {
                Debug.LogError($"Level {level.Index} yüklenmedi: {error}", this);
                return false;
            }

            commandInProgress = true;
            try
            {
                CurrentCampaignSlot = zeroBasedSlot;
                CurrentLevel = level;
                board = BsBoard.FromLevel(level);
                BoardRevision = 0;
                FailureReason = BartenderFailureReason.None;
                TimedOutOrderSlot = -1;
                ResetOrderDeadlines();
                ResetBoosters(level);

                SetState(BartenderLevelState.Playing);
                EvaluateTerminalState();
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

        public bool LoadNextLevel()
        {
            if (MutationBlocked) return false;
            int next = CurrentCampaignSlot + 1;
            if (next < 0) next = 0;
            if (next >= Campaign.Count)
            {
                UnloadInternal(BartenderLevelState.CampaignComplete);
                return false;
            }
            return LoadCampaignSlot(next);
        }

        public void UnloadLevel()
        {
            if (MutationBlocked) return;
            UnloadInternal(BartenderLevelState.Unloaded);
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
                // Captured before the mutation but only filed once the rules accepted it,
                // so a refused command cannot leave a phantom step on the undo stack.
                BsBoard undoSnapshot = CaptureUndoSnapshot();
                PourResult committed = board.Pour(source, target);
                if (!committed.Success)
                {
                    rejectionReason = committed.Reason;
                    return false;
                }

                BoardRevision++;
                CommitUndoSnapshot(undoSnapshot);
                receipt = new BartenderPourReceipt(
                    BoardRevision, committed, sourceBefore, source.Clone(),
                    targetBefore, target.Clone());

                // Settle all domain invariants before calling code owned by a future view.
                EvaluateTerminalState();
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
                BsBoard undoSnapshot = CaptureUndoSnapshot();
                if (!board.Deliver(glass, out int committedSlot) || committedSlot != matchedSlot)
                {
                    rejectionReason = "Teslim kuralı işlemi reddetti";
                    return false;
                }

                BoardRevision++;
                CommitUndoSnapshot(undoSnapshot);
                RefreshOrderDeadlinesAfterDelivery();
                receipt = new BartenderDeliveryReceipt(
                    BoardRevision, committedSlot, deliveredGlass, deliveredOrder,
                    LiveOrderAtSlot(committedSlot)?.Clone());

                EvaluateTerminalState();
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
        //  kilidi yokken. Failed'dan kurtarma bilerek YOK — BsRoundStateMachine terminal
        //  sonucu atomik olarak kilitliyor ve epoch'u artırıyor; oradan Playing'e dönmek
        //  akış FSM'ini controller ile ayrıştırırdı. Kurtarma istenirse doğru yer level
        //  yeniden yükleme, bir booster değil.
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
                int last = undoHistory.Count - 1;
                board = undoHistory[last];
                undoHistory.RemoveAt(last);
                UndoRemaining--;
                BoardRevision++;

                // Slot'lardaki OrderDef referansları klonla birlikte değişti; süre sözlüğü
                // referansla anahtarlandığı için baştan kurulmak zorunda.
                ResetOrderDeadlines();

                EvaluateTerminalState();
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
        /// Boş bir çalışma bardağı ekler. Tipi ÇAĞIRAN seçer: hangi tiplerin sahnede
        /// boş havuz slotu kaldığını yalnız sunum katmanı bilir, kural motoru bilmez.
        /// </summary>
        public bool TryAddExtraGlass(GlassType type, out int newGlassId,
                                     out string rejectionReason)
        {
            newGlassId = -1;
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (ExtraGlassRemaining <= 0)
            {
                rejectionReason = "Ekstra bardak hakkı kalmadı";
                return false;
            }
            if (!IsKnownGlassType(type))
            {
                rejectionReason = "Geçersiz bardak tipi";
                return false;
            }
            if (board.Glasses.Count >= maxActiveGlasses)
            {
                rejectionReason = $"Sahnede en fazla {maxActiveGlasses} bardak durabilir";
                return false;
            }

            commandInProgress = true;
            try
            {
                BsBoard undoSnapshot = CaptureUndoSnapshot();
                RtGlass added = board.AddEmptyGlass(type);
                if (added == null)
                {
                    rejectionReason = "Bardak eklenemedi";
                    return false;
                }

                newGlassId = added.Id;
                ExtraGlassRemaining--;
                BoardRevision++;
                CommitUndoSnapshot(undoSnapshot);

                EvaluateTerminalState();
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
        /// DIKKAT: sonucun çözülebilir kalacağı GARANTİ EDİLMEZ. Renk toplamı korunduğu
        /// için sipariş destesi hâlâ karşılanabilir durumdadır, ama dizilim kilitlenmiş
        /// bir board üretebilir. Bu bilinçli: doğrulama tam bir solver koşturmak demek
        /// ve bir buton dokunuşunun bütçesi değil. Kilitlenen board'u kural motoru
        /// zaten IsFail ile yakalar.
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
                BsBoard undoSnapshot = CaptureUndoSnapshot();
                if (!ShuffleMovableLayers())
                {
                    rejectionReason = "Karıştırılacak serbest sıvı yok";
                    return false;
                }

                ShuffleRemaining--;
                BoardRevision++;
                CommitUndoSnapshot(undoSnapshot);

                EvaluateTerminalState();
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
            ExtraGlassRemaining = Mathf.Max(0, level != null ? level.ExtraGlassCount : 0);
            ShuffleRemaining = Mathf.Max(0, level != null ? level.ShuffleCount : 0);
        }

        private BsBoard CaptureUndoSnapshot() =>
            undoHistoryDepth > 0 && board != null ? board.Clone() : null;

        private void CommitUndoSnapshot(BsBoard snapshot)
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
            duration = order != null ? order.TimeLimit : 0f;
            if (order != null && orderRemaining.TryGetValue(order, out remaining))
                return true;
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
                                     || presentationLockOwner != null;

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
            if (ExpireOrderIfNeeded())
            {
                reason = "Sipariş süresi doldu";
                return false;
            }
            reason = null;
            return true;
        }

        private void ResetOrderDeadlines()
        {
            orderRemaining.Clear();
            RefreshOrderDeadlinesAfterDelivery();
        }

        private void RefreshOrderDeadlinesAfterDelivery()
        {
            if (board == null || board.Slots == null)
            {
                orderRemaining.Clear();
                return;
            }

            timerRemovalScratch.Clear();
            foreach (KeyValuePair<OrderDef, float> pair in orderRemaining)
            {
                if (!ContainsOrderReference(board.Slots, pair.Key))
                    timerRemovalScratch.Add(pair.Key);
            }
            for (int i = 0; i < timerRemovalScratch.Count; i++)
                orderRemaining.Remove(timerRemovalScratch[i]);

            if (!board.TimedOrdersEnabled) return;
            for (int i = 0; i < board.Slots.Length; i++)
            {
                OrderDef order = board.Slots[i];
                if (order == null || order.TimeLimit <= 0f || orderRemaining.ContainsKey(order))
                    continue;
                orderRemaining.Add(order, order.TimeLimit);
            }
        }

        private void TickOrderDeadlines(float delta)
        {
            if (board == null || !board.TimedOrdersEnabled) return;
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order == null || !orderRemaining.TryGetValue(order, out float remaining))
                    continue;
                remaining -= delta;
                orderRemaining[order] = remaining;
                if (remaining > 0f) continue;
                Fail(BartenderFailureReason.OrderTimedOut, slot);
                return;
            }
        }

        private bool ExpireOrderIfNeeded()
        {
            if (board == null || !board.TimedOrdersEnabled) return false;
            for (int slot = 0; slot < board.Slots.Length; slot++)
            {
                OrderDef order = board.Slots[slot];
                if (order != null && orderRemaining.TryGetValue(order, out float remaining)
                                  && remaining <= 0f)
                {
                    Fail(BartenderFailureReason.OrderTimedOut, slot);
                    return true;
                }
            }
            return false;
        }

        private void EvaluateTerminalState()
        {
            if (State != BartenderLevelState.Playing || board == null) return;
            if (board.IsWin())
            {
                int unlocked = Mathf.Max(NextUnlockedCampaignSlot, CurrentCampaignSlot + 1);
                PlayerPrefs.SetInt(progressKey, Mathf.Min(unlocked, Campaign.Count));
                PlayerPrefs.Save();
                SetState(BartenderLevelState.Won);
            }
            else if (board.IsFail())
            {
                Fail(BartenderFailureReason.NoLegalMoves, -1);
            }
        }

        private void Fail(BartenderFailureReason reason, int timedOutSlot)
        {
            if (State != BartenderLevelState.Playing) return;
            FailureReason = reason;
            TimedOutOrderSlot = timedOutSlot;
            SetState(BartenderLevelState.Failed);
        }

        private void SetState(BartenderLevelState next)
        {
            if (State == next) return;
            State = next;
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
            board = null;
            CurrentLevel = null;
            CurrentCampaignSlot = -1;
            BoardRevision = 0;
            orderRemaining.Clear();
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
