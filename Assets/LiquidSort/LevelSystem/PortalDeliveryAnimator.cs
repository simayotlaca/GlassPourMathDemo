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
    ///     0.00 - 0.32   bardak kendi rafından servis hattına DİKEY yükselir
    ///     0.32 - 0.50   hat boyunca kapının ağzına kayar, portal hafif parlar
    ///     0.50 - 0.74   kemerin arkasına girer; ölçek 1 -> 0.72, çok hafif eğilir
    ///     0.74 - 0.87   içeride 0.72 -> 0.35 küçülür ve tamamen gizlenir
    ///     0.74 - 0.87   ✓ rozeti yeşil-altın parıltılara ayrılır
    ///     0.87 - 1.03   portal kısa bir "tok" bounce yapar
    ///
    /// Yükselme dikey, kayma yatay — teslim edilen bardak rafından kalkıp servis
    /// hattına çıkar, sonra o hat boyunca kapıya kayar. Tek çapraz uçuş yerine bu
    /// ikisi, çünkü çapraz hareket bardağın hangi rafta durduğunu gizliyor.
    /// Yükselme süresi mesafeyle ölçeklenir: alt raftaki bardak üst raftakinden
    /// uzun yol gider, aynı ivmeyle biraz daha uzun sürer.
    ///
    /// This component owns nothing but the flight. It never creates a GameObject, never
    /// reparents the vessel and hands every borrowed value back — pose, draw orders and
    /// sorting layer — before it reports the glass as hidden, so the caller's pool can
    /// take the vessel back as if it had simply been switched off.
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
        [Tooltip("Bardağın arkasında beliren hız çizgileri; ağız hattına elle yerleştirilir. "
               + "Alpha'sı bu bileşenin malıdır: teslim dışında 0.")]
        [SerializeField] private SpriteRenderer travelStreak;
        [Tooltip("Rozet dağılırken oynatılan yeşil-altın parıltı. Boşsa atlanır.")]
        [SerializeField] private ParticleSystem badgeSparkles;
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

        [Header("Servis hattına yükselme")]
        [Tooltip("Bardağın kendi rafından servis hattına çıkışı. Bu süre TAM bir Lift "
               + "Full Height kadar yol giden bardağa aittir; daha kısa yol gidenler "
               + "aynı ivmeyle daha çabuk çıkar. 0 = kapalı, bardak durduğu yerden "
               + "doğrudan kapının ağzına kayar.")]
        [SerializeField, Min(0f)] private float liftDuration = 0.32f;
        [Tooltip("Süre ölçeğinin referansı: en alt raftan servis hattına olan dikey "
               + "mesafe. Rafın giriş animasyonundaki drop height ile aynı fikir.")]
        [SerializeField, Min(0.1f)] private float liftFullHeight = 6f;
        [Tooltip("Yükselirken bardağın raf tahtalarının, direklerin ve sipariş "
               + "kartlarının önüne alınması. Portal sandviçi ancak kemere girerken "
               + "devralır. 0 = kapalı.")]
        [SerializeField, Min(0)] private int liftSortingBoost = 60;
        [Tooltip("Kalkarken hafif küçülme. 1 = spec'e sadık, ölçek ilk 0.32 sn boyunca "
               + "sabit kalır.")]
        [SerializeField, Range(0.3f, 1f)] private float liftScale = 1f;
        [SerializeField] private Ease liftEase = Ease.OutCubic;

        [Header("Zamanlama (spec: yükselme + 0.18 / 0.42 / 0.55)")]
        [Tooltip("Bardak kapının ağzına kayarken geçen süre.")]
        [SerializeField, Min(0.01f)] private float approachDuration = 0.18f;
        [Tooltip("Kemerin arkasına girerken geçen süre. Bardağın büyük kısmı burada gizlenir.")]
        [SerializeField, Min(0.01f)] private float entryDuration = 0.24f;
        [Tooltip("İçeride küçülüp tamamen kaybolurken geçen süre.")]
        [SerializeField, Min(0.01f)] private float hideDuration = 0.13f;
        [Tooltip("Bardak gizlendikten sonraki portal bounce'u. 0 = kapalı.")]
        [SerializeField, Min(0f)] private float bounceDuration = 0.16f;
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

        [Header("Işık")]
        [Tooltip("Bardak ağza yaklaşırken portalın ulaştığı parlaklık.")]
        [SerializeField, Range(0f, 1f)] private float glowApproachAlpha = 0.45f;
        [Tooltip("Yutma anındaki kısa flaş.")]
        [SerializeField, Range(0f, 1f)] private float glowSwallowAlpha = 1f;
        [Tooltip("Hız çizgilerinin tepe alpha'sı.")]
        [SerializeField, Range(0f, 1f)] private float streakAlpha = 0.85f;

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

        /// <summary>
        /// True until the whole delivery beat, including the portal bounce, is complete.
        /// The portal is intentionally single-flight: accepting a second vessel during the
        /// first one's bounce would make both flights fight over the same glow and pivot.
        /// </summary>
        public bool IsPlaying => flights.Count > 0;

        /// <summary>
        /// Bardağın tamamen gizlendiği an. Yükselme mesafeyle ölçeklendiği için bu
        /// değer tam yükseklikten kalkan bardağın, yani en uzun ihtimalin süresidir.
        /// </summary>
        public float GlassHiddenTime =>
            liftDuration + approachDuration + entryDuration + hideDuration;

        /// <summary>Gizlenme artı portal bounce'u; sipariş kartı bundan sonra yenilenir.</summary>
        public float TotalDuration => GlassHiddenTime + bounceDuration;

        /// <summary>
        /// Rozet sağlayıcı, tipik olarak <see cref="DeliveryBadgePresenter"/>. Play'e
        /// açıkça rozet verilmediğinde buraya sorulur; o da yoksa serialized rozet
        /// kullanılır. Boş bırakılırsa ✓ dağılma beat'i sessizce atlanır.
        /// </summary>
        public IPortalCheckBadgeSource CheckBadgeSource { get; set; }

        private void Awake() => CapturePortalRest();

        private void OnDisable() => CancelAll();

        private void OnValidate()
        {
            approachDuration = Mathf.Max(0.01f, approachDuration);
            entryDuration = Mathf.Max(0.01f, entryDuration);
            hideDuration = Mathf.Max(0.01f, hideDuration);
            bounceDuration = Mathf.Max(0f, bounceDuration);
            entryScale = Mathf.Clamp(entryScale, 0.05f, 1f);
            hideScale = Mathf.Clamp(hideScale, 0.01f, entryScale);
            entryDepth = Mathf.Clamp01(entryDepth);
            liftDuration = Mathf.Max(0f, liftDuration);
            liftFullHeight = Mathf.Max(0.1f, liftFullHeight);
            liftSortingBoost = Mathf.Max(0, liftSortingBoost);
            liftScale = Mathf.Clamp(liftScale, 0.3f, 1f);
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
            SpriteRenderer streak,
            ParticleSystem sparkles,
            Transform checkBadge,
            Transform mouth,
            Transform throat)
        {
            CancelAll();
            backLayers = behindTheGlass ?? new SpriteRenderer[0];
            frontLayers = inFrontOfTheGlass ?? new SpriteRenderer[0];
            portalPivot = bouncePivot;
            portalGlow = glow;
            travelStreak = streak;
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
                                    float tiltDegrees)
        {
            liftDuration = Mathf.Max(0f, lift);
            liftFullHeight = Mathf.Max(0.1f, liftHeight);
            approachDuration = Mathf.Max(0.01f, approach);
            entryDuration = Mathf.Max(0.01f, entry);
            hideDuration = Mathf.Max(0.01f, hide);
            bounceDuration = Mathf.Max(0f, bounce);
            entryDepth = Mathf.Clamp01(depth);
            entryScale = Mathf.Clamp(scaleAtEntry, 0.05f, 1f);
            hideScale = Mathf.Clamp(scaleAtHide, 0.01f, entryScale);
            entryTilt = Mathf.Clamp(tiltDegrees, 0f, 20f);
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
                if (flight.Sequence != null && flight.Sequence.IsActive())
                    flight.Sequence.Kill();
                flight.Sequence = null;
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
            Vector3 mouth = mouthAnchor.position;
            Vector3 throat = throatAnchor.position;
            Vector3 entryPoint = Vector3.LerpUnclamped(mouth, throat, entryDepth);

            // Kemer sağdaysa bardak sağa yatar, solda ise sola: hangi tarafa teslim
            // edildiğini elle işaretlemeye gerek kalmadan kapıya doğru eğilmiş görünür.
            float tiltSign = throat.x >= mouth.x ? -1f : 1f;
            Quaternion tilted = Quaternion.AngleAxis(tiltSign * entryTilt, Vector3.forward)
                                * flight.HomeRotation;

            Vector3 railAnchor = new Vector3(
                flight.HomeMotionAnchorPosition.x, mouth.y, mouth.z);
            float liftTime = MeasureLift(flight.HomeMotionAnchorPosition.y, mouth.y);
            Vector3 travelScale = liftTime > 0f
                ? flight.HomeScale * liftScale
                : flight.HomeScale;
            Vector3 railRoot = RootPositionForAnchor(
                flight, railAnchor, flight.HomeRotation, travelScale);
            Vector3 mouthRoot = RootPositionForAnchor(
                flight, mouth, flight.HomeRotation, travelScale);
            Vector3 entryRoot = RootPositionForAnchor(
                flight, entryPoint, tilted, flight.HomeScale * entryScale);
            Vector3 throatRoot = RootPositionForAnchor(
                flight, throat, tilted, flight.HomeScale * hideScale);

            float mouthTime = liftTime + approachDuration;
            float entryTime = mouthTime + entryDuration;
            float hiddenTime = entryTime + hideDuration;

            Sequence sequence = DOTween.Sequence()
                .SetRecyclable(true)
                .SetTarget(root)
                .SetUpdate(useUnscaledTime);

            // 0.00 - 0.32 — rafından servis hattına yükselir. Bu beat olmadan alt raftaki
            // bir bardak yarım saniyede ekranın öbür ucuna ışınlanmış gibi okunuyordu.
            if (liftTime > 0f)
            {
                sequence.Append(root.DOMove(railRoot, liftTime)
                    .SetEase(liftEase).SetRecyclable(true));
                if (liftScale < 1f)
                    sequence.Join(root.DOScale(travelScale, liftTime)
                        .SetEase(liftEase).SetRecyclable(true));
            }

            // 0.32 - 0.50 — hat boyunca kapının ağzına kayar. Ölçek ve açı bilerek sabit:
            // bu ana kadar bardak hâlâ rafın önündeki tam boy bardak.
            // The first leg lifts the visual foot to the service rail; the second slides it
            // horizontally to the doorway. Different vessel pivots therefore share one
            // authored rail height without jumping or cutting diagonally through shelves.
            sequence.Append(root.DOMove(mouthRoot, approachDuration)
                .SetEase(approachEase).SetRecyclable(true));

            // 0.50 - 0.74 — kemerin arkasına girer. Konum yolun yarısından fazlasına gider,
            // yani öndeki occluder bardağı kenarlardan fiziksel olarak kapatmaya başlar.
            sequence.Append(root.DOMove(entryRoot, entryDuration)
                .SetEase(entryEase).SetRecyclable(true));
            sequence.Join(root.DOScale(flight.HomeScale * entryScale, entryDuration)
                .SetEase(entryEase).SetRecyclable(true));
            if (entryTilt > 0f)
                sequence.Join(root.DORotateQuaternion(tilted, entryDuration)
                    .SetEase(Ease.OutSine).SetRecyclable(true));

            // 0.74 - 0.87 — son küçülme İÇERİDE olur. Alpha'ya hiç dokunulmaz; bardağın
            // görünmez olmasının tek sebebi kemerin önündeki maske.
            sequence.Append(root.DOMove(throatRoot, hideDuration)
                .SetEase(hideEase).SetRecyclable(true));
            sequence.Join(root.DOScale(flight.HomeScale * hideScale, hideDuration)
                .SetEase(hideEase).SetRecyclable(true));

            InsertBadgeBreakup(sequence, flight, entryTime);
            if (flight.OwnsPortalChrome)
                InsertPortalChrome(sequence, liftTime, mouthTime, entryTime, hiddenTime);

            // Sandviç tam kemere girerken devralır. Ondan öncesi yükselme lifti: bardak
            // geçtiği rafların ve kartların önünde durmalı, portalın penceresinde değil.
            if (flight.LiftOffset != flight.PortalOffset)
                sequence.InsertCallback(mouthTime, () => EnterPortalSorting(flight));

            sequence.InsertCallback(hiddenTime, () => HideGlass(flight));
            sequence.OnComplete(() => FinishFlight(flight));
            return sequence;
        }

        private static Vector3 RootPositionForAnchor(Flight flight, Vector3 anchorPosition,
                                                     Quaternion worldRotation,
                                                     Vector3 localScale)
        {
            Vector3 parentScale = flight.Root.parent != null
                ? flight.Root.parent.lossyScale
                : Vector3.one;
            Vector3 worldScale = Vector3.Scale(parentScale, localScale);
            Vector3 rootToAnchor = worldRotation
                                 * Vector3.Scale(flight.MotionAnchorLocal, worldScale);
            return anchorPosition - rootToAnchor;
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

        private void EnterPortalSorting(Flight flight)
        {
            flight.InPortal = true;
            if (!flight.Hidden && flight.Glass != null) ApplyBorrowedSorting(flight);
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
        /// Glow, streak and the swallow bounce. Only the leading flight drives these: two
        /// deliveries overlapping would otherwise fight over one portal's alpha.
        /// </summary>
        private void InsertPortalChrome(Sequence sequence, float liftTime, float mouthTime,
                                        float entryTime, float hiddenTime)
        {
            // Portal, bardak hattına çıkana kadar sessiz. Yükselirken parlamaya başlarsa
            // ışık bardaktan önce davranmış oluyor ve teslim önceden belli oluyor.
            float slide = Mathf.Max(0.01f, mouthTime - liftTime);
            if (portalGlow != null)
            {
                sequence.Insert(liftTime, portalGlow.DOFade(glowApproachAlpha, slide)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
                sequence.Insert(entryTime, portalGlow
                    .DOFade(glowSwallowAlpha, Mathf.Max(0.01f, hideDuration * 0.45f))
                    .SetEase(Ease.OutQuad).SetRecyclable(true));
                sequence.Insert(hiddenTime, portalGlow
                    .DOFade(0f, Mathf.Max(0.01f, bounceDuration))
                    .SetEase(Ease.InSine).SetRecyclable(true));
            }

            if (travelStreak != null)
            {
                sequence.Insert(liftTime, travelStreak
                    .DOFade(streakAlpha, slide * 0.55f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true));
                sequence.Insert(entryTime, travelStreak.DOFade(0f, hideDuration)
                    .SetEase(Ease.InQuad).SetRecyclable(true));
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
            HideGlass(flight);
            flights.Remove(flight);
            flight.Sequence = null;

            Action finished = flight.OnFinished;
            flight.OnFinished = null;
            finished?.Invoke();
        }

        private void PlaySparkles(Flight flight)
        {
            if (badgeSparkles == null) return;
            if (flight.Badge != null)
                badgeSparkles.transform.position = flight.Badge.position;
            badgeSparkles.Play(true);
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
            flight.PortalLayerId = portalLayerId;
            flight.PortalOffset = frontFloor - 1 - highest;
            // Yükselirken bardak portalın penceresinde değil, geçtiği rafların önünde
            // olmalı. Boost 0 ise ya da yükselme kapalıysa sandviç en baştan geçerli.
            flight.LiftOffset = liftSortingBoost > 0 && liftDuration > 0f
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

        private void LateUpdate()
        {
            for (int i = 0; i < flights.Count; i++)
            {
                Flight flight = flights[i];
                if (flight.Hidden || flight.Glass == null) continue;
                ApplyBorrowedSorting(flight);
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
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.sortingLayerID = flight.BaseLayerIds[i];
                renderer.sortingOrder = flight.BaseOrders[i];
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
        /// Glow and streak alpha belong to this component, not to the artwork: whatever
        /// alpha the scene was saved with is pushed to zero once and only a delivery ever
        /// raises it. The bounce pivot's rest scale is remembered for the same reason.
        /// </summary>
        private void CapturePortalRest()
        {
            if (portalRestCaptured) return;
            portalRestCaptured = true;
            portalPivotRestScale = portalPivot != null ? portalPivot.localScale : Vector3.one;
            SetAlpha(portalGlow, 0f);
            SetAlpha(travelStreak, 0f);
        }

        private void RestorePortalRest()
        {
            if (!portalRestCaptured) return;
            if (portalGlow != null && DOTween.IsTweening(portalGlow)) portalGlow.DOKill();
            if (travelStreak != null && DOTween.IsTweening(travelStreak)) travelStreak.DOKill();
            if (portalPivot != null)
            {
                if (DOTween.IsTweening(portalPivot)) portalPivot.DOKill();
                portalPivot.localScale = portalPivotRestScale;
            }
            SetAlpha(portalGlow, 0f);
            SetAlpha(travelStreak, 0f);
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
