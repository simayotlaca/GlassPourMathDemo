using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Teslim edilen bardağın ✓ rozetini portala bildiren kaynak. Rozetler bardak
    /// başına elle yerleştirildiği için portal onları kendi başına bulamaz; sorar.
    /// </summary>
    public interface IPortalCheckBadgeSource
    {
        /// <summary>Bu bardağın şu an görünen ✓ rozeti; yoksa null.</summary>
        Transform GetCheckBadge(LiquidBottle glass);
    }

    /// <summary>
    /// Servis geçidi: bir sipariş karşılandığında bardağı kemerin ARKASINA sokup orada
    /// yok eden teslim animasyonu.
    ///
    /// The whole point is that the glass is never faded out in front of the doorway. The
    /// portal is authored as three layers and the vessel is temporarily re-sorted into the
    /// gap between them:
    ///
    ///     backLayers    koyu mor iç boşluk + cyan ışık     (düşük çizim sırası)
    ///     &lt;bardak&gt;      uçuş boyunca ödünç alınan sıra     (aradaki pencere)
    ///     frontLayers   altın kemer + mor iç occluder      (yüksek çizim sırası)
    ///
    /// so the purple occluder covers the vessel from the sides as a matter of draw order.
    /// No SpriteMask and no alpha ramp is involved: by the time the glass stops being
    /// drawn it is already behind the arch.
    ///
    /// Timeline (varsayılan ayar, ölçeklenmemiş saniye):
    ///
    ///     0.00 - 0.08   bardak ayağını bırakmadan hafifçe yüklenir
    ///     0.08 - 0.40   kendi rafından servis hattına DİKEY yükselir
    ///     0.40 - 0.62   hat boyunca hızlanır, geçide sığacak ölçeğe gelir
    ///     0.62 - 0.87   kemerin arkasına girer ve çok hafif eğilir
    ///     0.87 - 1.01   içeride küçülür ve tamamen gizlenir
    ///     0.87 - 1.01   ✓ rozeti yeşil-altın parıltılara ayrılır
    ///     1.01 - 1.19   portal kısa bir "tok" bounce yapar
    ///
    /// Yükselme dikey, kayma yatay — teslim edilen bardak rafından kalkıp servis
    /// hattına çıkar, sonra o hat boyunca kapıya kayar. Yalnızca kapıya fazla yakın
    /// sağ bardaklar yükselirken yumuşakça dış bekleme noktasına birleşir; böylece
    /// rayda sola geri gidip hemen sağa dönen bir kanca oluşmaz.
    /// Yükselme süresi mesafeyle ölçeklenir: alt raftaki bardak üst raftakinden
    /// uzun yol gider, aynı ivmeyle biraz daha uzun sürer.
    ///
    /// This component owns only the flight and two pooled runtime particle accents. It never
    /// reparents the vessel and hands every borrowed value back — pose, draw orders and
    /// sorting layer — before it reports the glass as hidden, so the caller's pool can
    /// take the vessel back as if it had simply been switched off. No effect is instantiated
    /// per delivery.
    /// </summary>
    [DisallowMultipleComponent]
    // Deliberately behind LiquidBottle and BottleShell (both default 0). BottleShell
    // re-publishes the authored draw orders of a vessel and LiquidBottle re-stamps the
    // liquid quad's sorting layer, both in LateUpdate; the borrowed sandwich has to be
    // written after them or the arch would eat half of it for a frame.
    [DefaultExecutionOrder(120)]
    public sealed class PortalDeliveryAnimator : MonoBehaviour
    {
        private sealed class Flight
        {
            public LiquidBottle Glass;
            public Transform Root;

            public Vector3 HomePosition;
            public Quaternion HomeRotation;
            public Vector3 HomeScale;
            public Vector3 MotionAnchorLocal;
            public Vector3 HomeMotionAnchorPosition;

            public Renderer[] Renderers;
            public int[] BaseOrders;
            public int[] BaseLayerIds;
            public Renderer[] TravelHiddenRenderers;
            public bool[] TravelHiddenActiveStates;
            public Bounds HomeVisualBounds;
            public bool HasVisualBounds;
            /// <summary>Yükselirken: her şeyin önünde. Kemere girerken: sandviçin içinde.</summary>
            public int LiftOffset;
            public int PortalOffset;
            public bool InPortal;
            public int PortalLayerId;

            public Transform Badge;
            public SpriteRenderer BadgeRenderer;
            public Vector3 BadgeScale;
            public Color BadgeColor;

            public Sequence Sequence;
            public Action OnGlassHidden;
            public Action OnFinished;
            public bool OwnsPortalChrome;
            public bool Hidden;
        }

        [Header("Portal katmanları (çizim sırası sandviçi)")]
        [Tooltip("Bardağın ARKASINDA kalan her şey: koyu mor iç boşluk ve cyan ışık. "
               + "Boş bırakılabilir; o zaman yalnızca öndeki maske sıralamayı belirler.")]
        [SerializeField] private SpriteRenderer[] backLayers = new SpriteRenderer[0];
        [Tooltip("Bardağın ÖNÜNDE kalan her şey: altın kemer ve bardağı örten mor iç maske. "
               + "En az bir tane olmak zorunda — teslimin gizlenmesi tamamen buna bağlı.")]
        [SerializeField] private SpriteRenderer[] frontLayers = new SpriteRenderer[0];
        [Tooltip("Yutma anındaki 'tok' bounce'un uygulandığı kök. Boşsa bounce atlanır.")]
        [SerializeField] private Transform portalPivot;
        [Tooltip("Kapı ağzındaki parıltı. Alpha'sı bu bileşenin malıdır: teslim dışında 0.")]
        [SerializeField] private SpriteRenderer portalGlow;
        [Tooltip("Rozet dağılırken oynatılan yeşil-altın parıltı. Boşsa atlanır.")]
        [SerializeField] private ParticleSystem badgeSparkles;
        [Tooltip("Sparkles elle bağlanmadıysa ilk teslimde küçük, havuzlu bir varsayılan "
               + "ParticleSystem oluşturur. Runtime'da bir kez üretilir; teslim başına "
               + "Instantiate yapılmaz.")]
        [SerializeField] private bool createFallbackSparkles = true;
        [Tooltip("Play() kendi rozetini vermezse kullanılan ✓ rozeti.")]
        [SerializeField] private Transform defaultCheckBadge;

        [Header("Yol")]
        [Tooltip("Kapının ağzı: bardağın hâlâ tamamen görünür olduğu son nokta.")]
        [SerializeField] private Transform mouthAnchor;
        [Tooltip("Kemerin arkasındaki derinlik: bardağın tamamen gizlendiği nokta.")]
        [SerializeField] private Transform throatAnchor;
        [Tooltip("0.42 sn'de bardağın ağız-derinlik hattında ne kadar ilerlediği. "
               + "Spec: yarısından fazlası içeride olmalı, yani 0.5'in üstü.")]
        [SerializeField, Range(0f, 1f)] private float entryDepth = 0.68f;
        [Tooltip("Builder'ın ölçtüğü gerçek kapı açıklığı. Her bardak mevcut dünya "
               + "boyutundan bu dikdörtgene ayrı ayrı sığdırılır.")]
        [SerializeField] private Vector2 portalOpeningSize = new Vector2(0.61247f, 1.4285f);
        [Tooltip("Mouth anchor'dan portalın dış sol/sağ kenarına olan mesafe.")]
        [SerializeField, Min(0f)] private float portalOuterEdgeOffset = 0.3348f;
        [Tooltip("Bardağın açıklığın içinde bıraktığı güvenlik payı.")]
        [SerializeField, Range(0.5f, 1f)] private float portalFit = 0.93f;
        [Tooltip("Bardak kenarıyla altın portal ayağı arasındaki son görünür boşluk.")]
        [SerializeField, Min(0f)] private float mouthClearance = 0.05f;

        [Header("Servis hattına yükselme")]
        [Tooltip("Bardağın kendi rafından servis hattına çıkışı. Bu süre TAM bir Lift "
               + "Full Height kadar yol giden bardağa aittir; daha kısa yol gidenler "
               + "aynı ivmeyle daha çabuk çıkar. 0 = dikey lift kapalı; bardak "
               + "güvenli dış staging noktası üzerinden kapıya kayar.")]
        [SerializeField, Min(0f)] private float liftDuration = 0.32f;
        [Tooltip("Süre ölçeğinin referansı: en alt raftan servis hattına olan dikey "
               + "mesafe. Rafın giriş animasyonundaki drop height ile aynı fikir.")]
        [SerializeField, Min(0.1f)] private float liftFullHeight = 6f;
        [Tooltip("Yükselirken bardağın raf tahtalarının, direklerin ve sipariş "
               + "kartlarının önüne alınması. Portal sandviçi ancak kemere girerken "
               + "devralır. 0 = kapalı.")]
        [SerializeField, Min(0)] private int liftSortingBoost = 60;
        [Tooltip("Portal geometri ölçümü yapılamazsa kullanılan yedek ağız ölçeği. "
               + "Küçülme dikey liftte değil, servis hattındaki gidişte uygulanır.")]
        [SerializeField, Range(0.3f, 1f)] private float liftScale = 1f;
        [SerializeField] private Ease liftEase = Ease.OutCubic;

        [Header("Gönderim vurgusu")]
        [Tooltip("Tikten sonra hareketten önceki kısa yüklenme. Bardak ayağı yerinden "
               + "oynamaz; yalnızca hafifçe basılır ve geçide ters yönde eğilir.")]
        [SerializeField, Min(0f)] private float anticipationDuration = 0.08f;
        [Tooltip("Hazırlık anındaki yatay genişleme / dikey basılma oranı.")]
        [SerializeField, Range(0f, 0.2f)] private float anticipationSquash = 0.06f;
        [Tooltip("Hazırlık anında gidiş yönünün tersine olan küçük eğim.")]
        [SerializeField, Range(0f, 10f)] private float anticipationTilt = 2.5f;
        [Tooltip("Ray üzerinde geçide doğru olan yönsel eğim.")]
        [SerializeField, Range(0f, 10f)] private float approachLean = 3f;

        [Header("Zamanlama")]
        [Tooltip("Tam Approach Full Distance kadar ray yolu için geçen süre. Daha yakın "
               + "bardaklar aynı ivme hissiyle daha kısa sürede ağıza gelir.")]
        [SerializeField, Min(0.01f)] private float approachDuration = 0.22f;
        [Tooltip("Yaklaşma süresinin referans ray mesafesi.")]
        [SerializeField, Min(0.1f)] private float approachFullDistance = 5f;
        [Tooltip("En yakın bardakta bile gidiş efektinin okunacağı alt süre.")]
        [SerializeField, Min(0.01f)] private float minimumApproachDuration = 0.10f;
        [Tooltip("Her bardak için kapının dışında bırakılan en kısa ileri ray "
               + "mesafesi. Sağ uçtaki bardak bu noktaya lift sırasında birleşir; "
               + "ağızdan geri gidip yeniden dönmez.")]
        [SerializeField, Min(0f)] private float minimumApproachLead = 0.16f;
        [Tooltip("Kemerin arkasına girerken geçen süre. Bardağın büyük kısmı burada gizlenir.")]
        [SerializeField, Min(0.01f)] private float entryDuration = 0.25f;
        [Tooltip("İçeride küçülüp tamamen kaybolurken geçen süre.")]
        [SerializeField, Min(0.01f)] private float hideDuration = 0.14f;
        [Tooltip("Bardak gizlendikten sonraki portal bounce'u. 0 = kapalı.")]
        [SerializeField, Min(0f)] private float bounceDuration = 0.18f;
        [Tooltip("Duraklatılmış bir board veya timeScale oyunu yarım yolda bardağı "
               + "kemerin ağzında bırakamasın diye ölçeklenmemiş zaman.")]
        [SerializeField] private bool useUnscaledTime = true;

        [Header("Poz")]
        [Tooltip("0.42 sn'deki ölçek çarpanı. Spec: 1 -> 0.72.")]
        [SerializeField, Range(0.05f, 1f)] private float entryScale = 0.72f;
        [Tooltip("0.55 sn'deki ölçek çarpanı. Spec: 0.72 -> 0.35.")]
        [SerializeField, Range(0.01f, 1f)] private float hideScale = 0.35f;
        [Tooltip("Kemere girerken uygulanan çok hafif eğim, derece. Yön ağız->derinlik "
               + "vektöründen okunur, elle işaret vermek gerekmez.")]
        [SerializeField, Range(0f, 20f)] private float entryTilt = 5f;

        [Header("Yumuşatma")]
        [SerializeField] private Ease approachEase = Ease.InOutSine;
        [SerializeField] private Ease entryEase = Ease.InSine;
        [SerializeField] private Ease hideEase = Ease.InQuad;

        [Header("Gidiş izi")]
        [Tooltip("Ray/lift boyunca bardak arkasında kısa cyan-altın parçacık izi bırakır.")]
        [SerializeField] private bool showTravelTrail = true;
        [Tooltip("İzin saniyedeki parçacık sayısı.")]
        [SerializeField, Range(8f, 90f)] private float travelTrailRate = 42f;

        [Header("Işık")]
        [Tooltip("Bardak ağza yaklaşırken portalın ulaştığı parlaklık.")]
        [SerializeField, Range(0f, 1f)] private float glowApproachAlpha = 0.45f;
        [Tooltip("Yutma anındaki kısa flaş.")]
        [SerializeField, Range(0f, 1f)] private float glowSwallowAlpha = 1f;

        [Header("Bounce")]
        [Tooltip("Portalın 'tok' ezilme oranı: yatayda genişler, dikeyde kısalır. 0 = kapalı.")]
        [SerializeField, Range(0f, 0.3f)] private float bounceSquash = 0.07f;

        [Header("Önizleme")]
        [Tooltip("Yalnızca context menu'deki Preview Delivery için. Oyun akışında kullanılmaz.")]
        // Explicit initializer: this is the one serialized reference no Configure* method
        // writes, and the compiler would otherwise flag it as never assigned.
        [SerializeField] private LiquidBottle previewGlass = null;

        private readonly List<Flight> flights = new List<Flight>(2);
        private readonly List<Flight> cancelScratch = new List<Flight>(2);

        private Vector3 portalPivotRestScale = Vector3.one;
        private bool portalRestCaptured;
        private string lastLoggedError;
        private Material fallbackSparkleMaterial;
        private bool ownsFallbackSparkles;
        private ParticleSystem travelTrail;
        private Material travelTrailMaterial;

        /// <summary>
        /// True until the whole delivery beat, including the portal bounce, is complete.
        /// The portal is intentionally single-flight: accepting a second vessel during the
        /// first one's bounce would make both flights fight over the same glow and pivot.
        /// </summary>
        public bool IsPlaying => flights.Count > 0;

        /// <summary>
        /// Bardağın tamamen gizlendiği an. Yükselme mesafeyle ölçeklendiği için bu
        /// değer tam yükseklikten kalkan bardağın, yani en uzun ihtimalin süresidir.
        /// Lift kapatılmışsa aynı bütçe güvenli dış staging hareketini kapsar.
        /// </summary>
        public float GlassHiddenTime =>
            anticipationDuration + Mathf.Max(liftDuration, approachDuration)
            + approachDuration
            + entryDuration + hideDuration;

        /// <summary>Gizlenme artı portal bounce'u; sipariş kartı bundan sonra yenilenir.</summary>
        public float TotalDuration => GlassHiddenTime + bounceDuration;

        /// <summary>
        /// Rozet sağlayıcı, tipik olarak <see cref="DeliveryBadgePresenter"/>. Play'e
        /// açıkça rozet verilmediğinde buraya sorulur; o da yoksa serialized rozet
        /// kullanılır. Boş bırakılırsa ✓ dağılma beat'i sessizce atlanır.
        /// </summary>
        public IPortalCheckBadgeSource CheckBadgeSource { get; set; }

        private void Awake() => CapturePortalRest();

        private void OnDisable()
        {
            CancelAll();
        }

        private void OnDestroy()
        {
            ReleaseFallbackSparkles();
            ReleaseTravelTrail();
        }

        private void OnValidate()
        {
            approachDuration = Mathf.Max(0.01f, approachDuration);
            approachFullDistance = Mathf.Max(0.1f, approachFullDistance);
            minimumApproachDuration = Mathf.Clamp(
                minimumApproachDuration, 0.01f, approachDuration);
            minimumApproachLead = Mathf.Max(0f, minimumApproachLead);
            entryDuration = Mathf.Max(0.01f, entryDuration);
            hideDuration = Mathf.Max(0.01f, hideDuration);
            bounceDuration = Mathf.Max(0f, bounceDuration);
            entryScale = Mathf.Clamp(entryScale, 0.05f, 1f);
            hideScale = Mathf.Clamp(hideScale, 0.01f, entryScale);
            entryDepth = Mathf.Clamp(entryDepth, 0.5f, 1f);
            portalOpeningSize.x = Mathf.Max(0.01f, portalOpeningSize.x);
            portalOpeningSize.y = Mathf.Max(0.01f, portalOpeningSize.y);
            portalOuterEdgeOffset = Mathf.Max(0f, portalOuterEdgeOffset);
            portalFit = Mathf.Clamp(portalFit, 0.5f, 1f);
            mouthClearance = Mathf.Max(0f, mouthClearance);
            liftDuration = Mathf.Max(0f, liftDuration);
            liftFullHeight = Mathf.Max(0.1f, liftFullHeight);
            liftSortingBoost = Mathf.Max(0, liftSortingBoost);
            liftScale = Mathf.Clamp(liftScale, 0.3f, 1f);
            anticipationDuration = Mathf.Max(0f, anticipationDuration);
            anticipationSquash = Mathf.Clamp(anticipationSquash, 0f, 0.2f);
            anticipationTilt = Mathf.Clamp(anticipationTilt, 0f, 10f);
            approachLean = Mathf.Clamp(approachLean, 0f, 10f);
            travelTrailRate = Mathf.Clamp(travelTrailRate, 8f, 90f);
        }

        /// <summary>
        /// Authoring API for an editor builder, mirroring the shelf view's Configure*
        /// methods. Assigning here writes exactly the references an artist would drag into
        /// the Inspector; nothing is instantiated or discovered.
        /// </summary>
        public void ConfigureSceneBindings(
            SpriteRenderer[] behindTheGlass,
            SpriteRenderer[] inFrontOfTheGlass,
            Transform bouncePivot,
            SpriteRenderer glow,
            ParticleSystem sparkles,
            Transform checkBadge,
            Transform mouth,
            Transform throat)
        {
            CancelAll();
            ReleaseFallbackSparkles();
            backLayers = behindTheGlass ?? new SpriteRenderer[0];
            frontLayers = inFrontOfTheGlass ?? new SpriteRenderer[0];
            portalPivot = bouncePivot;
            portalGlow = glow;
            badgeSparkles = sparkles;
            defaultCheckBadge = checkBadge;
            mouthAnchor = mouth;
            throatAnchor = throat;
            portalRestCaptured = false;
            CapturePortalRest();
        }

        /// <summary>Authoring API for the timing, mirroring ConfigureSceneBindings.</summary>
        public void ConfigureTiming(float lift, float liftHeight, float approach,
                                    float entry, float hide, float bounce,
                                    float depth, float scaleAtEntry, float scaleAtHide,
                                    float tiltDegrees, float anticipation = 0.08f,
                                    float approachLead = 0.16f)
        {
            anticipationDuration = Mathf.Max(0f, anticipation);
            liftDuration = Mathf.Max(0f, lift);
            liftFullHeight = Mathf.Max(0.1f, liftHeight);
            approachDuration = Mathf.Max(0.01f, approach);
            minimumApproachDuration = Mathf.Min(
                minimumApproachDuration, approachDuration);
            minimumApproachLead = Mathf.Max(0f, approachLead);
            entryDuration = Mathf.Max(0.01f, entry);
            hideDuration = Mathf.Max(0.01f, hide);
            bounceDuration = Mathf.Max(0f, bounce);
            entryDepth = Mathf.Clamp(depth, 0.5f, 1f);
            entryScale = Mathf.Clamp(scaleAtEntry, 0.05f, 1f);
            hideScale = Mathf.Clamp(scaleAtHide, 0.01f, entryScale);
            entryTilt = Mathf.Clamp(tiltDegrees, 0f, 20f);
        }

        /// <summary>
        /// Builder'ın portal resminden ölçtüğü fiziksel açıklık. Runtime her bardağın
        /// mevcut renderer bounds'unu buna sığdırır; iki/üç raf ölçeğini tekrar çarpmaz.
        /// </summary>
        public void ConfigureGeometry(Vector2 openingSize, float outerEdgeOffset,
                                      float clearance, float fit = 0.93f)
        {
            portalOpeningSize = new Vector2(
                Mathf.Max(0.01f, openingSize.x),
                Mathf.Max(0.01f, openingSize.y));
            portalOuterEdgeOffset = Mathf.Max(0f, outerEdgeOffset);
            mouthClearance = Mathf.Max(0f, clearance);
            portalFit = Mathf.Clamp(fit, 0.5f, 1f);
        }

        public bool IsDelivering(LiquidBottle glass)
        {
            for (int i = 0; i < flights.Count; i++)
                if (!flights[i].Hidden && ReferenceEquals(flights[i].Glass, glass))
                    return true;
            return false;
        }

        /// <summary>
        /// Strict binding check with a message an artist can act on. Called by Play, so a
        /// half-authored portal refuses the flight instead of dropping the glass in mid-air.
        /// </summary>
        public bool ValidateBindings(out string reason)
        {
            if (mouthAnchor == null)
            {
                reason = "Mouth Anchor Inspector referansı eksik.";
                return false;
            }
            if (throatAnchor == null)
            {
                reason = "Throat Anchor Inspector referansı eksik.";
                return false;
            }
            if ((throatAnchor.position - mouthAnchor.position).sqrMagnitude < 0.0025f)
            {
                reason = "Mouth ve Throat anchor'ları aynı noktada; portalın giriş "
                       + "derinliği ölçülemiyor.";
                return false;
            }
            if (portalOpeningSize.x <= 0f || portalOpeningSize.y <= 0f)
            {
                reason = "Portal Opening Size pozitif olmalı.";
                return false;
            }
            if (portalOuterEdgeOffset <= mouthClearance)
            {
                reason = "Portal Outer Edge Offset, Mouth Clearance'dan büyük olmalı.";
                return false;
            }
            if (!MeasureLayers(frontLayers, "Front Layers", true,
                    out int frontFloor, out _, out int frontLayerId, out reason))
                return false;
            if (!MeasureLayers(backLayers, "Back Layers", false,
                    out _, out int backCeiling, out int backLayerId, out reason))
                return false;

            if (backLayerId != int.MinValue && backLayerId != frontLayerId)
            {
                reason = "Portal ön ve arka katmanları farklı sorting layer'da; aradaki "
                       + "pencere bir bardağı barındıramaz.";
                return false;
            }
            if (backCeiling != int.MinValue && backCeiling >= frontFloor)
            {
                reason = $"Portal arka katmanı ({backCeiling}) ön katmanın ({frontFloor}) "
                       + "önünde veya aynı sırada çiziliyor; bardak arada kalamaz.";
                return false;
            }

            reason = null;
            return true;
        }

        [ContextMenu("Validate Portal Bindings")]
        private void ValidateBindingsFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log("Portal delivery: bindings are valid.", this);
            else
                Debug.LogError("Portal delivery binding error: " + reason, this);
        }

        [ContextMenu("Preview Delivery")]
        private void PreviewFromContextMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Preview Delivery yalnızca play mode'da çalışır.", this);
                return;
            }
            if (previewGlass == null)
            {
                Debug.LogWarning("Preview Glass referansı eksik.", this);
                return;
            }
            if (!Play(previewGlass))
                Debug.LogWarning("Portal önizlemeyi reddetti; yukarıdaki hataya bakın.", this);
        }

        public bool Play(LiquidBottle glass, Action onGlassHidden = null,
                         Action onFinished = null)
            => Play(glass, null, null, onGlassHidden, onFinished);

        public bool Play(LiquidBottle glass, Transform checkBadge, Action onGlassHidden,
                         Action onFinished)
            => Play(glass, null, checkBadge, onGlassHidden, onFinished);

        /// <summary>
        /// Borrows one glass and delivers it through the arch.
        ///
        /// <paramref name="onGlassHidden"/> fires the instant the vessel is invisible and
        /// fully restored — that is the moment a pool may take it back. <paramref
        /// name="onFinished"/> fires after the portal bounce, which is where a sipariş
        /// kartı refresh belongs. Both also fire on cancellation, so no caller can leak a
        /// glass that never came home.
        /// </summary>
        public bool Play(LiquidBottle glass, Transform motionAnchor, Transform checkBadge,
                         Action onGlassHidden, Action onFinished)
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return false;
            if (glass == null || !glass.gameObject.activeInHierarchy) return false;
            if (flights.Count > 0 || IsDelivering(glass)) return false;
            if (!ValidateBindings(out string reason))
            {
                LogOnce(reason);
                return false;
            }

            CapturePortalRest();

            var flight = new Flight
            {
                Glass = glass,
                Root = glass.transform,
                OnGlassHidden = onGlassHidden,
                OnFinished = onFinished,
                OwnsPortalChrome = true
            };
            flight.HomePosition = flight.Root.position;
            flight.HomeRotation = flight.Root.rotation;
            flight.HomeScale = flight.Root.localScale;
            flight.MotionAnchorLocal = motionAnchor != null
                ? flight.Root.InverseTransformPoint(motionAnchor.position)
                : Vector3.zero;
            flight.HomeMotionAnchorPosition = motionAnchor != null
                ? motionAnchor.position
                : flight.HomePosition;

            CaptureBadge(flight, ResolveCheckBadge(glass, checkBadge));
            if (!BorrowSorting(flight))
            {
                RestoreBadge(flight);
                return false;
            }

            flights.Add(flight);
            flight.Sequence = BuildSequence(flight);
            return true;
        }

        /// <summary>
        /// Ends every flight on the spot: the glasses come home restored and switched off
        /// and both callbacks still fire, so a level unload can never strand one inside the
        /// arch or leave its pool slot occupied.
        /// </summary>
        public void CancelAll()
        {
            StopBadgeSparkles();
            StopTravelTrail(true);
            if (flights.Count == 0)
            {
                RestorePortalRest();
                return;
            }

            cancelScratch.Clear();
            cancelScratch.AddRange(flights);
            flights.Clear();
            for (int i = 0; i < cancelScratch.Count; i++)
            {
                Flight flight = cancelScratch[i];
                Sequence sequence = flight.Sequence;
                flight.Sequence = null;
                if (sequence != null && sequence.IsActive()) sequence.Kill();
                HideGlass(flight);
                Action finished = flight.OnFinished;
                flight.OnFinished = null;
                finished?.Invoke();
            }
            cancelScratch.Clear();
            RestorePortalRest();
        }

        // ---- Flight ---------------------------------------------------------------

        private Sequence BuildSequence(Flight flight)
        {
            Transform root = flight.Root;
            Vector3 homeAnchor = flight.HomeMotionAnchorPosition;
            Vector3 authoredMouth = mouthAnchor.position;
            Vector3 throat = throatAnchor.position;
            float portalDirection = throat.x >= authoredMouth.x ? 1f : -1f;
            float portalTiltSign = -portalDirection;

            Vector3 travelScale = ResolveTravelScale(flight);
            Vector3 entryTargetScale = RelativePortalScale(travelScale, entryScale);
            Vector3 hideTargetScale = RelativePortalScale(travelScale, hideScale);

            // Giriş rayı her bardakta portal yönünde akar. Sağ uçtaki bardaklar
            // gerekiyorsa uzun lift sırasında kapının dışına birleşir; rayda sola
            // gidip hemen sağa dönmez ve lean işareti son anda flip yapmaz.
            Quaternion approachRotation = Quaternion.AngleAxis(
                portalTiltSign * approachLean, Vector3.forward) * flight.HomeRotation;
            Vector3 mouth = ResolveMouthAnchor(
                flight, authoredMouth, throat, travelScale, approachRotation);
            Vector3 fullScaleStaging = ResolveMouthAnchor(
                flight, authoredMouth, throat, flight.HomeScale, approachRotation);
            Vector3 entryPoint = Vector3.LerpUnclamped(mouth, throat, entryDepth);

            Quaternion anticipationRotation = Quaternion.AngleAxis(
                -portalTiltSign * anticipationTilt, Vector3.forward)
                * flight.HomeRotation;
            Quaternion tilted = Quaternion.AngleAxis(
                portalTiltSign * entryTilt, Vector3.forward) * flight.HomeRotation;

            Transform portalBasis = mouthAnchor != null ? mouthAnchor.parent : null;
            float approachLead = portalBasis != null
                ? portalBasis.TransformVector(
                    Vector3.right * minimumApproachLead).magnitude
                : minimumApproachLead;
            float outsideApproachX = mouth.x - portalDirection * approachLead;
            float railX = portalDirection > 0f
                ? Mathf.Min(homeAnchor.x, outsideApproachX, fullScaleStaging.x)
                : Mathf.Max(homeAnchor.x, outsideApproachX, fullScaleStaging.x);
            Vector3 railAnchor = new Vector3(railX, mouth.y, mouth.z);
            float liftTime = MeasureLift(homeAnchor.y, mouth.y);
            // A disabled lift or a glass already on the rail still has to reach the safe
            // outside staging point without a frame-one jump. This fallback reuses the
            // approach timing budget and keeps the following portal leg one-directional.
            float stagingTime = liftTime <= 0f
                && (railAnchor - homeAnchor).sqrMagnitude > 0.000001f
                ? MeasureApproach(homeAnchor, railAnchor)
                : 0f;
            float serviceTime = liftTime + stagingTime;
            float approachTime = MeasureApproach(railAnchor, mouth);
            Vector3 anticipationScale = new Vector3(
                flight.HomeScale.x * (1f + anticipationSquash),
                flight.HomeScale.y * (1f - anticipationSquash),
                flight.HomeScale.z);

            float railTime = anticipationDuration + serviceTime;
            float mouthTime = railTime + approachTime;
            float entryTime = mouthTime + entryDuration;
            float hiddenTime = entryTime + hideDuration;

            Sequence sequence = DOTween.Sequence()
                .SetRecyclable(true)
                .SetTarget(root)
                .SetUpdate(useUnscaledTime);

            // Tikten sonra ayağını rafta tutup hafifçe yüklenir. Bu küçük anticipation
            // bardağın gönderildiğini okutur; bir pop ya da teleport değildir.
            if (anticipationDuration > 0f)
                sequence.Append(TweenAnchorPose(flight, homeAnchor, homeAnchor,
                    flight.HomeRotation, anticipationRotation,
                    flight.HomeScale, anticipationScale,
                    anticipationDuration, Ease.OutCubic));

            // Rafından servis hattına yükselir. Kimliğini burada tam boy korur;
            // portal sığdırması ancak ray üzerindeki gerçek gidişte başlar.
            // Kapıya fazla yakın sağ bardak, liftin uzun mesafesi içinde dış bekleme
            // noktasına usulca birleşir; ayrı bir geri gidiş beat'i oluşmaz.
            if (liftTime > 0f)
                sequence.Append(TweenAnchorPose(flight, homeAnchor, railAnchor,
                    anticipationDuration > 0f
                        ? anticipationRotation : flight.HomeRotation,
                    flight.HomeRotation,
                    anticipationDuration > 0f
                        ? anticipationScale : flight.HomeScale,
                    flight.HomeScale, liftTime, liftEase));
            else if (stagingTime > 0f)
                sequence.Append(TweenAnchorPose(flight, homeAnchor, railAnchor,
                    anticipationDuration > 0f
                        ? anticipationRotation : flight.HomeRotation,
                    flight.HomeRotation,
                    anticipationDuration > 0f
                        ? anticipationScale : flight.HomeScale,
                    flight.HomeScale, stagingTime, Ease.InOutSine));

            // Hat boyunca kapının ağzına kayar ve o sırada gerçek portal açıklığına
            // sığdırılır. İki/üç raf düzeni ile bardak tipi ayrı ölçüldüğü için küçük bir
            // shot bardağı, en büyük kupanın oranıyla ikinci kez küçülmez.
            // The first leg reaches the safe service staging point (via lift, or the
            // zero-lift fallback); the second slides to the doorway. Different vessel
            // pivots therefore share one authored rail height without a frame-one jump.
            Vector3 approachStartAnchor = serviceTime > 0f ? railAnchor : homeAnchor;
            Quaternion approachStartRotation = serviceTime > 0f
                ? flight.HomeRotation
                : (anticipationDuration > 0f
                    ? anticipationRotation : flight.HomeRotation);
            Vector3 approachStartScale = serviceTime > 0f
                ? flight.HomeScale
                : (anticipationDuration > 0f
                    ? anticipationScale : flight.HomeScale);
            sequence.Append(TweenAnchorPose(flight, approachStartAnchor, mouth,
                approachStartRotation, approachRotation,
                approachStartScale, travelScale, approachTime, approachEase));

            // Kemerin arkasına girer. Konum yolun yarısından fazlasına gider,
            // yani öndeki occluder bardağı kenarlardan fiziksel olarak kapatmaya başlar.
            sequence.Append(TweenAnchorPose(flight, mouth, entryPoint,
                approachRotation, tilted, travelScale, entryTargetScale,
                entryDuration, entryEase));

            // Son küçülme İÇERİDE olur. Alpha'ya hiç dokunulmaz; bardağın
            // görünmez olmasının tek sebebi kemerin önündeki maske.
            sequence.Append(TweenAnchorPose(flight, entryPoint, throat,
                tilted, tilted, entryTargetScale, hideTargetScale,
                hideDuration, hideEase));

            InsertBadgeBreakup(sequence, flight, entryTime);
            if (flight.OwnsPortalChrome)
                InsertPortalChrome(sequence, railTime, mouthTime, entryTime, hiddenTime);

            sequence.InsertCallback(anticipationDuration,
                () => BeginTravel(flight));
            sequence.InsertCallback(entryTime, () => StopTravelTrail(false));

            // Sandviç tam kemere girerken devralır. Ondan öncesi yükselme lifti: bardak
            // geçtiği rafların ve kartların önünde durmalı, portalın penceresinde değil.
            if (flight.LiftOffset != flight.PortalOffset)
                sequence.InsertCallback(mouthTime, () => EnterPortalSorting(flight));

            sequence.InsertCallback(hiddenTime, () => HideGlass(flight));
            sequence.OnComplete(() => FinishFlight(flight));
            sequence.OnKill(() => HandleSequenceKilled(flight));
            return sequence;
        }

        /// <summary>
        /// Konum, açı ve ölçeği ayrı tween'lere bırakırsak foot anchor ara karelerde
        /// mouth-throat hattından sapar: farklı ease'ler farklı root offsetleri üretir.
        /// Bu tek tween her karede ayağın dünya hedefini çözer, sonra root'u yerleştirir.
        /// </summary>
        private static Tween TweenAnchorPose(
            Flight flight, Vector3 fromAnchor, Vector3 toAnchor,
            Quaternion fromRotation, Quaternion toRotation,
            Vector3 fromScale, Vector3 toScale, float duration, Ease ease)
        {
            float progress = 0f;
            return DOTween.To(() => progress, value =>
                {
                    progress = value;
                    Quaternion rotation = Quaternion.SlerpUnclamped(
                        fromRotation, toRotation, value);
                    Vector3 scale = Vector3.LerpUnclamped(fromScale, toScale, value);
                    Vector3 anchor = Vector3.LerpUnclamped(
                        fromAnchor, toAnchor, value);
                    Transform movingRoot = flight.Root;
                    movingRoot.localScale = scale;
                    movingRoot.rotation = rotation;
                    // TransformVector reads the actual hierarchy matrix after the new pose,
                    // including rotated/non-uniform/negative parent scale. Subtracting that
                    // vector makes the foot land exactly on the authored world anchor.
                    movingRoot.position = anchor
                                        - movingRoot.TransformVector(
                                            flight.MotionAnchorLocal);
                }, 1f, duration)
                .SetEase(ease)
                .SetRecyclable(true)
                .SetTarget(flight.Root);
        }

        private Vector3 ResolveTravelScale(Flight flight)
        {
            float fitRatio = liftScale;
            if (flight.HasVisualBounds)
            {
                Vector2 opening = PortalOpeningWorldSize();
                Vector3 size = flight.HomeVisualBounds.size;
                if (opening.x > 0.001f && opening.y > 0.001f
                    && size.x > 0.001f && size.y > 0.001f)
                {
                    fitRatio = Mathf.Min(
                        1f,
                        portalFit * opening.x / size.x,
                        portalFit * opening.y / size.y);
                }
            }
            fitRatio = Mathf.Clamp(fitRatio, 0.15f, 1f);
            return flight.HomeScale * fitRatio;
        }

        private Vector2 PortalOpeningWorldSize()
        {
            Transform basis = mouthAnchor != null ? mouthAnchor.parent : null;
            if (basis == null) return portalOpeningSize;
            return new Vector2(
                basis.TransformVector(Vector3.right * portalOpeningSize.x).magnitude,
                basis.TransformVector(Vector3.up * portalOpeningSize.y).magnitude);
        }

        /// <summary>
        /// entryScale/hideScale eski sahnelerde liftScale'e göre author edilmiştir.
        /// Runtime fit ratio değişse bile bu iki oranı travel scale'e göre korumak,
        /// üç raflı küçük bardakların ikinci kez minyatürleşmesini engeller.
        /// </summary>
        private Vector3 RelativePortalScale(Vector3 travelScale, float authoredScale)
        {
            float ratio = authoredScale / Mathf.Max(0.001f, liftScale);
            return travelScale * Mathf.Clamp(ratio, 0.01f, 1f);
        }

        private Vector3 ResolveMouthAnchor(
            Flight flight, Vector3 authoredMouth, Vector3 throat,
            Vector3 travelScale, Quaternion approachRotation)
        {
            if (!flight.HasVisualBounds) return authoredMouth;

            float direction = throat.x >= authoredMouth.x ? 1f : -1f;
            Bounds bounds = flight.HomeVisualBounds;
            float horizontalExtent = direction > 0f
                ? bounds.max.x - flight.HomeMotionAnchorPosition.x
                : flight.HomeMotionAnchorPosition.x - bounds.min.x;
            float verticalExtent = Mathf.Max(
                Mathf.Abs(bounds.max.y - flight.HomeMotionAnchorPosition.y),
                Mathf.Abs(bounds.min.y - flight.HomeMotionAnchorPosition.y));

            float scaleX = SafeScaleRatio(travelScale.x, flight.HomeScale.x);
            float scaleY = SafeScaleRatio(travelScale.y, flight.HomeScale.y);
            float leanRadians = Quaternion.Angle(
                flight.HomeRotation, approachRotation) * Mathf.Deg2Rad;
            float projectedExtent = Mathf.Max(0f, horizontalExtent)
                                  * scaleX * Mathf.Abs(Mathf.Cos(leanRadians))
                                  + verticalExtent * scaleY
                                  * Mathf.Abs(Mathf.Sin(leanRadians));

            Transform basis = mouthAnchor != null ? mouthAnchor.parent : null;
            float edgeOffset = basis != null
                ? basis.TransformVector(Vector3.right * portalOuterEdgeOffset).magnitude
                : portalOuterEdgeOffset;
            float clearance = basis != null
                ? basis.TransformVector(Vector3.right * mouthClearance).magnitude
                : mouthClearance;

            Vector3 result = authoredMouth;
            float outerEdgeX = authoredMouth.x + direction * edgeOffset;
            result.x = outerEdgeX - direction * (projectedExtent + clearance);
            return result;
        }

        private static float SafeScaleRatio(float target, float source)
        {
            if (Mathf.Abs(source) <= 0.0001f) return 1f;
            return Mathf.Abs(target / source);
        }

        /// <summary>
        /// Yükselme süresi. Aynı ivme, farklı yol: alt raftaki bardak üst raftakinden
        /// uzun sürede çıkar. Bardak zaten servis hattındaysa beat tamamen atlanır,
        /// yoksa yerinde bekleyen bir kare doğuyordu.
        /// </summary>
        private float MeasureLift(float homeY, float serviceY)
        {
            if (liftDuration <= 0f) return 0f;
            float distance = Mathf.Abs(serviceY - homeY);
            if (distance <= 0.001f) return 0f;
            return Mathf.Max(0.02f,
                liftDuration * Mathf.Sqrt(distance / liftFullHeight));
        }

        private float MeasureApproach(Vector3 rail, Vector3 mouth)
        {
            float distance = Vector3.Distance(rail, mouth);
            if (distance <= 0.001f) return minimumApproachDuration;
            float scaled = approachDuration
                         * Mathf.Sqrt(distance / approachFullDistance);
            return Mathf.Clamp(scaled, minimumApproachDuration, approachDuration);
        }

        private void EnterPortalSorting(Flight flight)
        {
            flight.InPortal = true;
            if (!flight.Hidden && flight.Glass != null)
            {
                ApplyBorrowedSorting(flight);
                ConfigureTravelTrailSorting(flight);
            }
        }

        /// <summary>
        /// The ✓ pops once and then breaks up rather than sliding away with the glass, so
        /// the badge reads as spent at the same instant the order is.
        /// </summary>
        private void InsertBadgeBreakup(Sequence sequence, Flight flight, float entryTime)
        {
            if (flight.Badge == null) return;

            float pop = Mathf.Max(0.01f, hideDuration * 0.35f);
            float breakUp = Mathf.Max(0.01f, hideDuration - pop);

            sequence.Insert(entryTime, flight.Badge
                .DOScale(flight.BadgeScale * 1.25f, pop)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(entryTime + pop, flight.Badge
                .DOScale(Vector3.zero, breakUp)
                .SetEase(Ease.InBack).SetRecyclable(true));
            if (flight.BadgeRenderer != null)
                sequence.Insert(entryTime + pop, flight.BadgeRenderer
                    .DOFade(0f, breakUp)
                    .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.InsertCallback(entryTime + pop, () => PlaySparkles(flight));
        }

        /// <summary>
        /// Glow and the swallow bounce. Only the leading flight drives these: two
        /// deliveries overlapping would otherwise fight over one portal's alpha.
        /// </summary>
        private void InsertPortalChrome(Sequence sequence, float railTime, float mouthTime,
                                        float entryTime, float hiddenTime)
        {
            // Portal, bardak hattına çıkana kadar sessiz. Yükselirken parlamaya başlarsa
            // ışık bardaktan önce davranmış oluyor ve teslim önceden belli oluyor.
            float slide = Mathf.Max(0.01f, mouthTime - railTime);
            if (portalGlow != null)
            {
                sequence.Insert(railTime, portalGlow.DOFade(glowApproachAlpha, slide)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
                sequence.Insert(entryTime, portalGlow
                    .DOFade(glowSwallowAlpha, Mathf.Max(0.01f, hideDuration * 0.45f))
                    .SetEase(Ease.OutQuad).SetRecyclable(true));
                sequence.Insert(hiddenTime, portalGlow
                    .DOFade(0f, Mathf.Max(0.01f, bounceDuration))
                    .SetEase(Ease.InSine).SetRecyclable(true));
            }

            if (portalPivot == null || bounceDuration <= 0f || bounceSquash <= 0f) return;

            // Yatayda genişleyip dikeyde kısalan tek beat. Elastik bir punch yerine bu:
            // geçit bir şey yuttuğunda "tok" oturmalı, titrememeli.
            Vector3 rest = portalPivotRestScale;
            var squashed = new Vector3(rest.x * (1f + bounceSquash),
                                       rest.y * (1f - bounceSquash),
                                       rest.z);
            float down = bounceDuration * 0.38f;
            sequence.Insert(hiddenTime, portalPivot.DOScale(squashed, down)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(hiddenTime + down, portalPivot
                .DOScale(rest, bounceDuration - down)
                .SetEase(Ease.OutBack).SetRecyclable(true));
        }

        /// <summary>
        /// The glass is invisible from here on, so this is where it stops being ours: every
        /// borrowed value goes back and the vessel is switched off at its original pose.
        /// Idempotent, because both the timeline and a cancellation land here.
        /// </summary>
        private void HideGlass(Flight flight)
        {
            if (flight.Hidden) return;
            flight.Hidden = true;

            RestoreSorting(flight);
            RestoreBadge(flight);

            if (flight.Glass != null)
            {
                flight.Glass.gameObject.SetActive(false);
                flight.Root.position = flight.HomePosition;
                flight.Root.rotation = flight.HomeRotation;
                flight.Root.localScale = flight.HomeScale;
            }

            // Nulled first: the callback is allowed to tear this animator down.
            Action hidden = flight.OnGlassHidden;
            flight.OnGlassHidden = null;
            hidden?.Invoke();
        }

        private void FinishFlight(Flight flight)
        {
            flights.Remove(flight);
            flight.Sequence = null;
            StopTravelTrail(true);
            HideGlass(flight);

            Action finished = flight.OnFinished;
            flight.OnFinished = null;
            finished?.Invoke();
        }

        /// <summary>
        /// DOTween Safe Mode veya dışarıdan bir DOKill sequence'i beklenmedik biçimde
        /// keserse pool/presentation lock takılı kalmasın. Normal completion ve CancelAll
        /// önce Sequence'i null'ladığı için bu yol yalnız gerçek beklenmedik kill'de çalışır.
        /// </summary>
        private void HandleSequenceKilled(Flight flight)
        {
            if (flight == null || flight.Sequence == null) return;
            flight.Sequence = null;
            flights.Remove(flight);
            StopBadgeSparkles();
            StopTravelTrail(true);
            RestorePortalRest();
            HideGlass(flight);

            Action finished = flight.OnFinished;
            flight.OnFinished = null;
            finished?.Invoke();
        }

        private void PlaySparkles(Flight flight)
        {
            EnsureFallbackSparkles();
            if (badgeSparkles == null) return;
            // Play() does not rewind a non-looping ParticleSystem that is still inside
            // its duration window. Clear first so rapid consecutive deliveries each get
            // their own time-zero burst, including artist-assigned systems.
            badgeSparkles.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
            if (flight.Badge != null)
                badgeSparkles.transform.position = flight.Badge.position;
            badgeSparkles.Play(true);
        }

        private void StartTravelTrail(Flight flight)
        {
            if (!showTravelTrail || flight == null || flight.Hidden) return;
            EnsureTravelTrail();
            if (travelTrail == null) return;

            PositionTravelTrail(flight);
            ConfigureTravelTrailSorting(flight);
            ParticleSystem.EmissionModule emission = travelTrail.emission;
            emission.rateOverTime = travelTrailRate;
            travelTrail.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
            travelTrail.Play(true);
        }

        private void BeginTravel(Flight flight)
        {
            if (flight == null || flight.Hidden) return;
            HideTravelGroundShadows(flight);
            StartTravelTrail(flight);
        }

        private void StopTravelTrail(bool clear)
        {
            if (travelTrail == null) return;
            travelTrail.Stop(true, clear
                ? ParticleSystemStopBehavior.StopEmittingAndClear
                : ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>
        /// Yeni bir art/prefab istemeyen, tek örnekli servis izi. World-space parçacıklar
        /// emitter bardakla ilerlerken geride kalır; cyan ray ile altın portal dilini aynı
        /// küçük stretched-billboard kuyruğunda birleştirir.
        /// </summary>
        private void EnsureTravelTrail()
        {
            // A runtime child can be destroyed independently during teardown/rebind.
            // Its HideAndDontSave material is not owned by that GameObject, so release
            // it before replacing the system.
            if (travelTrail == null && travelTrailMaterial != null)
                ReleaseTravelTrailMaterial();
            if (travelTrail != null || !Application.isPlaying || !isActiveAndEnabled)
                return;

            var trailObject = new GameObject("Portal Travel Trail (Runtime)")
            {
                hideFlags = HideFlags.DontSave
            };
            trailObject.transform.SetParent(transform, false);
            travelTrail = trailObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = travelTrail.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = useUnscaledTime;
            main.maxParticles = 56;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.14f, 0.22f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.042f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color32(0x66, 0xDE, 0xFF, 0xD8),
                new Color32(0xFF, 0xD5, 0x61, 0xC8));
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = travelTrail.emission;
            emission.rateOverTime = travelTrailRate;

            ParticleSystem.ShapeModule shape = travelTrail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.018f;

            ParticleSystem.ColorOverLifetimeModule colourLife =
                travelTrail.colorOverLifetime;
            colourLife.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.55f, 0.93f, 1f), 0.62f),
                    new GradientColorKey(new Color(1f, 0.78f, 0.24f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.82f, 0.12f),
                    new GradientAlphaKey(0f, 1f),
                });
            colourLife.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystem.SizeOverLifetimeModule sizeLife = travelTrail.sizeOverLifetime;
            sizeLife.enabled = true;
            sizeLife.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.30f),
                    new Keyframe(0.16f, 1f),
                    new Keyframe(1f, 0f)));

            ParticleSystemRenderer particleRenderer =
                trailObject.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            particleRenderer.velocityScale = 0.18f;
            particleRenderer.lengthScale = 1.65f;

            Shader shader = Shader.Find("Legacy Shaders/Particles/Additive");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                travelTrailMaterial = new Material(shader)
                {
                    name = "Portal Travel Trail Material (Runtime)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                particleRenderer.sharedMaterial = travelTrailMaterial;
            }

            travelTrail.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void PositionTravelTrail(Flight flight)
        {
            if (travelTrail == null || flight.Root == null) return;
            travelTrail.transform.position = flight.Root.TransformPoint(
                flight.MotionAnchorLocal) + Vector3.up * 0.045f;
        }

        private void ConfigureTravelTrailSorting(Flight flight)
        {
            if (travelTrail == null || flight.Renderers == null) return;
            ParticleSystemRenderer trailRenderer =
                travelTrail.GetComponent<ParticleSystemRenderer>();
            if (trailRenderer == null) return;

            int offset = flight.InPortal ? flight.PortalOffset : flight.LiftOffset;
            int lowest = int.MaxValue;
            for (int i = 0; i < flight.BaseOrders.Length; i++)
                lowest = Mathf.Min(lowest, flight.BaseOrders[i] + offset);
            trailRenderer.sortingLayerID = flight.PortalLayerId;
            // The wake belongs behind the complete vessel stack (and its check badge),
            // not between the bottle renderers. LiftOffset still keeps it above shelves.
            trailRenderer.sortingOrder = lowest == int.MaxValue ? 0 : lowest - 1;
        }

        private void ReleaseTravelTrailMaterial()
        {
            if (travelTrailMaterial == null) return;
            if (Application.isPlaying) Destroy(travelTrailMaterial);
            else DestroyImmediate(travelTrailMaterial);
            travelTrailMaterial = null;
        }

        private void ReleaseTravelTrail()
        {
            ParticleSystem owned = travelTrail;
            travelTrail = null;
            if (owned != null)
            {
                if (Application.isPlaying) Destroy(owned.gameObject);
                else DestroyImmediate(owned.gameObject);
            }
            ReleaseTravelTrailMaterial();
        }

        /// <summary>
        /// The art hook existed before the scene had a particle prefab. Keep that hook
        /// authoritative when it is assigned; otherwise create one restrained burst the
        /// first time it is actually needed. This is deliberately runtime-only so opening
        /// or saving the scene never manufactures hidden authoring objects.
        /// </summary>
        private void EnsureFallbackSparkles()
        {
            // A runtime child may have been destroyed independently. Drop its material
            // before replacing it so repeated recovery cannot leak hidden materials.
            if (badgeSparkles == null && ownsFallbackSparkles)
                ReleaseFallbackSparkles();
            if (badgeSparkles != null || !createFallbackSparkles
                || !Application.isPlaying || !isActiveAndEnabled)
                return;

            var sparkleObject = new GameObject("Portal Badge Sparkles (Runtime)");
            sparkleObject.hideFlags = HideFlags.DontSave;
            sparkleObject.transform.SetParent(transform, false);
            badgeSparkles = sparkleObject.AddComponent<ParticleSystem>();
            ownsFallbackSparkles = true;
            badgeSparkles.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = badgeSparkles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 0.65f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = useUnscaledTime;
            main.maxParticles = 20;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.34f, 0.56f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.38f, 0.78f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.045f, 0.095f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color32(0x72, 0xE2, 0x77, 0xFF),
                new Color32(0xFF, 0xD2, 0x49, 0xFF));
            main.gravityModifier = 0.06f;

            ParticleSystem.EmissionModule emission = badgeSparkles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 14) });

            ParticleSystem.ShapeModule shape = badgeSparkles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.055f;

            ParticleSystem.ColorOverLifetimeModule colourLife =
                badgeSparkles.colorOverLifetime;
            colourLife.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.78f, 0.20f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.08f),
                    new GradientAlphaKey(0f, 1f),
                });
            colourLife.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystem.SizeOverLifetimeModule sizeLife = badgeSparkles.sizeOverLifetime;
            sizeLife.enabled = true;
            sizeLife.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.25f),
                    new Keyframe(0.20f, 1f),
                    new Keyframe(1f, 0f)));

            ParticleSystemRenderer particleRenderer =
                sparkleObject.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            particleRenderer.sortingOrder = SparkleSortingOrder(out int sortingLayerId);
            particleRenderer.sortingLayerID = sortingLayerId;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                fallbackSparkleMaterial = new Material(shader)
                {
                    name = "Portal Sparkle Material (Runtime)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                particleRenderer.sharedMaterial = fallbackSparkleMaterial;
            }

            // Keep an unequivocally stopped state after all modules are configured;
            // PlaySparkles owns every burst and always rewinds first.
            badgeSparkles.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void StopBadgeSparkles()
        {
            if (badgeSparkles == null) return;
            badgeSparkles.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void ReleaseFallbackSparkles()
        {
            if (!ownsFallbackSparkles) return;

            ParticleSystem owned = badgeSparkles;
            badgeSparkles = null;
            ownsFallbackSparkles = false;
            if (owned != null)
            {
                if (Application.isPlaying) Destroy(owned.gameObject);
                else DestroyImmediate(owned.gameObject);
            }
            ReleaseFallbackSparkleMaterial();
        }

        private void ReleaseFallbackSparkleMaterial()
        {
            if (fallbackSparkleMaterial == null) return;
            if (Application.isPlaying) Destroy(fallbackSparkleMaterial);
            else DestroyImmediate(fallbackSparkleMaterial);
            fallbackSparkleMaterial = null;
        }

        private int SparkleSortingOrder(out int sortingLayerId)
        {
            sortingLayerId = 0;
            int order = 200;
            for (int i = 0; i < frontLayers.Length; i++)
            {
                SpriteRenderer layer = frontLayers[i];
                if (layer == null) continue;
                sortingLayerId = layer.sortingLayerID;
                order = Mathf.Max(order, layer.sortingOrder + 2);
            }
            return order;
        }

        // ---- Draw order sandwich --------------------------------------------------

        /// <summary>
        /// Slides the whole vessel into the gap between the portal's two layer groups. The
        /// offset is measured against the authored orders rather than hard-coded, so the
        /// sandwich survives whatever numbers the artwork ends up using.
        /// </summary>
        private bool BorrowSorting(Flight flight)
        {
            if (!MeasureLayers(frontLayers, "Front Layers", true,
                    out int frontFloor, out _, out int portalLayerId, out string reason))
            {
                LogOnce(reason);
                return false;
            }
            MeasureLayers(backLayers, "Back Layers", false, out _, out int backCeiling,
                out _, out _);

            // Any leftover lift from the entrance drop or a pour would be baked into the
            // measurement below, so the vessel is normalised to its authored orders first.
            flight.Glass.SetSortingOffset(0);

            Renderer[] renderers = flight.Glass.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                LogOnce("Teslim edilen bardakta hiç renderer yok.");
                return false;
            }

            var baseOrders = new int[renderers.Length];
            var baseLayerIds = new int[renderers.Length];
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                baseOrders[i] = renderer.sortingOrder;
                baseLayerIds[i] = renderer.sortingLayerID;
                lowest = Mathf.Min(lowest, baseOrders[i]);
                highest = Mathf.Max(highest, baseOrders[i]);
            }

            // Occlusion is the load-bearing half of the effect, so the top of the vessel is
            // pinned one step under the front group no matter how tall its own stack is.
            flight.Renderers = renderers;
            flight.BaseOrders = baseOrders;
            flight.BaseLayerIds = baseLayerIds;
            flight.HasVisualBounds = TryMeasureVisualBounds(
                flight, out flight.HomeVisualBounds);
            CaptureTravelGroundShadows(flight);
            flight.PortalLayerId = portalLayerId;
            flight.PortalOffset = frontFloor - 1 - highest;
            // Yükselirken bardak portalın penceresinde değil, geçtiği rafların önünde
            // olmalı. Boost 0 ise ya da yükselme kapalıysa sandviç en baştan geçerli.
            flight.LiftOffset = liftSortingBoost > 0
                ? liftSortingBoost
                : flight.PortalOffset;
            flight.InPortal = flight.LiftOffset == flight.PortalOffset;

            if (backCeiling != int.MinValue && lowest + flight.PortalOffset <= backCeiling)
                LogOnce($"Portal penceresi dar: bardak {highest - lowest + 1} çizim sırası "
                      + $"istiyor, kemer {frontFloor - backCeiling - 1} bırakıyor. Bardak "
                      + "gizlenmeye devam eder ama iç boşluk bardağın önüne taşabilir.");

            ApplyBorrowedSorting(flight);
            return true;
        }

        private static bool TryMeasureVisualBounds(Flight flight, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            Renderer[] renderers = flight.Renderers;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled
                    || !renderer.gameObject.activeInHierarchy
                    || renderer is ParticleSystemRenderer)
                    continue;
                if (IsGroundContactShadow(flight, renderer)) continue;
                if (flight.Badge != null
                    && (renderer.transform == flight.Badge
                        || renderer.transform.IsChildOf(flight.Badge)))
                    continue;

                Bounds candidate = renderer.bounds;
                if (candidate.size.sqrMagnitude <= 0.000001f) continue;
                if (!found)
                {
                    bounds = candidate;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }
            return found;
        }

        /// <summary>
        /// BottleShell's broad contact shadow belongs to the shelf, not to the flying
        /// silhouette. It must neither make the vessel fit 5-17% smaller nor ride up to
        /// the portal like a second glass. Its exact active state is borrowed and restored.
        /// </summary>
        private static void CaptureTravelGroundShadows(Flight flight)
        {
            Renderer[] renderers = flight.Renderers;
            var hidden = new Renderer[renderers.Length];
            var activeStates = new bool[renderers.Length];
            int count = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsGroundContactShadow(flight, renderer)) continue;
                hidden[count] = renderer;
                activeStates[count] = renderer.gameObject.activeSelf;
                count++;
            }

            if (count == 0) return;
            Array.Resize(ref hidden, count);
            Array.Resize(ref activeStates, count);
            flight.TravelHiddenRenderers = hidden;
            flight.TravelHiddenActiveStates = activeStates;
        }

        private static void HideTravelGroundShadows(Flight flight)
        {
            Renderer[] hidden = flight.TravelHiddenRenderers;
            if (hidden == null) return;
            for (int i = 0; i < hidden.Length; i++)
            {
                Renderer renderer = hidden[i];
                if (renderer == null) continue;
                // Keep renderer.enabled untouched: BottleShell treats a disabled shadow
                // as stale and rebuilds it every LateUpdate. An inactive child preserves
                // the authored renderer state without triggering that refresh loop.
                renderer.gameObject.SetActive(false);
            }
        }

        private static bool IsGroundContactShadow(Flight flight, Renderer renderer)
        {
            if (renderer == null
                || !string.Equals(renderer.gameObject.name, "Shadow",
                    StringComparison.Ordinal))
                return false;
            BottleShell shell = renderer.GetComponentInParent<BottleShell>();
            return shell != null && (shell.transform == flight.Root
                || shell.transform.IsChildOf(flight.Root));
        }

        private void LateUpdate()
        {
            for (int i = 0; i < flights.Count; i++)
            {
                Flight flight = flights[i];
                if (flight.Hidden || flight.Glass == null) continue;
                ApplyBorrowedSorting(flight);
                if (travelTrail != null && travelTrail.isPlaying)
                {
                    PositionTravelTrail(flight);
                    ConfigureTravelTrailSorting(flight);
                }
            }
        }

        private static void ApplyBorrowedSorting(Flight flight)
        {
            int offset = flight.InPortal ? flight.PortalOffset : flight.LiftOffset;
            Renderer[] renderers = flight.Renderers;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.sortingLayerID = flight.PortalLayerId;
                renderer.sortingOrder = flight.BaseOrders[i] + offset;
            }
        }

        private static void RestoreSorting(Flight flight)
        {
            Renderer[] renderers = flight.Renderers;
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    renderer.sortingLayerID = flight.BaseLayerIds[i];
                    renderer.sortingOrder = flight.BaseOrders[i];
                }
            }

            Renderer[] hidden = flight.TravelHiddenRenderers;
            bool[] activeStates = flight.TravelHiddenActiveStates;
            if (hidden != null && activeStates != null)
            {
                int count = Mathf.Min(hidden.Length, activeStates.Length);
                for (int i = 0; i < count; i++)
                    if (hidden[i] != null)
                        hidden[i].gameObject.SetActive(activeStates[i]);
            }
            // LiquidBottle caches base orders lazily; it has to look again after we put the
            // authored numbers back, or the next pour lift would be measured off ours.
            if (flight.Glass != null) flight.Glass.InvalidateRenderers();
        }

        /// <summary>
        /// Reads one authored layer group. Mixed sorting layers inside a group are a
        /// rejection rather than a silent surprise: the whole sandwich assumes one layer.
        /// </summary>
        private static bool MeasureLayers(SpriteRenderer[] layers, string label, bool required,
                                          out int floor, out int ceiling, out int layerId,
                                          out string reason)
        {
            floor = int.MaxValue;
            ceiling = int.MinValue;
            layerId = int.MinValue;

            int found = 0;
            if (layers != null)
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    SpriteRenderer renderer = layers[i];
                    if (renderer == null)
                    {
                        reason = $"{label}[{i}] boş.";
                        return false;
                    }
                    if (found == 0) layerId = renderer.sortingLayerID;
                    else if (renderer.sortingLayerID != layerId)
                    {
                        reason = $"{label} tek bir sorting layer'da olmalı.";
                        return false;
                    }
                    floor = Mathf.Min(floor, renderer.sortingOrder);
                    ceiling = Mathf.Max(ceiling, renderer.sortingOrder);
                    found++;
                }
            }

            if (found == 0)
            {
                floor = int.MaxValue;
                if (required)
                {
                    reason = $"{label} en az bir renderer istiyor.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        // ---- Borrowed scene state -------------------------------------------------

        /// <summary>Açık argüman, sonra kayıtlı sağlayıcı, en son serialized rozet.</summary>
        private Transform ResolveCheckBadge(LiquidBottle glass, Transform explicitBadge)
        {
            if (explicitBadge != null) return explicitBadge;
            if (CheckBadgeSource != null)
            {
                Transform resolved = CheckBadgeSource.GetCheckBadge(glass);
                if (resolved != null) return resolved;
            }
            return defaultCheckBadge;
        }

        private void CaptureBadge(Flight flight, Transform badge)
        {
            if (badge == null) return;
            flight.Badge = badge;
            flight.BadgeScale = badge.localScale;
            flight.BadgeRenderer = badge.GetComponent<SpriteRenderer>();
            if (flight.BadgeRenderer != null) flight.BadgeColor = flight.BadgeRenderer.color;
        }

        private static void RestoreBadge(Flight flight)
        {
            if (flight.Badge == null) return;
            flight.Badge.localScale = flight.BadgeScale;
            if (flight.BadgeRenderer != null) flight.BadgeRenderer.color = flight.BadgeColor;
            flight.Badge = null;
            flight.BadgeRenderer = null;
        }

        /// <summary>
        /// Glow alpha belongs to this component, not to the artwork: whatever alpha the
        /// scene was saved with is pushed to zero once and only a delivery ever raises it.
        /// The bounce pivot's rest scale is remembered for the same reason.
        /// </summary>
        private void CapturePortalRest()
        {
            if (portalRestCaptured) return;
            portalRestCaptured = true;
            portalPivotRestScale = portalPivot != null ? portalPivot.localScale : Vector3.one;
            SetAlpha(portalGlow, 0f);
        }

        private void RestorePortalRest()
        {
            if (!portalRestCaptured) return;
            if (portalGlow != null && DOTween.IsTweening(portalGlow)) portalGlow.DOKill();
            if (portalPivot != null)
            {
                if (DOTween.IsTweening(portalPivot)) portalPivot.DOKill();
                portalPivot.localScale = portalPivotRestScale;
            }
            SetAlpha(portalGlow, 0f);
        }

        private static void SetAlpha(SpriteRenderer renderer, float alpha)
        {
            if (renderer == null) return;
            Color color = renderer.color;
            color.a = alpha;
            renderer.color = color;
        }

        private void LogOnce(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reason == lastLoggedError) return;
            lastLoggedError = reason;
            Debug.LogError("Portal delivery: " + reason, this);
        }

        private void OnDrawGizmosSelected()
        {
            if (mouthAnchor == null || throatAnchor == null) return;
            Vector3 mouth = mouthAnchor.position;
            Vector3 throat = throatAnchor.position;

            // Servis hattı: her bardak önce buraya dikey yükselir, sonra ağza kayar.
            Gizmos.color = new Color(0.42f, 0.88f, 1f, 0.35f);
            Gizmos.DrawLine(mouth + Vector3.left * 6f, mouth + Vector3.right * 2f);

            Gizmos.color = new Color(0.42f, 0.88f, 1f, 0.9f);
            Gizmos.DrawLine(mouth, throat);
            Gizmos.DrawWireSphere(mouth, 0.14f);

            Gizmos.color = new Color(1f, 0.84f, 0.32f, 0.9f);
            Gizmos.DrawWireSphere(Vector3.LerpUnclamped(mouth, throat, entryDepth), 0.10f);

            Gizmos.color = new Color(0.62f, 0.35f, 0.95f, 0.9f);
            Gizmos.DrawWireSphere(throat, 0.07f);
        }
    }
}
