using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Sipariş şeridi: açık slotları elle yerleştirilmiş kartlara bağlar.
    ///
    /// Bileşen hiçbir GameObject yaratmaz; kartlar da bardaklar gibi sahnede duran,
    /// Inspector'dan bağlanmış objelerdir. Tek yaptığı, kural motorunun slotlarını o
    /// kartlara PROJEKTE etmek.
    ///
    /// TESLİM ANI BURADA GECİKİR, tek bilerek yapılan istisna budur. Controller
    /// Delivered'ı BoardChanged'den hemen önce yayar; o an slot çoktan boşalmış ve
    /// desteden yeni kart gelmiştir. Şeridi orada tazelemek, oyuncunun karşıladığı
    /// kartı ✓ damgasını görmeden yok ederdi. Bu yüzden damga hemen basılır, kartın
    /// içeriği ise bardak kemerin arkasında kaybolduktan sonra yenilenir —
    /// <see cref="BartenderShelfLevelView.DeliveryPresentationFinished"/> tam olarak
    /// o anı bildirir.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OrderStripPresenter : MonoBehaviour
    {
        private const float DealStagger = 0.045f;
        private const float DeliveryStampMinimumHold = 0.30f;
        private const float DeliveryExitDuration = 0.18f;
        private const float QueueShiftDuration = 0.23f;
        private const float QueueShiftStagger = 0.025f;
        private const float QueueWatchdogGrace = 0.75f;

        [Header("Rig references")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Eşleşme yanması ve teslim beat'inin bitişi buradan okunur. Opsiyonel.")]
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Header("Elle yerleştirilmiş kartlar")]
        [Tooltip("Soldan sağa slot sırasıyla. Level'ın OrderSlots değeri kadarı kullanılır.")]
        [SerializeField] private OrderCardView[] cards = new OrderCardView[0];

        [Header("Bardak çizimleri")]
        [Tooltip("Her bardak tipi için ön görsel + iç boşluk maskesi. Üç kart aynı "
               + "listeyi paylaşır.")]
        [SerializeField] private List<OrderCardView.GlassIcon> glassIcons =
            new List<OrderCardView.GlassIcon>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private BartenderLevelController presentationBarrierController;
        private int controllerSubscriptionGeneration;
        private int viewSubscriptionGeneration;
        private Action<BsLevel> levelLoadedSubscription;
        private Action controllerSnapshotSubscription;
        private Action<BartenderLevelState> levelStateSubscription;
        private Action<BartenderDeliveryReceipt> deliveredSubscription;
        private Action<BartenderDeliveryReceipt> deliveryFinishedSubscription;
        private bool iconsPublished;
        private readonly OrderStripEventBus eventBus = new OrderStripEventBus();
        private readonly BsOrderStripStateMachine presentationState =
            new BsOrderStripStateMachine();
        private bool eventBusSubscribed;
        private int presentationEpoch;

        private int deferredAtFrame = -1;
        private float deferredAtUnscaledTime = -1f;
        private int pendingDeliveredSlot = -1;
        private BartenderDeliveryReceipt pendingDeliveryReceipt;
        private bool deliveryPresentationFinished;
        private bool snapshotDirty;
        private float dealCompletionAtUnscaledTime = -1f;
        private int dealCompletionEpoch = -1;

        private Vector2[] slotPositions = new Vector2[0];
        private bool slotPositionsCaptured;
        private bool hasPresentedLiveLevel;
        private int transitionDeliveredSlot = -1;
        private int transitionSlotCount;
        private Sequence queueTransition;
        private float queueWatchdogAtUnscaledTime = -1f;
        private BsLevel lastCapacityFaultLevel;
        private int lastCapacityFaultCardCount = -1;

        public IReadOnlyList<OrderCardView> Cards => cards;
        /// <summary>
        /// Kart dağıtımı veya Delivered→damga→uçuş→kuyruk geçişi sürerken true.
        /// Input owners bunu sunum bariyeri olarak kullanır; yeni kart daha yerine
        /// oturmadan bir sonraki gameplay komutu başlayamaz.
        /// </summary>
        public bool TransitionPlaying => presentationState.TransitionPlaying;

        private void Awake()
        {
            EnsureEventBusSubscription();
            ResolveDependencies();
            PublishIcons();
            CaptureSlotPositions();
        }

        private void OnEnable()
        {
            EnsureEventBusSubscription();
            ResolveDependencies();
            PublishIcons();
            CaptureSlotPositions();
            Subscribe();
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));
        }

        private void OnDisable()
        {
            Unsubscribe();
            EnsureEventBusSubscription();
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Deactivate));
        }

        private void OnDestroy()
        {
            ReleaseControllerPresentationBarrier();
            if (!eventBusSubscribed) return;
            eventBus.Unsubscribe(HandleEventBusSignal);
            eventBusSubscribed = false;
        }

        /// <summary>
        /// Authoring API for an editor builder, mirroring the shelf view's Configure*
        /// methods. Yalnızca elle sürüklenecek referansları yazar.
        /// </summary>
        public void ConfigureSceneBindings(BartenderLevelController levelController,
                                           BartenderShelfLevelView view,
                                           IReadOnlyList<OrderCardView> orderCards,
                                           IReadOnlyList<OrderCardView.GlassIcon> icons)
        {
            EnsureEventBusSubscription();
            Unsubscribe();
            // Rebind önce eski view neslini invalid eder ve eski kartları atomik gizler.
            // Yeni referanslar ancak bundan sonra yayınlanır; stale tween callback'i yeni
            // kart dizisine erişemez.
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Rebind));
            controller = levelController;
            shelfView = view;

            int count = orderCards != null ? orderCards.Count : 0;
            cards = new OrderCardView[count];
            for (int i = 0; i < count; i++) cards[i] = orderCards[i];
            slotPositionsCaptured = false;
            slotPositions = new Vector2[0];
            hasPresentedLiveLevel = false;

            glassIcons.Clear();
            if (icons != null)
            {
                for (int i = 0; i < icons.Count; i++)
                    if (icons[i] != null) glassIcons.Add(icons[i]);
            }

            iconsPublished = false;
            PublishIcons();
            CaptureSlotPositions();
            if (!isActiveAndEnabled)
            {
                HideCardsImmediate(true);
                return;
            }
            Subscribe();
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.Activate));
        }

        /// <summary>Strict binding check with a message an artist can act on.</summary>
        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (cards == null || cards.Length == 0)
            {
                reason = "Sipariş kartı bağlanmamış.";
                return false;
            }
            if (shelfView != null && shelfView.Controller == null)
            {
                reason = "Bağlı shelf view üzerinde BartenderLevelController eksik.";
                return false;
            }
            if (shelfView != null
                && !ReferenceEquals(shelfView.Controller, controller))
            {
                reason = "Order strip ve shelf view farklı controller'lara bağlı.";
                return false;
            }
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null)
                {
                    reason = $"Sipariş kartı [{i}] boş.";
                    return false;
                }
                if (!cards[i].IsReady())
                {
                    reason = $"Sipariş kartı [{i}] ({cards[i].name}) eksik parça taşıyor.";
                    return false;
                }
                for (int previous = 0; previous < i; previous++)
                {
                    if (!ReferenceEquals(cards[previous], cards[i])) continue;
                    reason = $"Sipariş kartı [{i}], [{previous}] ile aynı view nesnesi.";
                    return false;
                }
            }

            BsLevel capacityLevel = controller.CurrentLevel;
            int requiredCapacity = capacityLevel != null
                ? Mathf.Max(1, capacityLevel.OrderSlots)
                : 0;
            BsLevel[] campaign = Resources.LoadAll<BsLevel>("Levels");
            for (int i = 0; i < campaign.Length; i++)
            {
                BsLevel level = campaign[i];
                if (level == null || Mathf.Max(1, level.OrderSlots) <= requiredCapacity)
                    continue;
                requiredCapacity = Mathf.Max(1, level.OrderSlots);
                capacityLevel = level;
            }
            if (cards.Length < requiredCapacity)
            {
                string levelLabel = capacityLevel != null
                    ? $"Level {capacityLevel.Index}"
                    : "Campaign";
                reason = $"{levelLabel}, {requiredCapacity} order slot istiyor; "
                       + $"yalnız {cards.Length} kart bağlı.";
                return false;
            }
            foreach (GlassType type in BsRules.AllGlassTypes)
            {
                bool found = false;
                for (int i = 0; i < glassIcons.Count && !found; i++)
                    found = glassIcons[i] != null && glassIcons[i].type == type
                            && glassIcons[i].front != null
                            && glassIcons[i].interiorMask != null;
                if (found) continue;
                reason = $"{BsRules.DisplayName(type)} için kart çizimi bağlanmamış.";
                return false;
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Order Strip Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log($"Sipariş şeridi: {cards.Length} kart geçerli.", this);
            else
                Debug.LogError("Order strip binding error: " + reason, this);
        }

        private void LateUpdate()
        {
            switch (presentationState.State)
            {
                case BsOrderStripState.Dealing:
                    if (dealCompletionEpoch == presentationEpoch
                        && Time.unscaledTime >= dealCompletionAtUnscaledTime)
                        eventBus.Publish(OrderStripSignal.Epoch(
                            OrderStripSignalKind.DealAnimationFinished,
                            presentationEpoch));
                    return;

                case BsOrderStripState.StampHold:
                    if (Time.frameCount > deferredAtFrame
                        && Time.unscaledTime >= deferredAtUnscaledTime
                           + DeliveryStampMinimumHold)
                        eventBus.Publish(OrderStripSignal.Presentation(
                            OrderStripSignalKind.StampHoldElapsed,
                            presentationEpoch, pendingDeliveryReceipt));
                    return;

                case BsOrderStripState.WaitingForDelivery:
                    // Portalsız/instant presenter completion olayı yaymasa bile Busy
                    // takılmaz. Gerçek completion sonradan gelirse FSM onu idempotent
                    // olarak görmezden gelir.
                    if (deliveryPresentationFinished || shelfView == null
                        || !shelfView.DeliveryPlaying)
                        eventBus.Publish(OrderStripSignal.DeliveryFinished(
                            pendingDeliveryReceipt));
                    return;

                case BsOrderStripState.QueueAnimating:
                    if (queueWatchdogAtUnscaledTime >= 0f
                        && Time.unscaledTime >= queueWatchdogAtUnscaledTime)
                        eventBus.Publish(OrderStripSignal.Presentation(
                            OrderStripSignalKind.QueueAnimationAborted,
                            presentationEpoch, pendingDeliveryReceipt));
                    return;

                case BsOrderStripState.Faulted:
                case BsOrderStripState.Detached:
                case BsOrderStripState.Hidden:
                    return;

                case BsOrderStripState.Ready:
                    TickTimers();
                    return;
            }
        }

        /// <summary>Kartları controller'ın açık slotlarına göre yeniden kurar.</summary>
        public void Refresh()
        {
            EnsureEventBusSubscription();
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.SnapshotDirty));
        }

        /// <summary>
        /// Controller snapshot'ını kart görünümlerine uygular. Teslim geçişinde bu çağrı
        /// exit/shift bittikten sonra yapılır; böylece hareket eden kart eski ve doğru
        /// çizimini taşımaya devam eder.
        /// </summary>
        private float ApplySnapshot(bool forceDeal, bool suppressEntrances)
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            BsPalette palette = controller != null ? controller.Palette : null;
            bool live = level != null && controller.State != BartenderLevelState.Unloaded
                        && controller.State != BartenderLevelState.CampaignComplete;
            if (live && !EnsureCardCapacity(level)) return 0f;
            if (cards == null) return 0f;
            PublishIcons();
            CaptureSlotPositions();

            int slots = live ? Mathf.Max(1, level.OrderSlots) : 0;
            bool timed = live && level.AllowTimedOrders;
            bool dealLiveLevel = forceDeal || (live && !hasPresentedLiveLevel);
            int dealIndex = 0;
            float longestDealDuration = 0f;

            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;

                // Kart paleti serialize edilmiyor. Level controller paleti Start sırasında
                // Resources'tan da çözebildiği için bunu her snapshot'ta, SetOrder'dan önce
                // yayınla; aksi halde OrderCardView magenta hata rengine düşer.
                card.Initialize(palette);

                // Level'ın istediğinden fazla kart bağlıysa fazlası hiç kullanılmaz;
                // sahneye üç kart koyup iki slotlu bir level oynamak geçerli bir kurulum.
                bool inUse = i < slots;
                OrderDef order = inUse ? controller.OrderAtSlot(i) : null;
                bool changed = !SameOrder(card.Model, order);
                bool wasEmpty = card.Model == null;
                card.SetOrder(order, timed);
                bool visible = inUse && order != null;
                bool deal = visible && !suppressEntrances
                            && (dealLiveLevel || wasEmpty || changed);
                card.SetVisible(visible, !deal && !suppressEntrances);
                card.SetHighlighted(order != null && HasMatchingGlass(i));
                if (deal)
                {
                    Tween tween = card.PlayDealIn(dealIndex * DealStagger);
                    if (tween != null)
                        longestDealDuration = Mathf.Max(
                            longestDealDuration, tween.Duration(false));
                    dealIndex++;
                }
            }

            hasPresentedLiveLevel = live;
            return longestDealDuration;
        }

        private bool EnsureCardCapacity(BsLevel level)
        {
            int available = cards != null ? cards.Length : 0;
            int required = level != null ? Mathf.Max(1, level.OrderSlots) : 0;
            if (available >= required)
            {
                lastCapacityFaultLevel = null;
                lastCapacityFaultCardCount = -1;
                return true;
            }

            presentationState.Dispatch(BsOrderStripTrigger.BindingRejected);
            HideCardsImmediate(true);
            if (!ReferenceEquals(lastCapacityFaultLevel, level)
                || lastCapacityFaultCardCount != available)
            {
                lastCapacityFaultLevel = level;
                lastCapacityFaultCardCount = available;
                string levelLabel = level != null ? $"Level {level.Index}" : "Aktif level";
                Debug.LogError($"Order strip capacity error: {levelLabel}, {required} slot "
                             + $"istiyor; yalnız {available} kart bağlı. Gameplay güvenli "
                             + "olarak sunum bariyerinde tutuldu.", this);
            }
            return false;
        }

        /// <summary>
        /// Portal uçuşu bittiğinde eski kartlarla çıkış/kayma oynatılır. Snapshot bu
        /// noktada hâlâ uygulanmaz; aksi halde C kartı sola giderken üstünde yeni D
        /// siparişi belirirdi.
        /// </summary>
        private void BeginQueueTransition()
        {
            if (presentationState.State != BsOrderStripState.QueueAnimating) return;
            CaptureSlotPositions();

            int slotCount = ActiveSlotCount();
            int deliveredSlot = pendingDeliveredSlot;
            BartenderDeliveryReceipt deliveryReceipt = pendingDeliveryReceipt;
            if (cards == null || slotCount <= 0 || deliveredSlot < 0
                || deliveredSlot >= slotCount || cards[deliveredSlot] == null
                || deliveryReceipt == null)
            {
                FailClosedQueueTransition();
                return;
            }

            transitionDeliveredSlot = deliveredSlot;
            transitionSlotCount = slotCount;

            cards[deliveredSlot].PlayQueueExit(DeliveryExitDuration);
            float transitionDuration = DeliveryExitDuration;
            for (int i = deliveredSlot + 1; i < slotCount; i++)
            {
                OrderCardView card = cards[i];
                if (card == null || card.Model == null) continue;
                float delay = (i - deliveredSlot - 1) * QueueShiftStagger;
                card.PlayQueueShift(slotPositions[i - 1], QueueShiftDuration, delay);
                transitionDuration = Mathf.Max(
                    transitionDuration, delay + QueueShiftDuration);
            }

            int epoch = presentationEpoch;
            Sequence transition = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true).SetRecyclable(true)
                .AppendInterval(transitionDuration)
                .AppendCallback(() => eventBus.Publish(OrderStripSignal.Presentation(
                    OrderStripSignalKind.QueueAnimationFinished, epoch,
                    deliveryReceipt)));
            queueTransition = transition;
            queueWatchdogAtUnscaledTime = Time.unscaledTime
                                        + transitionDuration + QueueWatchdogGrace;
            transition.OnKill(() =>
            {
                // Normal completion callback'i CommitQueueTransition içinde handle'ı
                // önce null'lar. Buraya hâlâ aynı handle ile geliyorsak sequence dışarıdan
                // öldürülmüştür; Busy'yi fail-closed snapshot ile mutlaka serbest bırak.
                if (epoch != presentationEpoch
                    || !object.ReferenceEquals(queueTransition, transition)) return;
                queueTransition = null;
                eventBus.Publish(OrderStripSignal.Presentation(
                    OrderStripSignalKind.QueueAnimationAborted, epoch,
                    deliveryReceipt));
            });
        }

        private void CommitQueueTransition()
        {
            int deliveredSlot = transitionDeliveredSlot;
            int slotCount = transitionSlotCount;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            transitionDeliveredSlot = -1;
            transitionSlotCount = 0;

            RotateCardViewsLeft(deliveredSlot, slotCount);
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null)
                    cards[i].SetRestingPosition(slotPositions[i], true);

            ClearDeliveryLatch();
            snapshotDirty = false;
            ApplySnapshot(false, true);

            // RefillSlots yeni kartı kuyruğun sonuna koyar. Çıkan view oraya
            // döndürülmüştür; slot doluysa aynı kâğıt sağdan yeni sipariş olarak gelir.
            int replacementSlot = slotCount - 1;
            if (replacementSlot >= 0 && replacementSlot < cards.Length
                && cards[replacementSlot] != null
                && cards[replacementSlot].Model != null)
            {
                Tween tween = cards[replacementSlot].PlayDealIn(DealStagger);
                if (tween != null) StartDealBarrier(tween.Duration(false));
            }
        }

        private void RotateCardViewsLeft(int deliveredSlot, int slotCount)
        {
            if (cards == null || deliveredSlot < 0 || deliveredSlot >= slotCount
                || slotCount > cards.Length)
                return;

            OrderCardView departing = cards[deliveredSlot];
            for (int i = deliveredSlot; i < slotCount - 1; i++)
                cards[i] = cards[i + 1];
            cards[slotCount - 1] = departing;
        }

        private void CancelQueueTransition()
        {
            Sequence oldTransition = queueTransition;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            if (oldTransition != null && oldTransition.IsActive())
                oldTransition.Kill(false);
            transitionDeliveredSlot = -1;
            transitionSlotCount = 0;

            if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].ResetPose();
        }

        private void StartDealBarrier(float duration)
        {
            if (duration <= 0f) return;
            if (presentationState.State != BsOrderStripState.Dealing
                && !presentationState.Dispatch(BsOrderStripTrigger.BeginDeal))
                return;

            dealCompletionEpoch = presentationEpoch;
            dealCompletionAtUnscaledTime = Time.unscaledTime + duration;
        }

        private void FailClosedQueueTransition()
        {
            CancelQueueTransition();
            presentationState.Dispatch(BsOrderStripTrigger.QueueCompleted);
            ClearDeliveryLatch();
            snapshotDirty = false;
            float dealDuration = ApplySnapshot(false, false);
            if (dealDuration > 0f) StartDealBarrier(dealDuration);
        }

        private void ClearDeliveryLatch()
        {
            deferredAtFrame = -1;
            deferredAtUnscaledTime = -1f;
            pendingDeliveredSlot = -1;
            pendingDeliveryReceipt = null;
            deliveryPresentationFinished = false;
        }

        private void ResetPresentationBoundary(bool clearModels)
        {
            presentationEpoch++;
            CancelQueueTransition();
            dealCompletionAtUnscaledTime = -1f;
            dealCompletionEpoch = -1;
            ClearDeliveryLatch();
            snapshotDirty = false;
            hasPresentedLiveLevel = false;
            HideCardsImmediate(clearModels);
        }

        private void HideCardsImmediate(bool clearModels)
        {
            if (cards == null) return;
            BsPalette palette = controller != null ? controller.Palette : null;
            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;
                card.Initialize(palette);
                if (clearModels) card.SetOrder(null, false);
                card.SetVisible(false, false);
                card.ResetPose();
            }
        }

        private void SynchronizeCurrentLevel(bool forceDeal)
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            bool live = level != null
                        && controller.State != BartenderLevelState.Unloaded
                        && controller.State != BartenderLevelState.CampaignComplete;
            if (!live)
            {
                HideCardsImmediate(true);
                presentationState.Dispatch(BsOrderStripTrigger.LevelDeactivated);
                return;
            }

            float dealDuration = ApplySnapshot(forceDeal, false);
            snapshotDirty = false;
            if (dealDuration > 0f)
                StartDealBarrier(dealDuration);
            else if (presentationState.State == BsOrderStripState.Hidden)
                presentationState.Dispatch(BsOrderStripTrigger.ActivateLiveLevel);
        }

        private int ActiveSlotCount()
        {
            if (cards == null || controller == null || controller.CurrentLevel == null)
                return 0;
            return Mathf.Min(cards.Length,
                Mathf.Max(1, controller.CurrentLevel.OrderSlots));
        }

        private void CaptureSlotPositions()
        {
            if (slotPositionsCaptured && cards != null
                && slotPositions.Length == cards.Length)
                return;

            int count = cards != null ? cards.Length : 0;
            slotPositions = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;
                RectTransform cardRt = card.Rt != null
                    ? card.Rt
                    : card.transform as RectTransform;
                if (cardRt == null) continue;
                slotPositions[i] = cardRt.anchoredPosition;
                card.SetRestingPosition(slotPositions[i], false);
            }
            slotPositionsCaptured = true;
        }

        private static bool SameOrder(OrderDef a, OrderDef b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Kind != b.Kind || a.Glass != b.Glass
                || !Mathf.Approximately(a.TimeLimit, b.TimeLimit))
                return false;

            int count = a.Contents != null ? a.Contents.Count : 0;
            if (count != (b.Contents != null ? b.Contents.Count : 0)) return false;
            for (int i = 0; i < count; i++)
                if (a.Contents[i] != b.Contents[i]) return false;
            return true;
        }

        /// <summary>
        /// Bu slotu karşılayan, sahnede duran bir bardak var mı? Kural motoru bunu
        /// bardak başına söylüyor; şerit ise slot başına sormak zorunda.
        /// </summary>
        private bool HasMatchingGlass(int slotIndex)
        {
            if (controller == null) return false;
            BsBoard snapshot = controller.Board;
            if (snapshot == null) return false;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
                if (snapshot.MatchedSlot(snapshot.Glasses[i]) == slotIndex) return true;
            return false;
        }

        private void TickTimers()
        {
            if (cards == null || controller == null) return;
            BsLevel level = controller.CurrentLevel;
            if (level == null || !level.AllowTimedOrders) return;

            bool motionAllowed = controller.State == BartenderLevelState.Playing
                                 && !controller.PresentationLocked;

            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                if (controller.TryGetOrderTimeRemaining(i, out float remaining,
                        out float duration))
                    cards[i].SetTimer(remaining, duration, motionAllowed);
            }
        }

        private void PublishIcons()
        {
            if (iconsPublished || cards == null) return;
            iconsPublished = true;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].SetGlassIcons(glassIcons);
        }

        // ---- Wiring -----------------------------------------------------------------

        private void EnsureEventBusSubscription()
        {
            if (eventBusSubscribed) return;
            eventBus.Subscribe(HandleEventBusSignal);
            eventBusSubscribed = true;
        }

        private void HandleEventBusSignal(OrderStripSignal signal)
        {
            try
            {
                HandleEventBusSignalCore(signal);
            }
            finally
            {
                SynchronizeControllerPresentationBarrier();
            }
        }

        private void HandleEventBusSignalCore(OrderStripSignal signal)
        {
            switch (signal.Kind)
            {
                case OrderStripSignalKind.Activate:
                    if (presentationState.State == BsOrderStripState.Detached)
                        presentationState.Dispatch(BsOrderStripTrigger.Attach);
                    if (presentationState.State == BsOrderStripState.Hidden)
                        SynchronizeCurrentLevel(false);
                    return;

                case OrderStripSignalKind.Deactivate:
                case OrderStripSignalKind.Rebind:
                    ResetPresentationBoundary(true);
                    presentationState.Dispatch(BsOrderStripTrigger.Detach);
                    return;

                case OrderStripSignalKind.LevelLoaded:
                    if (presentationState.State == BsOrderStripState.Detached) return;
                    ResetPresentationBoundary(true);
                    presentationState.Dispatch(BsOrderStripTrigger.LevelLoaded);
                    SynchronizeCurrentLevel(true);
                    return;

                case OrderStripSignalKind.SnapshotDirty:
                    if (presentationState.State == BsOrderStripState.Ready)
                    {
                        snapshotDirty = false;
                        float dealDuration = ApplySnapshot(false, false);
                        if (dealDuration > 0f) StartDealBarrier(dealDuration);
                    }
                    else if (presentationState.State == BsOrderStripState.Hidden)
                    {
                        SynchronizeCurrentLevel(false);
                    }
                    else
                    {
                        // Dealing/hold/queue sırasında UI'yi değiştirme; yalnız kirli
                        // bilgisini tut. Queue commit veya deal completion en yeni
                        // authoritative snapshot'ı tek kez okur.
                        snapshotDirty = true;
                    }
                    return;

                case OrderStripSignalKind.LevelStateChanged:
                    HandleLevelStateSignal(signal.LevelState);
                    return;

                case OrderStripSignalKind.Delivered:
                    HandleDeliveredSignal(signal.Receipt);
                    return;

                case OrderStripSignalKind.DeliveryPresentationFinished:
                    HandleDeliveryPresentationFinishedSignal(signal.Receipt);
                    return;

                case OrderStripSignalKind.StampHoldElapsed:
                    if (!MatchesPendingDelivery(signal)
                        || !presentationState.Dispatch(
                            BsOrderStripTrigger.StampHoldElapsed)) return;
                    if (deliveryPresentationFinished || shelfView == null
                        || !shelfView.DeliveryPlaying)
                        eventBus.Publish(OrderStripSignal.DeliveryFinished(
                            pendingDeliveryReceipt));
                    return;

                case OrderStripSignalKind.QueueAnimationFinished:
                    if (signal.PresentationEpoch != presentationEpoch
                        || !MatchesPendingDelivery(signal)
                        || !presentationState.Dispatch(
                            BsOrderStripTrigger.QueueCompleted)) return;
                    CommitQueueTransition();
                    return;

                case OrderStripSignalKind.QueueAnimationAborted:
                    if (signal.PresentationEpoch != presentationEpoch
                        || !MatchesPendingDelivery(signal)
                        || presentationState.State
                           != BsOrderStripState.QueueAnimating) return;
                    FailClosedQueueTransition();
                    return;

                case OrderStripSignalKind.DealAnimationFinished:
                    if (signal.PresentationEpoch != presentationEpoch) return;
                    CompletePendingDeals();
                    if (!presentationState.Dispatch(
                            BsOrderStripTrigger.DealCompleted)) return;
                    dealCompletionAtUnscaledTime = -1f;
                    dealCompletionEpoch = -1;
                    if (snapshotDirty)
                    {
                        snapshotDirty = false;
                        float dealDuration = ApplySnapshot(false, false);
                        if (dealDuration > 0f) StartDealBarrier(dealDuration);
                    }
                    return;
            }
        }

        private void CompletePendingDeals()
        {
            if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].CompletePendingDeal();
        }

        private void SynchronizeControllerPresentationBarrier()
        {
            if (presentationBarrierController != null
                && !presentationBarrierController.IsPresentationBarrierOwnedBy(this))
                presentationBarrierController = null;

            bool shouldBlock = Application.isPlaying && isActiveAndEnabled
                               && controller != null
                               && presentationState.TransitionPlaying;
            if (presentationBarrierController != null
                && (!shouldBlock
                    || !ReferenceEquals(presentationBarrierController, controller)))
                ReleaseControllerPresentationBarrier();

            if (!shouldBlock || presentationBarrierController != null) return;
            if (controller.AcquirePresentationBarrier(this))
                presentationBarrierController = controller;
        }

        private void ReleaseControllerPresentationBarrier()
        {
            BartenderLevelController owner = presentationBarrierController;
            presentationBarrierController = null;
            if (owner != null) owner.ReleasePresentationBarrier(this);
        }

        private void HandleLevelStateSignal(BartenderLevelState state)
        {
            if (presentationState.State == BsOrderStripState.Detached) return;
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
            {
                ResetPresentationBoundary(true);
                presentationState.Dispatch(BsOrderStripTrigger.LevelDeactivated);
                return;
            }

            if (state == BartenderLevelState.Paused && cards != null)
            {
                for (int i = 0; i < cards.Length; i++)
                    if (cards[i] != null) cards[i].SuspendTimerEmphasis();
            }

            // Pause/Resume presentation state'i değildir ve teslim damgasını silemez.
            // Terminal state'te Ready isek son authoritative snapshot'ı projekte et;
            // hold/queue içindeysek commit'e kadar eski kartı koru.
            if ((state == BartenderLevelState.Won
                 || state == BartenderLevelState.Failed)
                && presentationState.State == BsOrderStripState.Ready)
                eventBus.Publish(OrderStripSignal.Simple(
                    OrderStripSignalKind.SnapshotDirty));
        }

        private void HandleDeliveredSignal(BartenderDeliveryReceipt receipt)
        {
            int slot = receipt != null ? receipt.SlotIndex : -1;
            int slotCount = ActiveSlotCount();
            bool currentReceipt = receipt != null && controller != null
                                  && receipt.Revision == controller.BoardRevision
                                  && receipt.DeliveredGlass != null
                                  && receipt.DeliveredOrder != null;
            if (!currentReceipt || slot < 0 || slot >= slotCount || cards == null
                || slot >= cards.Length || cards[slot] == null
                || !SameOrder(cards[slot].Model, receipt.DeliveredOrder))
            {
                eventBus.Publish(OrderStripSignal.Simple(
                    OrderStripSignalKind.SnapshotDirty));
                return;
            }

            if (!presentationState.Dispatch(BsOrderStripTrigger.DeliveryCommitted))
                return;

            dealCompletionAtUnscaledTime = -1f;
            dealCompletionEpoch = -1;
            deferredAtFrame = Time.frameCount;
            deferredAtUnscaledTime = Time.unscaledTime;
            pendingDeliveredSlot = slot;
            pendingDeliveryReceipt = receipt;
            deliveryPresentationFinished = false;

            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].SuspendTimerEmphasis();
            cards[slot].ShowDelivered();
        }

        private bool MatchesPendingDelivery(OrderStripSignal signal) =>
            pendingDeliveryReceipt != null && signal.Receipt != null
            && signal.PresentationEpoch == presentationEpoch
            && ReferenceEquals(signal.Receipt, pendingDeliveryReceipt);

        private bool MatchesPendingDelivery(BartenderDeliveryReceipt receipt) =>
            receipt != null && ReferenceEquals(receipt, pendingDeliveryReceipt);

        private void HandleDeliveryPresentationFinishedSignal(
            BartenderDeliveryReceipt receipt)
        {
            if (!MatchesPendingDelivery(receipt)) return;
            if (presentationState.State == BsOrderStripState.StampHold)
            {
                deliveryPresentationFinished = true;
                return;
            }
            if (presentationState.State != BsOrderStripState.WaitingForDelivery)
                return;

            deliveryPresentationFinished = true;
            if (!presentationState.Dispatch(
                    BsOrderStripTrigger.DeliveryPresentationFinished)) return;
            BeginQueueTransition();
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                UnsubscribeController();
                subscribedController = controller;
                if (subscribedController != null)
                {
                    int generation = ++controllerSubscriptionGeneration;
                    BartenderLevelController source = subscribedController;
                    levelLoadedSubscription = level =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleLevelLoaded(level);
                    };
                    controllerSnapshotSubscription = () =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleOrdersChanged();
                    };
                    levelStateSubscription = state =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleStateChanged(state);
                    };
                    deliveredSubscription = receipt =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleDelivered(receipt);
                    };
                    subscribedController.LevelLoaded += levelLoadedSubscription;
                    subscribedController.OrdersChanged += controllerSnapshotSubscription;
                    subscribedController.BoardChanged += controllerSnapshotSubscription;
                    subscribedController.StateChanged += levelStateSubscription;
                    subscribedController.Delivered += deliveredSubscription;
                }
            }

            if (subscribedView == shelfView) return;
            UnsubscribeView();
            subscribedView = shelfView;
            if (subscribedView != null)
            {
                int generation = ++viewSubscriptionGeneration;
                BartenderShelfLevelView source = subscribedView;
                deliveryFinishedSubscription = receipt =>
                {
                    if (IsCurrentViewSubscription(source, generation))
                        HandleDeliveryFinished(receipt);
                };
                subscribedView.DeliveryPresentationFinished +=
                    deliveryFinishedSubscription;
            }
        }

        private void Unsubscribe()
        {
            UnsubscribeController();
            UnsubscribeView();
        }

        private void UnsubscribeController()
        {
            controllerSubscriptionGeneration++;
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= levelLoadedSubscription;
                subscribedController.OrdersChanged -= controllerSnapshotSubscription;
                subscribedController.BoardChanged -= controllerSnapshotSubscription;
                subscribedController.StateChanged -= levelStateSubscription;
                subscribedController.Delivered -= deliveredSubscription;
            }
            subscribedController = null;
            levelLoadedSubscription = null;
            controllerSnapshotSubscription = null;
            levelStateSubscription = null;
            deliveredSubscription = null;
        }

        private void UnsubscribeView()
        {
            viewSubscriptionGeneration++;
            if (subscribedView != null)
                subscribedView.DeliveryPresentationFinished -=
                    deliveryFinishedSubscription;
            subscribedView = null;
            deliveryFinishedSubscription = null;
        }

        private bool IsCurrentControllerSubscription(
            BartenderLevelController source, int generation) =>
            isActiveAndEnabled && generation == controllerSubscriptionGeneration
            && ReferenceEquals(source, subscribedController)
            && ReferenceEquals(source, controller);

        private bool IsCurrentViewSubscription(BartenderShelfLevelView source,
                                               int generation) =>
            isActiveAndEnabled && generation == viewSubscriptionGeneration
            && ReferenceEquals(source, subscribedView)
            && ReferenceEquals(source, shelfView);

        private void HandleLevelLoaded(BsLevel level)
        {
            eventBus.Publish(OrderStripSignal.Loaded(level));
        }

        private void HandleOrdersChanged()
        {
            eventBus.Publish(OrderStripSignal.Simple(OrderStripSignalKind.SnapshotDirty));
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            eventBus.Publish(OrderStripSignal.StateChanged(state));
        }

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            eventBus.Publish(OrderStripSignal.Delivery(receipt));
        }

        private void HandleDeliveryFinished(BartenderDeliveryReceipt receipt)
        {
            eventBus.Publish(OrderStripSignal.DeliveryFinished(receipt));
        }
    }
}
