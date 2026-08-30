using System.Collections.Generic;
using BartenderSort.Core;
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
        private bool iconsPublished;

        /// <summary>
        /// Teslim edildi, ama kart henüz yenilenmedi. Uçuş bitene kadar şerit donar;
        /// aradaki bütün board bildirimleri tek tazelemede birleşir.
        /// </summary>
        private bool refreshDeferred;
        private int deferredAtFrame = -1;

        public IReadOnlyList<OrderCardView> Cards => cards;

        private void Awake()
        {
            ResolveDependencies();
            PublishIcons();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            PublishIcons();
            Subscribe();
            Refresh();
        }

        private void OnDisable()
        {
            Unsubscribe();
            refreshDeferred = false;
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
            Unsubscribe();
            controller = levelController;
            shelfView = view;

            int count = orderCards != null ? orderCards.Count : 0;
            cards = new OrderCardView[count];
            for (int i = 0; i < count; i++) cards[i] = orderCards[i];

            glassIcons.Clear();
            if (icons != null)
            {
                for (int i = 0; i < icons.Count; i++)
                    if (icons[i] != null) glassIcons.Add(icons[i]);
            }

            iconsPublished = false;
            PublishIcons();
            if (!isActiveAndEnabled) return;
            Subscribe();
            Refresh();
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
            TickTimers();

            if (!refreshDeferred || Time.frameCount <= deferredAtFrame) return;
            // Portalsız bir sahnede DeliveryPresentationFinished hiç çıkmaz. Uçuşun
            // bittiğini oradan değil, görünür durumdan okumak şeridi asla dondurmaz.
            if (shelfView != null && shelfView.DeliveryPlaying) return;
            refreshDeferred = false;
            Refresh();
        }

        /// <summary>Kartları controller'ın açık slotlarına göre yeniden kurar.</summary>
        public void Refresh()
        {
            if (cards == null) return;
            PublishIcons();

            BsLevel level = controller != null ? controller.CurrentLevel : null;
            bool live = level != null && controller.State != BartenderLevelState.Unloaded
                        && controller.State != BartenderLevelState.CampaignComplete;
            int slots = live ? Mathf.Max(1, level.OrderSlots) : 0;
            bool timed = live && level.AllowTimedOrders;

            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;

                // Level'ın istediğinden fazla kart bağlıysa fazlası hiç kullanılmaz;
                // sahneye üç kart koyup iki slotlu bir level oynamak geçerli bir kurulum.
                bool inUse = i < slots;
                OrderDef order = inUse ? controller.OrderAtSlot(i) : null;
                card.SetOrder(order, timed);
                card.SetVisible(inUse && order != null, true);
                card.SetHighlighted(order != null && HasMatchingGlass(i));
            }
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

            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                if (controller.TryGetOrderTimeRemaining(i, out float remaining,
                        out float duration))
                    cards[i].SetTimer(remaining, duration);
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
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.OrdersChanged += HandleOrdersChanged;
                    subscribedController.BoardChanged += HandleOrdersChanged;
                    subscribedController.StateChanged += HandleStateChanged;
                    subscribedController.Delivered += HandleDelivered;
                }
            }

            if (subscribedView == shelfView) return;
            UnsubscribeView();
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.DeliveryPresentationFinished += HandleDeliveryFinished;
        }

        private void Unsubscribe()
        {
            UnsubscribeController();
            UnsubscribeView();
        }

        private void UnsubscribeController()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.OrdersChanged -= HandleOrdersChanged;
                subscribedController.BoardChanged -= HandleOrdersChanged;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.Delivered -= HandleDelivered;
            }
            subscribedController = null;
        }

        private void UnsubscribeView()
        {
            if (subscribedView != null)
                subscribedView.DeliveryPresentationFinished -= HandleDeliveryFinished;
            subscribedView = null;
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            // Yeni level teslim uçuşunu iptal eder; bekleyen tazeleme artık eski tura ait.
            refreshDeferred = false;
            Refresh();
        }

        private void HandleOrdersChanged()
        {
            if (refreshDeferred) return;
            Refresh();
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
                refreshDeferred = false;
            Refresh();
        }

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            refreshDeferred = true;
            deferredAtFrame = Time.frameCount;
            if (receipt == null || cards == null) return;
            int slot = receipt.SlotIndex;
            if (slot >= 0 && slot < cards.Length && cards[slot] != null)
                cards[slot].ShowDelivered();
        }

        private void HandleDeliveryFinished()
        {
            if (!refreshDeferred) return;
            refreshDeferred = false;
            Refresh();
        }
    }
}
