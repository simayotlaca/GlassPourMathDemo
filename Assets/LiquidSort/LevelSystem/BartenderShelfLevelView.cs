using System;
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
        [Tooltip("Royal scale multiplier for rows of up to three glasses in the two-row layout.")]
        [SerializeField, Min(0.1f)] private float twoRowSpaciousGlassScale = 1.27f;
        [Tooltip("Royal scale multiplier for rows of up to three glasses in the three-row layout.")]
        [SerializeField, Min(0.1f)] private float threeRowSpaciousGlassScale = 1.00f;
        [Tooltip("Royal scale multiplier for every row containing four glasses.")]
        [SerializeField, Min(0.1f)] private float fourAcrossGlassScale = 0.90f;
        [Tooltip("Small optical overlap that seats the vessel artwork into the plank.")]
        [SerializeField, Min(0f)] private float opticalSeatInset = 0.02f;
        [SerializeField] private float glassPlaneZ;

        [Header("Post fitting")]
        [Tooltip("Share of the upper plank height hidden behind a vertical post.")]
        [SerializeField, Range(0f, 1.5f)] private float postUpperPlankOverlap = 0.92f;
        [Tooltip("Small overlap above the lower shelf surface.")]
        [SerializeField, Min(0f)] private float postLowerShelfInset = 0.02f;

        [Header("Layer presentation")]
        [SerializeField] private Color hiddenLayerColor =
            new Color(0.29f, 0.31f, 0.36f, 1f);

        private readonly List<Actor> actors = new List<Actor>(
            FullCampaignShotPoolSize + FullCampaignCocktailPoolSize
            + FullCampaignLattePoolSize + FullCampaignTumblerPoolSize);
        private readonly List<Actor> activeActors = new List<Actor>(MaximumActiveGlasses);
        private readonly Dictionary<int, Actor> actorByGlassId =
            new Dictionary<int, Actor>(MaximumActiveGlasses);
        private readonly Dictionary<LiquidBottle, int> glassIdByBottle =
            new Dictionary<LiquidBottle, int>(MaximumActiveGlasses);
        private readonly List<Color> colorScratch = new List<Color>(LiquidBottle.MaxBands);
        private readonly HashSet<LiquidBottle> uniqueBottleScratch =
            new HashSet<LiquidBottle>();

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

        public event Action PresentationChanged;
        public event Action<string> PresentationRejected;

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
            opticalSeatInset = Mathf.Max(0f, opticalSeatInset);
            postLowerShelfInset = Mathf.Max(0f, postLowerShelfInset);
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
            float scaleAtFour,
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
            fourAcrossGlassScale = Mathf.Max(0.1f, scaleAtFour);
            opticalSeatInset = Mathf.Max(0f, seatInset);
            glassPlaneZ = planeZ;
        }

        /// <summary>
        /// Applies one detached level/board snapshot to the serialized scene pools.
        /// No domain object is retained or mutated.
        /// </summary>
        public bool TryPresent(BsLevel level, BsBoard snapshot, BsPalette palette)
        {
            if (!ValidateSnapshot(level, snapshot, palette, out int rows, out string reason))
                return Reject(reason);

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
        /// Strict check for the largest non-Bira allocations in the 30-level campaign and
        /// all three possible shelf rows. Bira intentionally remains an explicit rejection
        /// until a capacity-five Royal scene pool exists.
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
                    GlassType.Tumbler, out reason))
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
                Debug.Log("Bartender shelf: supported campaign bindings are valid; "
                        + "Bira remains fail-closed.", this);
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
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.BoardChanged -= HandleBoardChanged;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            BsBoard snapshot = controller != null ? controller.Board : null;
            TryPresent(level, snapshot, controller != null ? controller.Palette : null);
        }

        private void HandleBoardChanged()
        {
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

            activeActors.Clear();
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass.Type == GlassType.Bira)
                    return Reject(UnsupportedBeerReason());

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
                if (!actor.Assigned || ContainsActor(activeActors, actor)) continue;
                Release(actor);
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
            if (ContainsBeer(level))
            {
                reason = UnsupportedBeerReason();
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
                    case GlassType.Bira:
                        reason = UnsupportedBeerReason();
                        return false;
                    default:
                        reason = $"Desteklenmeyen bardak tipi: {(int)glass.Type}.";
                        return false;
                }
            }

            if (!ValidatePoolCount(shotPool, shots, GlassType.Shot, out reason)
                || !ValidatePoolCount(cocktailPool, cocktails, GlassType.Kadeh, out reason)
                || !ValidatePoolCount(lattePool, lattes, GlassType.Latte, out reason)
                || !ValidatePoolCount(tumblerPool, tumblers, GlassType.Tumbler, out reason))
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
                || !AddPoolToCache(tumblerPool, GlassType.Tumbler, out reason))
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
            int count = activeActors.Count;
            if (count == 0 || configuredRowCount <= 0) return;

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
                float scale = rowCount >= 4
                    ? fourAcrossGlassScale
                    : configuredRowCount >= 3
                        ? threeRowSpaciousGlassScale
                        : twoRowSpaciousGlassScale;
                Vector2 shelfSeat = ShelfSeatInLayout(row);
                float firstX = shelfSeat.x - 0.5f * spacing * (rowCount - 1);
                int rowActorStart = actorIndex;

                for (int column = 0; column < rowCount; column++, actorIndex++)
                {
                    Actor actor = activeActors[actorIndex];
                    SeatActor(actor, firstX + column * spacing,
                        shelfSeat.y - opticalSeatInset, scale);
                }
                CenterRowSilhouette(rowActorStart, rowCount, shelfSeat.x);
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

        private static bool ContainsBeer(BsLevel level)
        {
            if (level == null) return false;
            if (level.Glasses != null)
            {
                for (int i = 0; i < level.Glasses.Count; i++)
                    if (level.Glasses[i] != null
                        && level.Glasses[i].Type == GlassType.Bira)
                        return true;
            }
            if (level.Orders != null)
            {
                for (int i = 0; i < level.Orders.Count; i++)
                    if (level.Orders[i] != null
                        && level.Orders[i].Glass == GlassType.Bira)
                        return true;
            }
            return false;
        }

        private static bool ContainsActor(List<Actor> list, Actor wanted)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], wanted)) return true;
            return false;
        }

        private void Release(Actor actor)
        {
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
        }

        private void ClearAssignments()
        {
            activeActors.Clear();
            actorByGlassId.Clear();
            glassIdByBottle.Clear();

            ClearPool(shotPool);
            ClearPool(cocktailPool);
            ClearPool(lattePool);
            ClearPool(tumblerPool);
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                actor.GlassId = -1;
                actor.Assigned = false;
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

        private static string UnsupportedBeerReason() =>
            "Bira tipi için scene-native Royal havuzu henüz yok. Profil uydurulmadı; "
          + "level sunumu güvenli olarak temizlendi ve domain board değiştirilmedi.";
    }
}
