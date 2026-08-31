using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Sipariş kartı: bardak tipi + içerik hedefi.
    ///
    /// BartenderSort projesinden taşındı. İki uyarlama var:
    ///   PrimeTween -> DOTween  (bu proje DOTween kullanıyor)
    ///   BsArt.OrderIcon kaldırıldı — o üreteç bu projede yok. İkon slotu duruyor;
    ///   sprite'ı dışarıdan <see cref="SetIcon"/> ile verilebilir, verilmezse gizlenir.
    ///
    /// Kaynakta eksik referans istisna atıyordu. Burada atmıyor: art henüz gelmedi,
    /// bağlı olan neyse o sürülür. <see cref="IsReady"/> eksiği raporlar.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OrderCardView : MonoBehaviour
    {
        /// <summary>Bir siparişin kaç birim gösterebileceğinin üst sınırı (fıçı bardak).</summary>
        public const int MaxUnits = 5;

        private const string TimerViewResource = "Ui/OrderTimer/OrderTimerView";
        private const string TimerPlateResource = "Ui/OrderTimer/Ui_OrderTimerPlate";
        private const string TimerClockResource = "Ui/OrderTimer/Ui_OrderTimerClock";
        private const string TimerFillResource = "Ui/OrderTimer/Ui_OrderTimerFill";
        private const string RuntimeTimerName = "Order Timer - Runtime";
        private const string TimeBoostFeedbackName = "Time Boost Feedback";

        private static readonly Color TimeBoostFlashColor =
            new Color32(0x78, 0xFF, 0xB0, 0xFF);

        private static GameObject cachedTimerViewPrefab;
        private static bool timerViewLoadAttempted;
        private static bool timerViewWarningIssued;
        private static Sprite cachedTimerPlate;
        private static Sprite cachedTimerClock;
        private static Sprite cachedTimerFill;
        private static bool timerArtLoadAttempted;
        private static bool timerArtWarningIssued;

        /// <summary>
        /// Bir bardak tipinin kart üstündeki çizimi. Üç parça birlikte anlam taşır:
        /// <paramref name="front"/> bardağın kendisi, <paramref name="interiorMask"/> o
        /// bardağın iç boşluğu, <paramref name="interiorRect"/> ise maskenin ön görselin
        /// dikdörtgeni içindeki 0..1 yeri. Üçü de <see cref="VesselProfile"/>'dan gelir;
        /// kart onları yeniden ölçmez, verilen değerlere güvenir.
        /// </summary>
        [Serializable]
        public sealed class GlassIcon
        {
            public GlassType type = GlassType.Kadeh;
            public Sprite front;
            public Sprite interiorMask;
            [Tooltip("İç boşluğun ön görsel dikdörtgeni içindeki normalize yeri.")]
            public Rect interiorRect = new Rect(0.2f, 0.1f, 0.6f, 0.6f);
        }

        [Header("Elle ayarlanmış kart görünümü")]
        [SerializeField] private RectTransform rt = null;
        [SerializeField] private CanvasGroup canvasGroup = null;
        [SerializeField] private Image background = null;
        [Tooltip("Eşleşmede kısa parlayan kenar. Arka plan değil kenar seçildi: kart "
               + "zaten renk katmanları taşıyor, arka planı boyamak onları bozardı.")]
        [SerializeField] private Image edge = null;
        [SerializeField] private Image icon = null;
        [SerializeField] private Image kindBadge = null;
        [Tooltip("Teslim edildi damgası.")]
        [SerializeField] private Image tickBadge = null;
        [SerializeField] private TextMeshProUGUI kindLabel = null;
        [SerializeField] private TextMeshProUGUI description = null;
        [SerializeField] private TextMeshProUGUI timerLabel = null;
        [Tooltip("TMP etiketi bağlanmamış eski sahneler için runtime saniye etiketi.")]
        [SerializeField] private Text timerLegacyLabel = null;
        [Tooltip("Slot boşken görünen yazı.")]
        [SerializeField] private TextMeshProUGUI emptyLabel = null;
        [SerializeField] private Image timerFill = null;
        [SerializeField] private RectTransform timerRoot = null;

        [Header("Sipariş çizimi")]
        [Tooltip("Bardak çiziminin sığdırıldığı kutu. Ön görsel en-boyunu koruyarak "
               + "bu kutuya oturur; kutunun kendisi hiç değişmez.")]
        [SerializeField] private RectTransform iconFitBox = null;
        [Tooltip("Bardağın ön görseli. Sıvı bantlarının ÖNÜNDE çizilmeli.")]
        [SerializeField] private Image glassFront = null;
        [Tooltip("İç boşluk maskesi. Üzerinde Mask (Show Mask Graphic kapalı) olmalı; "
               + "sıvı bantları onun çocuğudur ve bardağın şekline kırpılır.")]
        [SerializeField] private Image interiorMask = null;
        [Tooltip("Dipten yukarı sıvı bantları. En fazla beş; fazlası kullanılmaz.")]
        [SerializeField] private Image[] fillBands = new Image[0];
        [Tooltip("SET siparişlerinde bardağın altında beliren renk noktaları.")]
        [SerializeField] private RectTransform chipRow = null;
        [SerializeField] private Image[] chips = new Image[0];
        [Tooltip("İki nokta arasındaki merkez mesafesi, kart piksel biriminde.")]
        [SerializeField, Min(0f)] private float chipSpacing = 34f;

        [Header("Tamamlanma parıltısı")]
        [Tooltip("Tamamlanan siparişin kısa süre görünen net, sıcak ışık çizgisi.")]
        [SerializeField] private Color completionLineColor =
            new Color32(0xFF, 0xF4, 0xD0, 0xFF);
        [Tooltip("Parlak çizginin sönerken geçtiği sıcak altın ton.")]
        [SerializeField] private Color completionGlowColor =
            new Color32(0xFF, 0xBD, 0x3E, 0xFF);

        [Header("Durum renkleri")]
        [SerializeField] private Color accentColor = new Color(0.95f, 0.68f, 0.20f, 1f);
        [SerializeField] private Color badColor = new Color(0.85f, 0.28f, 0.24f, 1f);
        [SerializeField] private Color layerBadgeColor = new Color32(0xE8, 0x8B, 0x3C, 0xFF);
        [Tooltip("Boş slot dolu bir kart gibi görünmemeli — sadece soluk bir yuva.")]
        [SerializeField] private Color emptySlotColor = new Color(1f, 1f, 1f, 0.14f);
        [Header("Süre rayı")]
        [SerializeField] private Color timerNormalColor = new Color32(0xFF, 0xD3, 0x53, 0xFF);
        [SerializeField] private Color timerWarningColor = new Color32(0xFF, 0x9F, 0x3D, 0xFF);
        [SerializeField] private Color timerCriticalColor = new Color32(0xFF, 0x59, 0x64, 0xFF);
        [SerializeField, Range(0.05f, 0.95f)] private float timerWarningRatio = 0.33f;
        [SerializeField, Min(1f)] private float timerCriticalSeconds = 5f;

        private bool highlighted;
        private bool initialized;
        private bool restPositionInitialized;
        private bool desiredVisible;
        private readonly BsOrderCardStateMachine presentationState =
            new BsOrderCardStateMachine();
        private uint lifecycleRevision;
        private uint tickRevision;
        private uint edgeRevision;
        private uint timeBoostRevision;
        private Tween lifecycleTween;
        private Tween visibilityTween;
        private Tween edgeTween;
        private Tween tickTween;
        private Tween timeBoostTween;
        private Vector3 authoredScale = Vector3.one;
        private Vector3 authoredEdgeScale = Vector3.one;
        private Vector3 authoredTickScale = Vector3.one;
        private Vector3 authoredTimerScale = Vector3.one;
        private Vector2 restingAnchoredPosition;
        private int shownTimerSecond = -1;
        private float urgentTimerKick;
        private float timeBoostPulse;
        private float timeBoostFlash;
        private bool timerFillGeometryCaptured;
        private float timerFillFullWidth;
        private float timerFillLeftEdge;
        private Color timerFillBaseColor = Color.white;
        private Image timerClock;
        private Color timerClockBaseColor = Color.white;
        private RectTransform timeBoostFeedbackRoot;
        private CanvasGroup timeBoostFeedbackCanvasGroup;
        private TextMeshProUGUI timeBoostFeedbackLabel;
        private Text timeBoostFeedbackLegacyLabel;
        private Vector2 timeBoostFeedbackBasePosition;
        private BsPalette palette;
        private readonly Dictionary<GlassType, GlassIcon> iconByType =
            new Dictionary<GlassType, GlassIcon>(5);

        public OrderDef Model { get; private set; }
        public RectTransform Rt => rt;
        internal BsOrderCardState PresentationState => presentationState.State;

        /// <summary>
        /// Elle bağlanmamış parçaları sayar; kurulum denetimi için.
        ///
        /// TMP etiketleri BURADA ARANMAZ. Kart siparişi mockup'taki gibi çizimle
        /// anlatıyor: bardak resmi + katman/nokta. Yazı, bağlıysa sürülen bir ek;
        /// bağlı değilken kartın eksik olduğu anlamına gelmez.
        /// </summary>
        public bool IsReady()
        {
            return rt != null && canvasGroup != null && background != null && edge != null
                   && iconFitBox != null && glassFront != null && interiorMask != null
                   && fillBands != null && fillBands.Length >= MaxUnits
                   && chips != null && chips.Length >= MaxUnits;
        }

        public void Initialize(BsPalette pal)
        {
            palette = pal;
            // Presenter her snapshot'ta güncel controller paletini yeniden yayınlar.
            // Layout/tween dinlenme pozlarını ise yalnız ilk kurulumda yakala; aksi halde
            // devam eden bir deal animasyonunun geçici scale'i yeni authoredScale olur.
            if (initialized) return;

            if (rt == null) rt = transform as RectTransform;
            authoredScale = rt != null ? rt.localScale : Vector3.one;
            if (rt != null && !restPositionInitialized)
            {
                restingAnchoredPosition = rt.anchoredPosition;
                restPositionInitialized = true;
            }
            authoredEdgeScale = edge != null
                ? edge.rectTransform.localScale
                : Vector3.one;
            authoredTickScale = tickBadge != null
                ? tickBadge.rectTransform.localScale
                : Vector3.one;
            authoredTimerScale = timerRoot != null
                ? timerRoot.localScale
                : Vector3.one;
            CaptureTimerFillGeometry();
            CaptureTimerArtState();
            highlighted = false;
            CanonicalizeCompletionEdge();
            desiredVisible = false;
            presentationState.Dispatch(BsOrderCardTrigger.InitializeHidden);
            CanonicalizeVisibility(false);
            initialized = true;
        }

        /// <summary>
        /// Bardak çizimlerini verir. Tablo kartın kendisinde tutulmaz — üç kart aynı
        /// listeyi paylaşır ve listenin sahibi <see cref="OrderStripPresenter"/>'dır.
        /// </summary>
        public void SetGlassIcons(IReadOnlyList<GlassIcon> table)
        {
            iconByType.Clear();
            if (table == null) return;
            for (int i = 0; i < table.Count; i++)
            {
                GlassIcon icon = table[i];
                if (icon != null) iconByType[icon.type] = icon;
            }
        }

        /// <summary>
        /// Kartın ikonunu dışarıdan verir. Kaynak projede burada üretilmiş bir sprite
        /// vardı; o üreteç taşınmadığı için karar dışarıya bırakıldı.
        /// </summary>
        public void SetIcon(Sprite sprite)
        {
            if (icon == null) return;
            icon.sprite = sprite;
            icon.gameObject.SetActive(sprite != null);
        }

        public void SetOrder(OrderDef order, bool timedOrdersEnabled)
        {
            if (!initialized) Initialize(palette);

            bool orderChanged = !SameOrder(Model, order);
            if (orderChanged) CancelTimeBoostFeedback();
            Model = order;
            bool has = order != null;

            if (icon != null && icon.sprite == null) icon.gameObject.SetActive(false);
            if (kindBadge != null) kindBadge.gameObject.SetActive(has);
            if (tickBadge != null) tickBadge.gameObject.SetActive(false);
            if (background != null) background.color = has ? Color.white : emptySlotColor;
            if (orderChanged)
            {
                highlighted = false;
                CancelEdgeTween();
                CancelTickTween();
            }
            if (emptyLabel != null) emptyLabel.gameObject.SetActive(!has);

            if (!has)
            {
                if (description != null) description.text = "";
                if (timerRoot != null) timerRoot.gameObject.SetActive(false);
                ResetTimerVisual();
                DrawOrder(null);
                return;
            }

            bool layer = order.Kind == OrderKind.Layer;
            if (kindLabel != null) kindLabel.text = layer ? "KATMAN" : "SET";
            if (kindBadge != null) kindBadge.color = layer ? layerBadgeColor : accentColor;
            if (description != null && palette != null)
                description.text = order.Describe(palette);

            bool timed = timedOrdersEnabled && order.TimeLimit > 0f;
            if (!timed) CancelTimeBoostFeedback();
            if (timed) EnsureTimerVisuals();
            if (timerRoot != null) timerRoot.gameObject.SetActive(timed);
            if (!timed || orderChanged) ResetTimerVisual();
            DrawOrder(order);
        }

        // ---- Sipariş çizimi ---------------------------------------------------------
        //
        // Kart, kuralı METİNLE DEĞİL ÇİZİMLE anlatır ve iki tipi kasıtlı olarak farklı
        // çizer:
        //
        //   KATMAN — renkler bardağın İÇİNE, dipten yukarı, tam sırasıyla konur.
        //            Oyuncu "şu bardağı aynen böyle doldur" der.
        //   SET    — bardak BOŞ çizilir, renkler altında nokta olarak durur.
        //            Sıranın önemsiz olduğu bilgisi tam da buradan okunur.
        //
        // Tek birimlik bir SET'te ayrım anlamsız olduğu için o da dolu çizilir: tek
        // renkte "sırasız" diye bir şey yoktur ve boş bardak + tek nokta, kartı kuralın
        // olmadığı bir yerde karmaşıklaştırırdı.

        private void DrawOrder(OrderDef order)
        {
            GlassIcon icon = null;
            if (order != null) iconByType.TryGetValue(order.Glass, out icon);

            if (glassFront != null)
            {
                glassFront.sprite = icon != null ? icon.front : null;
                glassFront.enabled = icon != null && icon.front != null;
            }
            if (interiorMask != null)
            {
                interiorMask.sprite = icon != null ? icon.interiorMask : null;
                interiorMask.enabled = icon != null && icon.interiorMask != null;
            }
            if (icon != null) FitIcon(icon);

            int units = order != null && order.Contents != null ? order.Contents.Count : 0;
            bool asFill = order != null
                          && (order.Kind == OrderKind.Layer || units <= 1);
            DrawFillBands(asFill ? order : null, units);
            DrawChips(asFill || order == null ? null : order, units);
        }

        /// <summary>
        /// Ön görseli fit kutusuna en-boy koruyarak oturtur ve iç boşluk maskesini
        /// aynı ölçekle onun üstüne bindirir. Böylece beş ayrı bardak tek kutuda,
        /// birbirine göre doğru boyutta ve tam olarak kendi iç boşluğuyla çizilir.
        /// </summary>
        private void FitIcon(GlassIcon icon)
        {
            if (iconFitBox == null || glassFront == null || icon.front == null) return;

            Rect box = iconFitBox.rect;
            Vector2 spriteSize = icon.front.rect.size;
            if (spriteSize.x <= 0f || spriteSize.y <= 0f
                || box.width <= 0f || box.height <= 0f) return;

            float fit = Mathf.Min(box.width / spriteSize.x, box.height / spriteSize.y);
            Vector2 drawn = spriteSize * fit;

            RectTransform frontRt = glassFront.rectTransform;
            Center(frontRt);
            frontRt.sizeDelta = drawn;
            frontRt.anchoredPosition = Vector2.zero;

            if (interiorMask == null) return;
            RectTransform maskRt = interiorMask.rectTransform;
            Center(maskRt);
            Rect inside = icon.interiorRect;
            maskRt.sizeDelta = new Vector2(inside.width * drawn.x, inside.height * drawn.y);
            // interiorRect is measured from the front sprite's bottom-left; the RectTransform
            // is centred, so the offset is the interior centre minus the sprite centre.
            maskRt.anchoredPosition = new Vector2(
                (inside.center.x - 0.5f) * drawn.x,
                (inside.center.y - 0.5f) * drawn.y);
        }

        private static void Center(RectTransform target)
        {
            target.anchorMin = new Vector2(0.5f, 0.5f);
            target.anchorMax = new Vector2(0.5f, 0.5f);
            target.pivot = new Vector2(0.5f, 0.5f);
        }

        private void DrawFillBands(OrderDef order, int units)
        {
            if (fillBands == null) return;
            int shown = order != null ? Mathf.Clamp(units, 0, fillBands.Length) : 0;
            for (int i = 0; i < fillBands.Length; i++)
            {
                Image band = fillBands[i];
                if (band == null) continue;
                bool live = i < shown;
                band.gameObject.SetActive(live);
                if (!live) continue;

                // Dipten yukarı: Contents[0] en alttaki katmandır.
                RectTransform bandRt = band.rectTransform;
                bandRt.anchorMin = new Vector2(0f, i / (float)shown);
                bandRt.anchorMax = new Vector2(1f, (i + 1) / (float)shown);
                bandRt.offsetMin = Vector2.zero;
                bandRt.offsetMax = Vector2.zero;
                band.color = palette != null
                    ? palette.ColorAt(order.Contents[i])
                    : Color.magenta;
            }
        }

        private void DrawChips(OrderDef order, int units)
        {
            bool any = order != null && units > 0 && chips != null;
            if (chipRow != null) chipRow.gameObject.SetActive(any);
            if (chips == null) return;

            int shown = any ? Mathf.Clamp(units, 0, chips.Length) : 0;
            float first = -0.5f * chipSpacing * (shown - 1);
            for (int i = 0; i < chips.Length; i++)
            {
                Image chip = chips[i];
                if (chip == null) continue;
                bool live = i < shown;
                chip.gameObject.SetActive(live);
                if (!live) continue;
                chip.rectTransform.anchoredPosition =
                    new Vector2(first + i * chipSpacing, 0f);
                chip.color = palette != null
                    ? palette.ColorAt(order.Contents[i])
                    : Color.magenta;
            }
        }

        /// <summary>
        /// Boş slot şeritten silinir (deste tükendiğinde artık sipariş gelmez), dolu
        /// kart görünür kalır. Obje aktif kalır — slot indeksine bağlı süre/teslim
        /// mantığı bozulmasın.
        /// </summary>
        public void SetVisible(bool visible, bool animate)
        {
            if (canvasGroup == null) return;
            if (!initialized) Initialize(palette);

            if (!visible) CancelTimeBoostFeedback();

            desiredVisible = visible;
            bool lifecycleBoundary = presentationState.State
                                     == BsOrderCardState.Uninitialized
                                     || presentationState.State
                                     == BsOrderCardState.Disabled;
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                InvalidateLifecycleTweens();
                CanonicalizePose();
                presentationState.Dispatch(BsOrderCardTrigger.Disable);
                CanonicalizeVisibility(false);
                return;
            }
            // Serialize edilmiş CanvasGroup alpha'sı 1 olabilir. İlk gizleme hiçbir
            // zaman fade değildir; render edilmeden önce atomik Hidden'a geçer.
            if (!visible && lifecycleBoundary) animate = false;

            float targetAlpha = visible ? 1f : 0f;
            bool alreadyStable = visible
                ? presentationState.State == BsOrderCardState.Visible
                  && Mathf.Approximately(canvasGroup.alpha, 1f)
                : presentationState.State == BsOrderCardState.Hidden
                  && Mathf.Approximately(canvasGroup.alpha, 0f);
            bool matchingTransition = visible
                ? presentationState.State == BsOrderCardState.Dealing
                  && HasActiveTween(lifecycleTween, visibilityTween)
                : presentationState.State == BsOrderCardState.Exiting
                  && HasActiveTween(lifecycleTween, visibilityTween);

            // animate=false bir komuttur ve her zaman canonicalize edilir. animate=true
            // ise ancak gerçek state + alpha da hedefi doğruluyorsa no-op olabilir.
            if (animate && (alreadyStable || matchingTransition))
            {
                SetCanvasInteraction(false);
                return;
            }

            uint revision = InvalidateLifecycleTweens();
            CanonicalizePose();
            if (!animate || Mathf.Approximately(canvasGroup.alpha, targetAlpha))
            {
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(visible);
                return;
            }

            bool accepted = presentationState.Dispatch(visible
                ? BsOrderCardTrigger.BeginDeal
                : BsOrderCardTrigger.BeginExit);
            if (!accepted)
            {
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(visible);
                return;
            }

            SetCanvasInteraction(false);
            Tween tween = canvasGroup.DOFade(targetAlpha, 0.2f)
                .SetUpdate(true).SetRecyclable(true);
            visibilityTween = tween;
            tween.OnComplete(() => CompleteVisibilityTween(tween, revision, visible))
                .OnKill(() => ForgetVisibilityTween(tween, revision));
        }

        public void SetTimer(float remaining, float total) =>
            SetTimer(remaining, total, true);

        /// <summary>
        /// Gerçek sipariş deadline'ını ray + sayı olarak sunar. Hareket ayrı kapıdır:
        /// pause veya presentation lock sırasında değer yerinde kalır, rozet oynamaz.
        /// </summary>
        public void SetTimer(float remaining, float total, bool motionAllowed)
        {
            EnsureTimerVisuals();
            if (timerRoot == null || !timerRoot.gameObject.activeSelf) return;

            float safeRemaining = Mathf.Max(0f, remaining);
            float t = total > 0f ? Mathf.Clamp01(safeRemaining / total) : 0f;
            SetTimerProgress(t);

            bool critical = safeRemaining > 0f && safeRemaining <= timerCriticalSeconds;
            timerFillBaseColor = critical
                ? timerCriticalColor
                : t <= timerWarningRatio ? timerWarningColor : timerNormalColor;
            ApplyTimerFeedbackColors();

            int second = Mathf.CeilToInt(safeRemaining);
            if (second != shownTimerSecond)
            {
                SetTimerText(second + " sn");
                if (critical && shownTimerSecond >= 0) urgentTimerKick = 1f;
                shownTimerSecond = second;
            }

            if (!critical || !motionAllowed)
            {
                urgentTimerKick = 0f;
                ApplyTimerMotion();
                return;
            }

            // Yalnız son beş saniye: her saniye değişiminde tek, küçük bir vurgu.
            // Sürekli nefes/sallama yok; bardak çizimi ve teslim animasyonu sakin kalır.
            ApplyTimerMotion();
            urgentTimerKick = Mathf.MoveTowards(
                urgentTimerKick, 0f, Time.unscaledDeltaTime * 7.5f);
        }

        /// <summary>
        /// Teslim/presentation gecikmesinde değer başka slota ait olabilir; sayıyı
        /// değiştirmeden yalnız son-saniye vurgusunu dinlenme pozuna döndürür.
        /// </summary>
        public void SuspendTimerEmphasis()
        {
            CancelTimeBoostFeedback();
            ResetTimerMotion();
        }

        /// <summary>
        /// Başarılı +süre satın alımını gerçek deadline'dan bağımsız, geçici bir katman
        /// olarak sunar. Üst üste çağrılar önceki nesli öldürür; sayı/fill değeri her zaman
        /// presenter'ın çağırdığı <see cref="SetTimer(float,float,bool)"/> tarafından sürülür.
        /// </summary>
        public void PlayTimeBoostFeedback(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f
                || Model == null || Model.TimeLimit <= 0f || !desiredVisible
                || presentationState.State != BsOrderCardState.Visible
                || !isActiveAndEnabled || !gameObject.activeInHierarchy)
                return;

            EnsureTimerVisuals();
            if (timerRoot == null || !timerRoot.gameObject.activeSelf
                || !EnsureTimeBoostFeedbackVisuals())
                return;

            uint revision = InvalidateTimeBoostTween();
            SetTimeBoostFeedbackText(seconds);
            timeBoostFeedbackRoot.gameObject.SetActive(true);
            timeBoostFeedbackRoot.anchoredPosition = timeBoostFeedbackBasePosition;
            timeBoostFeedbackRoot.localScale = Vector3.one * 0.86f;
            timeBoostFeedbackCanvasGroup.alpha = 0f;
            timeBoostPulse = 0f;
            timeBoostFlash = 0f;
            ApplyTimerMotion();
            ApplyTimerFeedbackColors();

            Sequence sequence = DOTween.Sequence()
                .SetTarget(timeBoostFeedbackRoot).SetUpdate(true).SetRecyclable(true);
            sequence.Append(DOTween.To(
                    () => timeBoostPulse,
                    value =>
                    {
                        timeBoostPulse = value;
                        ApplyTimerMotion();
                    }, 1f, 0.12f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(DOTween.To(
                    () => timeBoostFlash,
                    value =>
                    {
                        timeBoostFlash = value;
                        ApplyTimerFeedbackColors();
                    }, 1f, 0.10f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(timeBoostFeedbackCanvasGroup.DOFade(1f, 0.10f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.AppendInterval(0.16f);
            sequence.Append(DOTween.To(
                    () => timeBoostPulse,
                    value =>
                    {
                        timeBoostPulse = value;
                        ApplyTimerMotion();
                    }, 0f, 0.28f)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            sequence.Join(DOTween.To(
                    () => timeBoostFlash,
                    value =>
                    {
                        timeBoostFlash = value;
                        ApplyTimerFeedbackColors();
                    }, 0f, 0.32f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(timeBoostFeedbackCanvasGroup.DOFade(0f, 0.32f)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.Insert(0f, timeBoostFeedbackRoot.DOAnchorPosY(
                    timeBoostFeedbackBasePosition.y + 27f, 0.60f)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            sequence.Insert(0f, timeBoostFeedbackRoot.DOScale(1.06f, 0.18f)
                .SetEase(Ease.OutBack).SetRecyclable(true));

            timeBoostTween = sequence;
            sequence.OnComplete(() => CompleteTimeBoostTween(sequence, revision))
                .OnKill(() => ForgetTimeBoostTween(sequence, revision));
        }

        /// <summary>Level/pause/lifecycle sınırlarında geçici +süre katmanını temizler.</summary>
        public void CancelTimeBoostFeedback() => InvalidateTimeBoostTween();

        /// <summary>
        /// Presenter'ın sabit slot koordinatını karta verir. Kart görünümleri teslimde
        /// gerçekten yer değiştirdiği için dinlenme pozu artık doğduğu slot değil,
        /// presenter'ın o anda ona atadığı slottur.
        /// </summary>
        public void SetRestingPosition(Vector2 anchoredPosition, bool snap)
        {
            if (rt == null) rt = transform as RectTransform;
            restingAnchoredPosition = anchoredPosition;
            restPositionInitialized = true;
            if (!snap || rt == null) return;

            // Kuyruktan çıkan gizli view aynı değerde bir siparişle yeniden kullanılabilir.
            // Değer eşitliği SetOrder'da assignment değişimini sakladığı için, eski teslim
            // parıltısını burada — yeni kart dağıtılmadan hemen önce — kesin olarak temizle.
            if (!desiredVisible)
            {
                highlighted = false;
                CancelEdgeTween();
            }

            InvalidateLifecycleTweens();
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            rt.anchoredPosition = anchoredPosition;
            rt.localScale = authoredScale;
            CanonicalizeVisibility(desiredVisible);
        }

        /// <summary>Yeni siparişi sağdan dağıtır; sprite veya ek pivot gerektirmez.</summary>
        public Tween PlayDealIn(float delay = 0f)
        {
            CancelTimeBoostFeedback();
            if (rt == null || canvasGroup == null) return null;
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                SetVisible(true, false);
                return null;
            }

            desiredVisible = true;
            uint revision = InvalidateLifecycleTweens();
            presentationState.Dispatch(BsOrderCardTrigger.BeginDeal);
            ResetTimerVisual();
            float width = Mathf.Max(1f, rt.rect.width);
            Vector2 start = restingAnchoredPosition + new Vector2(width * 0.48f, -width * 0.06f);
            rt.anchoredPosition = start;
            rt.localScale = authoredScale * 0.90f;
            canvasGroup.alpha = 0f;

            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            if (delay > 0f) sequence.AppendInterval(delay);
            sequence.Append(rt.DOAnchorPos(restingAnchoredPosition, 0.26f)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            sequence.Join(canvasGroup.DOFade(1f, 0.14f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(rt.DOScale(authoredScale * 1.035f, 0.21f)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            sequence.Append(rt.DOScale(authoredScale, 0.11f)
                .SetEase(Ease.OutBack).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, true);
        }

        /// <summary>Teslim damgası okunduktan sonra kartı kuyruktan çıkarır.</summary>
        public Tween PlayQueueExit(float duration)
        {
            CancelTimeBoostFeedback();
            if (rt == null || canvasGroup == null) return null;

            desiredVisible = false;
            uint revision = InvalidateLifecycleTweens();
            if (!presentationState.Dispatch(BsOrderCardTrigger.BeginExit))
            {
                presentationState.Dispatch(BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(false);
                return null;
            }
            float width = Mathf.Max(1f, rt.rect.width);
            float height = Mathf.Max(1f, rt.rect.height);
            Vector2 end = restingAnchoredPosition
                          + new Vector2(-width * 0.58f, height * 0.16f);

            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            sequence.Append(rt.DOAnchorPos(end, duration)
                .SetEase(Ease.InCubic).SetRecyclable(true));
            sequence.Join(rt.DOScale(authoredScale * 0.86f, duration)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.Join(canvasGroup.DOFade(0f, duration * 0.78f)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, false);
        }

        /// <summary>
        /// Kartın içeriğini değiştirmeden komşu slota taşır. Dinlenme konumu completion
        /// anında presenter tarafından commit edilir; yarıda kesilirse ResetPose eski
        /// slota güvenle dönebilir.
        /// </summary>
        public Tween PlayQueueShift(Vector2 destination, float duration, float delay)
        {
            CancelTimeBoostFeedback();
            if (rt == null) return null;
            desiredVisible = true;
            uint revision = InvalidateLifecycleTweens();
            if (!presentationState.Dispatch(BsOrderCardTrigger.BeginShift))
            {
                presentationState.Dispatch(BsOrderCardTrigger.ShowImmediate);
                CanonicalizeVisibility(true);
            }

            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            if (delay > 0f) sequence.AppendInterval(delay);
            sequence.Append(rt.DOAnchorPos(destination, duration)
                .SetEase(Ease.InOutCubic).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, false);
        }

        /// <summary>
        /// Teslim edildi damgasi. Oyunun en odullendirici ani ve eskiden sadece
        /// SetActive(true) idi -- damga bir karede beliriyordu. Simdi buyuyerek oturuyor
        /// ve kart hafifce geri cekiliyor: gozun takip edecegi bir hareket olmadan
        /// "odul" okunmuyor.
        /// </summary>
        public void ShowDelivered()
        {
            SuspendTimerEmphasis();
            if (timerRoot != null) timerRoot.gameObject.SetActive(false);

            // Eşleşme işareti eskiden tam opak yeşil olarak bütün portal sunumu boyunca
            // kartta kalıyordu. Teslim beat'inde onu tek kullanımlık sıcak bir ışık
            // çizgisine çevir: hızlıca belirginleşsin, tick okunurken doğal biçimde sönsün.
            highlighted = false;
            PlayCompletionEdgePulse();

            desiredVisible = true;
            uint poseRevision = InvalidateLifecycleTweens();
            presentationState.Dispatch(BsOrderCardTrigger.ResetVisible);
            CanonicalizePose();
            CanonicalizeVisibility(true);

            if (tickBadge != null)
            {
                tickBadge.gameObject.SetActive(true);
                RectTransform trt = tickBadge.rectTransform;
                uint revision = InvalidateTickTween();
                trt.localScale = Vector3.zero;
                Sequence tickSequence = DOTween.Sequence()
                    .SetTarget(trt).SetUpdate(true).SetRecyclable(true)
                    .Append(trt.DOScale(authoredTickScale * 1.3f, 0.16f)
                        .SetEase(Ease.OutQuad).SetRecyclable(true))
                    .Append(trt.DOScale(authoredTickScale, 0.2f)
                        .SetEase(Ease.OutBack).SetRecyclable(true));
                TrackTick(tickSequence, revision);
            }

            if (rt == null) return;
            Sequence poseSequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true)
                .Append(rt.DOScale(authoredScale * 0.94f, 0.14f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .Append(rt.DOScale(authoredScale, 0.18f)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
            TrackPosePulse(poseSequence, poseRevision);
        }

        /// <summary>
        /// Presenter watchdog endpoint. If a global tween pause/kill prevents the authored
        /// deal callback, the card is seated atomically before the strip releases gameplay.
        /// </summary>
        internal void CompletePendingDeal()
        {
            if (presentationState.State != BsOrderCardState.Dealing) return;
            desiredVisible = true;
            InvalidateLifecycleTweens();
            presentationState.Dispatch(BsOrderCardTrigger.ResetVisible);
            CanonicalizePose();
            CanonicalizeVisibility(true);
        }

        /// <summary>
        /// Bu siparişi karşılayan bir bardak var mı — parlayıp ince bir kenar izi bırakır.
        ///
        /// NEDEN GEREKLİ: bardak ✓ alıyordu ama HANGİ siparişi karşıladığı hiçbir yerde
        /// görünmüyordu. Üç kart varken oyuncu bunu tahmin etmek zorunda kalıyor ve
        /// "sipariş bir SET değil, sıralı bir KATMAN yığınıdır" fikri hiç oturmuyor.
        /// Eşleşmeyi göstermek, kuralı anlatmadan öğretiyor.
        /// </summary>
        public void SetHighlighted(bool on)
        {
            if (highlighted == on) return;
            highlighted = on;

            if (on) PlayCompletionEdgePulse();
            else FadeCompletionEdge();

            if (!on || rt == null || presentationState.State
                != BsOrderCardState.Visible || IsTweenActive(lifecycleTween)) return;
            uint revision = InvalidateLifecycleTweens();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true)
                .Append(rt.DOScale(authoredScale * 1.025f, 0.11f)
                    .SetEase(Ease.OutCubic).SetRecyclable(true))
                .Append(rt.DOScale(authoredScale, 0.20f)
                    .SetEase(Ease.InOutSine).SetRecyclable(true));
            TrackPosePulse(sequence, revision);
        }

        /// <summary>
        /// Placeholder kenarı kalıcı bir renge boyamak yerine kısa bir ışık izi oynatır.
        /// Çizgi fildişi-altın arasında parlar. Eşleşmede ince bir altın iz kalır;
        /// teslimde aynı efekt bütünüyle transparana oturur.
        /// </summary>
        private void PlayCompletionEdgePulse()
        {
            if (edge == null) return;

            uint revision = InvalidateEdgeTween();
            RectTransform lineRect = edge.rectTransform;
            Color lineWarm = WithAlpha(completionGlowColor, 0.62f);
            Color lineRest = CompletionEdgeRestColor();

            Sequence sequence = DOTween.Sequence()
                .SetTarget(edge).SetUpdate(true).SetRecyclable(true)
                .Append(edge.DOColor(completionLineColor, 0.10f)
                    .SetEase(Ease.OutSine).SetRecyclable(true))
                .Join(lineRect.DOScale(authoredEdgeScale * 1.012f, 0.14f)
                    .SetEase(Ease.OutCubic).SetRecyclable(true))
                .Append(edge.DOColor(lineWarm, 0.14f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .AppendInterval(0.06f)
                .Append(edge.DOColor(lineRest, 0.36f)
                    .SetEase(Ease.InSine).SetRecyclable(true))
                .Join(lineRect.DOScale(authoredEdgeScale, 0.36f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));

            TrackCompletionEdge(sequence, revision);
        }

        /// <summary>Eşleşme geri alınırsa o anki ışığı kesmeden kısa biçimde söndürür.</summary>
        private void FadeCompletionEdge()
        {
            if (edge == null) return;

            if (edge.color.a <= 0.002f)
            {
                CancelEdgeTween();
                return;
            }

            uint revision = InvalidateEdgeTween();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(edge).SetUpdate(true).SetRecyclable(true)
                .Append(edge.DOFade(0f, 0.22f)
                    .SetEase(Ease.InSine).SetRecyclable(true))
                .Join(edge.rectTransform.DOScale(authoredEdgeScale, 0.22f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
            TrackCompletionEdge(sequence, revision);
        }

        /// <summary>Kartı elle verilmiş dinlenme pozuna döndürür ve tween'leri keser.</summary>
        public void ResetPose()
        {
            CancelTimeBoostFeedback();
            InvalidateLifecycleTweens();
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            if (rt != null)
            {
                rt.localScale = authoredScale;
                if (restPositionInitialized) rt.anchoredPosition = restingAnchoredPosition;
            }
            CanonicalizeVisibility(desiredVisible);
            InvalidateTickTween();
            if (tickBadge != null)
            {
                tickBadge.rectTransform.localScale = authoredTickScale;
                tickBadge.gameObject.SetActive(false);
            }
            highlighted = false;
            CancelEdgeTween();
            ResetTimerVisual();
        }

        private void OnEnable()
        {
            if (!initialized) return;
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            CanonicalizeVisibility(desiredVisible);
        }

        private void OnDisable()
        {
            CancelTimeBoostFeedback();
            InvalidateLifecycleTweens();
            InvalidateTickTween();
            highlighted = false;
            CancelEdgeTween();
            presentationState.Dispatch(BsOrderCardTrigger.Disable);
            if (rt != null)
            {
                rt.localScale = authoredScale;
                if (restPositionInitialized) rt.anchoredPosition = restingAnchoredPosition;
            }
            CanonicalizeVisibility(false);
            if (tickBadge != null)
            {
                tickBadge.rectTransform.localScale = authoredTickScale;
                tickBadge.gameObject.SetActive(false);
            }
            ResetTimerVisual();
        }

        private Tween TrackLifecycle(Sequence sequence, uint revision, bool snapToRest)
        {
            if (sequence == null) return null;
            lifecycleTween = sequence;
            sequence.OnComplete(() =>
                {
                    if (revision != lifecycleRevision
                        || !ReferenceEquals(lifecycleTween, sequence)) return;
                    lifecycleTween = null;
                    presentationState.Dispatch(BsOrderCardTrigger.AnimationCompleted);
                    bool visible = presentationState.State == BsOrderCardState.Visible;
                    if (rt != null)
                    {
                        rt.localScale = authoredScale;
                        if (snapToRest && restPositionInitialized)
                            rt.anchoredPosition = restingAnchoredPosition;
                    }
                    CanonicalizeVisibility(visible);
                })
                .OnKill(() => ForgetLifecycleTween(sequence, revision));
            return sequence;
        }

        private Tween TrackPosePulse(Sequence sequence, uint revision)
        {
            if (sequence == null) return null;
            lifecycleTween = sequence;
            sequence.OnComplete(() =>
                {
                    if (revision != lifecycleRevision
                        || !ReferenceEquals(lifecycleTween, sequence)) return;
                    lifecycleTween = null;
                    if (rt != null) rt.localScale = authoredScale;
                    CanonicalizeVisibility(desiredVisible);
                })
                .OnKill(() => ForgetLifecycleTween(sequence, revision));
            return sequence;
        }

        private void TrackTick(Sequence sequence, uint revision)
        {
            if (sequence == null) return;
            tickTween = sequence;
            sequence.OnComplete(() =>
                {
                    if (revision != tickRevision
                        || !ReferenceEquals(tickTween, sequence)) return;
                    tickTween = null;
                    if (tickBadge != null)
                        tickBadge.rectTransform.localScale = authoredTickScale;
                })
                .OnKill(() =>
                {
                    if (revision == tickRevision
                        && ReferenceEquals(tickTween, sequence)) tickTween = null;
                });
        }

        private void TrackCompletionEdge(Sequence sequence, uint revision)
        {
            if (sequence == null) return;
            edgeTween = sequence;
            sequence.OnComplete(() => CompleteEdgeTween(sequence, revision))
                .OnKill(() => ForgetEdgeTween(sequence, revision));
        }

        private uint InvalidateLifecycleTweens()
        {
            lifecycleRevision++;
            Tween oldLifecycle = lifecycleTween;
            Tween oldVisibility = visibilityTween;
            lifecycleTween = null;
            visibilityTween = null;
            KillTween(oldLifecycle);
            KillTween(oldVisibility);
            return lifecycleRevision;
        }

        private uint InvalidateTickTween()
        {
            tickRevision++;
            Tween old = tickTween;
            tickTween = null;
            KillTween(old);
            return tickRevision;
        }

        private void CancelTickTween() => InvalidateTickTween();

        private uint InvalidateTimeBoostTween()
        {
            timeBoostRevision++;
            Tween old = timeBoostTween;
            timeBoostTween = null;
            KillTween(old);
            CanonicalizeTimeBoostFeedback();
            return timeBoostRevision;
        }

        private void CompleteTimeBoostTween(Tween tween, uint revision)
        {
            if (revision != timeBoostRevision
                || !ReferenceEquals(timeBoostTween, tween)) return;
            timeBoostTween = null;
            CanonicalizeTimeBoostFeedback();
        }

        private void ForgetTimeBoostTween(Tween tween, uint revision)
        {
            if (revision != timeBoostRevision
                || !ReferenceEquals(timeBoostTween, tween)) return;
            timeBoostTween = null;
            CanonicalizeTimeBoostFeedback();
        }

        private uint InvalidateEdgeTween()
        {
            edgeRevision++;
            Tween old = edgeTween;
            edgeTween = null;
            KillTween(old);
            return edgeRevision;
        }

        private void CancelEdgeTween()
        {
            InvalidateEdgeTween();
            CanonicalizeCompletionEdge();
        }

        private void CompleteEdgeTween(Tween tween, uint revision)
        {
            if (revision != edgeRevision || !ReferenceEquals(edgeTween, tween)) return;
            edgeTween = null;
            CanonicalizeCompletionEdge();
        }

        private void ForgetEdgeTween(Tween tween, uint revision)
        {
            if (revision != edgeRevision || !ReferenceEquals(edgeTween, tween)) return;
            edgeTween = null;
            CanonicalizeCompletionEdge();
        }

        private void CompleteVisibilityTween(Tween tween, uint revision, bool visible)
        {
            if (revision != lifecycleRevision
                || !ReferenceEquals(visibilityTween, tween)) return;
            visibilityTween = null;
            presentationState.Dispatch(BsOrderCardTrigger.AnimationCompleted);
            if (presentationState.State != (visible
                    ? BsOrderCardState.Visible
                    : BsOrderCardState.Hidden))
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
            CanonicalizeVisibility(visible);
        }

        private void ForgetVisibilityTween(Tween tween, uint revision)
        {
            if (revision == lifecycleRevision
                && ReferenceEquals(visibilityTween, tween)) visibilityTween = null;
        }

        private void ForgetLifecycleTween(Tween tween, uint revision)
        {
            if (revision == lifecycleRevision
                && ReferenceEquals(lifecycleTween, tween)) lifecycleTween = null;
        }

        private void CanonicalizePose()
        {
            if (rt == null) return;
            rt.localScale = authoredScale;
            if (restPositionInitialized)
                rt.anchoredPosition = restingAnchoredPosition;
        }

        private void CanonicalizeCompletionEdge()
        {
            if (edge != null)
            {
                edge.color = CompletionEdgeRestColor();
                edge.rectTransform.localScale = authoredEdgeScale;
            }
        }

        private Color CompletionEdgeRestColor() => highlighted
            ? WithAlpha(completionGlowColor, 0.16f)
            : Transparent(completionLineColor);

        private void CanonicalizeVisibility(bool visible)
        {
            if (canvasGroup == null) return;
            bool canRender = isActiveAndEnabled && gameObject.activeInHierarchy
                             && presentationState.State != BsOrderCardState.Disabled;
            canvasGroup.alpha = visible && canRender ? 1f : 0f;
            SetCanvasInteraction(false);
        }

        private void SetCanvasInteraction(bool interactive)
        {
            if (canvasGroup == null) return;
            canvasGroup.interactable = interactive;
            canvasGroup.blocksRaycasts = interactive;
        }

        private static void KillTween(Tween tween)
        {
            if (tween != null && tween.IsActive()) tween.Kill(false);
        }

        private static bool IsTweenActive(Tween tween) =>
            tween != null && tween.IsActive() && tween.IsPlaying();

        private static bool HasActiveTween(Tween first, Tween second) =>
            IsTweenActive(first) || IsTweenActive(second);

        private void ResetTimerVisual()
        {
            shownTimerSecond = -1;
            ResetTimerMotion();
            SetTimerText(string.Empty);
            SetTimerProgress(1f);
            timerFillBaseColor = timerNormalColor;
            ApplyTimerFeedbackColors();
        }

        private void ResetTimerMotion()
        {
            urgentTimerKick = 0f;
            ApplyTimerMotion();
        }

        private void ApplyTimerMotion()
        {
            if (timerRoot == null) return;
            float scale = 1f + urgentTimerKick * 0.055f + timeBoostPulse * 0.105f;
            timerRoot.localScale = authoredTimerScale * scale;
        }

        private void ApplyTimerFeedbackColors()
        {
            float flash = Mathf.Clamp01(timeBoostFlash);
            if (timerFill != null)
                timerFill.color = Color.Lerp(
                    timerFillBaseColor, TimeBoostFlashColor, flash * 0.82f);
            if (timerClock != null)
                timerClock.color = Color.Lerp(
                    timerClockBaseColor, TimeBoostFlashColor, flash * 0.88f);
        }

        private void CanonicalizeTimeBoostFeedback()
        {
            timeBoostPulse = 0f;
            timeBoostFlash = 0f;
            ApplyTimerMotion();
            ApplyTimerFeedbackColors();
            if (timeBoostFeedbackRoot == null) return;
            timeBoostFeedbackRoot.anchoredPosition = timeBoostFeedbackBasePosition;
            timeBoostFeedbackRoot.localScale = Vector3.one;
            if (timeBoostFeedbackCanvasGroup != null)
                timeBoostFeedbackCanvasGroup.alpha = 0f;
            timeBoostFeedbackRoot.gameObject.SetActive(false);
        }

        private void SetTimerText(string value)
        {
            if (timerLabel != null) timerLabel.text = value;
            if (timerLegacyLabel != null) timerLegacyLabel.text = value;
        }

        private void SetTimerProgress(float normalized)
        {
            if (timerFill == null) return;
            float value = Mathf.Clamp01(normalized);
            if (timerFill.type == Image.Type.Filled)
            {
                timerFill.fillAmount = value;
                return;
            }

            RectTransform fillRect = timerFill.rectTransform;
            if (!timerFillGeometryCaptured) CaptureTimerFillGeometry();
            float width = timerFillFullWidth * value;
            fillRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            Vector2 position = fillRect.anchoredPosition;
            position.x = timerFillLeftEdge + width * fillRect.pivot.x;
            fillRect.anchoredPosition = position;
        }

        private bool EnsureTimeBoostFeedbackVisuals()
        {
            if (timerRoot == null) return false;
            if (timeBoostFeedbackRoot != null && timeBoostFeedbackCanvasGroup != null
                && (timeBoostFeedbackLabel != null
                    || timeBoostFeedbackLegacyLabel != null))
                return true;

            Transform existing = timerRoot.Find(TimeBoostFeedbackName);
            if (existing != null && TryBindTimeBoostFeedback(existing)) return true;

            Component source = timerLabel != null
                ? (Component)timerLabel
                : timerLegacyLabel;
            if (source == null) return false;

            GameObject instance = Instantiate(source.gameObject, timerRoot, false);
            instance.name = TimeBoostFeedbackName;
            RectTransform feedbackRoot = instance.transform as RectTransform;
            if (feedbackRoot == null)
            {
                Destroy(instance);
                return false;
            }

            CanvasGroup group = instance.GetComponent<CanvasGroup>();
            if (group == null) group = instance.AddComponent<CanvasGroup>();
            timeBoostFeedbackRoot = feedbackRoot;
            timeBoostFeedbackCanvasGroup = group;
            timeBoostFeedbackLabel = instance.GetComponent<TextMeshProUGUI>();
            timeBoostFeedbackLegacyLabel = instance.GetComponent<Text>();
            if (timeBoostFeedbackLabel == null && timeBoostFeedbackLegacyLabel == null)
            {
                Destroy(instance);
                timeBoostFeedbackRoot = null;
                timeBoostFeedbackCanvasGroup = null;
                return false;
            }

            RectTransform sourceRoot = source.transform as RectTransform;
            timeBoostFeedbackBasePosition = sourceRoot != null
                ? sourceRoot.anchoredPosition + new Vector2(0f, 9f)
                : new Vector2(0f, 9f);
            timeBoostFeedbackRoot.anchoredPosition = timeBoostFeedbackBasePosition;
            timeBoostFeedbackRoot.SetAsLastSibling();
            ConfigureTimeBoostFeedbackLabel();
            timeBoostFeedbackCanvasGroup.alpha = 0f;
            instance.SetActive(false);
            return true;
        }

        private bool TryBindTimeBoostFeedback(Transform candidate)
        {
            RectTransform feedbackRoot = candidate as RectTransform;
            if (feedbackRoot == null) return false;
            TextMeshProUGUI tmp = candidate.GetComponent<TextMeshProUGUI>();
            Text legacy = candidate.GetComponent<Text>();
            if (tmp == null && legacy == null) return false;

            CanvasGroup group = candidate.GetComponent<CanvasGroup>();
            if (group == null) group = candidate.gameObject.AddComponent<CanvasGroup>();
            timeBoostFeedbackRoot = feedbackRoot;
            timeBoostFeedbackCanvasGroup = group;
            timeBoostFeedbackLabel = tmp;
            timeBoostFeedbackLegacyLabel = legacy;
            Component source = timerLabel != null ? (Component)timerLabel : timerLegacyLabel;
            RectTransform sourceRoot = source != null ? source.transform as RectTransform : null;
            timeBoostFeedbackBasePosition = sourceRoot != null
                ? sourceRoot.anchoredPosition + new Vector2(0f, 9f)
                : feedbackRoot.anchoredPosition;
            ConfigureTimeBoostFeedbackLabel();
            CanonicalizeTimeBoostFeedback();
            return true;
        }

        private void ConfigureTimeBoostFeedbackLabel()
        {
            if (timeBoostFeedbackLabel != null)
            {
                timeBoostFeedbackLabel.raycastTarget = false;
                timeBoostFeedbackLabel.color = TimeBoostFlashColor;
                timeBoostFeedbackLabel.alignment = TextAlignmentOptions.Center;
            }
            if (timeBoostFeedbackLegacyLabel != null)
            {
                timeBoostFeedbackLegacyLabel.raycastTarget = false;
                timeBoostFeedbackLegacyLabel.color = TimeBoostFlashColor;
                timeBoostFeedbackLegacyLabel.alignment = TextAnchor.MiddleCenter;
            }
        }

        private void SetTimeBoostFeedbackText(float seconds)
        {
            float rounded = Mathf.Round(seconds);
            string amount = Mathf.Approximately(seconds, rounded)
                ? Mathf.RoundToInt(seconds).ToString()
                : seconds.ToString("0.#");
            string value = "+" + amount + " sn";
            if (timeBoostFeedbackLabel != null) timeBoostFeedbackLabel.text = value;
            if (timeBoostFeedbackLegacyLabel != null)
                timeBoostFeedbackLegacyLabel.text = value;
        }

        private void CaptureTimerFillGeometry()
        {
            timerFillGeometryCaptured = false;
            if (timerFill == null) return;

            RectTransform fillRect = timerFill.rectTransform;
            float width = fillRect.rect.width;
            if (width <= 0f) width = Mathf.Abs(fillRect.sizeDelta.x);
            if (width <= 0f) return;

            timerFillFullWidth = width;
            timerFillLeftEdge = fillRect.anchoredPosition.x - width * fillRect.pivot.x;
            timerFillGeometryCaptured = true;
        }

        private void CaptureTimerArtState()
        {
            timerFillBaseColor = timerFill != null ? timerFill.color : timerNormalColor;
            if (timerRoot != null && timerClock == null)
            {
                Transform clock = timerRoot.Find("Clock");
                timerClock = clock != null ? clock.GetComponent<Image>() : null;
            }
            if (timerClock != null) timerClockBaseColor = timerClock.color;
            ApplyTimerFeedbackColors();
        }

        /// <summary>
        /// Görsel hiyerarşiyi tek Resources prefabından, yalnız ilk süreli siparişte kurar.
        /// Böylece standalone showcase ve ana rig aynı authored görünümü paylaşır; doğrudan
        /// bağlanmış bir timer varsa ona dokunmaz. Procedural üretim yalnız acil fallback'tir.
        /// </summary>
        private void EnsureTimerVisuals()
        {
            if (timerRoot != null) return;
            if (!Application.isPlaying) return;

            Transform existing = transform.Find(RuntimeTimerName);
            if (existing != null && TryBindRuntimeTimer(existing)) return;
            if (TryInstantiateTimerView()) return;

            // Prefabın bulunamadığı/bozuk olduğu paketlerde süre görünmez kalmasın.
            // Aşağıdaki procedural yol yalnız acil durum fallback'idir.
            if (!timerArtLoadAttempted)
            {
                timerArtLoadAttempted = true;
                cachedTimerPlate = Resources.Load<Sprite>(TimerPlateResource);
                cachedTimerClock = Resources.Load<Sprite>(TimerClockResource);
                cachedTimerFill = Resources.Load<Sprite>(TimerFillResource);
            }

            if (cachedTimerPlate == null || cachedTimerClock == null
                || cachedTimerFill == null)
            {
                if (!timerArtWarningIssued)
                {
                    timerArtWarningIssued = true;
                    Debug.LogWarning(
                        "Sipariş süre rayı sprite'larından biri Resources altında bulunamadı; "
                        + "saniye sayacı sade fallback ile gösterilecek.", this);
                }
            }

            int siblingIndex = tickBadge != null
                ? tickBadge.transform.GetSiblingIndex()
                : transform.childCount;

            RectTransform root = CreateTimerRect(RuntimeTimerName, transform,
                Vector2.zero, new Vector2(146f, 42f));
            // Kartın alt kenarına üstten dock edilir. Böylece kart ölçüsü değişse bile
            // sayaç yukarı kayıp chip satırına girmez ve doğal 3:1 plate oranı korunur.
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = new Vector2(0f, 20f);
            root.SetSiblingIndex(siblingIndex);
            timerRoot = root;

            Image plate = CreateTimerImage("Plate", root, cachedTimerPlate,
                Vector2.zero, new Vector2(146f, 42f));
            if (cachedTimerPlate != null)
            {
                plate.type = Image.Type.Sliced;
                plate.fillCenter = true;
            }
            else
            {
                // Art paketleme hatasında timeout görünmez kalmasın: sayı için sakin,
                // opak bir zemin bırak. Doğru asset geldiğinde bu yol hiç çalışmaz.
                plate.type = Image.Type.Simple;
                plate.color = new Color(0.18f, 0.06f, 0.46f, 0.96f);
                var outline = plate.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(1f, 0.65f, 0.08f, 0.95f);
                outline.effectDistance = new Vector2(1f, -1f);
            }

            if (cachedTimerFill != null)
            {
                RectTransform lane = CreateTimerRect("Fill Lane", root,
                    new Vector2(21f, -8f), new Vector2(78f, 7.5f));
                timerFill = CreateTimerImage("Fill", lane, cachedTimerFill,
                    Vector2.zero, lane.sizeDelta);
                timerFill.type = Image.Type.Sliced;
                timerFill.color = timerNormalColor;
                CaptureTimerFillGeometry();
            }

            if (cachedTimerClock != null)
            {
                timerClock = CreateTimerImage("Clock", root, cachedTimerClock,
                    new Vector2(-55f, 0f), new Vector2(32f, 32f));
                timerClock.preserveAspect = true;
            }

            timerLegacyLabel = CreateTimerLabel("Seconds", root,
                new Vector2(28f, 7f), new Vector2(72f, 17f));

            CaptureTimerArtState();
            root.gameObject.SetActive(false);
        }

        private bool TryInstantiateTimerView()
        {
            if (!timerViewLoadAttempted)
            {
                timerViewLoadAttempted = true;
                cachedTimerViewPrefab = Resources.Load<GameObject>(TimerViewResource);
            }

            if (cachedTimerViewPrefab == null)
            {
                WarnTimerPrefabFallback("Resources prefabı bulunamadı");
                return false;
            }

            GameObject instance = Instantiate(cachedTimerViewPrefab, transform, false);
            instance.name = RuntimeTimerName;
            instance.SetActive(false);
            SetLayerRecursively(instance.transform, gameObject.layer);

            if (!TryBindRuntimeTimer(instance.transform))
            {
                WarnTimerPrefabFallback("prefab binding sözleşmesi eksik");
                instance.name = RuntimeTimerName + " - Invalid";
                Destroy(instance);
                return false;
            }

            int siblingIndex = tickBadge != null
                ? tickBadge.transform.GetSiblingIndex()
                : transform.childCount - 1;
            timerRoot.SetSiblingIndex(siblingIndex);
            return true;
        }

        private bool TryBindRuntimeTimer(Transform candidate)
        {
            if (candidate == null) return false;
            RectTransform root = candidate as RectTransform;
            Transform fillNode = candidate.Find("Fill Lane/Fill");
            Transform secondsNode = candidate.Find("Seconds");
            Transform clockNode = candidate.Find("Clock");
            Image fill = fillNode != null ? fillNode.GetComponent<Image>() : null;
            Text legacy = secondsNode != null ? secondsNode.GetComponent<Text>() : null;
            TextMeshProUGUI tmp = secondsNode != null
                ? secondsNode.GetComponent<TextMeshProUGUI>()
                : null;
            if (root == null || fill == null || (legacy == null && tmp == null))
                return false;

            timerRoot = root;
            timerFill = fill;
            timerLegacyLabel = legacy;
            timerLabel = tmp;
            timerClock = clockNode != null ? clockNode.GetComponent<Image>() : null;
            authoredTimerScale = root.localScale;
            CaptureTimerFillGeometry();
            CaptureTimerArtState();
            return true;
        }

        private void WarnTimerPrefabFallback(string reason)
        {
            if (timerViewWarningIssued) return;
            timerViewWarningIssued = true;
            Debug.LogWarning(
                "OrderTimerView " + reason + "; procedural sayaç fallback'i kullanılacak.",
                this);
        }

        private static void SetLayerRecursively(Transform node, int layer)
        {
            if (node == null) return;
            node.gameObject.layer = layer;
            for (int i = 0; i < node.childCount; i++)
                SetLayerRecursively(node.GetChild(i), layer);
        }

        private static RectTransform CreateTimerRect(string name, Transform parent,
                                                     Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 0;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image CreateTimerImage(string name, Transform parent, Sprite sprite,
                                              Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            go.layer = parent != null ? parent.gameObject.layer : 0;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.raycastTarget = false;
            return image;
        }

        private static Text CreateTimerLabel(string name, Transform parent,
                                             Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text));
            go.layer = parent != null ? parent.gameObject.layer : 0;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Text label = go.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                         ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = 15;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(1f, 0.96f, 0.84f, 1f);
            label.raycastTarget = false;
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.12f, 0.03f, 0.28f, 0.92f);
            outline.effectDistance = new Vector2(1f, -1f);
            return label;
        }

        private static bool SameOrder(OrderDef a, OrderDef b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Kind != b.Kind || a.Glass != b.Glass
                || !Mathf.Approximately(a.TimeLimit, b.TimeLimit))
                return false;

            int count = a.Contents != null ? a.Contents.Count : 0;
            if (count != (b.Contents != null ? b.Contents.Count : 0)) return false;
            for (int i = 0; i < count; i++)
                if (a.Contents[i] != b.Contents[i]) return false;
            return true;
        }

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b,
                Mathf.Clamp01(color.a * alpha));

        private static Color Transparent(Color c) => WithAlpha(c, 0f);
    }
}
