using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Bardak durum rozeti. Aktif bir bardak/zincir kilidinde asma kilit, açık bir
    /// siparişi karşılayan bardakta ✓ gösterir; ✓ durumunda dokunuşla teslimi başlatır.
    ///
    /// Hazır bardağın gövdesine veya rozetine tek dokunuş teslim eder. Rozet ayrı bir
    /// hit-area sunar; domain komutu yine BartenderPourInteraction'ın tek transaction
    /// kapısından geçer. Böylece kullanıcının beklediği "hazır bardağa basınca gider"
    /// davranışı küçük ✓ görselini tam isabet ettirmeyi gerektirmez.
    ///
    /// Bileşen hiçbir GameObject yaratmaz. Her rozet, bardağı gibi elle yerleştirilmiş
    /// bir sahne objesidir ve bardağın ÇOCUĞU olmak zorundadır: teslim uçuşu boyunca
    /// rozetin bardakla birlikte gitmesini, portal sandviçine onunla girmesini ve
    /// kemerin arkasında onunla gizlenmesini bu sağlar.
    /// </summary>
    [DisallowMultipleComponent]
    // Pointer'ı BartenderPourInteraction'dan (varsayılan 0) ÖNCE okur. Rozete inen bir
    // dokunuş önce teslimi denesin; ancak reddedilirse aynı karede seçme/dökme yoluna
    // düşsün diye.
    [DefaultExecutionOrder(-50)]
    public sealed class DeliveryBadgePresenter : MonoBehaviour, IPortalCheckBadgeSource
    {
        [Serializable]
        public sealed class BadgeBinding
        {
            [Tooltip("Havuzdaki Royal bardak.")]
            public LiquidBottle bottle;
            [Tooltip("Bu bardağın ✓ rozeti. Bardağın çocuğu olmalı.")]
            public Transform badge;
            [Tooltip("Rozetin sprite'ı. Dokunma alanı bunun sınırlarından okunur.")]
            public SpriteRenderer badgeRenderer;
            [Tooltip("Rozetin elle verilmiş dinlenme ölçeği; zıplama buna döner.")]
            public Vector3 authoredLocalScale = Vector3.one;
        }

        [Header("Rig references")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private PortalDeliveryAnimator deliveryPortal;
        [Tooltip("Rozet tesliminin atomik deferral + presentation-lock kapısı.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [Tooltip("Opsiyonel. Boşken Camera.main çözülür.")]
        [SerializeField] private Camera inputCamera;

        [Header("Hand-authored badges")]
        [Tooltip("Her havuz bardağı için bir satır. Rozeti olmayan bardak sessizce atlanır.")]
        [SerializeField] private List<BadgeBinding> badges = new List<BadgeBinding>();
        [Tooltip("UnlockAfter veya LockUntil hâlâ aktifken ✓ taşıyıcısında gösterilecek "
               + "asma kilit sprite'ı.")]
        [SerializeField] private Sprite lockSprite;

        [Header("Dokunuş")]
        [Tooltip("Rozete dokunmak teslim etsin. Kapalıyken rozet yalnızca göstergedir.")]
        [SerializeField] private bool tapDelivers = true;
        [Tooltip("Rozetin sprite sınırlarına eklenen pay, layout birimi. Parmak "
               + "rozetten büyüktür; sıfır pay mobilde ıskalatır.")]
        [SerializeField, Min(0f)] private float tapPadding = 0.12f;

        [Header("Beliriş")]
        [Tooltip("Rozet YENİ belirdiğinde zıplar. Durum zaten aynıysa animasyon yok, "
               + "yoksa her hamlede yeniden zıplardı.")]
        [SerializeField, Min(0f)] private float popDuration = 0.22f;
        [SerializeField, Range(1f, 2f)] private float popScale = 1.25f;

        private readonly Dictionary<LiquidBottle, BadgeBinding> badgeByBottle =
            new Dictionary<LiquidBottle, BadgeBinding>();
        private readonly Dictionary<BadgeBinding, Sprite> checkSpriteByBadge =
            new Dictionary<BadgeBinding, Sprite>();
        // shownBadges deliberately means MATCHED badges. Lock badges stay out of this set
        // so neither the portal nor the badge hit-test can mistake a lock for a ✓.
        private readonly HashSet<BadgeBinding> shownBadges = new HashSet<BadgeBinding>();
        private readonly HashSet<BadgeBinding> lockedBadges = new HashSet<BadgeBinding>();

        private BartenderLevelController subscribedController;
        private bool cacheBuilt;

        /// <summary>
        /// Teslim edilmiş ama portalı henüz başlamamış bardak. Delivered, BoardChanged'den
        /// önce çıkar; o iki bildirim arasında bardak board'da yoktur ama rozeti hâlâ
        /// ekrandadır. Bu alan olmadan rozet tam dağılacağı anda sessizce kapanıyordu.
        /// </summary>
        private LiquidBottle pendingDeliveredBottle;

        public string LastRejection { get; private set; }

        private void Awake()
        {
            ResolveDependencies();
            BuildCache();
            HideAll();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            if (deliveryPortal != null) deliveryPortal.CheckBadgeSource = this;
            RefreshMatches();
        }

        private void OnDisable()
        {
            Unsubscribe();
            if (deliveryPortal != null
                && ReferenceEquals(deliveryPortal.CheckBadgeSource, this))
                deliveryPortal.CheckBadgeSource = null;
            HideAll();
        }

        private void OnValidate()
        {
            tapPadding = Mathf.Max(0f, tapPadding);
            popDuration = Mathf.Max(0f, popDuration);
            popScale = Mathf.Clamp(popScale, 1f, 2f);
        }

        /// <summary>
        /// Authoring API for an editor builder, mirroring the shelf view's Configure*
        /// methods. Yalnızca elle sürüklenecek referansları yazar.
        /// </summary>
        public void ConfigureSceneBindings(BartenderLevelController levelController,
                                           BartenderShelfLevelView view,
                                           PortalDeliveryAnimator portal,
                                           BartenderPourInteraction pour,
                                           IReadOnlyList<BadgeBinding> bindings,
                                           Camera sceneCamera = null,
                                           Sprite glassLockSprite = null)
        {
            Unsubscribe();
            controller = levelController;
            shelfView = view;
            deliveryPortal = portal;
            pourInteraction = pour;
            inputCamera = sceneCamera;
            lockSprite = glassLockSprite;

            badges.Clear();
            if (bindings != null)
            {
                for (int i = 0; i < bindings.Count; i++)
                {
                    BadgeBinding source = bindings[i];
                    if (source == null) continue;
                    badges.Add(new BadgeBinding
                    {
                        bottle = source.bottle,
                        badge = source.badge,
                        badgeRenderer = source.badgeRenderer,
                        authoredLocalScale = source.authoredLocalScale
                    });
                }
            }

            cacheBuilt = false;
            BuildCache();
            HideAll();
            if (isActiveAndEnabled)
            {
                Subscribe();
                if (deliveryPortal != null) deliveryPortal.CheckBadgeSource = this;
                RefreshMatches();
            }
        }

        /// <summary>
        /// Strict binding check with a message an artist can act on. Rozetin bardağın
        /// çocuğu olması bir tercih değil şart: teslim uçuşunda rozet bardakla birlikte
        /// gitmezse ekranın ortasında asılı kalır.
        /// </summary>
        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "BartenderShelfLevelView Inspector referansı eksik.";
                return false;
            }
            if (lockSprite == null)
            {
                reason = "Kilitli bardak rozeti için lock sprite referansı eksik.";
                return false;
            }
            if (tapDelivers && pourInteraction == null)
            {
                reason = "Rozet teslimi açık ama BartenderPourInteraction bağlantısı eksik.";
                return false;
            }
            if (pourInteraction != null
                && (pourInteraction.Controller != controller
                    || pourInteraction.ShelfView != shelfView))
            {
                reason = "Rozet ve pour interaction aynı controller/view rig'ine bağlı değil.";
                return false;
            }
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.bottle == null)
                {
                    reason = $"Badges[{i}] bardak referansı eksik.";
                    return false;
                }
                if (binding.badge == null)
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) rozet referansı eksik.";
                    return false;
                }
                if (!binding.badge.IsChildOf(binding.bottle.transform))
                {
                    reason = $"Badges[{i}] rozeti {binding.bottle.name} bardağının çocuğu "
                           + "değil; teslim uçuşunda bardakla gitmez.";
                    return false;
                }
                if (binding.badgeRenderer == null)
                {
                    reason = $"Badges[{i}] rozet SpriteRenderer'ı eksik; dokunma alanı "
                           + "onun sınırlarından okunuyor.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Badge Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log($"Delivery badges: {badges.Count} bağlantı geçerli.", this);
            else
                Debug.LogError("Delivery badge binding error: " + reason, this);
        }

        // ---- Portal badge source --------------------------------------------------

        /// <summary>
        /// Portal, teslim ettiği bardağın rozetini buradan sorar; böylece ✓ tam gizlenme
        /// anında parıltılara ayrılır. Rozeti olmayan bardak null döner, portal da o
        /// beat'i atlar.
        /// </summary>
        public Transform GetCheckBadge(LiquidBottle glass)
        {
            BuildCache();
            if (glass == null) return null;
            return badgeByBottle.TryGetValue(glass, out BadgeBinding binding)
                   && shownBadges.Contains(binding)
                   && binding.badge != null && binding.badge.gameObject.activeSelf
                ? binding.badge
                : null;
        }

        // ---- Match state ----------------------------------------------------------

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            pendingDeliveredBottle = null;
            if (receipt == null || receipt.DeliveredGlass == null || shelfView == null) return;
            if (shelfView.TryGetBottle(receipt.DeliveredGlass.Id, out LiquidBottle bottle))
                pendingDeliveredBottle = bottle;
        }

        private void HandleBoardChanged() => RefreshMatches();

        private void HandleLevelLoaded(BsLevel level) => RefreshMatches();

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
                HideAll();
            else
                RefreshMatches();
        }

        /// <summary>
        /// Which glasses currently satisfy an open order. Rozet bir hamlede belirip bir
        /// sonrakinde kaybolabilir — dökme, karşıladığı kartı bozabilir.
        /// </summary>
        private void RefreshMatches()
        {
            BuildCache();
            if (controller == null || shelfView == null || !shelfView.Ready)
            {
                HideAll();
                return;
            }

            BsBoard snapshot = controller.Board;
            if (snapshot == null)
            {
                HideAll();
                return;
            }

            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null) continue;

                // Uçuştaki bardağın rozeti portala ait: onun dağılma animasyonunu
                // buradan gizlemek, tam da göstermek istediğimiz beat'i yutardı. Teslim
                // edilip henüz havalanmamış bardak da aynı sebeple dokunulmadan geçilir.
                if (ReferenceEquals(binding.bottle, pendingDeliveredBottle)) continue;
                if (deliveryPortal != null && deliveryPortal.IsDelivering(binding.bottle))
                    continue;

                int glassId = -1;
                bool bound = binding.bottle != null
                    && binding.bottle.gameObject.activeInHierarchy
                    && shelfView.TryGetGlassId(binding.bottle, out glassId);
                RtGlass glass = bound ? snapshot.GlassById(glassId) : null;
                bool locked = HasActiveLock(glass, snapshot.Delivered);
                bool matched = !locked && glass != null
                    && snapshot.MatchedSlot(glass) >= 0;
                SetBadgeState(binding, matched
                    ? BadgeState.Matched
                    : (locked ? BadgeState.Locked : BadgeState.Hidden));
            }
        }

        private static bool HasActiveLock(RtGlass glass, int delivered) =>
            glass != null
            && (glass.IsChained(delivered) || glass.HasLocked(delivered));

        /// <summary>
        /// Cheap per-frame reconciliation. Teslim biten bardak havuza döndüğünde board
        /// değişmez, yani olay tabanlı tazeleme oraya uğramaz; rozet o bardakla birlikte
        /// kapanmazsa bir sonraki levelda hazır görünürdü.
        /// </summary>
        private void LateUpdate()
        {
            // Uçuş bittiği anda rozet artık kimsenin değil: portal onu geri verdi,
            // bardak havuza döndü, board ise hiç değişmedi.
            if (pendingDeliveredBottle != null
                && (deliveryPortal == null
                    || !deliveryPortal.IsDelivering(pendingDeliveredBottle)))
                pendingDeliveredBottle = null;

            if (shownBadges.Count == 0 && lockedBadges.Count == 0) return;
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null
                    || (!shownBadges.Contains(binding)
                        && !lockedBadges.Contains(binding))) continue;
                if (ReferenceEquals(binding.bottle, pendingDeliveredBottle)) continue;
                if (deliveryPortal != null && deliveryPortal.IsDelivering(binding.bottle))
                    continue;
                if (binding.bottle == null || !binding.bottle.gameObject.activeInHierarchy
                    || shelfView == null || !shelfView.Ready
                    || !shelfView.TryGetGlassId(binding.bottle, out _))
                    SetBadgeState(binding, BadgeState.Hidden);
            }
        }

        private enum BadgeState
        {
            Hidden,
            Locked,
            Matched
        }

        private void SetBadgeState(BadgeBinding binding, BadgeState state)
        {
            bool wasMatched = shownBadges.Contains(binding);
            bool wasLocked = lockedBadges.Contains(binding);
            if ((state == BadgeState.Hidden && !wasMatched && !wasLocked)
                || (state == BadgeState.Locked && wasLocked)
                || (state == BadgeState.Matched && wasMatched))
                return;

            GameObject badgeObject = binding.badge.gameObject;
            KillBadgeTween(binding.badge);
            shownBadges.Remove(binding);
            lockedBadges.Remove(binding);

            if (state == BadgeState.Hidden)
            {
                RestoreCheckSprite(binding);
                binding.badge.localScale = binding.authoredLocalScale;
                badgeObject.SetActive(false);
                return;
            }

            if (state == BadgeState.Locked)
            {
                if (lockSprite == null)
                {
                    RestoreCheckSprite(binding);
                    binding.badge.localScale = binding.authoredLocalScale;
                    badgeObject.SetActive(false);
                    return;
                }

                lockedBadges.Add(binding);
                binding.badgeRenderer.sprite = lockSprite;
                binding.badge.localScale = ScaleForSprite(binding, lockSprite);
                badgeObject.SetActive(true);
                return;
            }

            shownBadges.Add(binding);
            RestoreCheckSprite(binding);
            badgeObject.SetActive(true);
            if (popDuration <= 0f)
            {
                binding.badge.localScale = binding.authoredLocalScale;
                return;
            }

            // Oyunun en önemli olumlu geri bildirimi bu. Sessizce belirirse oyuncu
            // başardığını ancak fark ederse görüyor.
            float up = popDuration * 0.4f;
            binding.badge.localScale = Vector3.zero;
            Sequence pop = DOTween.Sequence().SetRecyclable(true)
                .SetTarget(binding.badge).SetUpdate(true);
            pop.Append(binding.badge
                .DOScale(binding.authoredLocalScale * popScale, up)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            pop.Append(binding.badge
                .DOScale(binding.authoredLocalScale, popDuration - up)
                .SetEase(Ease.OutBack).SetRecyclable(true));
        }

        private void RestoreCheckSprite(BadgeBinding binding)
        {
            if (binding.badgeRenderer != null
                && checkSpriteByBadge.TryGetValue(binding, out Sprite checkSprite))
                binding.badgeRenderer.sprite = checkSprite;
        }

        /// <summary>
        /// The imported lock and ✓ use the same PPU, but their trimmed art heights differ.
        /// Preserve the hand-authored on-glass height when swapping the sprite.
        /// </summary>
        private Vector3 ScaleForSprite(BadgeBinding binding, Sprite sprite)
        {
            if (sprite == null
                || !checkSpriteByBadge.TryGetValue(binding, out Sprite checkSprite)
                || checkSprite == null || sprite.bounds.size.y <= 0.0001f)
                return binding.authoredLocalScale;
            return binding.authoredLocalScale
                 * (checkSprite.bounds.size.y / sprite.bounds.size.y);
        }

        private void HideAll()
        {
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null) continue;
                KillBadgeTween(binding.badge);
                RestoreCheckSprite(binding);
                binding.badge.localScale = binding.authoredLocalScale;
                binding.badge.gameObject.SetActive(false);
            }
            shownBadges.Clear();
            lockedBadges.Clear();
        }

        private static void KillBadgeTween(Transform badge)
        {
            if (DOTween.IsTweening(badge)) badge.DOKill();
        }

        // ---- Tap ------------------------------------------------------------------

        private void Update()
        {
            if (!tapDelivers || !CanAcceptTap()) return;
            if (!TryReadPointerDown(out Vector2 screenPoint)) return;
            if (!TryPickBadge(screenPoint, out int glassId)) return;
            TryDeliver(glassId, out _);
        }

        /// <summary>
        /// Programmatic entry point. Animated delivery has one gateway: the interaction
        /// opens view deferral, commits the domain command and acquires the exact revision
        /// lock before allowing the portal presentation to start.
        /// </summary>
        public bool TryDeliver(int glassId, out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();
            if (pourInteraction == null)
            {
                LastRejection = "BartenderPourInteraction teslim kapısı eksik.";
                rejectionReason = LastRejection;
                return false;
            }

            bool delivered = pourInteraction.TryCommitAndAnimateDelivery(
                glassId, out string reason);
            LastRejection = delivered ? null : reason;
            rejectionReason = reason;
            return delivered;
        }

        private bool CanAcceptTap()
        {
            return controller != null && shelfView != null
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.DeliveryPlaying
                && !shelfView.SynchronizationDeferred
                && !controller.PresentationLocked
                && pourInteraction != null && !pourInteraction.Busy;
        }

        /// <summary>
        /// Hit-tests the shown badges only. Dokunma alanı rozetin kendi sprite sınırı
        /// artı paydır; gövde hit-testini BartenderPourInteraction ayrı yapar ve eşleşen
        /// bardakta o da aynı teslim transaction'ını başlatır.
        /// </summary>
        private bool TryPickBadge(Vector2 screenPoint, out int glassId)
        {
            glassId = -1;
            Camera camera = ResolveCamera();
            if (camera == null || shownBadges.Count == 0) return false;

            float bestDistance = float.MaxValue;
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || !shownBadges.Contains(binding)
                    || binding.badgeRenderer == null
                    || !binding.badgeRenderer.gameObject.activeInHierarchy) continue;
                if (binding.bottle == null
                    || !shelfView.TryGetGlassId(binding.bottle, out int candidateId))
                    continue;

                Bounds bounds = binding.badgeRenderer.bounds;
                float depth = Vector3.Dot(bounds.center - camera.transform.position,
                                          camera.transform.forward);
                Vector3 world = camera.ScreenToWorldPoint(
                    new Vector3(screenPoint.x, screenPoint.y, depth));
                if (Mathf.Abs(world.x - bounds.center.x) > bounds.extents.x + tapPadding
                    || Mathf.Abs(world.y - bounds.center.y) > bounds.extents.y + tapPadding)
                    continue;

                // Üst üste binen iki rozet olursa merkeze en yakın olan kazanır.
                float distance = (world - bounds.center).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                glassId = candidateId;
            }
            return glassId >= 0;
        }

        private static bool TryReadPointerDown(out Vector2 screenPoint)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Began)
                {
                    screenPoint = touch.position;
                    return true;
                }
            }
            if (Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                return true;
            }
            screenPoint = default;
            return false;
        }

        private Camera ResolveCamera()
        {
            if (inputCamera != null) return inputCamera;
            inputCamera = Camera.main;
            return inputCamera;
        }

        // ---- Wiring ---------------------------------------------------------------

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (pourInteraction == null)
                pourInteraction = GetComponent<BartenderPourInteraction>();
        }

        private void BuildCache()
        {
            if (cacheBuilt) return;
            cacheBuilt = true;
            badgeByBottle.Clear();
            checkSpriteByBadge.Clear();
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.bottle == null) continue;
                badgeByBottle[binding.bottle] = binding;
                if (binding.badgeRenderer != null)
                    checkSpriteByBadge[binding] = binding.badgeRenderer.sprite;
            }
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.BoardChanged += HandleBoardChanged;
            subscribedController.OrdersChanged += HandleBoardChanged;
            subscribedController.StateChanged += HandleStateChanged;
            subscribedController.Delivered += HandleDelivered;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.BoardChanged -= HandleBoardChanged;
                subscribedController.OrdersChanged -= HandleBoardChanged;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.Delivered -= HandleDelivered;
            }
            subscribedController = null;
            pendingDeliveredBottle = null;
        }
    }
}
