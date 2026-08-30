using System;
using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Binds the scene-independent Bartender level model to a hand-authored shelf scene.
    /// Every glass, plank and post is an Inspector reference. This component never creates
    /// a GameObject, instantiates a prefab or searches the scene at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderShelfLevelView : MonoBehaviour
    {
        public const int FullCampaignShotPoolSize = 4;
        public const int FullCampaignCocktailPoolSize = 5;
        public const int FullCampaignLattePoolSize = 6;
        public const int FullCampaignTumblerPoolSize = 8;
        /// <summary>
        /// Beş birimlik kulplu bardak için kampanyadaki eşzamanlı tepe kullanım.
        /// Level 30, on bir örneğin tamamını aynı anda sahnede tutuyor.
        /// </summary>
        public const int FullCampaignBiraPoolSize = 11;
        public const int MaximumActiveGlasses = 12;
        public const int MaximumColumnsPerRow = 4;

        [Serializable]
        public sealed class ShelfRowBinding
        {
            [Tooltip("The hand-authored plank renderer for this row, ordered top to bottom.")]
            public SpriteRenderer plank;
            [Tooltip("Hand-authored top-centre point where this row's glasses are seated.")]
            public Transform seatAnchor;
        }

        [Serializable]
        public sealed class ShelfSpanBinding
        {
            [Tooltip("The two hand-authored posts between two adjacent shelf planks.")]
            public SpriteRenderer leftPost;
            public SpriteRenderer rightPost;
        }

        [Serializable]
        public sealed class GlassBinding
        {
            [Tooltip("A scene-native RoyalGlassLab vessel root.")]
            public LiquidBottle bottle;
            [Tooltip("Hand-authored contact point at the visual centre of the glass foot.")]
            public Transform footAnchor;
            [Tooltip("Direct front-art reference used to centre the complete row silhouette.")]
            public SpriteRenderer placementRenderer;
            [Tooltip("Unscaled Royal source pose captured when the editor builder binds the pool.")]
            public Vector3 authoredLocalScale = Vector3.one;
            [Tooltip("Royal source rotation captured alongside the scale.")]
            public Quaternion authoredLocalRotation = Quaternion.identity;
        }

        private sealed class Actor
        {
            public LiquidBottle Bottle;
            public Transform FootAnchor;
            public SpriteRenderer PlacementRenderer;
            public GlassType Type;
            public Vector3 AuthoredLocalScale;
            public Quaternion AuthoredLocalRotation;
            public int GlassId = -1;
            public bool Assigned;

            /// <summary>
            /// World pose the layout pass decided on. The entrance and reseat animations
            /// only ever interpolate towards this value; they never invent a pose of their
            /// own, so cancelling one mid-flight lands on the same static layout.
            /// </summary>
            public Vector3 SeatPosition;
            public Vector3 SeatScale = Vector3.one;
            public bool Seated;
            public Vector3 PreviousSeatPosition;
            public Vector3 PreviousSeatScale = Vector3.one;
            public bool HasPreviousSeat;
            public int Row;
            public int Column;
            public float EntranceDelay;
            public float EntranceFallDistance;
            public float EntranceFallDuration;
            public bool SortingLifted;

            /// <summary>
            /// True while the delivery portal owns this vessel. The pool slot stays
            /// reserved and the release is deferred until the glass is actually hidden
            /// behind the arch, so nothing can switch it off mid-flight.
            /// </summary>
            public bool Delivering;
        }

        [Header("Level source")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Scene coordinate space")]
        [Tooltip("All configured surface heights and glass positions are local to this transform.")]
        [SerializeField] private Transform layoutSpace;

        [Header("Scene-native Royal glass pools")]
        [Tooltip("Full campaign maximum: 4. Each reference must use ShotRoyal.")]
        [SerializeField] private List<GlassBinding> shotPool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 5. Each reference must use CocktailRoyal.")]
        [SerializeField] private List<GlassBinding> cocktailPool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 6. Each reference must use MugRoyal.")]
        [SerializeField] private List<GlassBinding> lattePool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 8. Each reference must use TumblerRoyal.")]
        [SerializeField] private List<GlassBinding> tumblerPool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 11. Each reference must use BeerRoyal.")]
        [SerializeField] private List<GlassBinding> biraPool = new List<GlassBinding>();

        [Header("Hand-authored shelf references")]
        [Tooltip("Exactly three possible plank rows, ordered top to bottom.")]
        [SerializeField] private ShelfRowBinding[] shelfRows = new ShelfRowBinding[3];
        [Tooltip("Exactly two possible spans: row 1-2, then row 2-3. Four post renderers total.")]
        [SerializeField] private ShelfSpanBinding[] shelfSpans = new ShelfSpanBinding[2];

        [Header("Balanced layout")]
        [Tooltip("Top/bottom shelf-surface heights while two rows are visible.")]
        [SerializeField] private Vector2 twoRowSurfaceY = new Vector2(0.80f, -4.15f);
        [Tooltip("Top/middle/bottom shelf-surface heights while three rows are visible.")]
        [SerializeField] private Vector3 threeRowSurfaceY = new Vector3(1.65f, -1.30f, -4.25f);
        [Tooltip("Centre-to-centre spacing while a row contains two glasses.")]
        [SerializeField, Min(0.1f)] private float twoAcrossColumnSpacing = 2.60f;
        [Tooltip("Centre-to-centre spacing while a row contains three glasses.")]
        [SerializeField, Min(0.1f)] private float threeAcrossColumnSpacing = 2.18f;
        [Tooltip("Centre-to-centre spacing while a row contains four glasses.")]
        [SerializeField, Min(0.1f)] private float compactColumnSpacing = 1.60f;
        // Ölçek iki eksenli bir tablodur: KAÇ SATIR (yükseklik bütçesi) x KAÇ SÜTUN
        // (genişlik bütçesi). Tek bir "dört sütunlu" değeri iki satırlık bir levelı da
        // üç satırlık bir levelın dar yüksekliğine mahkûm ediyordu; 30 levelin 7'si tam
        // olarak o durumda (iki satır, dörderli diziliş) ve bardakları gereksiz küçüktü.
        [Tooltip("Royal scale multiplier for rows of up to three glasses in the two-row layout.")]
        [SerializeField, Min(0.1f)] private float twoRowSpaciousGlassScale = 1.27f;
        [Tooltip("Royal scale multiplier for rows of up to three glasses in the three-row layout.")]
        [SerializeField, Min(0.1f)] private float threeRowSpaciousGlassScale = 1.00f;
        [Tooltip("Royal scale multiplier for a four-across row inside the two-row layout.")]
        [SerializeField, Min(0.1f)] private float fourAcrossGlassScale = 0.90f;
        [Tooltip("Royal scale multiplier for a four-across row inside the three-row layout.")]
        [SerializeField, Min(0.1f)] private float fourAcrossThreeRowGlassScale = 0.80f;
        [Tooltip("Small optical overlap that seats the vessel artwork into the plank.")]
        [SerializeField, Min(0f)] private float opticalSeatInset = 0.02f;
        [SerializeField] private float glassPlaneZ;

        [Header("Post fitting")]
        [Tooltip("Share of the upper plank height hidden behind a vertical post.")]
        [SerializeField, Range(0f, 1.5f)] private float postUpperPlankOverlap = 0.92f;
        [Tooltip("Small overlap above the lower shelf surface.")]
        [SerializeField, Min(0f)] private float postLowerShelfInset = 0.02f;

        [Header("Level entrance animation")]
        [Tooltip("Level yüklendiğinde raf ve bardaklar animasyonla yerine otursun. "
               + "Kapalıyken sunum bugünkü gibi tek karede yerleşir.")]
        [SerializeField] private bool animateEntrance = true;
        [Tooltip("Bardakların bırakıldığı çizgi: en üst raf yüzeyinin kaç layout birimi "
               + "üstü. Kameranın üst kenarını aşacak kadar büyük olmalı, yoksa bardaklar "
               + "ekranın ortasında yoktan var olur.")]
        [SerializeField, Min(0.1f)] private float entranceDropHeight = 6.00f;
        [Tooltip("Tam bir düşüş yüksekliği kadar düşen bardağın süresi. Alt raftaki "
               + "bardaklar daha uzun yol gittiği için aynı ivmeyle biraz daha uzun düşer.")]
        [SerializeField, Min(0.01f)] private float entranceDropDuration = 0.32f;
        [Tooltip("Aynı sırada yan yana iki bardak arasındaki gecikme.")]
        [SerializeField, Min(0f)] private float entranceGlassStagger = 0.055f;
        [Tooltip("İki raf sırası arasındaki ek gecikme. Alt sıra önce dolar.")]
        [SerializeField, Min(0f)] private float entranceRowStagger = 0.12f;
        [Tooltip("Düşerken bardağın raf tahtalarının önüne alınma miktarı. Plank "
               + "sorting order'ının üstüne çıkacak kadar büyük olmalı; iniş anında "
               + "bardağın kendi sıralaması geri verilir. 0 = kapalı.")]
        [SerializeField, Min(0)] private int entranceSortingBoost = 60;
        [Tooltip("Rafa çarpma anındaki ezilme oranı. 0 = kapalı.")]
        [SerializeField, Range(0f, 0.4f)] private float entranceLandingSquash = 0.13f;
        [Tooltip("Ezilmeden normal ölçüye dönüş süresi.")]
        [SerializeField, Min(0f)] private float entranceSettleDuration = 0.20f;
        [Tooltip("Raf tahtalarının ve direklerin açılma süresi. 0 = anında görünür.")]
        [SerializeField, Min(0f)] private float shelfFadeDuration = 0.22f;
        [Tooltip("Level ortasında bardaklar yeniden dizilirken kayma süresi. 0 = anında.")]
        [SerializeField, Min(0f)] private float reseatDuration = 0.22f;

        [Header("Teslim geçidi")]
        [Tooltip("Karşılanan siparişin bardağını kemerin arkasına sokan servis geçidi. "
               + "Boşken teslim edilen bardak bugünkü gibi tek karede havuza döner.")]
        [SerializeField] private PortalDeliveryAnimator deliveryPortal;

        [Header("Layer presentation")]
        [SerializeField] private Color hiddenLayerColor =
            new Color(0.29f, 0.31f, 0.36f, 1f);

        private readonly List<Actor> actors = new List<Actor>(
            FullCampaignShotPoolSize + FullCampaignCocktailPoolSize
            + FullCampaignLattePoolSize + FullCampaignTumblerPoolSize
            + FullCampaignBiraPoolSize);
        private readonly List<Actor> activeActors = new List<Actor>(MaximumActiveGlasses);
        private readonly Dictionary<int, Actor> actorByGlassId =
            new Dictionary<int, Actor>(MaximumActiveGlasses);
        private readonly Dictionary<LiquidBottle, int> glassIdByBottle =
            new Dictionary<LiquidBottle, int>(MaximumActiveGlasses);
        private readonly List<Color> colorScratch = new List<Color>(LiquidBottle.MaxBands);
        private readonly HashSet<LiquidBottle> uniqueBottleScratch =
            new HashSet<LiquidBottle>();
        private readonly List<Actor> entranceOrder = new List<Actor>(MaximumActiveGlasses);
        private readonly List<SpriteRenderer> shelfFadeRenderers =
            new List<SpriteRenderer>(10);
        private readonly List<Color> shelfFadeColors = new List<Color>(10);
        private readonly List<Actor> deliveringActors = new List<Actor>(2);
        private readonly List<Actor> deliveryScratch = new List<Actor>(2);
        private Coroutine seatAnimation;
        private object synchronizationDeferralOwner;
        private bool deferredSynchronizationPending;

        /// <summary>
        /// Glass the controller reported as delivered on the notification that immediately
        /// precedes the board change. It is the only way to tell a vessel that was served
        /// from one that left the board for any other reason.
        /// </summary>
        private int pendingDeliveryGlassId = -1;

        private BartenderLevelController subscribedController;
        private BsLevel presentedLevel;
        private bool actorCacheBuilt;
        private int configuredColumns = 1;
        private int configuredRowCount;
        private string lastLoggedError;

        public BartenderLevelController Controller => controller;
        public bool Ready { get; private set; }
        public string LastError { get; private set; }
        public int ActiveGlassCount => activeActors.Count;
        public int VisibleShelfRows => configuredRowCount;
        /// <summary>True while glasses are still walking towards their seats.</summary>
        public bool SeatAnimationPlaying => seatAnimation != null;
        /// <summary>
        /// True through the complete delivery beat, including the portal bounce after the
        /// glass has already gone back to its pool.
        /// </summary>
        public bool DeliveryPlaying => deliveringActors.Count > 0
                                    || (deliveryPortal != null && deliveryPortal.IsPlaying);
        /// <summary>The directly serialized portal used by this view.</summary>
        public PortalDeliveryAnimator DeliveryPortal => deliveryPortal;
        /// <summary>True while an already committed board change is animating.</summary>
        public bool SynchronizationDeferred => synchronizationDeferralOwner != null;

        public event Action PresentationChanged;
        public event Action<string> PresentationRejected;
        /// <summary>Raised after the glass is hidden and the portal's final bounce settles.</summary>
        public event Action DeliveryPresentationFinished;

        private Transform LayoutSpace => layoutSpace != null ? layoutSpace : transform;

        private void Awake()
        {
            EnsureActorCache(out _);
        }

        private void OnEnable()
        {
            Subscribe();
            if (!Application.isPlaying) return;
            if (controller != null && controller.CurrentLevel != null)
                RefreshFromController();
            else
                ClearPresentation();
        }

        private void OnDisable()
        {
            Unsubscribe();
            synchronizationDeferralOwner = null;
            deferredSynchronizationPending = false;
            if (Application.isPlaying) ClearPresentation();
        }

        private void OnValidate()
        {
            twoAcrossColumnSpacing = Mathf.Max(0.1f, twoAcrossColumnSpacing);
            threeAcrossColumnSpacing = Mathf.Max(0.1f, threeAcrossColumnSpacing);
            compactColumnSpacing = Mathf.Max(0.1f, compactColumnSpacing);
            twoRowSpaciousGlassScale = Mathf.Max(0.1f, twoRowSpaciousGlassScale);
            threeRowSpaciousGlassScale = Mathf.Max(0.1f, threeRowSpaciousGlassScale);
            fourAcrossGlassScale = Mathf.Max(0.1f, fourAcrossGlassScale);
            fourAcrossThreeRowGlassScale = Mathf.Max(0.1f, fourAcrossThreeRowGlassScale);
            opticalSeatInset = Mathf.Max(0f, opticalSeatInset);
            postLowerShelfInset = Mathf.Max(0f, postLowerShelfInset);
            entranceDropHeight = Mathf.Max(0.1f, entranceDropHeight);
            entranceDropDuration = Mathf.Max(0.01f, entranceDropDuration);
            entranceGlassStagger = Mathf.Max(0f, entranceGlassStagger);
            entranceRowStagger = Mathf.Max(0f, entranceRowStagger);
            entranceSettleDuration = Mathf.Max(0f, entranceSettleDuration);
            shelfFadeDuration = Mathf.Max(0f, shelfFadeDuration);
            reseatDuration = Mathf.Max(0f, reseatDuration);
        }

        /// <summary>
        /// Rebuilds the view from the controller's detached board snapshot. Useful when a
        /// scene enables this component after the controller has already loaded a level.
        /// </summary>
        public bool RefreshFromController()
        {
            if (controller == null)
                return Reject("BartenderLevelController Inspector referansı eksik.");
            BsLevel level = controller.CurrentLevel;
            BsBoard snapshot = controller.Board;
            if (level == null || snapshot == null)
            {
                ClearPresentation();
                LastError = "Yüklü level yok.";
                return false;
            }

            if (!ReferenceEquals(presentedLevel, level) || !Ready)
                return TryPresent(level, snapshot, controller.Palette);
            return TrySynchronize(snapshot, controller.Palette);
        }

        /// <summary>
        /// Authoring API for an editor builder. The passed objects remain ordinary scene
        /// objects; assigning them here only writes the same references an artist would
        /// drag into the Inspector. No object is instantiated or discovered.
        /// </summary>
        public void ConfigureSceneBindings(
            BartenderLevelController levelController,
            Transform sceneLayoutSpace,
            IReadOnlyList<GlassBinding> shots,
            IReadOnlyList<GlassBinding> cocktails,
            IReadOnlyList<GlassBinding> lattes,
            IReadOnlyList<GlassBinding> tumblers,
            IReadOnlyList<GlassBinding> biras,
            SpriteRenderer topPlank,
            SpriteRenderer middlePlank,
            SpriteRenderer bottomPlank,
            Transform topSeatAnchor,
            Transform middleSeatAnchor,
            Transform bottomSeatAnchor,
            SpriteRenderer upperLeftPost,
            SpriteRenderer upperRightPost,
            SpriteRenderer lowerLeftPost,
            SpriteRenderer lowerRightPost)
        {
            if (Application.isPlaying) ClearPresentation();
            Unsubscribe();

            controller = levelController;
            layoutSpace = sceneLayoutSpace;
            CopyPool(shots, shotPool);
            CopyPool(cocktails, cocktailPool);
            CopyPool(lattes, lattePool);
            CopyPool(tumblers, tumblerPool);
            CopyPool(biras, biraPool);
            shelfRows = new[]
            {
                new ShelfRowBinding { plank = topPlank, seatAnchor = topSeatAnchor },
                new ShelfRowBinding { plank = middlePlank, seatAnchor = middleSeatAnchor },
                new ShelfRowBinding { plank = bottomPlank, seatAnchor = bottomSeatAnchor }
            };
            shelfSpans = new[]
            {
                new ShelfSpanBinding
                {
                    leftPost = upperLeftPost,
                    rightPost = upperRightPost
                },
                new ShelfSpanBinding
                {
                    leftPost = lowerLeftPost,
                    rightPost = lowerRightPost
                }
            };

            ResetActorCache();
            if (Application.isPlaying && isActiveAndEnabled)
            {
                Subscribe();
                if (controller != null && controller.CurrentLevel != null)
                    RefreshFromController();
                else
                    ClearPresentation();
            }
        }

        /// <summary>
        /// Authoring API for the delivery portal, mirroring ConfigureSceneBindings. Passing
        /// null restores today's behaviour: a served glass returns to its pool at once.
        /// </summary>
        public void ConfigureDeliveryPortal(PortalDeliveryAnimator portal)
        {
            if (Application.isPlaying) CancelPortalDeliveries();
            deliveryPortal = portal;
        }

        /// <summary>
        /// Authoring API for the pixel-audited layout values. Like the binding API, these
        /// values are serialized into the scene after an editor builder calls this method.
        /// </summary>
        public void ConfigureLayout(
            Vector2 twoRowSurfaces,
            Vector3 threeRowSurfaces,
            float spacingAtTwo,
            float spacingAtThree,
            float spacingAtFour,
            float twoRowScaleUpToThree,
            float threeRowScaleUpToThree,
            float scaleAtFourInTwoRows,
            float scaleAtFourInThreeRows,
            float seatInset,
            float planeZ = 0f)
        {
            twoRowSurfaceY = twoRowSurfaces;
            threeRowSurfaceY = threeRowSurfaces;
            twoAcrossColumnSpacing = Mathf.Max(0.1f, spacingAtTwo);
            threeAcrossColumnSpacing = Mathf.Max(0.1f, spacingAtThree);
            compactColumnSpacing = Mathf.Max(0.1f, spacingAtFour);
            twoRowSpaciousGlassScale = Mathf.Max(0.1f, twoRowScaleUpToThree);
            threeRowSpaciousGlassScale = Mathf.Max(0.1f, threeRowScaleUpToThree);
            fourAcrossGlassScale = Mathf.Max(0.1f, scaleAtFourInTwoRows);
            fourAcrossThreeRowGlassScale = Mathf.Max(0.1f, scaleAtFourInThreeRows);
            opticalSeatInset = Mathf.Max(0f, seatInset);
            glassPlaneZ = planeZ;
        }

        /// <summary>
        /// Authoring API for the level-entrance timing, mirroring ConfigureLayout. An editor
        /// builder bakes these into the scene; nothing here changes where a glass ends up.
        /// </summary>
        public void ConfigureEntrance(
            bool animate,
            float dropHeight,
            float dropDuration,
            float glassStagger,
            float rowStagger,
            int sortingBoost,
            float landingSquash,
            float settleDuration,
            float shelfFade,
            float reseat)
        {
            animateEntrance = animate;
            entranceDropHeight = Mathf.Max(0.1f, dropHeight);
            entranceDropDuration = Mathf.Max(0.01f, dropDuration);
            entranceGlassStagger = Mathf.Max(0f, glassStagger);
            entranceRowStagger = Mathf.Max(0f, rowStagger);
            entranceSortingBoost = Mathf.Max(0, sortingBoost);
            entranceLandingSquash = Mathf.Clamp(landingSquash, 0f, 0.4f);
            entranceSettleDuration = Mathf.Max(0f, settleDuration);
            shelfFadeDuration = Mathf.Max(0f, shelfFade);
            reseatDuration = Mathf.Max(0f, reseat);
        }

        /// <summary>
        /// Applies one detached level/board snapshot to the serialized scene pools.
        /// No domain object is retained or mutated.
        /// </summary>
        public bool TryPresent(BsLevel level, BsBoard snapshot, BsPalette palette)
        {
            if (!ValidateSnapshot(level, snapshot, palette, out int rows, out string reason))
                return Reject(reason);

            StopSeatAnimation();
            ClearAssignments();
            presentedLevel = level;
            configuredColumns = Mathf.Max(1, level.ColumnsPerRow);
            configuredRowCount = rows;

            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                Actor actor = Acquire(glass.Type);
                if (actor == null)
                    return Reject($"{BsRules.DisplayName(glass.Type)} scene havuzu tükendi.");

                actor.Assigned = true;
                actor.GlassId = glass.Id;
                actorByGlassId.Add(glass.Id, actor);
                glassIdByBottle.Add(actor.Bottle, glass.Id);
                activeActors.Add(actor);
                SetContents(actor.Bottle, glass, palette);
            }

            ApplyShelfLayout(configuredRowCount);
            LayoutActiveActors();
            ActivateAndRefreshActors();
            PlayEntrance();

            Ready = true;
            LastError = null;
            lastLoggedError = null;
            PresentationChanged?.Invoke();
            return true;
        }

        /// <summary>Maps a hand-authored scene glass back to its current domain id.</summary>
        public bool TryGetGlassId(LiquidBottle bottle, out int glassId)
        {
            if (bottle != null && glassIdByBottle.TryGetValue(bottle, out glassId))
                return true;
            glassId = -1;
            return false;
        }

        /// <summary>Maps a current domain id to its hand-authored scene glass.</summary>
        public bool TryGetBottle(int glassId, out LiquidBottle bottle)
        {
            if (actorByGlassId.TryGetValue(glassId, out Actor actor)
                && actor != null && actor.Bottle != null)
            {
                bottle = actor.Bottle;
                return true;
            }
            bottle = null;
            return false;
        }

        /// <summary>
        /// Is there still an unassigned scene vessel of this type? The +glass booster asks
        /// before it commits: the pool is a hand-authored, finite set of scene objects, and
        /// a domain glass with no vessel behind it rejects the whole presentation.
        /// </summary>
        public bool HasFreePoolSlot(GlassType type)
        {
            if (!EnsureActorCache(out _)) return false;
            return Acquire(type) != null;
        }

        /// <summary>
        /// Keeps the currently rendered bottle contents at their pre-command state while an
        /// already committed board revision is animated. Board notifications received during
        /// the deferral are coalesced into one authoritative refresh at the end.
        /// </summary>
        public bool TryBeginSynchronizationDeferral(object owner)
        {
            if (owner == null || synchronizationDeferralOwner != null) return false;
            synchronizationDeferralOwner = owner;
            deferredSynchronizationPending = false;
            return true;
        }

        public bool IsSynchronizationDeferredBy(object owner) =>
            owner != null && ReferenceEquals(synchronizationDeferralOwner, owner);

        /// <summary>
        /// Ends a deferral owned by <paramref name="owner"/> and reconciles the scene from
        /// the controller snapshot. Safe to call after either a completed or cancelled pour.
        /// </summary>
        public bool EndSynchronizationDeferralAndRefresh(object owner,
                                                          bool forceRefresh = false)
        {
            if (owner == null || !ReferenceEquals(synchronizationDeferralOwner, owner))
                return false;

            bool refresh = forceRefresh || deferredSynchronizationPending;
            synchronizationDeferralOwner = null;
            deferredSynchronizationPending = false;
            if (!refresh) return true;
            if (!isActiveAndEnabled) return false;
            return RefreshFromController();
        }

        /// <summary>
        /// Hit-tests only the active, domain-bound Royal vessels. The target scene therefore
        /// needs no colliders or generated input objects; its camera may be injected by the
        /// host or resolved from <see cref="Camera.main"/>.
        /// </summary>
        public bool TryPickBottle(Camera camera, Vector2 screenPoint, float padding,
                                  out LiquidBottle bottle, out int glassId)
        {
            bottle = null;
            glassId = -1;
            if (!Ready || camera == null) return false;

            float safePadding = Mathf.Max(0f, padding);
            float bestDistance = float.MaxValue;
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                LiquidBottle candidate = actor != null ? actor.Bottle : null;
                if (candidate == null || actor.Delivering
                    || !candidate.gameObject.activeInHierarchy)
                    continue;

                float depth = Vector3.Dot(candidate.transform.position
                                          - camera.transform.position,
                                          camera.transform.forward);
                Vector3 world = camera.ScreenToWorldPoint(
                    new Vector3(screenPoint.x, screenPoint.y, depth));
                SpriteRenderer placement = actor.PlacementRenderer;
                if (placement == null) continue;
                Bounds bounds = placement.bounds;
                Vector3 scale = candidate.transform.lossyScale;
                float worldPadding = safePadding * Mathf.Max(
                    Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                if (world.x < bounds.min.x - worldPadding
                    || world.x > bounds.max.x + worldPadding
                    || world.y < bounds.min.y - worldPadding
                    || world.y > bounds.max.y + worldPadding)
                    continue;

                float distance = Mathf.Abs(world.x - bounds.center.x);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bottle = candidate;
                glassId = actor.GlassId;
            }
            return bottle != null && glassId >= 0;
        }

        /// <summary>
        /// Strict check for the largest allocation of every glass type in the 30-level
        /// campaign and all three possible shelf rows. Bira joined the supported set once
        /// its capacity-five Royal scene pool existed; nothing is fail-closed any more.
        /// </summary>
        public bool ValidateFullCampaignBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (layoutSpace == null)
            {
                reason = "Layout Space Inspector referansı eksik.";
                return false;
            }
            if (!ValidatePoolCount(shotPool, FullCampaignShotPoolSize, GlassType.Shot,
                    out reason)
                || !ValidatePoolCount(cocktailPool, FullCampaignCocktailPoolSize,
                    GlassType.Kadeh, out reason)
                || !ValidatePoolCount(lattePool, FullCampaignLattePoolSize,
                    GlassType.Latte, out reason)
                || !ValidatePoolCount(tumblerPool, FullCampaignTumblerPoolSize,
                    GlassType.Tumbler, out reason)
                || !ValidatePoolCount(biraPool, FullCampaignBiraPoolSize,
                    GlassType.Bira, out reason))
                return false;

            if (!EnsureActorCache(out reason)) return false;
            if (!ValidateShelfBindings(3, out reason)) return false;
            reason = null;
            return true;
        }

        [ContextMenu("Validate Full Campaign Bindings")]
        private void ValidateFullCampaignBindingsFromContextMenu()
        {
            if (ValidateFullCampaignBindings(out string reason))
                Debug.Log("Bartender shelf: full campaign bindings are valid for all five "
                        + "glass types.", this);
            else
                Debug.LogError("Bartender shelf binding error: " + reason, this);
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.BoardChanged += HandleBoardChanged;
            subscribedController.StateChanged += HandleStateChanged;
            subscribedController.Delivered += HandleDelivered;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.BoardChanged -= HandleBoardChanged;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.Delivered -= HandleDelivered;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            if (synchronizationDeferralOwner != null)
            {
                deferredSynchronizationPending = true;
                return;
            }
            BsBoard snapshot = controller != null ? controller.Board : null;
            TryPresent(level, snapshot, controller != null ? controller.Palette : null);
        }

        /// <summary>
        /// Delivered always fires just before BoardChanged, so the id noted here is still
        /// valid when the synchronise pass below decides which vessels leave the shelf.
        /// </summary>
        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            pendingDeliveryGlassId = receipt != null && receipt.DeliveredGlass != null
                ? receipt.DeliveredGlass.Id
                : -1;
        }

        private void HandleBoardChanged()
        {
            if (synchronizationDeferralOwner != null)
            {
                deferredSynchronizationPending = true;
                return;
            }
            if (!Ready || controller == null || controller.CurrentLevel == null) return;
            TrySynchronize(controller.Board, controller.Palette);
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
                ClearPresentation();
        }

        private bool TrySynchronize(BsBoard snapshot, BsPalette palette)
        {
            if (snapshot == null) return Reject("Board snapshot boş.");
            if (palette == null || palette.Count == 0)
                return Reject("BsPalette Inspector/Resources bağlantısı eksik veya boş.");

            // Consumed here rather than left standing: a rejected or repeated synchronise
            // must not be able to fly a second glass through the arch.
            int deliveredGlassId = pendingDeliveryGlassId;
            pendingDeliveryGlassId = -1;

            activeActors.Clear();
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (!actorByGlassId.TryGetValue(glass.Id, out Actor actor))
                {
                    actor = Acquire(glass.Type);
                    if (actor == null)
                        return Reject($"{BsRules.DisplayName(glass.Type)} scene havuzu tükendi.");
                    actor.Assigned = true;
                    actor.GlassId = glass.Id;
                    actorByGlassId.Add(glass.Id, actor);
                    glassIdByBottle.Add(actor.Bottle, glass.Id);
                }
                else if (actor.Type != glass.Type)
                {
                    return Reject($"Bardak {glass.Id} tipi runtime sırasında değişti; "
                                + "sunum güvenli olarak temizlendi.");
                }

                actor.Assigned = true;
                activeActors.Add(actor);
                SetContents(actor.Bottle, glass, palette);
            }

            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (!actor.Assigned || actor.Delivering
                    || ContainsActor(activeActors, actor)) continue;
                if (!TryBeginPortalDelivery(actor, deliveredGlassId)) Release(actor);
            }

            int neededRows = RowCountFor(activeActors.Count, configuredColumns);
            // Shelf count is a level-layout decision. Deliveries re-balance the remaining
            // actors inside those rows but do not collapse the furniture mid-level.
            if (neededRows > configuredRowCount)
            {
                if (neededRows > 3)
                    return Reject("Board üç satırlık statik raf kapasitesini aşıyor.");
                if (!ValidateShelfBindings(neededRows, out string reason))
                    return Reject(reason);
                configuredRowCount = neededRows;
                ApplyShelfLayout(configuredRowCount);
            }

            LayoutActiveActors();
            ActivateAndRefreshActors();
            PlayReseat();
            PresentationChanged?.Invoke();
            return true;
        }

        private bool ValidateSnapshot(BsLevel level, BsBoard snapshot, BsPalette palette,
                                      out int rowCount, out string reason)
        {
            rowCount = 0;
            if (level == null)
            {
                reason = "Level asseti boş.";
                return false;
            }
            if (snapshot == null)
            {
                reason = "Board snapshot boş.";
                return false;
            }
            if (palette == null || palette.Count == 0)
            {
                reason = "BsPalette Inspector/Resources bağlantısı eksik veya boş.";
                return false;
            }
            if (level.ColumnsPerRow <= 0 || level.ColumnsPerRow > MaximumColumnsPerRow)
            {
                reason = $"ColumnsPerRow 1-{MaximumColumnsPerRow} aralığında olmalı.";
                return false;
            }
            if (snapshot.Glasses.Count > MaximumActiveGlasses)
            {
                reason = $"Level {snapshot.Glasses.Count} bardak istiyor; statik aktif slot sınırı "
                       + $"{MaximumActiveGlasses}.";
                return false;
            }

            int shots = 0;
            int cocktails = 0;
            int lattes = 0;
            int tumblers = 0;
            int biras = 0;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null)
                {
                    reason = $"Board bardak {i} boş.";
                    return false;
                }
                switch (glass.Type)
                {
                    case GlassType.Shot: shots++; break;
                    case GlassType.Kadeh: cocktails++; break;
                    case GlassType.Latte: lattes++; break;
                    case GlassType.Tumbler: tumblers++; break;
                    case GlassType.Bira: biras++; break;
                    default:
                        reason = $"Desteklenmeyen bardak tipi: {(int)glass.Type}.";
                        return false;
                }
            }

            if (!ValidatePoolCount(shotPool, shots, GlassType.Shot, out reason)
                || !ValidatePoolCount(cocktailPool, cocktails, GlassType.Kadeh, out reason)
                || !ValidatePoolCount(lattePool, lattes, GlassType.Latte, out reason)
                || !ValidatePoolCount(tumblerPool, tumblers, GlassType.Tumbler, out reason)
                || !ValidatePoolCount(biraPool, biras, GlassType.Bira, out reason))
                return false;
            if (!EnsureActorCache(out reason)) return false;

            rowCount = RowCountFor(snapshot.Glasses.Count, level.ColumnsPerRow);
            if (rowCount > 3)
            {
                reason = "Level üç satırlık statik raf kapasitesini aşıyor.";
                return false;
            }
            if (!ValidateShelfBindings(rowCount, out reason)) return false;
            reason = null;
            return true;
        }

        private bool EnsureActorCache(out string reason)
        {
            if (actorCacheBuilt)
            {
                reason = null;
                return true;
            }

            actors.Clear();
            uniqueBottleScratch.Clear();
            if (!AddPoolToCache(shotPool, GlassType.Shot, out reason)
                || !AddPoolToCache(cocktailPool, GlassType.Kadeh, out reason)
                || !AddPoolToCache(lattePool, GlassType.Latte, out reason)
                || !AddPoolToCache(tumblerPool, GlassType.Tumbler, out reason)
                || !AddPoolToCache(biraPool, GlassType.Bira, out reason))
                return false;

            actorCacheBuilt = true;
            reason = null;
            return true;
        }

        private void ResetActorCache()
        {
            actorCacheBuilt = false;
            actors.Clear();
            activeActors.Clear();
            actorByGlassId.Clear();
            glassIdByBottle.Clear();
            presentedLevel = null;
            Ready = false;
        }

        private static void CopyPool(IReadOnlyList<GlassBinding> source,
                                     List<GlassBinding> destination)
        {
            destination.Clear();
            if (source == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                GlassBinding binding = source[i];
                destination.Add(new GlassBinding
                {
                    bottle = binding != null ? binding.bottle : null,
                    footAnchor = binding != null ? binding.footAnchor : null,
                    placementRenderer = binding != null ? binding.placementRenderer : null,
                    authoredLocalScale = binding != null
                        ? binding.authoredLocalScale
                        : Vector3.one,
                    authoredLocalRotation = binding != null
                        ? binding.authoredLocalRotation
                        : Quaternion.identity
                });
            }
        }

        private bool AddPoolToCache(List<GlassBinding> pool, GlassType type,
                                    out string reason)
        {
            if (pool == null)
            {
                reason = BsRules.DisplayName(type) + " havuzu null.";
                return false;
            }
            for (int i = 0; i < pool.Count; i++)
            {
                GlassBinding binding = pool[i];
                if (!ValidateBottle(binding, type, i, out reason)) return false;
                LiquidBottle bottle = binding.bottle;
                if (!uniqueBottleScratch.Add(bottle))
                {
                    reason = $"'{bottle.name}' birden fazla scene havuzuna bağlanmış.";
                    return false;
                }
                actors.Add(new Actor
                {
                    Bottle = bottle,
                    FootAnchor = binding.footAnchor,
                    PlacementRenderer = binding.placementRenderer,
                    Type = type,
                    AuthoredLocalScale = binding.authoredLocalScale,
                    AuthoredLocalRotation = binding.authoredLocalRotation
                });
            }
            reason = null;
            return true;
        }

        private static bool ValidatePoolCount(List<GlassBinding> pool, int required,
                                              GlassType type, out string reason)
        {
            int count = pool != null ? pool.Count : 0;
            if (count < required)
            {
                reason = $"{BsRules.DisplayName(type)} havuzunda {required} scene objesi "
                       + $"gerekiyor, {count} bağlı.";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool ValidateBottle(GlassBinding binding, GlassType type, int index,
                                           out string reason)
        {
            LiquidBottle bottle = binding != null ? binding.bottle : null;
            if (bottle == null)
            {
                reason = $"{BsRules.DisplayName(type)} havuzu [{index}] boş.";
                return false;
            }
            if (binding.footAnchor == null
                || !binding.footAnchor.IsChildOf(bottle.transform))
            {
                reason = $"'{bottle.name}' doğrudan bağlı taban temas noktası eksik.";
                return false;
            }
            Vector3 authoredScale = binding.authoredLocalScale;
            if (Mathf.Abs(authoredScale.x) < 0.0001f
                || Mathf.Abs(authoredScale.y) < 0.0001f
                || Mathf.Abs(authoredScale.z) < 0.0001f)
            {
                reason = $"'{bottle.name}' serialized authored scale değeri geçersiz.";
                return false;
            }
            if (bottle.profile == null || !bottle.profile.IsBaked
                || bottle.profile.front == null)
            {
                reason = $"'{bottle.name}' bake edilmiş Royal profile/front taşımıyor.";
                return false;
            }
            if (binding.placementRenderer == null
                || !binding.placementRenderer.transform.IsChildOf(bottle.transform)
                || binding.placementRenderer.sprite != bottle.profile.front)
            {
                reason = $"'{bottle.name}' doğrudan bağlı ön-görsel referansı eksik.";
                return false;
            }
            int expected = BsRules.Capacity(type);
            if (bottle.profile.capacity != expected)
            {
                reason = $"'{bottle.name}' profile kapasitesi {bottle.profile.capacity}; "
                       + $"{BsRules.DisplayName(type)} için {expected} olmalı.";
                return false;
            }
            reason = null;
            return true;
        }

        private bool ValidateShelfBindings(int rowCount, out string reason)
        {
            if (layoutSpace == null)
            {
                reason = "Layout Space Inspector referansı eksik.";
                return false;
            }
            if (shelfRows == null || shelfRows.Length < rowCount)
            {
                reason = $"{rowCount} satır için yeterli plank binding yok.";
                return false;
            }
            for (int row = 0; row < rowCount; row++)
            {
                SpriteRenderer plank = shelfRows[row] != null ? shelfRows[row].plank : null;
                if (plank == null || plank.sprite == null)
                {
                    reason = $"Shelf row {row + 1} plank/sprite referansı eksik.";
                    return false;
                }
                if (shelfRows[row].seatAnchor == null
                    || !shelfRows[row].seatAnchor.IsChildOf(plank.transform))
                {
                    reason = $"Shelf row {row + 1} doğrudan bağlı üst-merkez noktası eksik.";
                    return false;
                }
            }

            int spanCount = Mathf.Max(0, rowCount - 1);
            if (shelfSpans == null || shelfSpans.Length < spanCount)
            {
                reason = $"{rowCount} satır için yeterli post-span binding yok.";
                return false;
            }
            for (int span = 0; span < spanCount; span++)
            {
                ShelfSpanBinding binding = shelfSpans[span];
                if (binding == null
                    || !ValidPost(binding.leftPost)
                    || !ValidPost(binding.rightPost))
                {
                    reason = $"Shelf span {span + 1} için iki post/sprite referansı gerekli.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private static bool ValidPost(SpriteRenderer post) =>
            post != null && post.sprite != null;

        private Actor Acquire(GlassType type)
        {
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (!actor.Assigned && actor.Type == type) return actor;
            }
            return null;
        }

        private void SetContents(LiquidBottle bottle, RtGlass glass, BsPalette palette)
        {
            colorScratch.Clear();
            for (int i = 0; i < glass.Layers.Count; i++)
            {
                Layer layer = glass.Layers[i];
                colorScratch.Add(layer.Hidden ? hiddenLayerColor : palette.ColorAt(layer.Color));
            }
            bottle.capacity = BsRules.Capacity(glass.Type);
            bottle.SetUnits(colorScratch);
        }

        private void LayoutActiveActors()
        {
            // The layout pass is the single authority on where a glass belongs, so any
            // running tween is dropped before it can write to a transform behind its back.
            StopSeatAnimation();

            int count = activeActors.Count;
            if (count == 0 || configuredRowCount <= 0) return;

            RememberCurrentSeats();

            int basePerRow = count / configuredRowCount;
            int remainder = count % configuredRowCount;

            int actorIndex = 0;
            for (int row = 0; row < configuredRowCount; row++)
            {
                int rowCount = basePerRow + (row < remainder ? 1 : 0);
                float spacing = rowCount <= 1
                    ? 0f
                    : rowCount == 2
                        ? twoAcrossColumnSpacing
                        : rowCount == 3
                            ? threeAcrossColumnSpacing
                            : compactColumnSpacing;
                bool tallLayout = configuredRowCount >= 3;
                float scale = rowCount >= 4
                    ? (tallLayout ? fourAcrossThreeRowGlassScale : fourAcrossGlassScale)
                    : (tallLayout ? threeRowSpaciousGlassScale : twoRowSpaciousGlassScale);
                Vector2 shelfSeat = ShelfSeatInLayout(row);
                float firstX = shelfSeat.x - 0.5f * spacing * (rowCount - 1);
                int rowActorStart = actorIndex;

                for (int column = 0; column < rowCount; column++, actorIndex++)
                {
                    Actor actor = activeActors[actorIndex];
                    actor.Row = row;
                    actor.Column = column;
                    SeatActor(actor, firstX + column * spacing,
                        shelfSeat.y - opticalSeatInset, scale);
                }
                CenterRowSilhouette(rowActorStart, rowCount, shelfSeat.x);
            }

            RecordSeatPoses();
        }

        /// <summary>
        /// Snapshots the pose each glass is leaving, so a mid-level rebalance can glide out
        /// of it instead of teleporting. Newly acquired glasses report no previous seat.
        /// </summary>
        private void RememberCurrentSeats()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                Transform actorTransform = actor.Bottle.transform;
                actor.HasPreviousSeat = actor.Seated;
                actor.PreviousSeatPosition = actorTransform.position;
                actor.PreviousSeatScale = actorTransform.localScale;
            }
        }

        /// <summary>
        /// Reads back the finished layout, including the row-centring shift, as the one pose
        /// every animation is allowed to move towards.
        /// </summary>
        private void RecordSeatPoses()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                Transform actorTransform = actor.Bottle.transform;
                actor.SeatPosition = actorTransform.position;
                actor.SeatScale = actorTransform.localScale;
                actor.Seated = true;
            }
        }

        private void SeatActor(Actor actor, float slotCenterX, float surfaceY, float scale)
        {
            Transform actorTransform = actor.Bottle.transform;
            actorTransform.localScale = actor.AuthoredLocalScale * scale;
            actorTransform.localRotation = actor.AuthoredLocalRotation;

            Vector3 desiredFoot = LayoutSpace.TransformPoint(
                new Vector3(slotCenterX, surfaceY, glassPlaneZ));
            Vector3 rootToFoot = actor.FootAnchor.position - actorTransform.position;
            actorTransform.position = desiredFoot - rootToFoot;
        }

        private void CenterRowSilhouette(int start, int count, float shelfCenterX)
        {
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            for (int i = start; i < start + count; i++)
            {
                SpriteRenderer renderer = activeActors[i].PlacementRenderer;
                Bounds bounds = renderer.sprite.bounds;
                AddLayoutX(renderer.transform, bounds.min.x, bounds.min.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.min.x, bounds.max.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.max.x, bounds.min.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.max.x, bounds.max.y,
                    ref minX, ref maxX);
            }

            if (float.IsInfinity(minX) || float.IsInfinity(maxX)) return;
            float shift = shelfCenterX - (minX + maxX) * 0.5f;
            if (Mathf.Abs(shift) < 0.0001f) return;

            for (int i = start; i < start + count; i++)
            {
                Transform root = activeActors[i].Bottle.transform;
                Vector3 local = LayoutSpace.InverseTransformPoint(root.position);
                local.x += shift;
                root.position = LayoutSpace.TransformPoint(local);
            }
        }

        private void AddLayoutX(Transform rendererTransform, float x, float y,
                                ref float minX, ref float maxX)
        {
            float layoutX = LayoutSpace.InverseTransformPoint(
                rendererTransform.TransformPoint(new Vector3(x, y, 0f))).x;
            minX = Mathf.Min(minX, layoutX);
            maxX = Mathf.Max(maxX, layoutX);
        }

        private void ActivateAndRefreshActors()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                LiquidBottle bottle = activeActors[i].Bottle;
                if (!bottle.gameObject.activeSelf) bottle.gameObject.SetActive(true);
                bottle.Refresh();
            }
        }

        // ---- Level entrance ------------------------------------------------------
        //
        // Every method below only walks an already-seated transform. The static layout has
        // already been applied by the time any of them runs, which is what keeps edit-mode
        // builds (Application.isPlaying == false) and a disabled animation identical to the
        // behaviour this view had before.

        private bool CanAnimate =>
            Application.isPlaying && isActiveAndEnabled && gameObject.activeInHierarchy;

        /// <summary>Drops the freshly presented glasses onto the shelf, bottom row first.</summary>
        private void PlayEntrance()
        {
            if (!CanAnimate || !animateEntrance || activeActors.Count == 0) return;

            OrderActorsForEntrance();
            BeginShelfFade();
            // Held off-stage rather than hovering above the plank while waiting their turn.
            for (int i = 0; i < activeActors.Count; i++)
                activeActors[i].Bottle.gameObject.SetActive(false);
            seatAnimation = StartCoroutine(EntranceRoutine());
        }

        /// <summary>
        /// Glides the remaining glasses to their new seats after a delivery rebalances the
        /// rows. Skipped when nothing actually moved.
        /// </summary>
        private void PlayReseat()
        {
            if (!CanAnimate || !animateEntrance || reseatDuration <= 0f) return;

            bool moved = false;
            for (int i = 0; i < activeActors.Count && !moved; i++)
            {
                Actor actor = activeActors[i];
                moved = actor.HasPreviousSeat
                    && ((actor.PreviousSeatPosition - actor.SeatPosition).sqrMagnitude > 1e-6f
                        || (actor.PreviousSeatScale - actor.SeatScale).sqrMagnitude > 1e-6f);
            }
            if (!moved) return;

            seatAnimation = StartCoroutine(ReseatRoutine());
        }

        private IEnumerator EntranceRoutine()
        {
            Vector3 layoutUp = LayoutSpace.TransformVector(Vector3.up);
            float total = MeasureEntranceFalls();

            float elapsed = 0f;
            while (elapsed < total)
            {
                StepShelfFade(elapsed);
                for (int i = 0; i < entranceOrder.Count; i++)
                {
                    Actor actor = entranceOrder[i];
                    StepEntranceActor(actor, elapsed - actor.EntranceDelay, layoutUp);
                }
                yield return null;
                // Unscaled so a paused board or a timeScale tweak cannot strand a glass
                // halfway to its seat.
                elapsed += Time.unscaledDeltaTime;
            }

            seatAnimation = null;
            FinishShelfFade();
            SnapActorsToSeat();
        }

        /// <summary>
        /// Every glass is released from one line above the top plank rather than from a
        /// fixed height over its own seat, so nothing appears out of thin air inside the
        /// frame. Same release line plus the same acceleration means a bottom-row glass
        /// simply falls for longer. Returns the length of the whole entrance.
        /// </summary>
        private float MeasureEntranceFalls()
        {
            float releaseY = SurfaceY(configuredRowCount, 0) + entranceDropHeight;
            float total = 0f;
            for (int i = 0; i < entranceOrder.Count; i++)
            {
                Actor actor = entranceOrder[i];
                // Measured at the foot, not at the transform root: the root can sit anywhere
                // inside the artwork, and it is the bottom of the glass that has to clear the
                // top of the frame before the drop starts.
                float footY = LayoutSpace.InverseTransformPoint(actor.FootAnchor.position).y;
                actor.EntranceFallDistance = Mathf.Max(0.01f, releaseY - footY);
                actor.EntranceFallDuration = entranceDropDuration
                    * Mathf.Sqrt(actor.EntranceFallDistance / entranceDropHeight);
                total = Mathf.Max(total, actor.EntranceDelay + actor.EntranceFallDuration
                                       + entranceSettleDuration);
            }
            return total;
        }

        private void StepEntranceActor(Actor actor, float time, Vector3 layoutUp)
        {
            Transform actorTransform = actor.Bottle.transform;
            GameObject actorObject = actor.Bottle.gameObject;

            if (time < 0f)
            {
                // Held off-stage while it waits its turn, so an idle glass is never left
                // hanging over the shelf.
                if (actorObject.activeSelf) actorObject.SetActive(false);
                actorTransform.localScale = actor.SeatScale;
                return;
            }
            if (!actorObject.activeSelf)
            {
                // Coming back from off-stage the vessel has dropped its generated art, so it
                // is refreshed on the spot rather than left to the next LateUpdate.
                actorObject.SetActive(true);
                actor.Bottle.Refresh();
            }

            if (time < actor.EntranceFallDuration)
            {
                LiftEntranceSorting(actor);
                // Quadratic: the glass reads as dropped under gravity, not slid in.
                float fall = time / actor.EntranceFallDuration;
                actorTransform.position = actor.SeatPosition
                    + layoutUp * (actor.EntranceFallDistance * (1f - fall * fall));
                actorTransform.localScale = actor.SeatScale;
                return;
            }

            DropEntranceSorting(actor);
            actorTransform.position = actor.SeatPosition;
            actorTransform.localScale =
                LandingScale(actor.SeatScale, time - actor.EntranceFallDuration);
        }

        /// <summary>
        /// Keeps a falling glass in front of every plank it passes, the same lift a pour
        /// uses. Re-applied each frame on purpose: BottleShell re-publishes its authored
        /// draw orders on the first LateUpdate after the vessel is switched back on, which
        /// would otherwise swallow the lift and leave the drop half-eaten by a shelf.
        /// </summary>
        private void LiftEntranceSorting(Actor actor)
        {
            if (entranceSortingBoost <= 0) return;
            actor.SortingLifted = true;
            actor.Bottle.SetSortingOffset(entranceSortingBoost);
        }

        private static void DropEntranceSorting(Actor actor)
        {
            if (!actor.SortingLifted) return;
            actor.SortingLifted = false;
            if (actor.Bottle != null) actor.Bottle.SetSortingOffset(0);
        }

        /// <summary>
        /// One damped squash-and-stretch beat on contact. Returns the exact seat scale once
        /// the beat is over, so repeated levels cannot accumulate drift.
        /// </summary>
        private Vector3 LandingScale(Vector3 seatScale, float sinceLanding)
        {
            if (entranceLandingSquash <= 0f || entranceSettleDuration <= 0f) return seatScale;
            float life = sinceLanding / entranceSettleDuration;
            if (life >= 1f) return seatScale;

            float amount = entranceLandingSquash * (1f - life)
                         * Mathf.Cos(life * Mathf.PI * 1.5f);
            return new Vector3(seatScale.x * (1f + amount),
                               seatScale.y * (1f - amount),
                               seatScale.z);
        }

        private IEnumerator ReseatRoutine()
        {
            float elapsed = 0f;
            while (elapsed < reseatDuration)
            {
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / reseatDuration));
                for (int i = 0; i < activeActors.Count; i++)
                {
                    Actor actor = activeActors[i];
                    if (!actor.HasPreviousSeat) continue;
                    Transform actorTransform = actor.Bottle.transform;
                    actorTransform.position = Vector3.Lerp(
                        actor.PreviousSeatPosition, actor.SeatPosition, k);
                    actorTransform.localScale = Vector3.Lerp(
                        actor.PreviousSeatScale, actor.SeatScale, k);
                }
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            seatAnimation = null;
            SnapActorsToSeat();
        }

        /// <summary>
        /// Ends any animation on the exact layout pose. Also used as the failure path: a
        /// stopped coroutine can never leave a glass in the air.
        /// </summary>
        private void SnapActorsToSeat()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                DropEntranceSorting(actor);
                if (actor.Bottle == null || !actor.Seated) continue;
                GameObject actorObject = actor.Bottle.gameObject;
                if (!actorObject.activeSelf) actorObject.SetActive(true);
                actor.Bottle.transform.position = actor.SeatPosition;
                actor.Bottle.transform.localScale = actor.SeatScale;
            }
        }

        private void StopSeatAnimation()
        {
            if (seatAnimation != null)
            {
                StopCoroutine(seatAnimation);
                seatAnimation = null;
            }
            for (int i = 0; i < activeActors.Count; i++)
                DropEntranceSorting(activeActors[i]);
            FinishShelfFade();
        }

        private void OrderActorsForEntrance()
        {
            entranceOrder.Clear();
            // Bottom row first: the shelf visibly fills from the plank the player reads last.
            for (int row = configuredRowCount - 1; row >= 0; row--)
            {
                for (int i = 0; i < activeActors.Count; i++)
                    if (activeActors[i].Row == row) entranceOrder.Add(activeActors[i]);
            }

            float delay = 0f;
            int previousRow = -1;
            for (int i = 0; i < entranceOrder.Count; i++)
            {
                Actor actor = entranceOrder[i];
                if (previousRow >= 0)
                    delay += actor.Row == previousRow
                        ? entranceGlassStagger
                        : entranceRowStagger;
                actor.EntranceDelay = delay;
                previousRow = actor.Row;
            }
        }

        private void BeginShelfFade()
        {
            shelfFadeRenderers.Clear();
            shelfFadeColors.Clear();
            if (shelfFadeDuration <= 0f) return;

            for (int row = 0; row < configuredRowCount; row++)
                AddShelfFadeRenderer(shelfRows[row] != null ? shelfRows[row].plank : null);
            for (int span = 0; span < configuredRowCount - 1; span++)
            {
                ShelfSpanBinding binding = shelfSpans[span];
                if (binding == null) continue;
                AddShelfFadeRenderer(binding.leftPost);
                AddShelfFadeRenderer(binding.rightPost);
            }
            StepShelfFade(0f);
        }

        private void AddShelfFadeRenderer(SpriteRenderer renderer)
        {
            if (renderer == null || !renderer.enabled) return;
            shelfFadeRenderers.Add(renderer);
            shelfFadeColors.Add(renderer.color);
        }

        private void StepShelfFade(float elapsed)
        {
            if (shelfFadeRenderers.Count == 0) return;
            float k = shelfFadeDuration <= 0f
                ? 1f
                : Mathf.Clamp01(elapsed / shelfFadeDuration);
            for (int i = 0; i < shelfFadeRenderers.Count; i++)
            {
                SpriteRenderer renderer = shelfFadeRenderers[i];
                if (renderer == null) continue;
                Color color = shelfFadeColors[i];
                color.a *= k;
                renderer.color = color;
            }
            if (k >= 1f) FinishShelfFade();
        }

        /// <summary>Restores the authored plank/post colours the fade borrowed.</summary>
        private void FinishShelfFade()
        {
            for (int i = 0; i < shelfFadeRenderers.Count; i++)
                if (shelfFadeRenderers[i] != null)
                    shelfFadeRenderers[i].color = shelfFadeColors[i];
            shelfFadeRenderers.Clear();
            shelfFadeColors.Clear();
        }

        private void ApplyShelfLayout(int rowCount)
        {
            DisableAllShelves();
            for (int row = 0; row < rowCount; row++)
            {
                ShelfRowBinding binding = shelfRows[row];
                SpriteRenderer plank = binding.plank;
                MovePlankSurfaceTo(binding, SurfaceY(rowCount, row));
                plank.enabled = true;
            }

            for (int span = 0; span < rowCount - 1; span++)
            {
                float upperSurface = SurfaceY(rowCount, span);
                float lowerSurface = SurfaceY(rowCount, span + 1);
                float upperPlankHeight = SpriteHeightInLayout(shelfRows[span].plank);
                float top = upperSurface - upperPlankHeight * postUpperPlankOverlap;
                float bottom = lowerSurface + postLowerShelfInset;
                FitPost(shelfSpans[span].leftPost, top, bottom);
                FitPost(shelfSpans[span].rightPost, top, bottom);
            }
        }

        private void MovePlankSurfaceTo(ShelfRowBinding binding, float surfaceY)
        {
            SpriteRenderer plank = binding.plank;
            float currentSurfaceY = LayoutSpace
                .InverseTransformPoint(binding.seatAnchor.position).y;
            Vector3 local = LayoutSpace.InverseTransformPoint(plank.transform.position);
            local.y += surfaceY - currentSurfaceY;
            plank.transform.position = LayoutSpace.TransformPoint(local);
        }

        private Vector2 ShelfSeatInLayout(int row)
        {
            Vector3 local = LayoutSpace.InverseTransformPoint(
                shelfRows[row].seatAnchor.position);
            return new Vector2(local.x, local.y);
        }

        private float SpriteHeightInLayout(SpriteRenderer renderer)
        {
            if (renderer == null || renderer.sprite == null) return 0f;
            float worldHeight = renderer.transform
                .TransformVector(Vector3.up * renderer.sprite.bounds.size.y).magnitude;
            float layoutWorldUnit = LayoutSpace.TransformVector(Vector3.up).magnitude;
            return worldHeight / Mathf.Max(0.0001f, layoutWorldUnit);
        }

        private void FitPost(SpriteRenderer post, float top, float bottom)
        {
            if (post == null || post.sprite == null || top <= bottom) return;

            Transform postTransform = post.transform;
            Vector3 local = LayoutSpace.InverseTransformPoint(postTransform.position);
            local.y = (top + bottom) * 0.5f;
            postTransform.position = LayoutSpace.TransformPoint(local);

            Transform parent = postTransform.parent;
            Vector3 localUp = postTransform.localRotation * Vector3.up;
            float parentWorldPerUnit = parent != null
                ? parent.TransformVector(localUp).magnitude
                : localUp.magnitude;
            float layoutWorldUnit = LayoutSpace.TransformVector(Vector3.up).magnitude;
            float wantedWorldHeight = (top - bottom) * layoutWorldUnit;
            float scaleY = wantedWorldHeight
                         / Mathf.Max(0.0001f,
                             post.sprite.bounds.size.y * parentWorldPerUnit);
            Vector3 scale = postTransform.localScale;
            scale.y = Mathf.Sign(Mathf.Approximately(scale.y, 0f) ? 1f : scale.y) * scaleY;
            postTransform.localScale = scale;
            post.enabled = true;
        }

        private float SurfaceY(int rowCount, int row)
        {
            if (rowCount <= 2) return row == 0 ? twoRowSurfaceY.x : twoRowSurfaceY.y;
            if (row == 0) return threeRowSurfaceY.x;
            return row == 1 ? threeRowSurfaceY.y : threeRowSurfaceY.z;
        }

        private static int RowCountFor(int glassCount, int columnsPerRow)
        {
            int columns = Mathf.Max(1, columnsPerRow);
            int needed = Mathf.CeilToInt(glassCount / (float)columns);
            return Mathf.Max(2, needed);
        }


        private static bool ContainsActor(List<Actor> list, Actor wanted)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], wanted)) return true;
            return false;
        }

        private void Release(Actor actor)
        {
            DropEntranceSorting(actor);
            actorByGlassId.Remove(actor.GlassId);
            if (actor.Bottle != null)
            {
                glassIdByBottle.Remove(actor.Bottle);
                actor.Bottle.SetUnits(null);
                actor.Bottle.transform.localScale = actor.AuthoredLocalScale;
                actor.Bottle.transform.localRotation = actor.AuthoredLocalRotation;
                actor.Bottle.gameObject.SetActive(false);
            }
            actor.GlassId = -1;
            actor.Assigned = false;
            actor.Seated = false;
            actor.HasPreviousSeat = false;
        }

        // ---- Delivery portal ------------------------------------------------------
        //
        // A served glass is the one case where the board is ahead of the shelf: the rules
        // dropped it a frame ago, but the player still has to see it handed over. The actor
        // is unbound from the board here and only returned to its pool once the portal
        // reports the vessel as hidden, so a delivery in flight can never be switched off,
        // reseated or handed to a new level.

        private bool TryBeginPortalDelivery(Actor actor, int deliveredGlassId)
        {
            if (deliveryPortal == null || deliveredGlassId < 0
                || actor.GlassId != deliveredGlassId || actor.Bottle == null)
                return false;

            DropEntranceSorting(actor);
            if (!deliveryPortal.Play(
                    actor.Bottle,
                    actor.FootAnchor,
                    null,
                    () => FinishPortalDelivery(actor),
                    NotifyDeliveryPresentationFinished))
                return false;

            actorByGlassId.Remove(actor.GlassId);
            glassIdByBottle.Remove(actor.Bottle);
            actor.GlassId = -1;
            actor.Seated = false;
            actor.HasPreviousSeat = false;
            // Still Assigned on purpose: the pool slot belongs to this flight until it lands.
            actor.Delivering = true;
            deliveringActors.Add(actor);
            return true;
        }

        private void FinishPortalDelivery(Actor actor)
        {
            if (!actor.Delivering) return;
            actor.Delivering = false;
            deliveringActors.Remove(actor);
            Release(actor);
        }

        private void NotifyDeliveryPresentationFinished()
        {
            DeliveryPresentationFinished?.Invoke();
        }

        /// <summary>
        /// Ends every flight on the exact pose the pool expects. The portal fires the same
        /// completion callbacks it would have fired on its own, so this is a shortcut, not
        /// a second release path.
        /// </summary>
        private void CancelPortalDeliveries()
        {
            if (deliveryPortal != null) deliveryPortal.CancelAll();
            if (deliveringActors.Count == 0) return;

            // Only reachable if the portal reference was lost mid-flight.
            deliveryScratch.Clear();
            deliveryScratch.AddRange(deliveringActors);
            deliveringActors.Clear();
            for (int i = 0; i < deliveryScratch.Count; i++)
            {
                Actor actor = deliveryScratch[i];
                actor.Delivering = false;
                Release(actor);
            }
            deliveryScratch.Clear();
            // A lost/replaced portal cannot invoke its onFinished callback. The fallback
            // release above is still a terminal presentation beat, so lock owners need the
            // same completion signal exactly once.
            NotifyDeliveryPresentationFinished();
        }

        private void ClearAssignments()
        {
            CancelPortalDeliveries();
            activeActors.Clear();
            actorByGlassId.Clear();
            glassIdByBottle.Clear();

            ClearPool(shotPool);
            ClearPool(cocktailPool);
            ClearPool(lattePool);
            ClearPool(tumblerPool);
            ClearPool(biraPool);
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                actor.GlassId = -1;
                actor.Assigned = false;
                actor.Seated = false;
                actor.HasPreviousSeat = false;
                actor.Delivering = false;
            }
        }

        private static void ClearPool(List<GlassBinding> pool)
        {
            if (pool == null) return;
            for (int i = 0; i < pool.Count; i++)
            {
                GlassBinding binding = pool[i];
                LiquidBottle bottle = binding != null ? binding.bottle : null;
                if (bottle == null) continue;
                bottle.SetUnits(null);
                bottle.transform.localScale = binding.authoredLocalScale;
                bottle.transform.localRotation = binding.authoredLocalRotation;
                bottle.gameObject.SetActive(false);
            }
        }

        private void ClearPresentation()
        {
            StopSeatAnimation();
            ClearAssignments();
            DisableAllShelves();
            configuredRowCount = 0;
            configuredColumns = 1;
            presentedLevel = null;
            Ready = false;
            PresentationChanged?.Invoke();
        }

        private void DisableAllShelves()
        {
            if (shelfRows != null)
            {
                for (int i = 0; i < shelfRows.Length; i++)
                    if (shelfRows[i] != null && shelfRows[i].plank != null)
                        shelfRows[i].plank.enabled = false;
            }
            if (shelfSpans == null) return;
            for (int i = 0; i < shelfSpans.Length; i++)
            {
                ShelfSpanBinding span = shelfSpans[i];
                if (span == null) continue;
                if (span.leftPost != null) span.leftPost.enabled = false;
                if (span.rightPost != null) span.rightPost.enabled = false;
            }
        }

        private bool Reject(string reason)
        {
            ClearPresentation();
            LastError = string.IsNullOrEmpty(reason) ? "Bilinmeyen sunum hatası." : reason;
            if (lastLoggedError != LastError)
            {
                Debug.LogError("Bartender shelf presentation rejected: " + LastError, this);
                lastLoggedError = LastError;
            }
            PresentationRejected?.Invoke(LastError);
            return false;
        }
    }
}
