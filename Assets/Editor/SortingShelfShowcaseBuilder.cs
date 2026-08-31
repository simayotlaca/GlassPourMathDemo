using System;
using System.Collections.Generic;
using System.IO;
using BartenderSort.Core;
using LiquidSort;
using LiquidSort.Levels;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Creates the sorting-game scene as ordinary, serialized scene objects. There are
/// deliberately no prefab instances and no runtime object generator: every Royal glass,
/// shelf plank, post, order card, button and level-system link is saved directly into the
/// scene, so after a build an artist can carry on arranging the hierarchy by hand and this
/// file could be deleted without the scene noticing.
///
/// WHAT THIS FILE IS FOR — and it is not "place things".
///
/// Placing things by hand is cheap. Keeping the placements CONSISTENT WITH EACH OTHER is
/// not, and that is where the previous pass broke: the shelf surface sat at y=0.80 while
/// the order cards sat at y=0.85, so every glass on the top shelf grew straight through
/// the cards and into the delivery rail. Nobody wrote a wrong number; the numbers simply
/// did not know about each other.
///
/// So the layout here is SOLVED, not typed. A vertical budget is declared once
/// (<see cref="TopBarCenterY"/> … <see cref="BottomBarCenterY"/>); the builder then
/// measures the real artwork — how tall the tallest vessel actually draws, how thick a
/// plank actually is, how far apart the posts actually stand — and derives the shelf
/// surfaces, the column spacings and the four glass scales from that budget. Change one
/// band and everything downstream moves with it. Change a drawing and the same thing
/// happens, without touching this file.
///
/// The result is written into <see cref="BartenderShelfLevelView"/> as plain serialized
/// numbers. The runtime never re-derives them: it reads what the build decided, exactly
/// as it would read what an artist decided.
/// </summary>
[InitializeOnLoad]
public static class SortingShelfShowcaseBuilder
{
    public const string ScenePath = "Assets/LiquidSort/SortingShelfShowcase.unity";
    public const string PortablePrefabPath =
        "Assets/LiquidSort/Prefabs/BartenderShelfRig.prefab";

    private const string RequestPath = "Temp/sorting-shelf-showcase.req";
    private const string DonePath = "Temp/sorting-shelf-showcase.done";
    private const string PreviewPath = "Temp/SortingShelfShowcase.png";
    private const string BackdropMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/SortingShelfBackdrop.mat";
    private const string GlassLightMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/RoyalGlassLight.mat";
    private const string PourStreamMaterialPath =
        "Assets/LiquidSort/Materials/PourStream.mat";
    private const string SourceGlassScenePath =
        "Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity";

    private const string ArtRoot = "Assets/LiquidSort/RoyalGlassLab/Art";
    private const string ShelfPlankPath = ArtRoot + "/ShelfParts/ShelfPlank_Burgundy_v1.png";
    private const string ShelfPostPath = ArtRoot + "/ShelfParts/ShelfPost_BurgundyGold_v1.png";
    private const string CheckBadgePath = ArtRoot + "/CheckBadge_Clean.png";
    private const string SkyPath = ArtRoot + "/UpperStage/UpperSkyBackdrop_v1.png";
    private const string ArchPath = ArtRoot + "/UpperStage/UpperStoneArch_v1.png";
    private const string CurtainPath = ArtRoot + "/UpperStage/UpperCurtainPair_v1.png";
    private const string ColumnPath = ArtRoot + "/DeliveryTop/DeliveryColumn_Left_v1.png";
    private const string RailBasePath = ArtRoot + "/DeliveryTop/DeliveryRail_Base_v1.png";
    private const string RailGlowPath = ArtRoot + "/DeliveryTop/DeliveryRail_GuideGlow_v1.png";
    private const string PortalBackPath = ArtRoot + "/DeliveryTop/DeliveryPortal_Back_v4.png";
    private const string PortalFrontPath = ArtRoot + "/DeliveryTop/DeliveryPortal_Front_v4.png";
    private const string PortalOccluderPath =
        ArtRoot + "/DeliveryTop/DeliveryPortal_Occluder_v4.png";
    private const string OrderCardArtRoot = ArtRoot + "/OrderCards/Final";
    private const string OrderCardPanelPath =
        OrderCardArtRoot + "/OrderCard_Panel_Cream.png";
    private const string OrderCardFramePath =
        OrderCardArtRoot + "/OrderCard_Frame_Purple.png";
    private const string OrderCardClipPath =
        OrderCardArtRoot + "/OrderCard_Clip_Gold.png";
    private const string OrderCardRailPath =
        OrderCardArtRoot + "/OrderCard_Rail_RedGold.png";
    private const string OrderCardChipFillPath =
        OrderCardArtRoot + "/OrderCard_ColorPip_Fill_Tintable.png";
    private const string OrderCardChipRimPath =
        OrderCardArtRoot + "/OrderCard_ColorPip_Rim_Gold.png";

    private const string ShotProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/ShotRoyal.asset";
    private const string CocktailProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/CocktailRoyal.asset";
    private const string MugProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/MugRoyal.asset";
    private const string TallProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/TumblerRoyal.asset";
    private const string BeerProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/BeerRoyal.asset";
    private const string PalettePath =
        "Assets/LiquidSort/LevelSystem/Resources/BsPalette.asset";
    private const string PreviewLevelPath =
        "Assets/LiquidSort/LevelSystem/Resources/Levels/Level_004.asset";

    // Kept separate from the canonical Royal lab layer (29).
    private const int StageLayer = 28;

    // ---- The design frame -------------------------------------------------------
    //
    // 720x1280 portrait at orthographic size 6. Everything below is expressed in the
    // world units that frame implies, so a number here can be read straight off the
    // design: y=+6 is the top of the screen, y=-6 the bottom.

    private const float CameraHalfHeight = 6.00f;
    private const int DesignWidth = 720;
    private const int DesignHeight = 1280;
    private const float FrameHalfWidth =
        CameraHalfHeight * DesignWidth / (float)DesignHeight;      // 3.375
    /// <summary>World units per design pixel; the HUD canvases are scaled by this.</summary>
    private const float WorldPerDesignPixel = 2f * CameraHalfHeight / DesignHeight;

    // ---- Vertical budget --------------------------------------------------------
    //
    // Five bands, top to bottom. These are THE authored numbers of this scene; every
    // other vertical value in the file is derived from them.

    private const float TopBarCenterY = 5.42f;
    private const float TopBarHeight = 0.86f;
    /// <summary>Surface a delivered glass stands on while it waits at the door.</summary>
    private const float DeliveryRailY = 3.26f;
    private const float OrderStripCenterY = 1.66f;
    private const float OrderCardWidth = 1.52f;
    private const float OrderCardHeight = 1.80f;
    private const float OrderCardSpacing = 2.06f;
    private const float PlayfieldTopY = 0.72f;
    private const float PlayfieldBottomY = -4.62f;
    private const float BottomBarCenterY = -5.32f;
    private const float BottomButtonDiameter = 1.24f;
    private const float BottomButtonSpacing = 1.78f;

    // ---- Playfield solve inputs -------------------------------------------------

    private const float ShelfScaleX = 1.55f;
    private const float ShelfScaleY = 1.10f;
    private const float PostScaleX = 0.72f;
    private const float PostCenterX = 3.02f;
    /// <summary>Gap between the top of a glass and the plank hanging above it.</summary>
    private const float RowClearance = 0.14f;
    /// <summary>Gap between two neighbouring glasses in the same row.</summary>
    private const float ColumnClearance = 0.10f;
    /// <summary>
    /// Ceiling on the derived glass scale. A four-glass level has height to spare and
    /// would otherwise blow the vessels up past the size their artwork was drawn for.
    /// </summary>
    private const float MaximumGlassScale = 1.10f;
    private const float OpticalSeatInset = 0.02f;

    // ---- Draw order -------------------------------------------------------------
    //
    // One list, so the sandwich the delivery portal builds and the lift the entrance
    // uses can be checked against each other by reading rather than by running.

    private const int BackdropOrder = -100;
    private const int SkyOrder = -40;
    // The rail is FURNITURE STANDING IN FRONT of the arch, not a stripe painted on the
    // back wall. Drawn behind it, the arch legs ate its gold end-caps and it read as a
    // red line. Everything that stands on the counter is ordered above it in turn.
    private const int RailBaseOrder = 44;
    private const int RailGlowOrder = 46;
    private const int PostOrder = 5;
    private const int PlankOrder = 10;
    /// <summary>Above every renderer BottleShell publishes (-1 … 8), below the planks' row.</summary>
    private const int CheckBadgeOrder = 12;
    private const int TravelStreakOrder = 47;
    private const int OrderCardCanvasOrder = 30;
    private const int ArchOrder = 40;
    private const int ColumnOrder = 48;
    private const int CurtainOrder = 50;
    // The portal window has to be wider than the vessel's own 14-order stack, or the
    // arch interior would draw over the glass it is meant to be swallowing.
    private const int PortalBackOrder = 100;
    private const int PortalGlowOrder = 101;
    private const int PortalOccluderOrder = 130;
    private const int PortalFrontOrder = 132;
    private const int ScreenCanvasOrder = 200;
    /// <summary>Entrance/lift boost. Must clear the planks and the order cards.</summary>
    private const int EntranceSortingBoost = 60;

    // ---- Animation timing -------------------------------------------------------

    private const bool EntranceEnabled = true;
    private const float EntranceDropHeight = 7.60f;
    private const float EntranceDropDuration = 0.32f;
    private const float EntranceGlassStagger = 0.055f;
    private const float EntranceRowStagger = 0.12f;
    private const float EntranceLandingSquash = 0.13f;
    private const float EntranceSettleDuration = 0.20f;
    private const float ShelfFadeDuration = 0.22f;
    private const float ReseatDuration = 0.22f;

    private const float PortalLiftDuration = 0.32f;
    private const float PortalApproachDuration = 0.18f;
    private const float PortalEntryDuration = 0.24f;
    private const float PortalHideDuration = 0.13f;
    private const float PortalBounceDuration = 0.16f;
    private const float PortalEntryDepth = 0.68f;
    private const float PortalEntryScale = 0.72f;
    private const float PortalHideScale = 0.35f;
    private const float PortalEntryTilt = 5f;

    // ---- Palette ----------------------------------------------------------------

    private static readonly Color CardCream = Hex(0xF3E4C4);
    private static readonly Color CardRim = Hex(0x6C4BB0);
    private static readonly Color CardClipHole = Hex(0xE9A93C);
    private static readonly Color ButtonFace = Hex(0x4B34A8);
    private static readonly Color ButtonRim = Hex(0xE9A93C);
    private static readonly Color ButtonGlyph = Hex(0xFFF6E2);
    private static readonly Color BadgeFace = Hex(0x3A2380);
    private static readonly Color BadgeText = Hex(0xFFF1CF);
    private static readonly Color OverlayDim = new Color(0.03f, 0.01f, 0.08f, 0.78f);

    private const int ExpectedPoolSize =
        BartenderShelfLevelView.FullCampaignShotPoolSize
        + BartenderShelfLevelView.FullCampaignCocktailPoolSize
        + BartenderShelfLevelView.FullCampaignLattePoolSize
        + BartenderShelfLevelView.FullCampaignTumblerPoolSize
        + BartenderShelfLevelView.FullCampaignBiraPoolSize;

    private static bool refreshed;
    private static readonly Dictionary<int, Rect> VisualBoundsCache =
        new Dictionary<int, Rect>();

    static SortingShelfShowcaseBuilder() => EditorApplication.update += PollRequest;

    private static void PollRequest()
    {
        if (!File.Exists(RequestPath)) { refreshed = false; return; }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating
            || EditorApplication.isPlayingOrWillChangePlaymode) return;

        // Give Unity one complete import/compile tick before resolving the new shader,
        // sprites and editor type.
        if (!refreshed)
        {
            refreshed = true;
            AssetDatabase.Refresh();
            return;
        }

