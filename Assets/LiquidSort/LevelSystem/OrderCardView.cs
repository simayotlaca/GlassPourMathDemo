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
        [Tooltip("Eşleşme yandığında boyanan kenar. Arka plan değil kenar seçildi: "
               + "kart zaten renk katmanları taşıyor, arka planı boyamak onları bozardı.")]
        [SerializeField] private Image edge = null;
        [SerializeField] private Image icon = null;
        [SerializeField] private Image kindBadge = null;
        [Tooltip("Teslim edildi damgası.")]
        [SerializeField] private Image tickBadge = null;
        [SerializeField] private TextMeshProUGUI kindLabel = null;
        [SerializeField] private TextMeshProUGUI description = null;
        [SerializeField] private TextMeshProUGUI timerLabel = null;
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

        [Header("Durum renkleri")]
        [SerializeField] private Color goodColor = new Color(0.30f, 0.80f, 0.42f, 1f);
        [SerializeField] private Color accentColor = new Color(0.95f, 0.68f, 0.20f, 1f);
        [SerializeField] private Color badColor = new Color(0.85f, 0.28f, 0.24f, 1f);
        [SerializeField] private Color layerBadgeColor = new Color32(0xE8, 0x8B, 0x3C, 0xFF);
        [Tooltip("Boş slot dolu bir kart gibi görünmemeli — sadece soluk bir yuva.")]
        [SerializeField] private Color emptySlotColor = new Color(1f, 1f, 1f, 0.14f);

        private bool highlighted;
        private bool initialized;
        private Vector3 authoredScale = Vector3.one;
        private Vector3 authoredTickScale = Vector3.one;
        private BsPalette palette;
        private readonly Dictionary<GlassType, GlassIcon> iconByType =
            new Dictionary<GlassType, GlassIcon>(5);

        public OrderDef Model { get; private set; }
        public RectTransform Rt => rt;

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
            if (rt == null) rt = transform as RectTransform;
            authoredScale = rt != null ? rt.localScale : Vector3.one;
            authoredTickScale = tickBadge != null
                ? tickBadge.rectTransform.localScale
                : Vector3.one;
            if (edge != null) edge.color = Transparent(goodColor);
            highlighted = false;
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

            Model = order;
            bool has = order != null;

            if (icon != null && icon.sprite == null) icon.gameObject.SetActive(false);
            if (kindBadge != null) kindBadge.gameObject.SetActive(has);
            if (tickBadge != null) tickBadge.gameObject.SetActive(false);
            if (background != null) background.color = has ? Color.white : emptySlotColor;
            if (edge != null) edge.color = Transparent(goodColor);
            highlighted = false;
            if (emptyLabel != null) emptyLabel.gameObject.SetActive(!has);

            if (!has)
            {
                if (description != null) description.text = "";
                if (timerRoot != null) timerRoot.gameObject.SetActive(false);
                DrawOrder(null);
                return;
            }

            bool layer = order.Kind == OrderKind.Layer;
            if (kindLabel != null) kindLabel.text = layer ? "KATMAN" : "SET";
            if (kindBadge != null) kindBadge.color = layer ? layerBadgeColor : accentColor;
            if (description != null && palette != null)
                description.text = order.Describe(palette);

            bool timed = timedOrdersEnabled && order.TimeLimit > 0f;
            if (timerRoot != null) timerRoot.gameObject.SetActive(timed);
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
            canvasGroup.DOKill();
            float a = visible ? 1f : 0f;
            if (!animate || Mathf.Approximately(canvasGroup.alpha, a))
            {
                canvasGroup.alpha = a;
                return;
            }
            canvasGroup.DOFade(a, 0.2f).SetUpdate(true).SetRecyclable(true);
        }

        public void SetTimer(float remaining, float total)
        {
            if (timerRoot == null || !timerRoot.gameObject.activeSelf) return;
            float t = total > 0f ? Mathf.Clamp01(remaining / total) : 0f;
            if (timerFill != null)
            {
                timerFill.rectTransform.anchorMax = new Vector2(t, 1f);
                timerFill.color = t > 0.5f ? goodColor : (t > 0.22f ? accentColor : badColor);
            }
            if (timerLabel != null)
                timerLabel.text = Mathf.CeilToInt(Mathf.Max(0f, remaining)) + " sn";
        }

        /// <summary>
        /// Teslim edildi damgasi. Oyunun en odullendirici ani ve eskiden sadece
        /// SetActive(true) idi -- damga bir karede beliriyordu. Simdi buyuyerek oturuyor
        /// ve kart hafifce geri cekiliyor: gozun takip edecegi bir hareket olmadan
        /// "odul" okunmuyor.
        /// </summary>
        public void ShowDelivered()
        {
            if (tickBadge != null)
            {
                tickBadge.gameObject.SetActive(true);
                RectTransform trt = tickBadge.rectTransform;
                trt.DOKill();
                trt.localScale = Vector3.zero;
                DOTween.Sequence().SetTarget(trt).SetUpdate(true).SetRecyclable(true)
                    .Append(trt.DOScale(authoredTickScale * 1.3f, 0.16f)
                        .SetEase(Ease.OutQuad).SetRecyclable(true))
                    .Append(trt.DOScale(authoredTickScale, 0.2f)
                        .SetEase(Ease.OutBack).SetRecyclable(true));
            }

            if (rt == null) return;
            rt.DOKill();
            DOTween.Sequence().SetTarget(rt).SetUpdate(true).SetRecyclable(true)
                .Append(rt.DOScale(authoredScale * 0.94f, 0.14f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .Append(rt.DOScale(authoredScale, 0.18f)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
        }

        /// <summary>
        /// Bu siparişi karşılayan bir bardak var mı — kenarı yakar.
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

            if (edge != null)
            {
                edge.DOKill();
                edge.DOColor(on ? goodColor : Transparent(goodColor), 0.18f)
                    .SetEase(Ease.OutQuad).SetUpdate(true).SetRecyclable(true);
            }

            if (!on || rt == null) return;
            rt.DOKill();
            DOTween.Sequence().SetTarget(rt).SetUpdate(true).SetRecyclable(true)
                .Append(rt.DOScale(authoredScale * 1.06f, 0.12f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .Append(rt.DOScale(authoredScale, 0.16f)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
        }

        /// <summary>Kartı elle verilmiş dinlenme pozuna döndürür ve tween'leri keser.</summary>
        public void ResetPose()
        {
            if (rt != null)
            {
                rt.DOKill();
                rt.localScale = authoredScale;
            }
            if (tickBadge != null)
            {
                tickBadge.rectTransform.DOKill();
                tickBadge.rectTransform.localScale = authoredTickScale;
                tickBadge.gameObject.SetActive(false);
            }
            if (edge != null)
            {
                edge.DOKill();
                edge.color = Transparent(goodColor);
            }
            highlighted = false;
        }

        private void OnDisable() => ResetPose();

        private static Color Transparent(Color c) => new Color(c.r, c.g, c.b, 0f);
    }
}
