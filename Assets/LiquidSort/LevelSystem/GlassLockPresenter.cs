using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Presents the level system's two independent lock mechanics on their bottles:
    /// one padlock per active <see cref="Layer.LockUntil"/> unit, and one counted chain
    /// for <see cref="RtGlass.UnlockAfter"/>. The icons are the same procedural sprites
    /// used by BartenderSortGame; no delivery-check badge is repurposed as a lock.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class GlassLockPresenter : MonoBehaviour
    {
        private sealed class UnitLockVisual
        {
            public SpriteRenderer Seal;
            public SpriteRenderer Rim;
            public SpriteRenderer Shadow;
            public SpriteRenderer Icon;
        }

        private sealed class BottleVisuals
        {
            public LiquidBottle Bottle;
            public readonly List<UnitLockVisual> UnitLocks =
                new List<UnitLockVisual>(LiquidBottle.MaxBands);
            public SpriteRenderer ChainHalo;
            public SpriteRenderer ChainRim;
            public SpriteRenderer ChainShadow;
            public SpriteRenderer ChainIcon;
            public SpriteRenderer ChainBadge;
            public SpriteRenderer ChainBadgeRim;
            public TextMesh ChainCount;
            public Renderer ChainCountRenderer;
        }

        private const string MarkerPrefix = "GameplayLock_";
        private const string UnitMarkerPrefix = MarkerPrefix + "Unit_";
        private const string ChainHaloName = MarkerPrefix + "ChainHalo";
        private const string ChainRimName = MarkerPrefix + "ChainRim";
        private const string ChainShadowName = MarkerPrefix + "ChainShadow";
        private const string ChainMarkerName = MarkerPrefix + "Chain";
        private const string ChainBadgeName = MarkerPrefix + "ChainBadge";
        private const string ChainBadgeRimName = MarkerPrefix + "ChainBadgeRim";
        private const string ChainCountName = MarkerPrefix + "ChainCount";

        // BottleShell ends at order 7 and the authored delivery check sits at 12.
        private const int LockSealOrder = 8;
        private const int LockShadowOrder = 9;
        private const int LockRimOrder = 10;
        private const int UnitLockOrder = 11;
        private const int ChainHaloOrder = 8;
        private const int ChainShadowOrder = 9;
        private const int ChainRimOrder = 10;
        private const int ChainIconOrder = 11;
        private const int ChainBadgeOrder = 10;
        private const int ChainBadgeRimOrder = 11;
        private const int ChainCountOrder = 12;

        // Ratios are the source GlassView dimensions: Size.y=238, lock max=44.03,
        // chain=54, chain icon y=+10, counter diameter=38 and counter y=-34.
        private const float UnitScaleWithinBand = 0.70f;
        private const float UnitMaxHeightShare = 0.185f;
        private const float UnitSealWidthShare = 0.78f;
        private const float UnitSealHeightShare = 0.78f;
        private const float UnitSealIconPadding = 1.30f;
        private const float ChainHeightShare = 54f / 238f;
        private const float ChainIconOffsetShare = 10f / 238f;
        private const float ChainBadgeSizeShare = 38f / 238f;
        private const float ChainBadgeOffsetShare = 34f / 238f;
        private const float ChainSealWidthShare = 0.84f;
        private const float ChainSealHeightScale = 1.24f;

        private static readonly Color UnitLockTint =
            new Color(1f, 0.91f, 0.58f, 1f);
        private static readonly Color UnitSealTint =
            new Color(0.20f, 0.055f, 0.29f, 0.72f);
        private static readonly Color UnitRimTint =
            new Color(1f, 0.62f, 0.08f, 0.82f);
        private static readonly Color LockShadowTint =
            new Color(0.18f, 0.045f, 0.015f, 0.76f);
        private static readonly Color ChainTint =
            new Color(1f, 0.77f, 0.16f, 1f);
        private static readonly Color ChainHaloTint =
            new Color(0.15f, 0.035f, 0.25f, 0.86f);
        private static readonly Color ChainRimTint =
            new Color(1f, 0.67f, 0.08f, 0.92f);
        private static readonly Color ChainBadgeTint =
            new Color32(0x2A, 0x22, 0x10, 0xFF);
        private static readonly Color ChainCountTint =
            new Color(1f, 0.88f, 0.45f, 1f);

        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;

        private readonly Dictionary<LiquidBottle, BottleVisuals> visuals =
            new Dictionary<LiquidBottle, BottleVisuals>();
        private readonly HashSet<LiquidBottle> touched = new HashSet<LiquidBottle>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private bool refreshPending;
        private static Font counterFont;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            RequestRefresh();
        }

        private void OnDisable()
        {
            Unsubscribe();
            refreshPending = false;
            HideAll();
        }

        private void LateUpdate()
        {
            RebindIfNeeded();
            if (refreshPending && shelfView != null && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.SynchronizationDeferred)
                RequestRefresh();
        }

        private void HandlePresentationChanged() => RequestRefresh();

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
            {
                refreshPending = false;
                HideAll();
            }
        }

        /// <summary>
        /// Entrance sorting temporarily boosts an already-cached renderer set. Creating a
        /// renderer during that lift would make the boosted orders look like authored base
        /// orders on the next cache pass. Wait until the entrance/reseat owns no bottle,
        /// then create or reveal markers against stable BottleShell orders.
        /// </summary>
        private void RequestRefresh()
        {
            if (shelfView != null
                && (shelfView.SeatAnimationPlaying || shelfView.SynchronizationDeferred))
            {
                refreshPending = true;
                HideAll();
                return;
            }

            refreshPending = false;
            RefreshLocks();
        }

        private void RefreshLocks()
        {
            touched.Clear();
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

            int delivered = snapshot.Delivered;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null
                    || !shelfView.TryGetBottle(glass.Id, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                    continue;

                BottleVisuals set = GetOrCreateVisuals(bottle);
                touched.Add(bottle);
                RefreshUnitLocks(set, glass, delivered);
                RefreshChain(set, glass, delivered);
            }

            foreach (KeyValuePair<LiquidBottle, BottleVisuals> pair in visuals)
                if (pair.Key == null || !touched.Contains(pair.Key)) Hide(pair.Value);
        }

        private void RefreshUnitLocks(BottleVisuals set, RtGlass glass, int delivered)
        {
            int shown = 0;
            for (int layerIndex = 0; layerIndex < glass.Layers.Count; layerIndex++)
            {
                Layer layer = glass.Layers[layerIndex];
                if (!layer.IsLocked(delivered)) continue;
                if (!set.Bottle.TryGetUnitVisualBand(layerIndex, out Vector2 center,
                                                     out float bandHeight))
                    continue;

                UnitLockVisual marker = GetOrCreateUnitLock(set, shown++);
                float artHeight = ArtHeight(set.Bottle);
                float wantedHeight = Mathf.Min(
                    bandHeight * UnitScaleWithinBand,
                    artHeight * UnitMaxHeightShare);
                // A lock is presentation metadata, not part of the liquid surface. Keep
                // the marker as one transparent icon in the bottle's canonical local
                // space; opaque discs/rims read as a second liquid ellipse and make the
                // Royal glass appear to change shape when a lock mechanic is introduced.
                if (marker.Seal != null) marker.Seal.enabled = false;
                if (marker.Rim != null) marker.Rim.enabled = false;
                if (marker.Shadow != null) marker.Shadow.enabled = false;
                PlaceSprite(marker.Icon, center, wantedHeight, UnitLockTint);
            }

            for (int i = shown; i < set.UnitLocks.Count; i++)
                Hide(set.UnitLocks[i]);
        }

        private void RefreshChain(BottleVisuals set, RtGlass glass, int delivered)
        {
            bool chained = glass.IsChained(delivered);
            if (!chained
                || !set.Bottle.TryGetLiquidColumnVisualCenter(out Vector2 center,
                                                              out _))
            {
                HideChain(set);
                return;
            }

            float artHeight = ArtHeight(set.Bottle);
            Vector2 iconCenter = center + Vector2.up * (artHeight * ChainIconOffsetShare);
            Vector2 badgeCenter = center - Vector2.up * (artHeight * ChainBadgeOffsetShare);
            float iconHeight = artHeight * ChainHeightShare;
            float badgeDiameter = artHeight * ChainBadgeSizeShare;
            // As with per-unit locks, chain decoration must not add an opaque surface
            // inside the vessel. The icon and count are enough and inherit the exact
            // bottle/profile scale through their local transform.
            if (set.ChainHalo != null) set.ChainHalo.enabled = false;
            if (set.ChainRim != null) set.ChainRim.enabled = false;
            if (set.ChainShadow != null) set.ChainShadow.enabled = false;
            PlaceSprite(set.ChainIcon, iconCenter, iconHeight, ChainTint);
            PlaceSprite(set.ChainBadge, badgeCenter, badgeDiameter, ChainBadgeTint);
            if (set.ChainBadgeRim != null) set.ChainBadgeRim.enabled = false;

            TextMesh count = set.ChainCount;
            count.text = glass.ChainRemaining(delivered).ToString();
            count.color = ChainCountTint;
            count.characterSize = badgeDiameter * 0.12f;
            count.transform.localPosition = new Vector3(badgeCenter.x, badgeCenter.y, 0f);
            count.transform.localRotation = Quaternion.identity;
            count.transform.localScale = Vector3.one;
            set.ChainCountRenderer.enabled = true;
        }

        private BottleVisuals GetOrCreateVisuals(LiquidBottle bottle)
        {
            if (visuals.TryGetValue(bottle, out BottleVisuals found)) return found;

            Renderer source = FindVisualSource(bottle);
            int sortingLayerId = source != null
                ? source.sortingLayerID
                : SortingLayer.NameToID(bottle.sortingLayer);
            var set = new BottleVisuals
            {
                Bottle = bottle,
                ChainIcon = GetOrCreateSpriteRenderer(
                    bottle, ChainMarkerName, BartenderLockIcons.Chain,
                    sortingLayerId, ChainIconOrder),
                ChainBadge = GetOrCreateSpriteRenderer(
                    bottle, ChainBadgeName, BartenderLockIcons.Circle,
                    sortingLayerId, ChainBadgeOrder)
            };

            set.ChainCount = GetOrCreateCounter(
                bottle, sortingLayerId, out Renderer countRenderer);
            set.ChainCountRenderer = countRenderer;
            visuals[bottle] = set;
            bottle.InvalidateRenderers();
            Hide(set);
            return set;
        }

        private UnitLockVisual GetOrCreateUnitLock(BottleVisuals set, int index)
        {
            while (set.UnitLocks.Count <= index)
            {
                int slot = set.UnitLocks.Count;
                Renderer source = FindVisualSource(set.Bottle);
                int sortingLayerId = source != null
                    ? source.sortingLayerID
                    : SortingLayer.NameToID(set.Bottle.sortingLayer);
                string markerName = UnitMarkerPrefix + slot;
                var marker = new UnitLockVisual
                {
                    Icon = GetOrCreateSpriteRenderer(
                        set.Bottle, markerName, BartenderLockIcons.Lock,
                        sortingLayerId, UnitLockOrder)
                };
                set.UnitLocks.Add(marker);
                set.Bottle.InvalidateRenderers();
            }
            return set.UnitLocks[index];
        }

        private static SpriteRenderer GetOrCreateSpriteRenderer(
            LiquidBottle bottle, string childName, Sprite sprite,
            int sortingLayerId, int sortingOrder)
        {
            Transform child = bottle.transform.Find(childName);
            if (child == null)
            {
                var childObject = new GameObject(childName);
                childObject.transform.SetParent(bottle.transform, false);
                child = childObject.transform;
            }

            child.gameObject.layer = bottle.gameObject.layer;
            SpriteRenderer renderer = child.GetComponent<SpriteRenderer>();
            if (renderer == null) renderer = child.gameObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = Color.white;
            renderer.sortingLayerID = sortingLayerId;
            renderer.sortingOrder = sortingOrder;
            renderer.maskInteraction = SpriteMaskInteraction.None;
            renderer.enabled = false;
            return renderer;
        }

        private static TextMesh GetOrCreateCounter(LiquidBottle bottle,
                                                   int sortingLayerId,
                                                   out Renderer renderer)
        {
            Transform child = bottle.transform.Find(ChainCountName);
            if (child == null)
            {
                var childObject = new GameObject(ChainCountName);
                childObject.transform.SetParent(bottle.transform, false);
                child = childObject.transform;
            }

            child.gameObject.layer = bottle.gameObject.layer;
            TextMesh text = child.GetComponent<TextMesh>();
            if (text == null) text = child.gameObject.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 64;
            text.fontStyle = FontStyle.Bold;
            text.richText = false;
            text.color = ChainCountTint;
            text.font = CounterFont();

            renderer = child.GetComponent<Renderer>();
            if (text.font != null && renderer != null)
                renderer.sharedMaterial = text.font.material;
            if (renderer != null)
            {
                renderer.sortingLayerID = sortingLayerId;
                renderer.sortingOrder = ChainCountOrder;
                renderer.enabled = false;
            }
            return text;
        }

        private static Font CounterFont()
        {
            if (counterFont != null) return counterFont;
            counterFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            return counterFont;
        }

        private static void PlaceSprite(SpriteRenderer renderer, Vector2 center,
                                        float wantedHeight, Color tint)
        {
            Sprite sprite = renderer.sprite;
            if (sprite == null || wantedHeight <= 0.0001f
                || sprite.bounds.size.y <= 0.0001f)
            {
                renderer.enabled = false;
                return;
            }

            Transform marker = renderer.transform;
            marker.localPosition = new Vector3(center.x, center.y, 0f);
            marker.localRotation = Quaternion.identity;
            float scale = wantedHeight / sprite.bounds.size.y;
            marker.localScale = new Vector3(scale, scale, 1f);
            renderer.color = tint;
            renderer.enabled = true;
        }

        private static void PlaceRect(SpriteRenderer renderer, Vector2 center,
                                      Vector2 wantedSize, Color tint)
        {
            Sprite sprite = renderer.sprite;
            if (sprite == null || wantedSize.x <= 0.0001f || wantedSize.y <= 0.0001f
                || sprite.bounds.size.x <= 0.0001f
                || sprite.bounds.size.y <= 0.0001f)
            {
                renderer.enabled = false;
                return;
            }

            Transform marker = renderer.transform;
            marker.localPosition = new Vector3(center.x, center.y, 0f);
            marker.localRotation = Quaternion.identity;
            marker.localScale = new Vector3(
                wantedSize.x / sprite.bounds.size.x,
                wantedSize.y / sprite.bounds.size.y,
                1f);
            renderer.color = tint;
            renderer.enabled = true;
        }

        /// <summary>
        /// A tapered unit is only as wide as its narrowest sampled chord. Using the centre
        /// chord alone lets a wide oval seal escape through the wall near the top or bottom
        /// of a cocktail bowl; three samples keep the decoration inside the actual liquid.
        /// </summary>
        private static float SafeBandHalfWidth(LiquidBottle bottle, float centerY,
                                               float bandHeight)
        {
            Vector2[] polygon = bottle.InteriorPolygon;
            float center = VesselFillMath.HalfWidthAt(polygon, centerY, out _);
            if (center <= 0.0001f) return 0f;

            float inset = Mathf.Max(0f, bandHeight) * 0.36f;
            float lower = VesselFillMath.HalfWidthAt(polygon, centerY - inset, out _);
            float upper = VesselFillMath.HalfWidthAt(polygon, centerY + inset, out _);
            if (lower <= 0.0001f) lower = center;
            if (upper <= 0.0001f) upper = center;
            return Mathf.Min(center, Mathf.Min(lower, upper));
        }

        /// <summary>
        /// A restrained idle glint keeps the lock readable over every drink colour without
        /// moving the bottle or competing with pour/seat animation. Only child icons move;
        /// unlock feedback remains owned by MechanicRevealPresenter.
        /// </summary>
        private void AnimateIdleLocks()
        {
            if (!Application.isPlaying || visuals.Count == 0) return;

            float time = Time.unscaledTime;
            foreach (BottleVisuals set in visuals.Values)
            {
                if (set == null || set.Bottle == null) continue;
                float phase = Mathf.Abs(set.Bottle.GetInstanceID() % 97) * 0.071f;

                for (int i = 0; i < set.UnitLocks.Count; i++)
                {
                    UnitLockVisual marker = set.UnitLocks[i];
                    if (marker == null || marker.Icon == null || !marker.Icon.enabled)
                        continue;

                    float wave = 0.5f + 0.5f * Mathf.Sin(
                        time * 2.15f + phase + i * 0.83f);
                    if (marker.Shadow != null)
                        marker.Icon.transform.localScale =
                            marker.Shadow.transform.localScale
                            * Mathf.Lerp(1f, 1.022f, wave);
                    marker.Icon.color = WithAlpha(
                        UnitLockTint, Mathf.Lerp(0.90f, 1f, wave));
                    if (marker.Rim != null)
                        marker.Rim.color = WithAlpha(
                            UnitRimTint, Mathf.Lerp(0.68f, 0.90f, wave));
                }

                if (set.ChainIcon == null || !set.ChainIcon.enabled) continue;
                float chainWave = Mathf.Sin(time * 1.45f + phase);
                set.ChainIcon.transform.localRotation =
                    Quaternion.Euler(0f, 0f, chainWave * 1.8f);
                if (set.ChainShadow != null)
                    set.ChainIcon.transform.localScale =
                        set.ChainShadow.transform.localScale
                        * (1f + Mathf.Abs(chainWave) * 0.014f);
                if (set.ChainHalo != null)
                    set.ChainHalo.color = WithAlpha(
                        ChainHaloTint, 0.74f + Mathf.Abs(chainWave) * 0.12f);
                if (set.ChainRim != null)
                    set.ChainRim.color = WithAlpha(
                        ChainRimTint, 0.82f + Mathf.Abs(chainWave) * 0.10f);
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = Mathf.Clamp01(alpha);
            return color;
        }

        private static float ArtHeight(LiquidBottle bottle)
        {
            VesselProfile profile = bottle.profile;
            if (profile != null && profile.front != null)
                return Mathf.Max(0.0001f, profile.front.bounds.size.y);
            return Mathf.Max(0.0001f, bottle.InteriorBounds.height);
        }

        private static Renderer FindVisualSource(LiquidBottle bottle)
        {
            Transform front = bottle.transform.Find("FrontGlass");
            Renderer exact = front != null ? front.GetComponent<Renderer>() : null;
            if (exact != null) return exact;

            Renderer[] renderers = bottle.GetComponentsInChildren<Renderer>(true);
            Renderer best = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer candidate = renderers[i];
                if (candidate == null || candidate.name.StartsWith(MarkerPrefix,
                                                                   StringComparison.Ordinal))
                    continue;
                if (best == null || candidate.sortingOrder > best.sortingOrder)
                    best = candidate;
            }
            return best;
        }

        private void HideAll()
        {
            foreach (BottleVisuals set in visuals.Values) Hide(set);
        }

        private static void Hide(BottleVisuals set)
        {
            if (set == null) return;
            for (int i = 0; i < set.UnitLocks.Count; i++)
                Hide(set.UnitLocks[i]);
            HideChain(set);
        }

        private static void Hide(UnitLockVisual visual)
        {
            if (visual == null) return;
            if (visual.Seal != null) visual.Seal.enabled = false;
            if (visual.Rim != null) visual.Rim.enabled = false;
            if (visual.Shadow != null) visual.Shadow.enabled = false;
            if (visual.Icon != null) visual.Icon.enabled = false;
        }

        private static void HideChain(BottleVisuals set)
        {
            if (set == null) return;
            if (set.ChainHalo != null) set.ChainHalo.enabled = false;
            if (set.ChainRim != null) set.ChainRim.enabled = false;
            if (set.ChainShadow != null) set.ChainShadow.enabled = false;
            if (set.ChainIcon != null) set.ChainIcon.enabled = false;
            if (set.ChainBadge != null) set.ChainBadge.enabled = false;
            if (set.ChainBadgeRim != null) set.ChainBadgeRim.enabled = false;
            if (set.ChainCountRenderer != null) set.ChainCountRenderer.enabled = false;
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void RebindIfNeeded()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            BartenderLevelController wanted = shelfView != null
                ? shelfView.Controller
                : controller;
            if (ReferenceEquals(wanted, controller)
                && ReferenceEquals(subscribedView, shelfView))
                return;

            Unsubscribe();
            HideAll();
            controller = wanted;
            Subscribe();
            RequestRefresh();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                    subscribedController.StateChanged -= HandleStateChanged;
                subscribedController = controller;
                if (subscribedController != null)
                    subscribedController.StateChanged += HandleStateChanged;
            }

            if (subscribedView == shelfView) return;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.PresentationChanged += HandlePresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
                subscribedController.StateChanged -= HandleStateChanged;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedController = null;
            subscribedView = null;
        }
    }

    /// <summary>
    /// Installs the additive presenter beside every authored shelf view at runtime. This
    /// follows the same scene-safe pattern as MechanicRevealPresenter and never rebuilds
    /// a scene or prefab.
    /// </summary>
    internal static class GlassLockPresenterInstaller
    {
        private static bool sceneHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            sceneHooked = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForLoadedScenes()
        {
            if (!sceneHooked)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
                sceneHooked = true;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
                InstallInScene(SceneManager.GetSceneAt(i));
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode _) =>
            InstallInScene(scene);

        private static void InstallInScene(Scene scene)
        {
            if (!Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                BartenderShelfLevelView[] views =
                    roots[i].GetComponentsInChildren<BartenderShelfLevelView>(true);
                for (int j = 0; j < views.Length; j++)
                {
                    BartenderShelfLevelView view = views[j];
                    if (view != null && view.GetComponent<GlassLockPresenter>() == null)
                        view.gameObject.AddComponent<GlassLockPresenter>();
                }
            }
        }
    }

    /// <summary>
    /// Exact lock and chain rasterisation ported from BartenderSortGame/BsIcons.cs.
    /// The cache produces white-alpha sprites that each presenter tints independently.
    /// </summary>
    internal static class BartenderLockIcons
    {
        private const int Size = 96;
        private static readonly Dictionary<string, Sprite> Cache =
            new Dictionary<string, Sprite>();

        public static Sprite Lock => Get("lock", DrawLock);
        public static Sprite Chain => Get("chain", DrawChain);
        public static Sprite Circle => Get("circle", DrawCircle);
        public static Sprite SoftDisc => Get("soft-disc", DrawSoftDisc);
        public static Sprite Ring => Get("ring", DrawRing);

        private static Sprite Get(string key, Action<float[]> draw)
        {
            if (Cache.TryGetValue(key, out Sprite sprite) && sprite != null) return sprite;

            var mask = new float[Size * Size];
            draw(mask);
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
                name = "BartenderLock_" + key
            };
            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255,
                    (byte)Mathf.Clamp(mask[i] * 255f, 0f, 255f));
            texture.SetPixels32(pixels);
            texture.Apply(false);

            sprite = Sprite.Create(texture, new Rect(0, 0, Size, Size),
                                   new Vector2(0.5f, 0.5f));
            sprite.name = "BartenderLock_" + key;
            sprite.hideFlags = HideFlags.DontSave;
            Cache[key] = sprite;
            return sprite;
        }

        private static void Line(float[] mask, float x0, float y0, float x1, float y1,
                                 float thickness)
        {
            Vector2 a = new Vector2(x0 * Size, y0 * Size);
            Vector2 b = new Vector2(x1 * Size, y1 * Size);
            float radius = thickness * Size * 0.5f;
            int minX = Mathf.Max(0, (int)(Mathf.Min(a.x, b.x) - radius - 2));
            int maxX = Mathf.Min(Size, (int)(Mathf.Max(a.x, b.x) + radius + 2));
            int minY = Mathf.Max(0, (int)(Mathf.Min(a.y, b.y) - radius - 2));
            int maxY = Mathf.Min(Size, (int)(Mathf.Max(a.y, b.y) + radius + 2));
            Vector2 ab = b - a;
            float lengthSquared = Mathf.Max(0.0001f, ab.sqrMagnitude);

            for (int y = minY; y < maxY; y++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    var point = new Vector2(x + 0.5f, y + 0.5f);
                    float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
                    float distance = Vector2.Distance(point, a + ab * t);
                    float coverage = 1f - Smooth(radius - 1f, radius + 0.6f, distance);
                    int index = y * Size + x;
                    if (coverage > mask[index]) mask[index] = coverage;
                }
            }
        }

        private static void Arc(float[] mask, float centerX, float centerY, float radius,
                                float thickness, float angleFrom, float angleTo,
                                int segments = 40)
        {
            float previousX = 0f;
            float previousY = 0f;
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Lerp(angleFrom, angleTo, i / (float)segments)
                            * Mathf.Deg2Rad;
                float x = centerX + Mathf.Cos(angle) * radius;
                float y = centerY + Mathf.Sin(angle) * radius;
                if (i > 0)
                    Line(mask, previousX, previousY, x, y, thickness);
                previousX = x;
                previousY = y;
            }
        }

        private static float Smooth(float from, float to, float value)
        {
            if (to - from < 0.0001f) return value < from ? 0f : 1f;
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static void DrawLock(float[] mask)
        {
            Arc(mask, 0.5f, 0.56f, 0.17f, 0.075f, 0f, 180f);
            Line(mask, 0.33f, 0.56f, 0.33f, 0.48f, 0.075f);
            Line(mask, 0.67f, 0.56f, 0.67f, 0.48f, 0.075f);
            Box(mask, 0.26f, 0.14f, 0.74f, 0.50f);
            Punch(mask, 0.5f, 0.34f, 0.065f);
        }

        private static void DrawChain(float[] mask)
        {
            Arc(mask, 0.30f, 0.50f, 0.155f, 0.075f, 0f, 360f);
            Arc(mask, 0.70f, 0.50f, 0.155f, 0.075f, 0f, 360f);
            Line(mask, 0.45f, 0.50f, 0.55f, 0.50f, 0.075f);
        }

        private static void DrawCircle(float[] mask)
        {
            Vector2 center = Vector2.one * (Size * 0.5f);
            float radius = Size * 0.48f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float distance = Vector2.Distance(
                        new Vector2(x + 0.5f, y + 0.5f), center);
                    mask[y * Size + x] = 1f - Smooth(
                        radius - 1f, radius + 0.6f, distance);
                }
            }
        }

        private static void DrawSoftDisc(float[] mask)
        {
            Vector2 center = Vector2.one * (Size * 0.5f);
            float radius = Size * 0.48f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float distance = Vector2.Distance(
                        new Vector2(x + 0.5f, y + 0.5f), center);
                    mask[y * Size + x] = 0.82f * (1f - Smooth(
                        radius * 0.74f, radius + 0.6f, distance));
                }
            }
        }

        private static void DrawRing(float[] mask)
        {
            DrawCircle(mask);
            Punch(mask, 0.5f, 0.5f, 0.35f);
        }

        private static void Box(float[] mask, float x0, float y0, float x1, float y1)
        {
            int minX = Mathf.Clamp(Mathf.FloorToInt(x0 * Size), 0, Size);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(x1 * Size), 0, Size);
            int minY = Mathf.Clamp(Mathf.FloorToInt(y0 * Size), 0, Size);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(y1 * Size), 0, Size);
            for (int y = minY; y < maxY; y++)
                for (int x = minX; x < maxX; x++)
                    mask[y * Size + x] = 1f;
        }

        private static void Punch(float[] mask, float centerX, float centerY, float radius)
        {
            float pixelX = centerX * Size;
            float pixelY = centerY * Size;
            float pixelRadius = radius * Size;
            int minX = Mathf.Max(0, (int)(pixelX - pixelRadius - 2));
            int maxX = Mathf.Min(Size, (int)(pixelX + pixelRadius + 2));
            int minY = Mathf.Max(0, (int)(pixelY - pixelRadius - 2));
            int maxY = Mathf.Min(Size, (int)(pixelY + pixelRadius + 2));
            Vector2 center = new Vector2(pixelX, pixelY);
            for (int y = minY; y < maxY; y++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    float distance = Vector2.Distance(
                        new Vector2(x + 0.5f, y + 0.5f), center);
                    float inside = 1f - Smooth(
                        pixelRadius - 1f, pixelRadius + 0.6f, distance);
                    int index = y * Size + x;
                    mask[index] = Mathf.Min(mask[index], 1f - inside);
                }
            }
        }
    }
}