        refreshed = false;
        File.Delete(RequestPath);
        try
        {
            ShelfSolve solve = Build();
            File.WriteAllText(DonePath,
                "ok\nscene=" + ScenePath
                + "\npreview=" + PreviewPath
                + "\nprefab=" + PortablePrefabPath
                + "\nactivePreviewGlasses=6\nmanualGlassPool=" + ExpectedPoolSize
                + "\nlayout=3+3\nshelfRows=3\nposts=4\nprefabInstances=0\n"
                + "levelSystem=connected\npourAnimation=connected\n"
                + "orderCards=3\nboosters=3\ndeliveryPortal=connected\n"
                + "checkBadges=" + ExpectedPoolSize + "\n"
                + solve.Describe());
        }
        catch (Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    [MenuItem("Tools/LiquidSort/Rebuild Sorting Shelf Showcase")]
    public static void RebuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    /// <summary>
    /// Non-interactive entry point used to verify the authored scene in an isolated
    /// Unity project copy while the user's live editor remains open.
    /// </summary>
    public static void BuildForAutomation() => Build();

    // =================================================================================
    //  Layout solve
    // =================================================================================

    /// <summary>
    /// Everything the playfield band implies once the artwork has been measured. This is
    /// the whole point of the file: the four glass scales and three surface heights are a
    /// consequence of the budget, not four and three more numbers to keep in sync.
    /// </summary>
    private sealed class ShelfSolve
    {
        public float PlankBand;
        public float TallestGlass;
        public float WidestGlass;
        public float InnerWidth;
        public Vector2 TwoRowSurfaces;
        public Vector3 ThreeRowSurfaces;
        public float SpacingTwo;
        public float SpacingThree;
        public float SpacingFour;
        public float ScaleTwoRow;
        public float ScaleThreeRow;
        public float ScaleFourInTwoRows;
        public float ScaleFourInThreeRows;

        public string Describe() =>
            $"plankBand={PlankBand:0.###}\ntallestGlass={TallestGlass:0.###}\n"
            + $"widestGlass={WidestGlass:0.###}\ninnerWidth={InnerWidth:0.###}\n"
            + $"twoRowSurfaces={TwoRowSurfaces.x:0.###},{TwoRowSurfaces.y:0.###}\n"
            + $"threeRowSurfaces={ThreeRowSurfaces.x:0.###},{ThreeRowSurfaces.y:0.###},"
            + $"{ThreeRowSurfaces.z:0.###}\n"
            + $"spacing={SpacingTwo:0.###},{SpacingThree:0.###},{SpacingFour:0.###}\n"
            + $"glassScale={ScaleTwoRow:0.###},{ScaleThreeRow:0.###},"
            + $"{ScaleFourInTwoRows:0.###},{ScaleFourInThreeRows:0.###}\n";
    }

    /// <summary>
    /// Divides the playfield band into <paramref name="rows"/> equal slots. Each slot
    /// holds one plank plus the glasses standing on the plank below it, so the surface
    /// heights and the height available to a glass fall out of the same division and can
    /// never contradict each other.
    /// </summary>
    private static float SlotHeight(int rows) =>
        (PlayfieldTopY - PlayfieldBottomY) / rows;

    private static float SurfaceY(int rows, int row, float plankBand) =>
        PlayfieldTopY - SlotHeight(rows) * (row + 1) + plankBand;

    private static float GlassHeightBudget(int rows, float plankBand) =>
        SlotHeight(rows) - plankBand - RowClearance;

    private static ShelfSolve SolveLayout(
        float plankVisualHeight, float postVisualWidth,
        IEnumerable<List<BartenderShelfLevelView.GlassBinding>> pools)
    {
        var solve = new ShelfSolve
        {
            PlankBand = plankVisualHeight * ShelfScaleY,
            InnerWidth = 2f * (PostCenterX - postVisualWidth * PostScaleX * 0.5f)
        };

        foreach (List<BartenderShelfLevelView.GlassBinding> pool in pools)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                BartenderShelfLevelView.GlassBinding binding = pool[i];
                Rect visual = SpriteVisualBounds(binding.placementRenderer.sprite);
                Vector3 authored = binding.authoredLocalScale;
                solve.TallestGlass = Mathf.Max(solve.TallestGlass,
                    visual.height * Mathf.Abs(authored.y));
                solve.WidestGlass = Mathf.Max(solve.WidestGlass,
                    visual.width * Mathf.Abs(authored.x));
            }
        }
        if (solve.TallestGlass <= 0f || solve.WidestGlass <= 0f)
            throw new InvalidOperationException(
                "Could not measure the Royal vessels; the layout cannot be solved.");

        solve.TwoRowSurfaces = new Vector2(
            SurfaceY(2, 0, solve.PlankBand), SurfaceY(2, 1, solve.PlankBand));
        solve.ThreeRowSurfaces = new Vector3(
            SurfaceY(3, 0, solve.PlankBand),
            SurfaceY(3, 1, solve.PlankBand),
            SurfaceY(3, 2, solve.PlankBand));

        solve.SpacingTwo = solve.InnerWidth / 2f;
        solve.SpacingThree = solve.InnerWidth / 3f;
        solve.SpacingFour = solve.InnerWidth / 4f;

        float heightAtTwo = GlassHeightBudget(2, solve.PlankBand) / solve.TallestGlass;
        float heightAtThree = GlassHeightBudget(3, solve.PlankBand) / solve.TallestGlass;
        float widthAtThree = WidthFit(solve, 3);
        float widthAtFour = WidthFit(solve, 4);

        solve.ScaleTwoRow = Clamp(Mathf.Min(heightAtTwo, widthAtThree));
        solve.ScaleThreeRow = Clamp(Mathf.Min(heightAtThree, widthAtThree));
        solve.ScaleFourInTwoRows = Clamp(Mathf.Min(heightAtTwo, widthAtFour));
        solve.ScaleFourInThreeRows = Clamp(Mathf.Min(heightAtThree, widthAtFour));
        return solve;
    }

    private static float WidthFit(ShelfSolve solve, int across) =>
        (solve.InnerWidth / across - ColumnClearance) / solve.WidestGlass;

    private static float Clamp(float scale) =>
        Mathf.Clamp(scale, 0.15f, MaximumGlassScale);

    // =================================================================================
    //  Build
    // =================================================================================

    private static ShelfSolve Build()
    {
        VisualBoundsCache.Clear();
        foreach (string path in new[]
                 {
                     ShelfPlankPath, ShelfPostPath, CheckBadgePath, SkyPath, ArchPath,
                     CurtainPath, ColumnPath, RailBasePath, RailGlowPath,
                     PortalBackPath, PortalFrontPath, PortalOccluderPath
                 })
            ConfigureStageSprite(path);
        BartenderUiArtFactory.EnsureUiArt();

        Scene previous = SceneManager.GetActiveScene();
        bool canRestorePrevious = previous.IsValid() && previous.isLoaded &&
                                  !string.IsNullOrEmpty(previous.path);
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool alreadyOpen = scene.IsValid() && scene.isLoaded;
        if (alreadyOpen)
        {
            foreach (GameObject sceneRoot in scene.GetRootGameObjects())
                UnityEngine.Object.DestroyImmediate(sceneRoot);
        }
        else
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                canRestorePrevious ? NewSceneMode.Additive : NewSceneMode.Single);
            // Give the destination a real path before opening the Royal source scene
            // additively. Unity refuses additive scene operations beside an unsaved,
            // untitled scene in batch mode.
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not initialize " + ScenePath);
        }
        EditorSceneManager.SetActiveScene(scene);

        Scene sourceScene = SceneManager.GetSceneByPath(SourceGlassScenePath);
        bool sourceAlreadyOpen = sourceScene.IsValid() && sourceScene.isLoaded;

        try
        {
            if (!sourceAlreadyOpen)
                sourceScene = EditorSceneManager.OpenScene(SourceGlassScenePath,
                    OpenSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene);

            // NewScene/OpenScene can unload editor-only asset handles that were not yet
            // serialized anywhere. Resolve every authored asset only after both scenes
            // are stable so Level/Palette references remain valid in batch mode too.
            var art = new StageArt();
            VesselProfile shot = Load<VesselProfile>(ShotProfilePath);
            VesselProfile cocktail = Load<VesselProfile>(CocktailProfilePath);
            VesselProfile mug = Load<VesselProfile>(MugProfilePath);
            VesselProfile tall = Load<VesselProfile>(TallProfilePath);
            VesselProfile beer = Load<VesselProfile>(BeerProfilePath);
            BsPalette palette = Load<BsPalette>(PalettePath);
            BsLevel previewLevel = Load<BsLevel>(PreviewLevelPath);
            Material backdropMaterial = EnsureBackdropMaterial();
            Material glassLight = Load<Material>(GlassLightMaterialPath);
            Font font = ResolveBuiltinFont();

            GameObject shotSource = FindSceneObject(sourceScene, "01 Shot Royal");
            GameObject cocktailSource = FindSceneObject(sourceScene, "02 Cocktail Royal");
            GameObject mugSource = FindSceneObject(sourceScene, "03 Mug Royal");
            GameObject tallSource = FindSceneObject(sourceScene, "04 Tumbler Royal");
            GameObject beerSource = FindSceneObject(sourceScene, "05 Beer Royal");
            ValidateSourceGlass(shotSource, shot);
            ValidateSourceGlass(cocktailSource, cocktail);
            ValidateSourceGlass(mugSource, mug);
            ValidateSourceGlass(tallSource, tall);
            ValidateSourceGlass(beerSource, beer);

            Camera camera = BuildCamera();
            BuildEventSystem();

            var root = new GameObject("Portrait Design Frame 720x1280 - Hand Authored");

            Transform environment = Folder(root.transform, "01 Environment - Hand Authored");
            BuildBackdrop(environment, backdropMaterial);
            ShelfPieces shelf = BuildShelf(environment, art);

            DeliveryStage delivery = BuildDeliveryStage(
                Folder(root.transform, "02 Upper Delivery - Hand Authored"), art);

            Transform glasses = Folder(root.transform,
                $"03 Glasses - Manual Royal Pools ({ExpectedPoolSize})");
            var badges = new List<DeliveryBadgePresenter.BadgeBinding>(ExpectedPoolSize);
            List<BartenderShelfLevelView.GlassBinding> shots = BuildVesselPool(
                shotSource, glasses, "01 Shot Pool - 4 Direct Scene Objects", "Shot",
                BartenderShelfLevelView.FullCampaignShotPoolSize, null, 0f, art, badges);
            List<BartenderShelfLevelView.GlassBinding> cocktails = BuildVesselPool(
                cocktailSource, glasses, "02 Cocktail Pool - 5 Direct Scene Objects",
                "Cocktail", BartenderShelfLevelView.FullCampaignCocktailPoolSize,
                glassLight, 0.32f, art, badges);
            List<BartenderShelfLevelView.GlassBinding> lattes = BuildVesselPool(
                mugSource, glasses, "03 Latte Mug Pool - 6 Direct Scene Objects",
                "Latte Mug", BartenderShelfLevelView.FullCampaignLattePoolSize,
                null, 0f, art, badges);
            List<BartenderShelfLevelView.GlassBinding> tumblers = BuildVesselPool(
                tallSource, glasses, "04 Tumbler Pool - 8 Direct Scene Objects", "Tumbler",
                BartenderShelfLevelView.FullCampaignTumblerPoolSize, glassLight, 0.26f,
                art, badges);
            List<BartenderShelfLevelView.GlassBinding> biras = BuildVesselPool(
                beerSource, glasses, "05 Five-Unit Handled Pool - 11 Direct Scene Objects",
                "Beer", BartenderShelfLevelView.FullCampaignBiraPoolSize, glassLight,
                0.26f, art, badges);

            ShelfSolve solve = SolveLayout(
                SpriteVisualBounds(art.Plank).height,
                SpriteVisualBounds(art.Post).width,
                new[] { shots, cocktails, lattes, tumblers, biras });
            ApplyMeasuredBadgeScale(badges, solve);

            List<OrderCardView.GlassIcon> icons = BuildGlassIconTable(
                shot, cocktail, mug, tall, beer);
            OrderStrip strip = BuildOrderStrip(root.transform, art, icons, camera);
            ScreenControls controls = BuildScreenControls(root.transform, art, font,
                camera);

            Transform systems = Folder(root.transform,
                "06 Level System - Serialized References");
            LevelRig rig = BuildLevelRig(systems, root.transform, palette,
                shelf, delivery, strip, controls, shots, cocktails, lattes, tumblers,
                biras, badges, icons, solve);

            var note = new GameObject(
                $"NOTE - Level, {ExpectedPoolSize} glasses, 3 planks, 4 posts, 3 order "
                + "cards and 3 boosters are direct scene links");
            note.transform.SetParent(root.transform, false);

            SetLayerRecursively(root.transform, StageLayer);

            if (!rig.ShelfView.ValidateFullCampaignBindings(out string bindingError))
                throw new InvalidOperationException(bindingError);
            if (!rig.ShelfView.TryPresent(previewLevel, BsBoard.FromLevel(previewLevel),
                    palette))
                throw new InvalidOperationException(rig.ShelfView.LastError);

            PreviewHud(strip, controls, previewLevel, palette);
            Validate(scene, rig, solve, art, cocktail, tall, beer, glassLight);

            // The safe-area fitter is attached last and its reference pose is written
            // explicitly. Letting it capture on its own would bake whatever aspect the
            // editor's Game View happened to have into the scene.
            AttachSafeAreaFitter(camera, root.transform);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);

            if (SystemInfo.graphicsDeviceType
                != UnityEngine.Rendering.GraphicsDeviceType.Null)
                RenderPreview(camera);
            else
                Debug.Log("LiquidSort: sorting preview skipped because no graphics "
                        + "device is available.");
            // A prefab cannot hold a reference to a scene object outside its root. The
            // canvases are released here, after the scene has been saved with them bound,
            // so the authoring file shows a real HUD and the prefab resolves one instead.
            strip.Canvas.worldCamera = null;
            controls.Canvas.worldCamera = null;
            SavePortablePrefab(root);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidSort: hand-authored sorting shelf showcase and portable rig "
                    + "created.\n" + solve.Describe());
            return solve;
        }
        finally
        {
            if (canRestorePrevious)
                EditorSceneManager.SetActiveScene(previous);
            if (!alreadyOpen && canRestorePrevious)
                EditorSceneManager.CloseScene(scene, true);
            if (!sourceAlreadyOpen && sourceScene.IsValid() && sourceScene.isLoaded)
                EditorSceneManager.CloseScene(sourceScene, true);
        }
    }

    /// <summary>Every sprite the stage is made of, resolved once.</summary>
    private sealed class StageArt
    {
        public readonly Sprite Plank = Load<Sprite>(ShelfPlankPath);
        public readonly Sprite Post = Load<Sprite>(ShelfPostPath);
        public readonly Sprite CheckBadge = Load<Sprite>(CheckBadgePath);
        public readonly Sprite Sky = Load<Sprite>(SkyPath);
        public readonly Sprite Arch = Load<Sprite>(ArchPath);
        public readonly Sprite Curtain = Load<Sprite>(CurtainPath);
        public readonly Sprite Column = Load<Sprite>(ColumnPath);
        public readonly Sprite RailBase = Load<Sprite>(RailBasePath);
        public readonly Sprite RailGlow = Load<Sprite>(RailGlowPath);
        public readonly Sprite PortalBack = Load<Sprite>(PortalBackPath);
        public readonly Sprite PortalFront = Load<Sprite>(PortalFrontPath);
        public readonly Sprite PortalOccluder = Load<Sprite>(PortalOccluderPath);

        public readonly Sprite OrderCardPanel = Load<Sprite>(OrderCardPanelPath);
        public readonly Sprite OrderCardFrame = Load<Sprite>(OrderCardFramePath);
        public readonly Sprite OrderCardClip = Load<Sprite>(OrderCardClipPath);
        public readonly Sprite OrderCardRail = Load<Sprite>(OrderCardRailPath);
        public readonly Sprite OrderChipFill = Load<Sprite>(OrderCardChipFillPath);
        public readonly Sprite OrderChipRim = Load<Sprite>(OrderCardChipRimPath);
        public readonly Sprite CardPanel =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardPanelPath);
        public readonly Sprite CardEdge =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardEdgePath);
        public readonly Sprite InteriorPlaceholder =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardClipPath);
        public readonly Sprite Pill =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.PillPath);
        public readonly Sprite Disc =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.DiscPath);
        public readonly Sprite DiscRing =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.DiscRingPath);
        public readonly Sprite Chip =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.ChipPath);
        public readonly Sprite GlyphUndo =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.GlyphUndoPath);
        public readonly Sprite GlyphShuffle =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.GlyphShufflePath);
        public readonly Sprite GlyphPlus =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.GlyphPlusPath);
        public readonly Sprite GlyphGear =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.GlyphGearPath);
    }

    private static Camera BuildCamera()
    {
        var cameraObject = new GameObject("Main Camera - Portrait 720x1280");
        cameraObject.layer = StageLayer;
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);

        var camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = CameraHalfHeight;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex(0x080515);
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 100f;
        camera.allowHDR = false;
        camera.cullingMask = 1 << StageLayer;
        cameraObject.AddComponent<AudioListener>();
        return camera;
    }

    /// <summary>
    /// Buttons need one. It stays outside the portable rig for the same reason the camera
    /// does: a host scene owns exactly one event system and the prefab must not bring a
    /// second one with it.
    /// </summary>
    private static void BuildEventSystem()
    {
        var go = new GameObject("Event System - Host Provided");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
    }

    private static void BuildBackdrop(Transform parent, Material material)
    {
        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backdrop.name = "Backdrop - Quiet Purple Gradient";
        backdrop.transform.SetParent(parent, false);
        backdrop.transform.localPosition = new Vector3(0f, 0f, 5f);
        backdrop.transform.localScale = new Vector3(20f, 20f, 1f);
        backdrop.layer = StageLayer;
        Collider collider = backdrop.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        MeshRenderer renderer = backdrop.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingOrder = BackdropOrder;
    }

    // ---- Shelf ------------------------------------------------------------------

    private sealed class ShelfPieces
    {
        public SpriteRenderer TopPlank;
        public SpriteRenderer MiddlePlank;
        public SpriteRenderer BottomPlank;
        public Transform TopSeatAnchor;
        public Transform MiddleSeatAnchor;
        public Transform BottomSeatAnchor;
        public SpriteRenderer UpperLeftPost;
        public SpriteRenderer UpperRightPost;
        public SpriteRenderer LowerLeftPost;
        public SpriteRenderer LowerRightPost;
    }

    /// <summary>
    /// Builds three identical planks and four posts. Their heights are not authored here:
    /// <see cref="BartenderShelfLevelView.ApplyShelfLayout"/> moves each plank so its seat
    /// anchor lands on the solved surface and stretches each post to span the gap. What
    /// this method fixes is only what the level cannot change — the drawing, the width and
    /// the draw order.
    /// </summary>
    private static ShelfPieces BuildShelf(Transform parent, StageArt art)
    {
        Transform structure = Folder(parent,
            "Shelf Structure - 3 Rows + 2 Spans (Direct Links)");
        Transform shelves = Folder(structure, "01 Planks - Level Controlled");
        Transform spans = Folder(structure, "02 Post Spans - Level Controlled");
        Transform upperSpan = Folder(spans, "Span 01 - Between Row 1 and Row 2");
        Transform lowerSpan = Folder(spans, "Span 02 - Between Row 2 and Row 3");

        var plankScale = new Vector2(ShelfScaleX, ShelfScaleY);
        var postScale = new Vector2(PostScaleX, 1f);
        var pieces = new ShelfPieces
        {
            TopPlank = BuildSprite(shelves, "Shelf Row 01 - Direct Plank Asset",
                art.Plank, Vector2.zero, plankScale, PlankOrder),
            MiddlePlank = BuildSprite(shelves, "Shelf Row 02 - Direct Plank Asset",
                art.Plank, Vector2.zero, plankScale, PlankOrder),
            BottomPlank = BuildSprite(shelves, "Shelf Row 03 - Direct Plank Asset",
                art.Plank, Vector2.zero, plankScale, PlankOrder),
            UpperLeftPost = BuildSprite(upperSpan, "Post Span 01 Left - Direct Post Asset",
                art.Post, new Vector2(-PostCenterX, 0f), postScale, PostOrder),
            UpperRightPost = BuildSprite(upperSpan, "Post Span 01 Right - Direct Post Asset",
                art.Post, new Vector2(PostCenterX, 0f), postScale, PostOrder),
            LowerLeftPost = BuildSprite(lowerSpan, "Post Span 02 Left - Direct Post Asset",
                art.Post, new Vector2(-PostCenterX, 0f), postScale, PostOrder),
            LowerRightPost = BuildSprite(lowerSpan, "Post Span 02 Right - Direct Post Asset",
                art.Post, new Vector2(PostCenterX, 0f), postScale, PostOrder)
        };
        pieces.TopSeatAnchor = BuildSeatAnchor(pieces.TopPlank,
            "Glass Seat Anchor Row 01 - Direct Link");
        pieces.MiddleSeatAnchor = BuildSeatAnchor(pieces.MiddlePlank,
            "Glass Seat Anchor Row 02 - Direct Link");
        pieces.BottomSeatAnchor = BuildSeatAnchor(pieces.BottomPlank,
            "Glass Seat Anchor Row 03 - Direct Link");
        return pieces;
    }

    private static Transform BuildSeatAnchor(SpriteRenderer plank, string name)
    {
        var seat = new GameObject(name);
        seat.transform.SetParent(plank.transform, false);
        Rect bounds = SpriteVisualBounds(plank.sprite);
        seat.transform.localPosition = new Vector3(bounds.center.x, bounds.yMax, 0f);
        return seat.transform;
    }

    // ---- Delivery stage ---------------------------------------------------------

    private sealed class DeliveryStage
    {
        public PortalDeliveryAnimator Portal;
        public SpriteRenderer[] BackLayers;
        public SpriteRenderer[] FrontLayers;
        public Transform Pivot;
        public SpriteRenderer Glow;
        public SpriteRenderer Streak;
        public Transform Mouth;
        public Transform Throat;
    }

    /// <summary>
    /// The service stage above the shelf: sky, arch, curtains, the rail a served glass
    /// travels along, and the gold gate it disappears into.
    ///
    /// The four portal sprites share one 1792x1536 canvas, so they are placed as siblings
    /// with identical transforms under a single pivot; anything else would knock them out
    /// of register. Where the vessel enters and where it becomes invisible is then read
    /// back OUT of that artwork — the mouth from the left edge of the interior, the throat
    /// from the middle of the occluder — rather than being guessed as two more numbers.
    /// </summary>
    private static DeliveryStage BuildDeliveryStage(Transform parent, StageArt art)
    {
        var stage = new DeliveryStage();

        SpriteRenderer sky = BuildSprite(parent, "Upper Sky Backdrop - Manual",
            art.Sky, Vector2.zero, Vector2.one, SkyOrder);
        FitWidth(sky, 2f * FrameHalfWidth + 0.15f);
        PlaceByBottom(sky, 0f, DeliveryRailY - 0.30f);

        // The arch spans the frame exactly and hangs a little below the top edge, so its
        // keystone reads as the top of a room rather than as a strip cut off by the bezel.
        // Its legs end under the rail on purpose; the rail is what they stand behind.
        SpriteRenderer arch = BuildSprite(parent, "Upper Stone Arch - Manual",
            art.Arch, Vector2.zero, Vector2.one, ArchOrder);
        FitWidth(arch, 2f * FrameHalfWidth);
        PlaceByTop(arch, 0f, CameraHalfHeight + 0.34f);

        SpriteRenderer curtain = BuildSprite(parent, "Upper Curtain Pair - Manual",
            art.Curtain, Vector2.zero, Vector2.one, CurtainOrder);
        FitWidth(curtain, 2f * FrameHalfWidth + 0.15f);
        PlaceByTop(curtain, 0f, CameraHalfHeight);

        // The post STANDS ON the rail: it is a piece of furniture on the service counter,
        // not a pillar holding the building up. Sizing it from the frame height instead
        // of from its own drawing is what made it tower over the arch.
        SpriteRenderer column = BuildSprite(parent, "Delivery Column Left - Manual",
            art.Column, Vector2.zero, Vector2.one, ColumnOrder);
        FitHeight(column, 2.35f);
        PlaceByBottom(column, -FrameHalfWidth + 0.92f, DeliveryRailY - 0.16f);

        SpriteRenderer rail = BuildSprite(parent, "Delivery Rail Base - Manual",
            art.RailBase, Vector2.zero, Vector2.one, RailBaseOrder);
        FitWidth(rail, 2f * FrameHalfWidth - 0.30f);
        PlaceByTop(rail, 0f, DeliveryRailY);

        SpriteRenderer glow = BuildSprite(parent, "Delivery Rail Guide Idle - Manual",
            art.RailGlow, Vector2.zero, Vector2.one, RailGlowOrder);
        FitWidth(glow, 2f * FrameHalfWidth - 0.90f);
        PlaceByBottom(glow, -0.15f, DeliveryRailY - 0.02f);

        stage.Streak = BuildSprite(parent, "Travel Streak - Manual",
            art.RailGlow, Vector2.zero, Vector2.one, TravelStreakOrder);
        FitWidth(stage.Streak, 2.40f);
        PlaceByBottom(stage.Streak, 0.20f, DeliveryRailY + 0.28f);
        SetAlpha(stage.Streak, 0f);

        // The gate. Fitting is done on the FRONT sprite because that is the drawing the
        // player reads as "the door"; the other three ride along on the shared canvas.
        Transform pivot = Folder(parent, "Portal Pivot - Manual");
        // Height, then seat: the gate is measured from its own drawing and then placed so
        // its foot lands on the rail, which is also where a delivered glass is standing.
        // The gate drawing is square, so its height IS its width: 2.45 units tall made it
        // 2.45 wide as well, which is a third of the screen and leaves the served glass
        // nowhere to stand. Sized to sit in the right quarter with its dome touching the
        // frame edge, the way the reference composition has it.
        const float portalHeight = 2.05f;
        float portalScale = FitScaleForHeight(art.PortalFront, portalHeight);
        var portalCenter = new Vector2(2.34f,
            DeliveryRailY - SpriteVisualBounds(art.PortalFront).yMin * portalScale);
        pivot.localPosition = new Vector3(portalCenter.x, portalCenter.y, 0f);
        pivot.localScale = new Vector3(portalScale, portalScale, 1f);
        stage.Pivot = pivot;

        SpriteRenderer back = BuildSprite(pivot, "Portal Back - Manual",
            art.PortalBack, Vector2.zero, Vector2.one, PortalBackOrder);
        stage.Glow = BuildSprite(pivot, "Portal Glow - Manual",
            art.PortalBack, Vector2.zero, Vector2.one, PortalGlowOrder);
        stage.Glow.color = new Color(0.42f, 0.92f, 1f, 0f);
        SpriteRenderer occluder = BuildSprite(pivot, "Portal Occluder - Manual",
            art.PortalOccluder, Vector2.zero, Vector2.one, PortalOccluderOrder);
        SpriteRenderer front = BuildSprite(pivot, "Portal Front - Manual",
            art.PortalFront, Vector2.zero, Vector2.one, PortalFrontOrder);

        stage.BackLayers = new[] { back, stage.Glow };
        stage.FrontLayers = new[] { occluder, front };

        // Read the path out of the drawing. The interior's left edge is the last place a
        // vessel is still completely visible; the middle of the occluder is the first
        // place it is completely gone.
        Rect interior = SpriteVisualBounds(art.PortalBack);
        Rect hidden = SpriteVisualBounds(art.PortalOccluder);
        float mouthX = portalCenter.x + (interior.xMin + 0.12f) * portalScale;
        float throatX = portalCenter.x + hidden.center.x * portalScale;

        stage.Mouth = Anchor(parent, "Mouth Anchor - Manual",
            new Vector2(mouthX, DeliveryRailY));
        stage.Throat = Anchor(parent, "Throat Anchor - Manual",
            new Vector2(throatX, DeliveryRailY + 0.18f));

        stage.Portal = parent.gameObject.AddComponent<PortalDeliveryAnimator>();
        stage.Portal.ConfigureSceneBindings(stage.BackLayers, stage.FrontLayers,
            pivot, stage.Glow, stage.Streak, null, null, stage.Mouth, stage.Throat);
        stage.Portal.ConfigureTiming(PortalLiftDuration,
            // Lift timing scales with distance; a full height is bottom shelf to rail.
            DeliveryRailY - SurfaceY(3, 2, SpriteVisualBounds(art.Plank).height * ShelfScaleY),
            PortalApproachDuration, PortalEntryDuration, PortalHideDuration,
            PortalBounceDuration, PortalEntryDepth, PortalEntryScale, PortalHideScale,
            PortalEntryTilt);
        return stage;
    }

    // ---- Glass pools ------------------------------------------------------------

    private static List<BartenderShelfLevelView.GlassBinding> BuildVesselPool(
        GameObject source, Transform parent, string folderName, string vesselName, int count,
        Material glassLight, float glassLightIntensity, StageArt art,
        List<DeliveryBadgePresenter.BadgeBinding> badges)
    {
        Transform poolRoot = Folder(parent, folderName);
        var pool = new List<BartenderShelfLevelView.GlassBinding>(count);
        for (int i = 0; i < count; i++)
            pool.Add(ClonePoolVessel(source, poolRoot,
                $"{vesselName} Scene Glass {i + 1:00}",
                glassLight, glassLightIntensity, art, badges));
        return pool;
    }

    private static BartenderShelfLevelView.GlassBinding ClonePoolVessel(
        GameObject source, Transform parent, string name,
        Material glassLight, float glassLightIntensity, StageArt art,
        List<DeliveryBadgePresenter.BadgeBinding> badges)
    {
        GameObject go = UnityEngine.Object.Instantiate(source);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;

        LiquidBottle bottle = go.GetComponent<LiquidBottle>();
        BottleShell shell = go.GetComponent<BottleShell>();
        if (bottle == null || shell == null || bottle.profile == null ||
            bottle.profile.front == null)
            throw new InvalidOperationException(source.name +
                " is not a complete RoyalGlassLab vessel.");

        bottle.capacity = bottle.profile.capacity;
        bottle.SetUnits(null);
        bottle.Refresh();

        // The Royal source scene may already be open in the editor with stale in-memory
        // shell values even though its saved asset is current. Normalise this canonical
        // reflection layer explicitly so a shelf rebuild cannot silently lose it.
        shell.drawGlassLight = glassLight != null;
        shell.glassLightMaterial = glassLight;
        GlassLightProfile lightProfile = GlassLightProfile.Reference;
        lightProfile.rimStrength = 0f;
        lightProfile.fillStrength = 0f;
        lightProfile.shoulderStrength = 0f;
        shell.lightProfile = lightProfile;
        shell.lightIntensity = glassLight != null ? glassLightIntensity : 0f;
        shell.selectionBoost = 0f;
        shell.Build();
        SpriteRenderer placementRenderer = FindPlacementRenderer(go, bottle.profile.front);

        Rect visual = SpriteVisualBounds(bottle.profile.front);
        var foot = new GameObject("Glass Foot Anchor - Direct Link");
        foot.transform.SetParent(go.transform, false);
        foot.transform.localPosition = new Vector3(bottle.mouthLocal.x, visual.yMin, 0f);

        // The tick has to be a CHILD of the vessel: LiquidBottle.SetSortingOffset and the
        // portal's sandwich both walk GetComponentsInChildren, so parenting it here is what
        // makes it rise, tilt and vanish with the glass it belongs to.
        SpriteRenderer badgeRenderer = BuildSprite(go.transform,
            "Delivery Check Badge - Direct Link", art.CheckBadge,
            new Vector2(visual.xMax * 0.72f, visual.yMax * 0.94f),
            Vector2.one, CheckBadgeOrder);
        badgeRenderer.gameObject.SetActive(false);
        badges.Add(new DeliveryBadgePresenter.BadgeBinding
        {
            bottle = bottle,
            badge = badgeRenderer.transform,
            badgeRenderer = badgeRenderer,
            authoredLocalScale = Vector3.one
        });

        var binding = new BartenderShelfLevelView.GlassBinding
        {
            bottle = bottle,
            footAnchor = foot.transform,
            placementRenderer = placementRenderer,
            authoredLocalScale = go.transform.localScale,
            authoredLocalRotation = go.transform.localRotation
        };
        go.SetActive(false);
        return binding;
    }

    /// <summary>
    /// Sizes every tick to the same on-screen height. A badge is a child of its vessel, so
    /// it inherits both the authored vessel scale and the layout scale; without dividing
    /// those back out the shot glass would wear a tick twice the size of the beer mug's.
    /// </summary>
    private static void ApplyMeasuredBadgeScale(
        List<DeliveryBadgePresenter.BadgeBinding> badges, ShelfSolve solve)
    {
        const float targetWorldHeight = 0.62f;
        foreach (DeliveryBadgePresenter.BadgeBinding badge in badges)
        {
            float badgeHeight = SpriteVisualBounds(badge.badgeRenderer.sprite).height;
            float inherited = Mathf.Abs(badge.bottle.transform.localScale.y)
                              * solve.ScaleTwoRow;
            float local = targetWorldHeight
                          / Mathf.Max(0.0001f, badgeHeight * inherited);
            badge.authoredLocalScale = new Vector3(local, local, 1f);
            badge.badge.localScale = badge.authoredLocalScale;
        }
    }

    // ---- Order cards ------------------------------------------------------------

    private sealed class OrderStrip
    {
        public Canvas Canvas;
        public List<OrderCardView> Cards = new List<OrderCardView>(3);
    }

    /// <summary>
    /// Reads each vessel's baked interior out of its profile and turns it into a sprite a
    /// card can draw. The card then paints the liquid inside the real glass shape rather
    /// than inside a rectangle that happens to sit behind it.
    /// </summary>
    private static List<OrderCardView.GlassIcon> BuildGlassIconTable(
        VesselProfile shot, VesselProfile cocktail, VesselProfile mug,
        VesselProfile tall, VesselProfile beer)
    {
        var table = new List<OrderCardView.GlassIcon>(5);
        void Add(GlassType type, VesselProfile profile, string fileName)
        {
            Sprite mask = BartenderUiArtFactory.EnsureInteriorMaskSprite(
                profile, fileName, out Rect interior);
            table.Add(new OrderCardView.GlassIcon
            {
                type = type,
                front = profile.front,
                interiorMask = mask,
                interiorRect = interior
            });
        }

        Add(GlassType.Shot, shot, "OrderInterior_Shot");
        Add(GlassType.Kadeh, cocktail, "OrderInterior_Cocktail");
        Add(GlassType.Latte, mug, "OrderInterior_Mug");
        Add(GlassType.Tumbler, tall, "OrderInterior_Tumbler");
        Add(GlassType.Bira, beer, "OrderInterior_Beer");
        return table;
    }

    private static OrderStrip BuildOrderStrip(Transform parent, StageArt art,
                                              List<OrderCardView.GlassIcon> icons,
                                              Camera camera)
    {
        var strip = new OrderStrip();
        strip.Canvas = BuildWorldCanvas(parent, "04 Order Cards - Level Controlled",
            OrderCardCanvasOrder, false, camera);

        float railY = OrderStripCenterY + OrderCardHeight * 0.5f + 0.10f;
        Image rail = BuildImage(strip.Canvas.transform, "Order Rail - Red Gold",
            art.OrderCardRail, Color.white, PxPoint(new Vector2(0f, railY)),
            new Vector2(Px(6.36f), Px(0.38f)), Image.Type.Simple);
        rail.transform.SetAsFirstSibling();

        for (int i = 0; i < 3; i++)
        {
            float x = (i - 1) * OrderCardSpacing;
            strip.Cards.Add(BuildOrderCard(strip.Canvas.transform,
                $"Order Card Slot {i + 1:00}", new Vector2(x, OrderStripCenterY), art));
        }

        for (int i = 0; i < strip.Cards.Count; i++) strip.Cards[i].SetGlassIcons(icons);
        return strip;
    }

    /// <summary>
    /// One card, in design pixels. The layout mirrors the mock-up exactly: a cream panel
    /// with a clip at the top, the target glass filling most of the body, and a row of
    /// colour dots underneath for the orders whose layer sequence does not matter.
    /// </summary>
    private static OrderCardView BuildOrderCard(Transform parent, string name,
                                                Vector2 worldCenter, StageArt art)
    {
        float width = Px(OrderCardWidth);
        float height = Px(OrderCardHeight);
        RectTransform card = BuildRect(parent, name, PxPoint(worldCenter),
            new Vector2(width, height));

        var group = card.gameObject.AddComponent<CanvasGroup>();
        Image background = BuildImage(card, "Card Panel", art.OrderCardPanel, Color.white,
            Vector2.zero, new Vector2(width, height), Image.Type.Simple);
        BuildImage(card, "Card Frame - Purple", art.OrderCardFrame, Color.white,
            Vector2.zero, new Vector2(width, height), Image.Type.Simple);
        Image edge = BuildImage(card, "Card Match Edge", art.CardEdge, Color.clear,
            Vector2.zero, new Vector2(width, height), Image.Type.Sliced);

        // The clipboard clip straddles the top edge, which is what makes the panel read
        // as a pinned order slip rather than as a plain rounded rectangle.
        BuildImage(card, "Card Clip - Gold", art.OrderCardClip, Color.white,
            new Vector2(0f, height * 0.5f - Px(0.03f)),
            new Vector2(Px(0.58f), Px(0.31f)), Image.Type.Simple);

        // The glass sits in the upper two thirds so the dot row underneath never collides
        // with it, whichever of the five vessels the order asks for.
        var iconBoxSize = new Vector2(width * 0.70f, height * 0.56f);
        RectTransform iconBox = BuildRect(card, "Glass Icon Fit Box",
            new Vector2(0f, height * 0.10f), iconBoxSize);

        Image interior = BuildImage(iconBox, "Interior Mask", art.InteriorPlaceholder,
            Color.white,
            Vector2.zero, iconBoxSize, Image.Type.Simple);
        Mask mask = interior.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        var bands = new Image[OrderCardView.MaxUnits];
        for (int i = 0; i < bands.Length; i++)
        {
            bands[i] = BuildImage(interior.rectTransform, $"Fill Band {i + 1:00}", null,
                Color.white, Vector2.zero, Vector2.one, Image.Type.Simple);
            bands[i].gameObject.SetActive(false);
        }

        Image glassFront = BuildImage(iconBox, "Glass Front", null, Color.white,
            Vector2.zero, iconBoxSize, Image.Type.Simple);

        RectTransform chipRow = BuildRect(card, "Set Chip Row",
            new Vector2(0f, -height * 0.32f), new Vector2(width * 0.86f, Px(0.30f)));
        var chips = new Image[OrderCardView.MaxUnits];
        float chipDiameter = Px(0.24f);
        for (int i = 0; i < chips.Length; i++)
        {
            chips[i] = BuildImage(chipRow, $"Set Chip {i + 1:00} - Tint Fill",
                art.OrderChipFill, Color.white,
                Vector2.zero, new Vector2(chipDiameter, chipDiameter), Image.Type.Simple);
            BuildImage(chips[i].rectTransform, "Gold Rim", art.OrderChipRim, Color.white,
                Vector2.zero, new Vector2(chipDiameter, chipDiameter), Image.Type.Simple);
            chips[i].gameObject.SetActive(false);
        }

        Image tick = BuildImage(card, "Delivered Tick", art.CheckBadge, Color.white,
            new Vector2(width * 0.34f, -height * 0.32f),
            new Vector2(Px(0.46f), Px(0.46f)), Image.Type.Simple);
        tick.gameObject.SetActive(false);

        var view = card.gameObject.AddComponent<OrderCardView>();
        SerializedObject serialized = new SerializedObject(view);
        SetRef(serialized, "rt", card);
        SetRef(serialized, "canvasGroup", group);
        SetRef(serialized, "background", background);
        SetRef(serialized, "edge", edge);
        SetRef(serialized, "tickBadge", tick);
        SetRef(serialized, "iconFitBox", iconBox);
        SetRef(serialized, "glassFront", glassFront);
        SetRef(serialized, "interiorMask", interior);
        SetRefArray(serialized, "fillBands", bands);
        SetRef(serialized, "chipRow", chipRow);
        SetRefArray(serialized, "chips", chips);
        SetFloat(serialized, "chipSpacing", chipDiameter * 1.22f);
        SetColor(serialized, "emptySlotColor", new Color(1f, 1f, 1f, 0.12f));
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    // ---- Screen controls --------------------------------------------------------

    private sealed class ScreenControls
    {
        public Canvas Canvas;
        public Text LevelLabel;
        public GameObject LevelBadgeRoot;
        public Button SettingsButton;
        public Button UndoButton;
        public Button ExtraGlassButton;
        public Button ShuffleButton;
        public GameObject PauseOverlay;
        public GameObject SettingsCard;
        public GameObject ExitCard;
        public Button ResumeButton;
        public Button CloseButton;
        public Button ExitButton;
        public Button MusicButton;
        public Button SoundButton;
        public Button VibrationButton;
        public GameObject MusicOffMark;
        public GameObject SoundOffMark;
        public GameObject VibrationOffMark;
        public Button ConfirmExitButton;
        public Button CancelExitButton;
    }

    private static ScreenControls BuildScreenControls(Transform parent, StageArt art,
                                                      Font font, Camera camera)
    {
        var controls = new ScreenControls();
        controls.Canvas = BuildScreenCanvas(parent, "05 Screen Controls - Level Controlled",
            ScreenCanvasOrder, camera);

        // The HUD is the one part of the composition that must NOT ride the 720x1280
        // block. On a 19.5:9 phone that block is width-limited, leaving roughly a fifth
        // of the screen unused above and below it -- and a status bar floating a
        // centimetre inside the bezel reads as a bug, not as a design. Anchoring the two
        // bars to the safe area instead puts them where the hardware actually ends,
        // while the board keeps the proportions it was drawn at.
        Transform canvas = BuildSafeArea(controls.Canvas.transform).transform;

        Transform topBar = BuildEdgeBar(canvas, "01 Top Bar", true,
            Px(CameraHalfHeight - TopBarCenterY), Px(TopBarHeight));
        RectTransform badge = BuildRect(topBar, "Level Badge - Level Controlled",
            Vector2.zero, new Vector2(Px(2.90f), Px(TopBarHeight)));
        BuildImage(badge, "Badge Panel", art.Pill, BadgeFace, Vector2.zero,
            badge.sizeDelta, Image.Type.Sliced);
        BuildImage(badge, "Badge Rim", art.Pill, ButtonRim, Vector2.zero,
            badge.sizeDelta + new Vector2(Px(0.09f), Px(0.09f)), Image.Type.Sliced)
            .transform.SetAsFirstSibling();
        controls.LevelLabel = BuildText(badge, "Badge Label", font, "SEVİYE 1",
            Vector2.zero, badge.sizeDelta, Mathf.RoundToInt(Px(0.44f)), BadgeText);
        controls.LevelBadgeRoot = badge.gameObject;

        controls.SettingsButton = BuildRoundButton(topBar, "Settings Button", art,
            art.GlyphGear, Vector2.zero, BottomButtonDiameter * 0.86f,
            new Vector2(Px(FrameHalfWidth - 0.62f), 0f));

        Transform bottomBar = BuildEdgeBar(canvas, "02 Bottom Controls", false,
            Px(BottomBarCenterY + CameraHalfHeight), Px(BottomButtonDiameter));
        controls.UndoButton = BuildRoundButton(bottomBar, "Undo Button", art, art.GlyphUndo,
            Vector2.zero, BottomButtonDiameter,
            new Vector2(-Px(BottomButtonSpacing), 0f));
        controls.ExtraGlassButton = BuildRoundButton(bottomBar, "Add Glass Button", art,
            art.GlyphPlus, Vector2.zero, BottomButtonDiameter, Vector2.zero);
        controls.ShuffleButton = BuildRoundButton(bottomBar, "Shuffle Button", art,
            art.GlyphShuffle, Vector2.zero, BottomButtonDiameter,
            new Vector2(Px(BottomButtonSpacing), 0f));

        BuildPauseOverlay(canvas, art, font, controls);
        return controls;
    }

    /// <summary>
    /// A screen-space canvas authored in the same 720x1280 numbers as everything else.
    /// Matching on WIDTH is the whole point: the design's width is what the fitter also
    /// pins, so a button is the same size relative to the board on every phone, and only
    /// the vertical margins change.
    /// </summary>
    private static Canvas BuildScreenCanvas(Transform parent, string name, int sortingOrder,
                                            Camera camera)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var canvas = go.AddComponent<Canvas>();
        // Camera space rather than overlay: an overlay canvas is invisible to an offscreen
        // camera render and floats a kilometre away in the Scene view, so the authoring
        // preview and the artist's viewport would both stop telling the truth about it.
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.sortingOrder = sortingOrder;
        // Bound here rather than left to the runtime binder. A camera-space canvas with no
        // camera collapses to zero scale, and that is the state the SCENE would be saved
        // in -- an authoring file that shows nothing where the HUD is. The prefab drops
        // this reference on save, which is exactly when the binder takes over.
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0f;

        go.AddComponent<WorldCanvasCameraBinder>();
        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    private static RectTransform BuildSafeArea(Transform parent)
    {
        var go = new GameObject("Safe Area", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = Stretch((RectTransform)go.transform);
        go.AddComponent<BsSafeArea>();
        return rect;
    }

    /// <summary>
    /// A full-width bar pinned to the top or the bottom of the safe area. Children are
    /// placed by their x offset alone, so the same numbers used for the world composition
    /// keep working.
    /// </summary>
    private static Transform BuildEdgeBar(Transform parent, string name, bool top,
                                          float insetFromEdge, float height)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, top ? 1f : 0f);
        rect.anchorMax = new Vector2(1f, top ? 1f : 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(0f, height);
        rect.anchoredPosition = new Vector2(0f,
            top ? -(insetFromEdge + height * 0.5f) : insetFromEdge + height * 0.5f);
        rect.localScale = Vector3.one;
        return rect;
    }

    private static RectTransform Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        return rect;
    }

    /// <summary>
    /// The card behind the gear. It is built even though no mock-up covers it, because a
    /// gear that pauses the game with nothing on screen is a trap: the player would have
    /// no way back to a running board.
    /// </summary>
    private static void BuildPauseOverlay(Transform canvas, StageArt art, Font font,
                                          ScreenControls controls)
    {
        RectTransform overlay = Stretch(BuildRect(canvas,
            "03 Pause Overlay - Flow Controlled", Vector2.zero, Vector2.zero));
        controls.PauseOverlay = overlay.gameObject;
        // Stretched past the safe area on purpose: the dim has to reach the bezel, or a
        // notched phone shows a lit strip above a paused game.
        RectTransform dim = Stretch(BuildImage(overlay, "Dim", null, OverlayDim,
            Vector2.zero, Vector2.zero, Image.Type.Simple).rectTransform);
        dim.offsetMin = new Vector2(0f, -DesignHeight);
        dim.offsetMax = new Vector2(0f, DesignHeight);
        dim.GetComponent<Image>().raycastTarget = true;

        var cardSize = new Vector2(Px(5.20f), Px(4.60f));
        RectTransform card = BuildRect(overlay, "Settings Card", Vector2.zero, cardSize);
        controls.SettingsCard = card.gameObject;
        BuildImage(card, "Card Rim", art.CardPanel, ButtonRim, Vector2.zero,
            cardSize + new Vector2(Px(0.14f), Px(0.14f)), Image.Type.Sliced);
        BuildImage(card, "Card Panel", art.CardPanel, BadgeFace, Vector2.zero, cardSize,
            Image.Type.Sliced);
        BuildText(card, "Title", font, "AYARLAR",
            new Vector2(0f, cardSize.y * 0.36f), new Vector2(cardSize.x, Px(0.60f)),
            Mathf.RoundToInt(Px(0.52f)), BadgeText);

        float toggleDiameter = BottomButtonDiameter * 0.92f;
        controls.MusicButton = BuildRoundButton(card, "Music Button", art, null,
            Vector2.zero, toggleDiameter, PxPoint(new Vector2(-1.30f, 0.55f)));
        controls.SoundButton = BuildRoundButton(card, "Sound Button", art, null,
            Vector2.zero, toggleDiameter, PxPoint(new Vector2(0f, 0.55f)));
        controls.VibrationButton = BuildRoundButton(card, "Vibration Button", art, null,
            Vector2.zero, toggleDiameter, PxPoint(new Vector2(1.30f, 0.55f)));
        BuildText(controls.MusicButton.transform, "Label", font, "MÜZİK", Vector2.zero,
            new Vector2(Px(1.20f), Px(0.40f)), Mathf.RoundToInt(Px(0.26f)), ButtonGlyph);
        BuildText(controls.SoundButton.transform, "Label", font, "SES", Vector2.zero,
            new Vector2(Px(1.20f), Px(0.40f)), Mathf.RoundToInt(Px(0.26f)), ButtonGlyph);
        BuildText(controls.VibrationButton.transform, "Label", font, "TİTREŞİM",
            Vector2.zero, new Vector2(Px(1.20f), Px(0.40f)), Mathf.RoundToInt(Px(0.24f)),
            ButtonGlyph);
        controls.MusicOffMark = BuildOffMark(controls.MusicButton.transform, art);
        controls.SoundOffMark = BuildOffMark(controls.SoundButton.transform, art);
        controls.VibrationOffMark = BuildOffMark(controls.VibrationButton.transform, art);

        controls.ResumeButton = BuildPillButton(card, "Resume Button", art, font, "DEVAM",
            PxPoint(new Vector2(0f, -0.70f)), new Vector2(Px(3.20f), Px(0.86f)));
        controls.ExitButton = BuildPillButton(card, "Exit Button", art, font, "ÇIKIŞ",
            PxPoint(new Vector2(0f, -1.70f)), new Vector2(Px(3.20f), Px(0.86f)));
        controls.CloseButton = BuildRoundButton(card, "Close Button", art, null,
            Vector2.zero, BottomButtonDiameter * 0.66f,
            new Vector2(cardSize.x * 0.5f - Px(0.18f), cardSize.y * 0.5f - Px(0.18f)));
        BuildText(controls.CloseButton.transform, "Label", font, "X", Vector2.zero,
            new Vector2(Px(0.60f), Px(0.60f)), Mathf.RoundToInt(Px(0.40f)), ButtonGlyph);

        var confirmSize = new Vector2(Px(4.80f), Px(2.60f));
        RectTransform confirm = BuildRect(overlay, "Exit Confirmation Card", Vector2.zero,
            confirmSize);
        controls.ExitCard = confirm.gameObject;
        BuildImage(confirm, "Card Rim", art.CardPanel, ButtonRim, Vector2.zero,
            confirmSize + new Vector2(Px(0.14f), Px(0.14f)), Image.Type.Sliced);
        BuildImage(confirm, "Card Panel", art.CardPanel, BadgeFace, Vector2.zero,
            confirmSize, Image.Type.Sliced);
        BuildText(confirm, "Question", font, "Ana menüye dönülsün mü?",
            new Vector2(0f, confirmSize.y * 0.26f), new Vector2(confirmSize.x, Px(0.70f)),
            Mathf.RoundToInt(Px(0.34f)), BadgeText);
        controls.ConfirmExitButton = BuildPillButton(confirm, "Confirm Exit", art, font,
            "EVET", new Vector2(-confirmSize.x * 0.24f, -confirmSize.y * 0.22f),
            new Vector2(Px(1.90f), Px(0.80f)));
        controls.CancelExitButton = BuildPillButton(confirm, "Cancel Exit", art, font,
            "VAZGEÇ", new Vector2(confirmSize.x * 0.24f, -confirmSize.y * 0.22f),
            new Vector2(Px(1.90f), Px(0.80f)));

        // Closed at rest. BartenderPausePresenter is the only thing that opens them, and
        // it opens them from the flow state, never from the button press.
        controls.ExitCard.SetActive(false);
        controls.PauseOverlay.SetActive(false);
    }

    private static GameObject BuildOffMark(Transform parent, StageArt art)
    {
        Image mark = BuildImage(parent, "Off Mark", art.Chip,
            new Color(0.92f, 0.29f, 0.26f, 0.92f), Vector2.zero,
            new Vector2(Px(0.86f), Px(0.14f)), Image.Type.Simple);
        mark.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -32f);
        mark.raycastTarget = false;
        mark.gameObject.SetActive(false);
        return mark.gameObject;
    }

    // ---- Level rig --------------------------------------------------------------

    private sealed class LevelRig
    {
        public BartenderLevelController Controller;
        public BartenderSession Session;
        public BartenderShelfLevelView ShelfView;
        public BartenderPourInteraction Interaction;
        public PourAnimator Animator;
        public PourStream Stream;
        public DeliveryBadgePresenter Badges;
        public OrderStripPresenter Strip;
        public LevelBadgePresenter LevelBadge;
        public BoosterBarPresenter Boosters;
        public BartenderPausePresenter Pause;
    }

    private static LevelRig BuildLevelRig(Transform systems, Transform layoutSpace,
        BsPalette palette, ShelfPieces shelf, DeliveryStage delivery,
        OrderStrip strip, ScreenControls controls,
        List<BartenderShelfLevelView.GlassBinding> shots,
        List<BartenderShelfLevelView.GlassBinding> cocktails,
        List<BartenderShelfLevelView.GlassBinding> lattes,
        List<BartenderShelfLevelView.GlassBinding> tumblers,
        List<BartenderShelfLevelView.GlassBinding> biras,
        List<DeliveryBadgePresenter.BadgeBinding> badges,
        List<OrderCardView.GlassIcon> icons, ShelfSolve solve)
    {
        var rig = new LevelRig();
        GameObject host = systems.gameObject;

        var streamObject = new GameObject("Pour Stream - Portable Rig Link");
        streamObject.transform.SetParent(systems, false);
        rig.Stream = streamObject.AddComponent<PourStream>();
        rig.Stream.material = Load<Material>(PourStreamMaterialPath);

        rig.Animator = host.AddComponent<PourAnimator>();
        rig.Animator.stream = rig.Stream;

        rig.Controller = host.AddComponent<BartenderLevelController>();
        SerializedObject controllerSerialized = new SerializedObject(rig.Controller);
        SetRef(controllerSerialized, "palette", palette);
        SetInt(controllerSerialized, "maxActiveGlasses",
            BartenderShelfLevelView.MaximumActiveGlasses);
        controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

        rig.Session = host.AddComponent<BartenderSession>();
        SetRefAndApply(rig.Session, "controller", rig.Controller);

        rig.ShelfView = host.AddComponent<BartenderShelfLevelView>();
        rig.ShelfView.ConfigureSceneBindings(rig.Controller, layoutSpace,
            shots, cocktails, lattes, tumblers, biras,
            shelf.TopPlank, shelf.MiddlePlank, shelf.BottomPlank,
            shelf.TopSeatAnchor, shelf.MiddleSeatAnchor, shelf.BottomSeatAnchor,
            shelf.UpperLeftPost, shelf.UpperRightPost,
            shelf.LowerLeftPost, shelf.LowerRightPost);
        rig.ShelfView.ConfigureLayout(
            solve.TwoRowSurfaces, solve.ThreeRowSurfaces,
            solve.SpacingTwo, solve.SpacingThree, solve.SpacingFour,
            solve.ScaleTwoRow, solve.ScaleThreeRow,
            solve.ScaleFourInTwoRows, solve.ScaleFourInThreeRows,
            OpticalSeatInset);
        rig.ShelfView.ConfigureEntrance(
            EntranceEnabled, EntranceDropHeight, EntranceDropDuration,
            EntranceGlassStagger, EntranceRowStagger, EntranceSortingBoost,
            EntranceLandingSquash, EntranceSettleDuration, ShelfFadeDuration,
            ReseatDuration);
        rig.ShelfView.ConfigureDeliveryPortal(delivery.Portal);

        // Camera stays outside the portable rig. Empty resolves Camera.main in whichever
        // scene receives it, avoiding a cross-scene object reference in the prefab.
        rig.Interaction = host.AddComponent<BartenderPourInteraction>();
        rig.Interaction.Configure(
            rig.Controller, rig.ShelfView, rig.Animator, null, rig.Session);

        rig.Badges = host.AddComponent<DeliveryBadgePresenter>();
        rig.Badges.ConfigureSceneBindings(rig.Controller, rig.ShelfView, delivery.Portal,
            rig.Interaction, badges);

        rig.Strip = host.AddComponent<OrderStripPresenter>();
        rig.Strip.ConfigureSceneBindings(rig.Controller, rig.ShelfView, strip.Cards, icons);

        rig.LevelBadge = host.AddComponent<LevelBadgePresenter>();
        rig.LevelBadge.ConfigureSceneBindings(rig.Controller, controls.LevelLabel,
            controls.LevelBadgeRoot);

        rig.Boosters = host.AddComponent<BoosterBarPresenter>();
        rig.Boosters.ConfigureSceneBindings(rig.Controller, rig.ShelfView, rig.Interaction,
            controls.UndoButton, controls.ExtraGlassButton, controls.ShuffleButton);

        rig.Pause = host.AddComponent<BartenderPausePresenter>();
        SerializedObject pause = new SerializedObject(rig.Pause);
        SetRef(pause, "session", rig.Session);
        SetRef(pause, "controller", rig.Controller);
        SetRef(pause, "pauseButton", controls.SettingsButton);
        SetRef(pause, "settingsOverlay", controls.PauseOverlay);
        SetRef(pause, "settingsCard", controls.SettingsCard);
        SetRef(pause, "closeButton", controls.CloseButton);
        SetRef(pause, "resumeButton", controls.ResumeButton);
        SetRef(pause, "exitButton", controls.ExitButton);
        SetRef(pause, "musicButton", controls.MusicButton);
        SetRef(pause, "soundButton", controls.SoundButton);
        SetRef(pause, "vibrationButton", controls.VibrationButton);
        SetRef(pause, "musicOffMark", controls.MusicOffMark);
        SetRef(pause, "soundOffMark", controls.SoundOffMark);
        SetRef(pause, "vibrationOffMark", controls.VibrationOffMark);
        SetRef(pause, "exitConfirmationCard", controls.ExitCard);
        SetRef(pause, "confirmExitButton", controls.ConfirmExitButton);
        SetRef(pause, "cancelExitButton", controls.CancelExitButton);
        pause.ApplyModifiedPropertiesWithoutUndo();

        // The order strip lives in the world composition, so it needs an event camera and
        // resolves one itself: writing the authoring camera here would be a reference out
        // of the prefab root, which the prefab save silently drops. The screen-space HUD
        // canvas needs no camera at all.
        strip.Canvas.GetComponent<WorldCanvasCameraBinder>().ConfigureSceneBindings(null);
        controls.Canvas.GetComponent<WorldCanvasCameraBinder>()
            .ConfigureSceneBindings(null);
        return rig;
    }

    /// <summary>
    /// Fills the HUD with the same level the shelf is previewing.
    ///
    /// The presenters cannot do this themselves: they read a controller that has not
    /// loaded anything yet, because loading happens in Start. Without this the saved
    /// scene would show three blank cards and a badge reading "BARTENDER" next to a
    /// perfectly laid out Level 4 board, and nobody could tell the layout from a bug.
    /// </summary>
    private static void PreviewHud(OrderStrip strip, ScreenControls controls,
                                   BsLevel level, BsPalette palette)
    {
        int slots = Mathf.Max(1, level.OrderSlots);
        for (int i = 0; i < strip.Cards.Count; i++)
        {
            OrderCardView card = strip.Cards[i];
            card.Initialize(palette);
            bool filled = i < slots && i < level.Orders.Count;
            card.SetOrder(filled ? level.Orders[i] : null, level.AllowTimedOrders);
            card.SetVisible(filled, false);
        }
        controls.LevelLabel.text = "SEVİYE " + level.Index;
    }

    private static void AttachSafeAreaFitter(Camera camera, Transform root)
    {
        var fitter = camera.gameObject.AddComponent<WorldSpaceSafeAreaFitter>();
        SerializedObject serialized = new SerializedObject(fitter);
        SetRef(serialized, "targetCamera", camera);
        SetRef(serialized, "compositionRoot", root);
        SerializedProperty resolution = serialized.FindProperty("referenceResolution");
        resolution.vector2IntValue = new Vector2Int(DesignWidth, DesignHeight);
        SetFloat(serialized, "referenceOrthographicSize", CameraHalfHeight);
        serialized.FindProperty("respectSafeArea").boolValue = true;
        serialized.FindProperty("referencePoseCaptured").boolValue = true;
        serialized.FindProperty("referenceCameraLocalPosition").vector3Value =
            camera.transform.InverseTransformPoint(Vector3.zero);
        serialized.FindProperty("referenceCameraRelativeRotation").quaternionValue =
            Quaternion.identity;
        serialized.FindProperty("referenceLocalScale").vector3Value = Vector3.one;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        // OnEnable already ran once and may have fitted the root to whatever aspect the
        // editor happens to show. The authored pose is identity and that is what the file
        // must contain; the fitter derives everything else from it at run time.
        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        root.localScale = Vector3.one;
    }

    // =================================================================================
    //  Scene-object helpers
    // =================================================================================

    private static SpriteRenderer BuildSprite(Transform parent, string name, Sprite sprite,
        Vector2 position, Vector2 scale, int sortingOrder)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(position.x, position.y, 0f);
        go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
        go.layer = StageLayer;

        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.color = Color.white;
        renderer.sortingLayerName = "Default";
        renderer.sortingOrder = sortingOrder;
        return renderer;
    }

    private static Transform Anchor(Transform parent, string name, Vector2 position)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(position.x, position.y, 0f);
        return go.transform;
    }

    private static void SetAlpha(SpriteRenderer renderer, float alpha)
    {
        Color color = renderer.color;
        color.a = alpha;
        renderer.color = color;
    }

    private static float FitScaleForWidth(Sprite sprite, float worldWidth) =>
        worldWidth / Mathf.Max(0.0001f, SpriteVisualBounds(sprite).width);

    private static float FitScaleForHeight(Sprite sprite, float worldHeight) =>
        worldHeight / Mathf.Max(0.0001f, SpriteVisualBounds(sprite).height);

    private static void FitWidth(SpriteRenderer renderer, float worldWidth)
    {
        float scale = FitScaleForWidth(renderer.sprite, worldWidth);
        renderer.transform.localScale = new Vector3(scale, scale, 1f);
    }

    private static void FitHeight(SpriteRenderer renderer, float worldHeight)
    {
        float scale = FitScaleForHeight(renderer.sprite, worldHeight);
        renderer.transform.localScale = new Vector3(scale, scale, 1f);
    }

    /// <summary>
    /// Positions by the drawing's visible edge rather than by its transform. Every one of
    /// these PNGs carries a different amount of transparent margin, so aligning the roots
    /// would put the visible art somewhere slightly different every time.
    /// </summary>
    private static void PlaceByBottom(SpriteRenderer renderer, float centerX, float bottomY)
    {
        Rect visual = SpriteVisualBounds(renderer.sprite);
        Vector3 scale = renderer.transform.localScale;
        renderer.transform.localPosition = new Vector3(
            centerX - visual.center.x * scale.x,
            bottomY - visual.yMin * scale.y, 0f);
    }

    private static void PlaceByTop(SpriteRenderer renderer, float centerX, float topY)
    {
        Rect visual = SpriteVisualBounds(renderer.sprite);
        Vector3 scale = renderer.transform.localScale;
        renderer.transform.localPosition = new Vector3(
            centerX - visual.center.x * scale.x,
            topY - visual.yMax * scale.y, 0f);
    }

    // ---- uGUI helpers -----------------------------------------------------------

    private static float Px(float worldUnits) => worldUnits / WorldPerDesignPixel;

    private static Vector2 PxPoint(Vector2 world) =>
        new Vector2(Px(world.x), Px(world.y));

    /// <summary>
    /// A world-space canvas whose rect is the 720x1280 design itself. One canvas unit is
    /// one design pixel, so every UI number in this file can be read next to the mock-up,
    /// while the whole thing still lives inside the composition the safe-area fitter
    /// scales.
    /// </summary>
    private static Canvas BuildWorldCanvas(Transform parent, string name, int sortingOrder,
                                           bool interactive, Camera camera = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingLayerName = "Default";
        canvas.sortingOrder = sortingOrder;

        // Sized after the Canvas is attached: switching render mode is what resets a
        // canvas RectTransform, and a reset here would silently halve every child.
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(DesignWidth, DesignHeight);
        rect.localScale = new Vector3(WorldPerDesignPixel, WorldPerDesignPixel, 1f);
        rect.localPosition = new Vector3(0f, 0f, -0.20f);

        canvas.worldCamera = camera;
        go.AddComponent<WorldCanvasCameraBinder>();
        if (interactive) go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    private static RectTransform BuildRect(Transform parent, string name,
                                           Vector2 anchoredPosition, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPosition;
        rect.localScale = Vector3.one;
        return rect;
    }

    private static Image BuildImage(Transform parent, string name, Sprite sprite,
                                    Color color, Vector2 anchoredPosition, Vector2 size,
                                    Image.Type type)
    {
        RectTransform rect = BuildRect(parent, name, anchoredPosition, size);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.type = type;
        image.raycastTarget = false;
        return image;
    }

    private static Text BuildText(Transform parent, string name, Font font, string content,
                                  Vector2 anchoredPosition, Vector2 size, int fontSize,
                                  Color color)
    {
        RectTransform rect = BuildRect(parent, name, anchoredPosition, size);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = Mathf.Max(1, fontSize);
        text.fontStyle = FontStyle.Bold;
        text.color = color;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private static Button BuildRoundButton(Transform parent, string name, StageArt art,
                                           Sprite glyph, Vector2 worldCenter,
                                           float worldDiameter,
                                           Vector2? explicitAnchoredPosition = null)
    {
        float diameter = Px(worldDiameter);
        RectTransform rect = BuildRect(parent, name,
            explicitAnchoredPosition ?? PxPoint(worldCenter),
            new Vector2(diameter, diameter));

        Image face = rect.gameObject.AddComponent<Image>();
        face.sprite = art.Disc;
        face.color = ButtonFace;
        face.raycastTarget = true;

        BuildImage(rect, "Rim", art.DiscRing, ButtonRim, Vector2.zero,
            new Vector2(diameter, diameter), Image.Type.Simple);
        if (glyph != null)
            BuildImage(rect, "Glyph", glyph, ButtonGlyph, Vector2.zero,
                new Vector2(diameter * 0.62f, diameter * 0.62f), Image.Type.Simple);

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = face;
        ApplyButtonColors(button);
        return button;
    }

    private static Button BuildPillButton(Transform parent, string name, StageArt art,
                                          Font font, string label, Vector2 anchoredPosition,
                                          Vector2 size)
    {
        RectTransform rect = BuildRect(parent, name, anchoredPosition, size);
        Image face = rect.gameObject.AddComponent<Image>();
        face.sprite = art.Pill;
        face.type = Image.Type.Sliced;
        face.color = ButtonFace;
        face.raycastTarget = true;

        BuildText(rect, "Label", font, label, Vector2.zero, size,
            Mathf.RoundToInt(size.y * 0.46f), ButtonGlyph);

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = face;
        ApplyButtonColors(button);
        return button;
    }

    /// <summary>
    /// A disabled booster has to look disabled. The presenters drive `interactable` from
    /// the rules, so this tint is the only place that turns a rule into something visible.
    /// </summary>
    private static void ApplyButtonColors(Button button)
    {
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(0.45f, 0.45f, 0.50f, 0.70f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;
    }

    private static Font ResolveBuiltinFont()
    {
        // Unity 6 renamed the built-in Arial. Both names are tried so the builder keeps
        // working on an older editor without carrying a font asset of its own.
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font == null)
            throw new InvalidOperationException(
                "No built-in font available for the HUD labels.");
        return font;
    }

    // ---- SerializedObject helpers ------------------------------------------------
    //
    // Several authored components keep their references private, which is correct: they
    // are an artist's Inspector fields, not an API. Writing them through SerializedObject
    // is exactly what dragging into the Inspector does, and it fails loudly if a field is
    // ever renamed instead of silently building a half-wired scene.

    private static SerializedProperty Find(SerializedObject serialized, string path)
    {
        SerializedProperty property = serialized.FindProperty(path);
        if (property == null)
            throw new InvalidOperationException(
                $"{serialized.targetObject.GetType().Name}.{path} could not be serialized.");
        return property;
    }

    private static void SetRef(SerializedObject serialized, string path,
                               UnityEngine.Object value) =>
        Find(serialized, path).objectReferenceValue = value;

    private static void SetRefAndApply(UnityEngine.Object target, string path,
                                       UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target);
        SetRef(serialized, path, value);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetRefArray<T>(SerializedObject serialized, string path,
                                       IList<T> values) where T : UnityEngine.Object
    {
        SerializedProperty property = Find(serialized, path);
        property.arraySize = values.Count;
        for (int i = 0; i < values.Count; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    private static void SetFloat(SerializedObject serialized, string path, float value) =>
        Find(serialized, path).floatValue = value;

    private static void SetInt(SerializedObject serialized, string path, int value) =>
        Find(serialized, path).intValue = value;

    private static void SetColor(SerializedObject serialized, string path, Color value) =>
        Find(serialized, path).colorValue = value;

    // =================================================================================
    //  Asset helpers
    // =================================================================================

    private static SpriteRenderer FindPlacementRenderer(GameObject vessel, Sprite front)
    {
        SpriteRenderer fallback = null;
        SpriteRenderer[] renderers = vessel.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            SpriteRenderer renderer = renderers[i];
            if (renderer.sprite != front) continue;
            if (renderer.name == "FrontGlass") return renderer;
            if (fallback == null) fallback = renderer;
        }
        if (fallback != null) return fallback;
        throw new InvalidOperationException(vessel.name
            + " has no direct SpriteRenderer for its Royal front artwork.");
    }

    /// <summary>
    /// The drawing's visible rectangle in vessel-local units, measured from the source PNG
    /// rather than from the sprite rect. Every PNG in this project carries a different
    /// transparent margin, and laying a scene out from the margin instead of the drawing
    /// is exactly how a shelf ends up half a unit off.
    /// </summary>
    private static Rect SpriteVisualBounds(Sprite sprite)
    {
        int key = sprite.GetInstanceID();
        if (VisualBoundsCache.TryGetValue(key, out Rect cached)) return cached;

        Bounds fallback = sprite.bounds;
        Rect result = Rect.MinMaxRect(
            fallback.min.x, fallback.min.y, fallback.max.x, fallback.max.y);
        string assetPath = AssetDatabase.GetAssetPath(sprite.texture);
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(projectRoot))
        {
            VisualBoundsCache[key] = result;
            return result;
        }

        string absolutePath = Path.Combine(projectRoot, assetPath);
        if (!File.Exists(absolutePath))
        {
            VisualBoundsCache[key] = result;
            return result;
        }

        var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            if (!readable.LoadImage(File.ReadAllBytes(absolutePath), false))
                return result;

            Rect rect = sprite.rect;
            int xStart = Mathf.Clamp(Mathf.FloorToInt(rect.xMin), 0, readable.width - 1);
            int xEnd = Mathf.Clamp(Mathf.CeilToInt(rect.xMax), 1, readable.width);
            int yStart = Mathf.Clamp(Mathf.FloorToInt(rect.yMin), 0, readable.height - 1);
            int yEnd = Mathf.Clamp(Mathf.CeilToInt(rect.yMax), 1, readable.height);
            Color32[] pixels = readable.GetPixels32();
            int minX = xEnd;
            int minY = yEnd;
            int maxX = xStart - 1;
            int maxY = yStart - 1;
            for (int y = yStart; y < yEnd; y++)
            {
                int row = y * readable.width;
                for (int x = xStart; x < xEnd; x++)
                {
                    if (pixels[row + x].a < 8) continue;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (maxX >= minX && maxY >= minY)
            {
                float ppu = Mathf.Max(1f, sprite.pixelsPerUnit);
                float left = (minX - rect.xMin - sprite.pivot.x) / ppu;
                float bottom = (minY - rect.yMin - sprite.pivot.y) / ppu;
                float right = (maxX + 1f - rect.xMin - sprite.pivot.x) / ppu;
                float top = (maxY + 1f - rect.yMin - sprite.pivot.y) / ppu;
                result = Rect.MinMaxRect(left, bottom, right, top);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(readable);
        }

        VisualBoundsCache[key] = result;
        return result;
    }

    private static GameObject FindSceneObject(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child.gameObject;
        }

        throw new InvalidOperationException(name + " was not found in " + SourceGlassScenePath);
    }

    private static void ValidateSourceGlass(GameObject source, VesselProfile expectedProfile)
    {
        LiquidBottle bottle = source != null ? source.GetComponent<LiquidBottle>() : null;
        if (bottle == null || bottle.profile != expectedProfile)
            throw new InvalidOperationException("RoyalGlassLab source link is wrong for " +
                                                expectedProfile.name + ".");
        if (PrefabUtility.IsPartOfPrefabInstance(source))
            throw new InvalidOperationException(source.name +
                " must remain a scene-native RoyalGlassLab object, not a prefab.");
    }

    private static Material EnsureBackdropMaterial()
    {
        Shader shader = Shader.Find("LiquidSort/SortingShelfBackdrop");
        if (shader == null)
            throw new InvalidOperationException("SortingShelfBackdrop shader is unavailable.");

        Material material = AssetDatabase.LoadAssetAtPath<Material>(BackdropMaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "Sorting Shelf Backdrop" };
            AssetDatabase.CreateAsset(material, BackdropMaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetColor("_TopColor", Hex(0x160829));
        material.SetColor("_BottomColor", Hex(0x03020D));
        material.SetColor("_GlowColor", Hex(0x4A1268));
        material.SetFloat("_GlowStrength", 0.46f);
        material.SetFloat("_Vignette", 0.58f);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>
    /// One import recipe for every stage drawing. 384 pixels per unit is what the shelf
    /// parts were authored at; sharing it means the measured world sizes in this file are
    /// comparable across the whole set.
    /// </summary>
    private static void ConfigureStageSprite(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException("Missing stage artwork", path);

        bool needsImport = importer.textureType != TextureImporterType.Sprite
                           || importer.spriteImportMode != SpriteImportMode.Single
                           || Mathf.Abs(importer.spritePixelsPerUnit - 384f) > 0.001f
                           || importer.mipmapEnabled
                           || !importer.alphaIsTransparency
                           || importer.isReadable
                           || importer.textureCompression != TextureImporterCompression.Uncompressed
                           || importer.wrapMode != TextureWrapMode.Clamp
                           || importer.filterMode != FilterMode.Bilinear
                           || importer.maxTextureSize != 2048;
        if (!needsImport) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 384f;
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        settings.spritePivot = new Vector2(0.5f, 0.5f);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.isReadable = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 2048;
        importer.SaveAndReimport();
    }

    // =================================================================================
    //  Validation
    // =================================================================================

    private static void Validate(Scene scene, LevelRig rig, ShelfSolve solve, StageArt art,
        VesselProfile cocktail, VesselProfile tall, VesselProfile beer, Material glassLight)
    {
        if (rig.ShelfView == null || rig.Controller == null
            || rig.ShelfView.Controller != rig.Controller)
            throw new InvalidOperationException(
                "The level controller and shelf view are not directly linked.");
        if (rig.Animator == null || rig.Stream == null
            || rig.Animator.stream != rig.Stream || rig.Stream.material == null)
            throw new InvalidOperationException(
                "The portable pour animator/stream link is incomplete.");
        if (rig.Interaction == null || rig.Interaction.Controller != rig.Controller
            || rig.Interaction.ShelfView != rig.ShelfView
            || rig.Interaction.Animator != rig.Animator
            || rig.Interaction.Session != rig.Session)
            throw new InvalidOperationException(
                "The Bartender pour interaction is not linked to the rig.");
        if (!rig.ShelfView.ValidateFullCampaignBindings(out string bindingError))
            throw new InvalidOperationException(bindingError);
        string portalError = null;
        if (rig.ShelfView.DeliveryPortal == null
            || !rig.ShelfView.DeliveryPortal.ValidateBindings(out portalError))
            throw new InvalidOperationException(
                "Delivery portal binding error: "
                + (rig.ShelfView.DeliveryPortal == null ? "not linked" : portalError));
        if (!rig.Badges.ValidateBindings(out string badgeError))
            throw new InvalidOperationException(badgeError);
        if (!rig.Strip.ValidateBindings(out string stripError))
            throw new InvalidOperationException(stripError);
        if (!rig.LevelBadge.ValidateBindings(out string levelBadgeError))
            throw new InvalidOperationException(levelBadgeError);
        if (!rig.Boosters.ValidateBindings(out string boosterError))
            throw new InvalidOperationException(boosterError);
        if (!rig.ShelfView.Ready || rig.ShelfView.ActiveGlassCount != 6
            || rig.ShelfView.VisibleShelfRows != 2)
            throw new InvalidOperationException(
                "The saved authoring preview must be Level 4's six-glass 3+3 layout.");

        ValidateVerticalBudget(solve);

        // The service rail is a solid shelf roughly two thirds of a unit thick, and the
        // order strip sits directly under it. Nothing in the budget above knows that, so
        // the two silently overlapped and the rail read as a thin red line with its gold
        // finials swallowed. Measured here, from the drawing, for the same reason every
        // other number in this file is measured.
        float railBand = SpriteVisualBounds(art.RailBase).height
                         * (2f * FrameHalfWidth - 0.30f)
                         / SpriteVisualBounds(art.RailBase).width;
        float railBottom = DeliveryRailY - railBand;
        float cardTop = OrderStripCenterY + OrderCardHeight * 0.5f;
        if (cardTop > railBottom + 0.001f)
            throw new InvalidOperationException(
                $"The order strip reaches y={cardTop:0.###} and covers the delivery rail, "
              + $"which ends at y={railBottom:0.###}. Raise the rail or shorten the card.");

        int linkedPlanks = 0;
        int linkedPosts = 0;
        int glassPoolSize = 0;
        int activePreviewGlasses = 0;
        int reflectedGlasses = 0;
        int checkBadges = 0;
        int oldSandboxBoards = 0;
        foreach (GameObject sceneRoot in scene.GetRootGameObjects())
        {
            oldSandboxBoards += sceneRoot.GetComponentsInChildren<WaterSortBoard>(true).Length;
            LiquidBottle[] bottles = sceneRoot.GetComponentsInChildren<LiquidBottle>(true);
            glassPoolSize += bottles.Length;
            for (int i = 0; i < bottles.Length; i++)
            {
                LiquidBottle bottle = bottles[i];
                if (bottle == null || bottle.profile == null || bottle.profile.front == null)
                    throw new InvalidOperationException(
                        "A hand-bound Royal glass is missing its profile/front.");
                if (bottle.gameObject.activeSelf) activePreviewGlasses++;

                BottleShell shell = bottle.GetComponent<BottleShell>();
                bool expectsReflection = bottle.profile == cocktail
                                      || bottle.profile == tall
                                      || bottle.profile == beer;
                float expectedIntensity = bottle.profile == cocktail ? 0.32f : 0.26f;
                if (shell == null || shell.drawGlassLight != expectsReflection)
                    throw new InvalidOperationException(
                        bottle.name + " has an inconsistent Royal reflection state.");
                if (expectsReflection)
                {
                    if (shell.glassLightMaterial != glassLight
                        || Mathf.Abs(shell.lightIntensity - expectedIntensity) > 0.0001f)
                        throw new InvalidOperationException(
                            bottle.name + " lost its canonical Royal reflection settings.");
                    reflectedGlasses++;
                }
            }

            foreach (Transform item in sceneRoot.GetComponentsInChildren<Transform>(true))
            {
                if (PrefabUtility.IsPartOfPrefabInstance(item.gameObject))
                    throw new InvalidOperationException(
                        "Prefab instance found in hand-authored scene: " + item.name);
                SpriteRenderer renderer = item.GetComponent<SpriteRenderer>();
                if (renderer == null) continue;
                if (renderer.sprite == art.Plank) linkedPlanks++;
                if (renderer.sprite == art.Post) linkedPosts++;
                if (renderer.sprite == art.CheckBadge
                    && item.GetComponentInParent<LiquidBottle>(true) != null) checkBadges++;
            }
        }

        if (glassPoolSize != ExpectedPoolSize || activePreviewGlasses != 6)
            throw new InvalidOperationException(
                $"Expected {ExpectedPoolSize} direct Royal pool objects and 6 active "
              + $"preview glasses; got {glassPoolSize}/{activePreviewGlasses}.");
        if (checkBadges != ExpectedPoolSize)
            throw new InvalidOperationException(
                $"Every pool vessel needs its own delivery tick; got {checkBadges} of "
              + $"{ExpectedPoolSize}.");
        if (reflectedGlasses != 24)
            throw new InvalidOperationException(
                $"Expected 24 reflected Cocktail/Tumbler/Beer glasses; got "
              + $"{reflectedGlasses}.");
        if (linkedPlanks != 3 || linkedPosts != 4)
            throw new InvalidOperationException(
                $"Expected three direct planks and four direct posts; got "
              + $"{linkedPlanks}/{linkedPosts}.");
        if (oldSandboxBoards != 0)
            throw new InvalidOperationException(
                "WaterSortBoard must not coexist with the Bartender level-system view.");
    }

    /// <summary>
    /// The check the previous pass was missing. Every solved surface must leave the
    /// tallest possible vessel inside the playfield band, so a glass can never grow
    /// through the order strip above it or through the shelf it is standing under.
    /// </summary>
    private static void ValidateVerticalBudget(ShelfSolve solve)
    {
        void Check(int rows, float surface, int row, float scale)
        {
            float top = surface + solve.TallestGlass * scale;
            float ceiling = row == 0
                ? PlayfieldTopY
                : SurfaceY(rows, row - 1, solve.PlankBand) - solve.PlankBand;
            if (top > ceiling + 0.001f)
                throw new InvalidOperationException(
                    $"Solved layout overflows: {rows}-row row {row + 1} reaches "
                  + $"y={top:0.###} but only has room to y={ceiling:0.###}. "
                  + "Widen the playfield band or lower the glass scale ceiling.");
        }

        Check(2, solve.TwoRowSurfaces.x, 0, solve.ScaleTwoRow);
        Check(2, solve.TwoRowSurfaces.y, 1, solve.ScaleTwoRow);
        Check(3, solve.ThreeRowSurfaces.x, 0, solve.ScaleThreeRow);
        Check(3, solve.ThreeRowSurfaces.y, 1, solve.ScaleThreeRow);
        Check(3, solve.ThreeRowSurfaces.z, 2, solve.ScaleThreeRow);

        float bottomOfLowestPlank = solve.ThreeRowSurfaces.z - solve.PlankBand;
        if (bottomOfLowestPlank < PlayfieldBottomY - 0.001f)
            throw new InvalidOperationException(
                $"The lowest plank ends at y={bottomOfLowestPlank:0.###}, past the "
              + $"playfield floor y={PlayfieldBottomY:0.###}.");

        float orderStripBottom = OrderStripCenterY - OrderCardHeight * 0.5f;
        if (orderStripBottom < PlayfieldTopY - 0.001f)
            throw new InvalidOperationException(
                $"The order strip reaches y={orderStripBottom:0.###} and overlaps the "
              + $"playfield ceiling y={PlayfieldTopY:0.###}.");

        float bottomBarTop = BottomBarCenterY + BottomButtonDiameter * 0.5f;
        if (bottomBarTop > PlayfieldBottomY + 0.001f)
            throw new InvalidOperationException(
                $"The booster bar reaches y={bottomBarTop:0.###} and overlaps the "
              + $"playfield floor y={PlayfieldBottomY:0.###}.");
    }

    // =================================================================================
    //  Portable prefab
    // =================================================================================

    /// <summary>
    /// Exports the scene-native hierarchy as a single scene-transfer unit without turning
    /// the authoring scene into a prefab instance. The showcase deliberately stays on its
    /// isolated preview layer; the portable copy is saved on Default so an ordinary target
    /// camera can render it immediately.
    /// </summary>
    private static void SavePortablePrefab(GameObject root)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        EnsureAssetFolder(Path.GetDirectoryName(PortablePrefabPath)?.Replace('\\', '/'));

        Transform rootTransform = root.transform;
        Transform previousParent = rootTransform.parent;
        int previousSibling = rootTransform.GetSiblingIndex();
        Vector3 previousLocalPosition = rootTransform.localPosition;
        Quaternion previousLocalRotation = rootTransform.localRotation;
        Vector3 previousLocalScale = rootTransform.localScale;

        var staging = new GameObject("Portable Prefab Save Staging (Editor Only)");
        staging.SetActive(false);
        try
        {
            // Inactive-in-hierarchy runs ExecuteAlways cleanup on transient meshes, sprites
            // and textures before Unity serializes the stable object/reference structure.
            rootTransform.SetParent(staging.transform, false);
            rootTransform.localPosition = Vector3.zero;
            rootTransform.localRotation = Quaternion.identity;
            rootTransform.localScale = Vector3.one;
            SetLayerRecursively(rootTransform, 0);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(
                root, PortablePrefabPath, out bool success);
            if (!success || saved == null)
                throw new IOException("Could not save " + PortablePrefabPath);
        }
        finally
        {
            rootTransform.SetParent(previousParent, false);
            rootTransform.localPosition = previousLocalPosition;
            rootTransform.localRotation = previousLocalRotation;
            rootTransform.localScale = previousLocalScale;
            rootTransform.SetSiblingIndex(previousSibling);
            SetLayerRecursively(rootTransform, StageLayer);
            UnityEngine.Object.DestroyImmediate(staging);
        }

        ValidatePortablePrefab();
    }

    private static void ValidatePortablePrefab()
    {
        GameObject prefabRoot = PrefabUtility.LoadPrefabContents(PortablePrefabPath);
        try
        {
            if (prefabRoot == null)
                throw new InvalidOperationException("Portable Bartender prefab could not be loaded.");
            if (prefabRoot.transform.localPosition != Vector3.zero
                || prefabRoot.transform.localRotation != Quaternion.identity
                || prefabRoot.transform.localScale != Vector3.one)
                throw new InvalidOperationException(
                    "Portable Bartender prefab root must keep an identity transform.");

            foreach (Transform item in prefabRoot.GetComponentsInChildren<Transform>(true))
                if (item.gameObject.layer != 0)
                    throw new InvalidOperationException(
                        "Portable Bartender prefab must use Default layer: " + item.name);

            if (prefabRoot.GetComponentsInChildren<Camera>(true).Length != 0
                || prefabRoot.GetComponentsInChildren<AudioListener>(true).Length != 0
                || prefabRoot.GetComponentsInChildren<EventSystem>(true).Length != 0)
                throw new InvalidOperationException(
                    "Portable Bartender prefab must not own a camera, AudioListener or "
                  + "EventSystem; the host scene provides all three.");
            if (prefabRoot.GetComponentsInChildren<WaterSortBoard>(true).Length != 0)
                throw new InvalidOperationException(
                    "Portable Bartender prefab must not contain WaterSortBoard.");

            BartenderLevelController[] controllers =
                prefabRoot.GetComponentsInChildren<BartenderLevelController>(true);
            BartenderShelfLevelView[] views =
                prefabRoot.GetComponentsInChildren<BartenderShelfLevelView>(true);
            PourAnimator[] animators =
                prefabRoot.GetComponentsInChildren<PourAnimator>(true);
            PourStream[] streams = prefabRoot.GetComponentsInChildren<PourStream>(true);
            BartenderPourInteraction[] interactions =
                prefabRoot.GetComponentsInChildren<BartenderPourInteraction>(true);
            BartenderSession[] sessions =
                prefabRoot.GetComponentsInChildren<BartenderSession>(true);
            DeliveryBadgePresenter[] badges =
                prefabRoot.GetComponentsInChildren<DeliveryBadgePresenter>(true);
            if (controllers.Length != 1 || views.Length != 1 || animators.Length != 1
                || streams.Length != 1 || interactions.Length != 1
                || sessions.Length != 1 || badges.Length != 1)
                throw new InvalidOperationException(
                    "Portable Bartender prefab gameplay components are incomplete or duplicated.");

            BartenderLevelController controller = controllers[0];
            BartenderShelfLevelView view = views[0];
            PourAnimator animator = animators[0];
            PourStream stream = streams[0];
            BartenderPourInteraction interaction = interactions[0];
            if (view.Controller != controller
                || animator.stream != stream
                || interaction.Controller != controller
                || interaction.ShelfView != view
                || interaction.Animator != animator
                || interaction.Session != sessions[0]
                || sessions[0].Controller != controller)
                throw new InvalidOperationException(
                    "Portable Bartender prefab has a broken internal gameplay reference.");
            if (!view.ValidateFullCampaignBindings(out string bindingError))
                throw new InvalidOperationException(bindingError);
            if (!badges[0].ValidateBindings(out string badgeError))
                throw new InvalidOperationException(badgeError);

            LiquidBottle[] bottles =
                prefabRoot.GetComponentsInChildren<LiquidBottle>(true);
            int activeBottles = 0;
            for (int i = 0; i < bottles.Length; i++)
                if (bottles[i].gameObject.activeSelf) activeBottles++;
            if (bottles.Length != ExpectedPoolSize || activeBottles != 6)
                throw new InvalidOperationException(
                    $"Portable Bartender prefab expected {ExpectedPoolSize} pool glasses "
                  + $"and six active preview glasses; got {bottles.Length}/{activeBottles}.");

            PortalDeliveryAnimator[] portals =
                prefabRoot.GetComponentsInChildren<PortalDeliveryAnimator>(true);
            if (portals.Length != 1)
                throw new InvalidOperationException(
                    "Portable Bartender prefab needs exactly one delivery portal.");
            if (!portals[0].ValidateBindings(out string portalError))
                throw new InvalidOperationException(portalError);

            OrderStripPresenter[] strips =
                prefabRoot.GetComponentsInChildren<OrderStripPresenter>(true);
            string stripError = null;
            if (strips.Length != 1 || !strips[0].ValidateBindings(out stripError))
                throw new InvalidOperationException(
                    "Portable Bartender prefab order strip is not bound: "
                    + (strips.Length != 1 ? "wrong component count" : stripError));

            BoosterBarPresenter[] boosters =
                prefabRoot.GetComponentsInChildren<BoosterBarPresenter>(true);
            string boosterError = null;
            if (boosters.Length != 1
                || !boosters[0].ValidateBindings(out boosterError))
                throw new InvalidOperationException(
                    "Portable Bartender prefab booster bar is not bound: "
                    + (boosters.Length != 1 ? "wrong component count" : boosterError));
        }
        finally
        {
            if (prefabRoot != null) PrefabUtility.UnloadPrefabContents(prefabRoot);
        }
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        if (string.IsNullOrEmpty(assetFolder) || AssetDatabase.IsValidFolder(assetFolder)) return;
        int slash = assetFolder.LastIndexOf('/');
        if (slash <= 0)
            throw new InvalidOperationException("Invalid asset folder: " + assetFolder);
        string parent = assetFolder.Substring(0, slash);
        string name = assetFolder.Substring(slash + 1);
        EnsureAssetFolder(parent);
        if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, name)))
            throw new IOException("Could not create asset folder " + assetFolder);
    }

    private static void RenderPreview(Camera camera)
    {
        var target = new RenderTexture(DesignWidth, DesignHeight, 24,
            RenderTextureFormat.ARGB32)
        {
            name = "SortingShelfShowcasePreview",
            antiAliasing = 1,
            filterMode = FilterMode.Bilinear
        };
        target.Create();

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        var texture = new Texture2D(DesignWidth, DesignHeight, TextureFormat.RGBA32,
            false, false);
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, DesignWidth, DesignHeight), 0, 0, false);
            texture.Apply(false, false);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new DirectoryNotFoundException("Could not resolve the Unity project root.");
            string absolutePreview = Path.Combine(projectRoot, PreviewPath);
            string previewDirectory = Path.GetDirectoryName(absolutePreview);
            if (!string.IsNullOrEmpty(previewDirectory))
                Directory.CreateDirectory(previewDirectory);
            File.WriteAllBytes(absolutePreview, texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            UnityEngine.Object.DestroyImmediate(texture);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    private static Transform Folder(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static T Load<T>(string path) where T : UnityEngine.Object
    {
        T asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null) throw new FileNotFoundException("Missing asset", path);
        return asset;
    }

    private static Color Hex(int rgb) => new Color(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }
}
