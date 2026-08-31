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
/// deliberately no prefab instances inside the gameplay stage and no runtime object
/// generator: every Royal glass, shelf plank, post, order card, button and level-system
/// link is saved directly into the scene. The full-screen main menu is the intentional
/// exception: it remains a linked prefab so its hand-authored UI can evolve independently.
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
    private const string MainMenuPrefabPath =
        "Assets/LiquidSort/Prefabs/UI/BartenderMainMenuCanvas.prefab";

    private const string RequestPath = "Temp/sorting-shelf-showcase.req";
    private const string DonePath = "Temp/sorting-shelf-showcase.done";
    private const string PreviewPath = "Temp/SortingShelfShowcase.png";
    // The supplied phone reference is 607 x 1280. Keep a render at that exact frame so
    // visual decisions are made from the physical phone composition, not a 720 x 1280
    // editor-only preview that has not received the responsive fit.
    private const string PhonePreviewPath = "Temp/SortingShelfShowcase_iPhone.png";
    private const int PhonePreviewWidth = 607;
    private const int PhonePreviewHeight = 1280;
    private const string BackdropMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/SortingShelfBackdrop.mat";
    private const string PortalCutoutMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/GeneratedPortalCutout.mat";
    private const string GlassLightMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/RoyalGlassLight.mat";
    private const string PourStreamMaterialPath =
        "Assets/LiquidSort/Materials/PourStream.mat";
    private const string SourceGlassScenePath =
        "Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity";

    private const string ArtRoot = "Assets/LiquidSort/RoyalGlassLab/Art";
    private const string LevelBadgeArtPath =
        ArtRoot + "/Ui/LevelBadge_Cute_Empty_Clean.png";
    private const string LevelBadgeLabelRoot = ArtRoot + "/Ui/LevelBadge/Labels";
    private const string ExtractedButtonRoot = ArtRoot + "/Ui/ExtractedButtons";
    private const string UndoButtonArtPath =
        ExtractedButtonRoot + "/Ui_ButtonUndo.png";
    private const string AddTimeButtonArtPath =
        ExtractedButtonRoot + "/Ui_ButtonAddTime.png";
    private const string ShuffleButtonArtPath =
        ExtractedButtonRoot + "/Ui_ButtonShuffle.png";
    private const string BottomFloorPath =
        ArtRoot + "/Floor/BottomFloor_TanTiles_v1.png";
    private const string FloorMedallionPath =
        ArtRoot + "/Floor/FloorMedallion_Oval_Tan_v1.png";
    private const string ShelfPlankPath = ArtRoot + "/ShelfParts/ShelfPlank_Burgundy_v1.png";
    private const string ShelfPostPath = ArtRoot + "/ShelfParts/ShelfPost_BurgundyGold_v1.png";
    private const string CheckBadgePath = ArtRoot + "/CheckBadge_Clean.png";
    private const string SkyPath = ArtRoot + "/UpperStage/UpperSkyBackdrop_v1.png";
    private const string ArchPath = ArtRoot + "/UpperStage/UpperStoneArch_v1.png";
    private const string CurtainPath = ArtRoot + "/UpperStage/UpperCurtainPair_v1.png";
    // This is deliberately a baked composition, not a fourth decorative overlay. The
    // sky, curtains and stone bridge form one immovable piece of stage architecture on
    // a phone; keeping them as separate responsive sprites was what let their seams and
    // side crops drift apart.
    private const string BakedUpperStagePath =
        ArtRoot + "/UpperStage/UpperStage_SkyArchCurtain_Baked.png";
    private const string ColumnPath = ArtRoot + "/DeliveryTop/DeliveryColumn_Left_v1.png";
    private const string RailBasePath = ArtRoot + "/DeliveryTop/DeliveryRail_Base_v1.png";
    private const string PortalBackPath = ArtRoot + "/DeliveryTop/DeliveryPortal_Back_v4.png";
    private const string PortalFrontPath = ArtRoot + "/DeliveryTop/DeliveryPortal_Front_v4.png";
    private const string PortalSidePath =
        ArtRoot + "/DeliveryTop/DeliveryPortal_Side_v1.png";
    private const string PortalOccluderPath =
        ArtRoot + "/DeliveryTop/DeliveryPortal_Occluder_v4.png";
    private const string OrderCardArtRoot = ArtRoot + "/OrderCards/Final";
    private const string OrderCardPanelPath =
        OrderCardArtRoot + "/OrderCard_Panel_Cream.png";
    private const string OrderCardFramePath =
        OrderCardArtRoot + "/OrderCard_Frame_Purple.png";
    private const string OrderCardClipPath =
        OrderCardArtRoot + "/OrderCard_Clip_Gold.png";
    private const string OrderCardChipFillPath =
        OrderCardArtRoot + "/OrderCard_ColorPip_Fill_Tintable.png";
    private const string OrderCardChipRimPath =
        OrderCardArtRoot + "/OrderCard_ColorPip_Rim_Gold.png";

    private const string SettingsArtRoot = "Assets/LiquidSort/Settings/Art";
    private const string SettingsPanelPath =
        SettingsArtRoot + "/SettingsButton_Panel_Blue.png";
    private const string SettingsFramePath =
        SettingsArtRoot + "/SettingsButton_Frame_Gold.png";
    private const string SettingsIconPath =
        SettingsArtRoot + "/SettingsButton_Icon_SettingsCombined.png";
    private const string SettingsSoundPath =
        SettingsArtRoot + "/SettingsButton_Icon_Sound.png";
    private const string SettingsMusicPath =
        SettingsArtRoot + "/SettingsButton_Icon_Music.png";
    private const string SettingsVibrationPath =
        SettingsArtRoot + "/SettingsButton_Icon_Vibration.png";
    private const string SettingsExitPath =
        SettingsArtRoot + "/SettingsButton_Icon_Exit.png";
    private const string SettingsMuteSlashPath =
        SettingsArtRoot + "/SettingsButton_Overlay_MuteSlash.png";
    private const string ExitConfirmationArtRoot =
        ArtRoot + "/ExitConfirmation/Runtime";
    private const string ExitConfirmationFramePath =
        ArtRoot + "/ResultPopup/Runtime/ResultPopup_Frame_UserApproved.png";
    private const string ExitConfirmationDoorPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Icon_Door.png";
    private const string ExitConfirmationQuestionPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Text_Question_TR.png";
    private const string ExitConfirmationConfirmButtonPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Button_Confirm_Red.png";
    private const string ExitConfirmationConfirmTextPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Text_Yes_TR.png";
    private const string ExitConfirmationCancelButtonPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Button_Cancel_Blue.png";
    private const string ExitConfirmationCancelTextPath =
        ExitConfirmationArtRoot + "/ExitConfirmation_Text_Cancel_TR.png";
    private const string SettingsClickPath =
        "Assets/Resources/Audio/SFX_ButtonClick.ogg";

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
    private const int BakedUpperStageWidthPixels = 2048;
    private const int BakedUpperStageLayer = 31;
    // Exact 2/3 projection of the source 1080x1920 GameplayPauseCanvas:
    // 138 button, 22 right margin, 150 top inset, 156 vertical step, 762 card.
    private const float SettingsButtonSize = 92f;
    private const float SettingsRightInset = 14.666667f;
    private const float SettingsTopInset = 100f;
    private const float SettingsButtonStep = 104f;
    private const float SettingsCardHeight = 508f;
    private const float FrameHalfWidth =
        CameraHalfHeight * DesignWidth / (float)DesignHeight;      // 3.375
    /// <summary>World units per design pixel; the HUD canvases are scaled by this.</summary>
    private const float WorldPerDesignPixel = 2f * CameraHalfHeight / DesignHeight;

    // ---- Vertical budget --------------------------------------------------------
    //
    // Five bands, top to bottom. These are THE authored numbers of this scene; every
    // other vertical value in the file is derived from them.

    // ---- Delivery stage, measured off the approved composition ------------------
    //
    // The reference is 660 px wide, which is this frame's 6.75 units, so 97.8 px is one
    // unit. Every number below was read off it at that scale and is written in units, so
    // it survives a change of screen, of resolution and of reference image.
    //
    //   rail top .............. 318 px below the frame top -> 3.25 above the rail line
    //   arch span ............. 533 px -> 5.45 wide, apex 3.17 above the rail
    //   post .................. 44 x 227 px -> 0.45 x 2.32, centre 0.31 from the left
    //   tower ................. 110 x 214 px -> 1.12 x 2.19, right edge 0.10 from the right
    //   served glass .......... centre 495 px -> world x +1.69
    //   travel trail guide .... centre 415 px -> world x +0.87
    //
    // The post drawing matches its measurement exactly (0.1946 aspect x 2.32 = 0.451).
    // The TOWER DOES NOT: the reference tower is 0.51 wide per unit of height, the
    // artwork is 1.01 -- twice as wide. Matching both would mean scaling x and y
    // differently, which turns the dome into an oval. Height and the right-hand edge are
    // matched instead, and the size is chosen so the served glass still lands on +1.65,
    // a pixel and a half from where the reference stands it.
    /// <summary>
    /// Visible span of the stone bridge in the approved reference. Its 3.13-unit
    /// visible height then lands its feet at the delivery rail without stretching the
    /// stones to fill the whole screen width.
    /// </summary>
    private const float ArchSpan = 5.45f;
    /// <summary>
    /// Amount of the bridge crown deliberately hidden above the physical phone edge.
    /// This is a crop, not a scale increase: the reference reaches the wider part of the
    /// same arch at y=0 while keeping its lower legs their authored size.
    /// </summary>
    private const float ArchTopBleed = 0.82f;

    private const float PostHeight = 2.32f;
    private const float PostSinkIntoRail = 0.17f;
    private const float TowerHeight = 2.50f;
    /// <summary>
    /// The gap the gate and the post each keep from their OWN wall. One number for both
    /// ends of the service counter: the two used to be nudged separately, so the post
    /// stood 0.079 from the left wall while the gate stood 0.205 from the right, and the
    /// stage read as lopsided without anything obviously being wrong.
    /// </summary>
    private const float StageWallInset = 0.055f;
    private const float TowerRightEdgeX = FrameHalfWidth - StageWallInset;

    /// <summary>
    /// How far the curtain hangs INTO the frame. The art is one full-width pair. Its
    /// visible silhouette is slightly widened and made taller so the phone's top edge
    /// samples the same deeper folds as the reference, while the lower drape remains at
    /// the same y coordinate.
    /// </summary>
    private const float CurtainVisibleDrop = 1.03f;
    private const float CurtainHorizontalScale = 1.06f;
    // Measured against the physical 607 x 1280 reference: its curtain ends at y=90
    // while keeping a 137 px side cover at the top, so it is taller than the raw art
    // but still anchored to the same lower visible edge.
    private const float CurtainVerticalScale = 1.24f;
    // These values apply only inside the baked top plate. The bridge is intentionally
    // larger than the live frame and is clipped by the plate: that is how its sides run
    // into the rail and tower rather than ending as two loose floating legs.
    private const float BakedBridgeVisibleSpan = 7.14f;
    private const float BakedBridgeVerticalScale = 1.434f;
    private const float BakedBridgeTopBleed = 1.32f;
    private const float TowerSinkIntoRail = 0.19f;
    // The doorway, as a share of the tower - MEASURED OFF THE DRAWING, not authored.
    // The gate art carries no alpha: image generation handed over an opaque neutral
    // checkerboard, and GeneratedPortalCutout mattes it by CHROMA, so the doorway is
    // simply the neutral island inside the gold. Reading that island back gives the only
    // rectangle the purple interior can sit in without leaking past the frame or leaving
    // a gap inside it. The absolute numbers this replaces described a doorway 34% too
    // narrow, seated too low - which is why the depth behind the gate never lined up.
    private const float PortalOpeningWidthRatio = 0.5895f;
    private const float PortalOpeningHeightRatio = 0.5714f;
    private static readonly Vector2 PortalOpeningCenterRatio =
        new Vector2(-0.0158f, -0.1883f);

    // Matches the approved scene's 88 design-pixel top inset. Keeping this authored
    // value in the builder prevents a rebuild from moving the badge upward by ~20 px.
    private const float TopBarCenterY = 5.175f;
    private const float TopBarHeight = 0.59f;
    private const float LevelBadgeScale = 1.10f;
    private const float LevelBadgeWidth = 1.73f * LevelBadgeScale;
    // The clean crown badge is 1632x768. It deliberately overhangs the compact top-bar
    // root; no mask is used, so the crown can occupy the sky while the purple panel
    // remains centred on the old interaction-free HUD slot.
    private const float LevelBadgeArtHeight = LevelBadgeWidth * 768f / 1632f;
    private const float LevelBadgeArtOffsetY = 0.101f * LevelBadgeScale;
    private static readonly Vector2 LevelLabelSize =
        new Vector2(1.50f, 0.375f) * LevelBadgeScale;
    private const float TopSettingsButtonSize = 72f;
    private static readonly Vector2 TopSettingsButtonInset = new Vector2(28f, 40f);
    /// <summary>Surface a delivered glass stands on while it waits at the door.</summary>
    // The phone reference places the rail 15 physical pixels lower than the canonical
    // 720-wide mock-up. Keeping it here puts the bridge feet behind the rail's side art
    // rather than making the bridge itself taller.
    private const float DeliveryRailY = 2.84f;
    // The baked plate reaches a little behind the rail. The rail remains a separate
    // foreground prop and hides this lower edge, so the upper architecture has no seam.
    private const float BakedUpperStageBottomBleed = 0.30f;
    private static float BakedUpperStageBottomY =>
        DeliveryRailY - BakedUpperStageBottomBleed;
    private const float OrderStripCenterY = 1.46f;
    private const float OrderCardWidth = 1.52f;
    private const float OrderCardHeight = 1.80f;
    private const float OrderCardSpacing = 2.06f;
    /// <summary>
    /// Uniform blow-up of the finished card. The restored 1.46 centre keeps the enlarged
    /// card clear of the manually approved top-row Royal vessels and the service rail.
    /// </summary>
    private const float OrderCardScale = 1.15f;
    // Recovered from the manually approved shelf composition. This is the ceiling used
    // for both the two-row and three-row budgets, so changing it silently moves every
    // seat and changes every fitted glass scale.
    private const float PlayfieldTopY = 0.54f;
    private const float PlayfieldBottomY = -4.62f;
    private const float BottomBarCenterY = -5.32f;
    private const float BottomButtonDiameter = 1.24f;
    private const float BottomButtonSpacing = 1.78f;

    // ---- Playfield solve inputs -------------------------------------------------

    private const float ShelfScaleX = 1.55f;
    /// <summary>
    /// Plank thickness multiplier from the manually approved shelf composition.
    /// </summary>
    private const float ShelfScaleY = 1.10f;
    private const float PostScaleX = 0.72f;
    // Kept independent from the fitted post height. SpriteRenderer's sliced mode grows
    // only the purple centre, so this remains the authored size of both gold collars.
    private const float PostCapScaleY = 0.44f;
    private const float PostCenterX = 3.02f;
    /// <summary>
    /// Preserve the measured three-row furniture and vessel pose without a second
    /// contraction pass.
    /// </summary>
    private const float ThreeRowCompositionScale = 1f;
    /// <summary>
    /// Measured gap between the top of a Royal vessel and the plank above it.
    /// </summary>
    private const float RowClearance = 0.14f;
    /// <summary>Measured gap between neighbouring Royal vessels.</summary>
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
    /// <summary>
    /// The tan floor is HUD geometry but stage SCENERY - it belongs behind the shelves,
    /// not in front of them with the buttons. It lives inside the screen canvas, which
    /// sorts at 200, so it used to cover the bottom plank no matter what order the planks
    /// took. A sorting override on its own sub-canvas is what lets a UI image sit under a
    /// SpriteRenderer at all.
    /// </summary>
    private const int BottomFloorOrder = -10;
    private const int SkyOrder = -40;
    // The rail is FURNITURE STANDING IN FRONT of the arch, not a stripe painted on the
    // back wall. Drawn behind it, the arch legs ate its gold end-caps and it read as a
    // red line. Everything that stands on the counter is ordered above it in turn.
    private const int RailBaseOrder = 44;
    // The shelf furniture is drawn BEHIND the vessels standing on it. It used to be in
    // front (plank 10, post 5), which read as the glasses being sunk into the plank -
    // their feet disappeared behind it. Both drop below the -1 floor of BottleShell's own
    // renderers. Back to front: post, plank shadow, plank. This tucks the posts behind the
    // board instead of laying their gold collars flat on its face, while the existing
    // under-board shadow falls across the joint and supplies the reference's depth cue.
    private const int PostOrder = -5;
    private const int PlankShadowOrder = -4;
    private const int PlankOrder = -3;
    /// <summary>
    /// The soft dark band the reference keeps under every shelf. It is NOT a new asset:
    /// the plank's own drawing, tinted black and dropped a little, is the shadow. Doing it
    /// with a real light was never on the table - this project is Built-in RP and every
    /// sprite runs the unlit Sprites/Default, so a Light in the scene changes nothing.
    /// </summary>
    private const float PlankShadowDrop = -0.14f;
    private const float PlankShadowAlpha = 0.46f;
    // The dark copy is deliberately a fraction wider/taller than the board.  It gives
    // the reference's soft lower lip without drawing a separate painted shadow texture,
    // and because it is a child it follows every dynamic shelf layout exactly.
    private static readonly Vector2 PlankShadowScale = new Vector2(1.018f, 1.14f);
    /// <summary>Above every renderer BottleShell publishes (-1 … 8).</summary>
    private const int CheckBadgeOrder = 12;
    private const int OrderCardCanvasOrder = 30;
    private const int ArchOrder = 40;
    // One plate behind the counter, post and portal: the bake is scenery; the tower
    // remains a real foreground object seated into the right side of that scenery.
    private const int BakedUpperStageOrder = ArchOrder;
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
    // RoyalGlassLab silhouettes are canonical and may move, but must not be squashed.
    private const float EntranceLandingSquash = 0f;
    private const float EntranceSettleDuration = 0.20f;
    private const float ShelfFadeDuration = 0.22f;
    private const float ReseatDuration = 0.22f;

    private const float PortalAnticipationDuration = 0.08f;
    private const float PortalLiftDuration = 0.32f;
    private const float PortalApproachDuration = 0.22f;
    private const float PortalMinimumApproachLead = 0.16f;
    private const float PortalEntryDuration = 0.25f;
    private const float PortalHideDuration = 0.14f;
    private const float PortalBounceDuration = 0.18f;
    private const float PortalEntryDepth = 0.68f;
    private const float PortalEntryTilt = 5f;
    private const float PortalFit = 0.93f;
    private const float PortalMouthClearance = 0.05f;

    // ---- Palette ----------------------------------------------------------------

    private static readonly Color CardCream = Hex(0xF3E4C4);
    private static readonly Color CardRim = Hex(0x6C4BB0);
    private static readonly Color CardClipHole = Hex(0xE9A93C);
    private static readonly Color ButtonFace = Hex(0x4B34A8);
    private static readonly Color ButtonRim = Hex(0xE9A93C);
    private static readonly Color ButtonGlyph = Hex(0xFFF6E2);
    private static readonly Color BadgeFace = Hex(0x3A2380);
    private static readonly Color BadgeText = Hex(0xFFF1CF);
    private const int ExpectedPoolSize =
        BartenderShelfLevelView.FullCampaignShotPoolSize
        + BartenderShelfLevelView.FullCampaignCocktailPoolSize
        + BartenderShelfLevelView.FullCampaignLattePoolSize
        + BartenderShelfLevelView.FullCampaignTumblerPoolSize
        + BartenderShelfLevelView.FullCampaignBiraPoolSize;

    private static bool refreshed;
    private static readonly Dictionary<int, Rect> VisualBoundsCache =
        new Dictionary<int, Rect>();
    private static readonly Dictionary<int, Rect> ChromaBoundsCache =
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
        string request = File.ReadAllText(RequestPath).Trim();
        File.Delete(RequestPath);
        try
        {
            // A visual-only bake should not rebuild the full playable level. It is used
            // while matching reference art, and produces the same final sprite the scene
            // build consumes without making the user wait for glasses, cards and rigs.
            if (string.Equals(request, "bake-upper-stage-only",
                    StringComparison.OrdinalIgnoreCase))
            {
                BakeUpperStageOnly();
                File.WriteAllText(DonePath,
                    "ok\nupperStageBake=" + BakedUpperStagePath);
                return;
            }

            ShelfSolve solve = Build();
            File.WriteAllText(DonePath,
                "ok\nscene=" + ScenePath
                + "\npreview=" + PreviewPath
                + "\nphonePreview=" + PhonePreviewPath
                + "\nprefab=" + PortablePrefabPath
                + "\nactivePreviewGlasses=6\nmanualGlassPool=" + ExpectedPoolSize
                + "\nlayout=3+3\nshelfRows=3\nposts=4\n"
                + "gameplayPrefabInstances=0\nmainMenuPrefabInstances=1\n"
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

    [MenuItem("Tools/LiquidSort/Bake Upper Stage Background")]
    public static void BakeUpperStageOnly()
    {
        VisualBoundsCache.Clear();
        Sprite baked = BakeUpperStageBackground(new StageArt());
        if (baked == null)
            throw new InvalidOperationException("Upper-stage bake did not produce a sprite.");
        AssetDatabase.SaveAssets();
    }

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

        /// <summary>
        /// Runtime keeps this small same-layout reserve even on a three-across board. It
        /// leaves room for selection lift above a wide Royal cocktail without falling all
        /// the way back to the much smaller three-row campaign budget.
        /// </summary>
        public float SafeTwoRowScale => Mathf.Min(ScaleTwoRow, ScaleFourInTwoRows);

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

    /// <summary>
    /// Height a vessel may reach on a plank while keeping the approved overhead gap.
    /// </summary>
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
                Vector3 authored = binding.bottle.profile.ShelfReferenceLocalScale;
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

        // Four scales, but only ever ONE of them per board: the level system reads the
        // pair (row count, busiest row) and dresses every glass on every plank in the
        // single scale that lands here. That is why each entry takes the min of a height
        // budget and a width budget - the board has to survive its tightest row, and the
        // looser rows then simply carry more air rather than a second glass size.
        // The up-to-three entries are budgeted at the three-across cell on purpose: a
        // two-glass row has width to spare, and letting it grow would make the same
        // vessel a different size from one level to the next.
        solve.ScaleTwoRow = Clamp(Mathf.Min(heightAtTwo, widthAtThree));
        solve.ScaleThreeRow = Clamp(Mathf.Min(heightAtThree, widthAtThree));
        solve.ScaleFourInTwoRows = Clamp(Mathf.Min(heightAtTwo, widthAtFour));
        solve.ScaleFourInThreeRows = Clamp(Mathf.Min(heightAtThree, widthAtFour));
        return solve;
    }

    /// <summary>
    /// Scale at which the widest vessel still fits one cell of an
    /// <paramref name="across"/>-glass row after reserving the approved neighbour gap.
    /// </summary>
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
        ChromaBoundsCache.Clear();
        foreach (string path in new[]
                 {
                     ShelfPlankPath, ShelfPostPath, CheckBadgePath, SkyPath, ArchPath,
                     CurtainPath, ColumnPath, RailBasePath,
                     PortalBackPath, PortalFrontPath, PortalOccluderPath, PortalSidePath
                 })
            ConfigureStageSprite(path);
        ConfigureUiSprite(LevelBadgeArtPath);
        for (int level = 1; level <= 30; level++)
            ConfigureUiSprite(LevelBadgeLabelPath(level));
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
            Material portalCutoutMaterial = EnsurePortalCutoutMaterial();
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
            // The Royal source scene is open additively while this hierarchy is built.
            // Move host-owned objects explicitly so a Unity version change cannot place
            // them in the source scene and silently strip the screen-canvas reference.
            SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
            GameObject eventSystem = BuildEventSystem();
            SceneManager.MoveGameObjectToScene(eventSystem, scene);

            var root = new GameObject("Bartender Shelf Rig - Responsive Portrait");
            Transform worldContent = Folder(root.transform,
                "01 World Composition 720x1280 - Width Fit + Top Aligned");

            Transform environment = Folder(worldContent,
                "01 Environment - Hand Authored");
            BuildBackdrop(environment, backdropMaterial);
            ShelfPieces shelf = BuildShelf(environment, art);

            Transform deliveryRoot = Folder(worldContent,
                "02 Upper Delivery - Hand Authored");

            Transform glasses = Folder(worldContent,
                $"03 Glasses - Manual Royal Pools ({ExpectedPoolSize})");
            // This folder is the layout origin. A scene-only offset here moves every
            // correctly seated Royal vessel away from its solved shelf surface.
            glasses.localPosition = Vector3.zero;
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
            // Populated only now: the gate has to know how big a glass it will be asked
            // to swallow before it can decide how much that glass must shrink on the way.
            DeliveryStage delivery = BuildDeliveryStage(
                deliveryRoot, art, solve, portalCutoutMaterial);

            List<OrderCardView.GlassIcon> icons = BuildGlassIconTable(
                shot, cocktail, mug, tall, beer);
            OrderStrip strip = BuildOrderStrip(worldContent, art, icons, camera);
            ScreenControls controls = BuildScreenControls(root.transform, art, font,
                camera);

            Transform systems = Folder(root.transform,
                "03 Level System - Serialized References");
            LevelRig rig = BuildLevelRig(systems, worldContent, art, palette,
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
            WorldSpaceSafeAreaFitter safeAreaFitter =
                AttachSafeAreaFitter(root.transform, camera, worldContent);
            if (!rig.ShelfView.ConfigureResponsiveFitForAuthoring(safeAreaFitter))
                throw new InvalidOperationException(
                    "Could not bind the responsive fitter to the authored shelf view.");

            // Reassert both camera-space canvas bindings immediately before saving and
            // previewing. The portable prefab intentionally clears these references
            // later, after the authored scene and preview are complete.
            strip.Canvas.worldCamera = camera;
            controls.Canvas.worldCamera = camera;

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);

            if (SystemInfo.graphicsDeviceType
                != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                RenderPreview(camera);
                RenderPreview(camera, safeAreaFitter, rig.ShelfView,
                    PhonePreviewWidth, PhonePreviewHeight, PhonePreviewPath);
            }
            else
                Debug.Log("LiquidSort: sorting preview skipped because no graphics "
                        + "device is available.");
            // A prefab cannot hold a reference to a scene object outside its root. The
            // canvases are released here, after the scene has been saved with them bound,
            // so the authoring file shows a real HUD and the prefab resolves one instead.
            strip.Canvas.worldCamera = null;
            controls.Canvas.worldCamera = null;
            SavePortablePrefab(root);

            // Keep the portable gameplay rig self-starting, then turn off automatic
            // loading only for the authored menu scene. This preserves the prefab's
            // standalone contract while making Editor Play land on the main menu.
            strip.Canvas.worldCamera = camera;
            controls.Canvas.worldCamera = camera;
            AttachMainMenu(scene, rig.Controller);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save main-menu wiring to " + ScenePath);

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

    private static string LevelBadgeLabelPath(int level) =>
        $"{LevelBadgeLabelRoot}/LevelBadge_Label_{level:000}.png";

    private static Sprite[] LoadLevelLabels()
    {
        var sprites = new Sprite[30];
        for (int level = 1; level <= sprites.Length; level++)
            sprites[level - 1] = Load<Sprite>(LevelBadgeLabelPath(level));
        return sprites;
    }

    /// <summary>Every sprite the stage is made of, resolved once.</summary>
    private sealed class StageArt
    {
        public readonly Sprite BottomFloor = Load<Sprite>(BottomFloorPath);
        public readonly Sprite FloorMedallion = Load<Sprite>(FloorMedallionPath);
        public readonly Sprite UndoButton = Load<Sprite>(UndoButtonArtPath);
        public readonly Sprite AddTimeButton = Load<Sprite>(AddTimeButtonArtPath);
        public readonly Sprite ShuffleButton = Load<Sprite>(ShuffleButtonArtPath);
        public readonly Sprite LevelBadge = Load<Sprite>(LevelBadgeArtPath);
        public readonly Sprite[] LevelLabels = LoadLevelLabels();
        public readonly Sprite Plank = Load<Sprite>(ShelfPlankPath);
        public readonly Sprite Post = Load<Sprite>(ShelfPostPath);
        public readonly Sprite CheckBadge = Load<Sprite>(CheckBadgePath);
        public readonly Sprite Sky = Load<Sprite>(SkyPath);
        public readonly Sprite Arch = Load<Sprite>(ArchPath);
        public readonly Sprite Curtain = Load<Sprite>(CurtainPath);
        public readonly Sprite Column = Load<Sprite>(ColumnPath);
        public readonly Sprite RailBase = Load<Sprite>(RailBasePath);
        public readonly Sprite PortalBack = Load<Sprite>(PortalBackPath);
        public readonly Sprite PortalFront = Load<Sprite>(PortalFrontPath);
        public readonly Sprite PortalSide = Load<Sprite>(PortalSidePath);
        public readonly Sprite PortalOccluder = Load<Sprite>(PortalOccluderPath);

        public readonly Sprite OrderCardPanel = Load<Sprite>(OrderCardPanelPath);
        public readonly Sprite OrderCardFrame = Load<Sprite>(OrderCardFramePath);
        public readonly Sprite OrderCardClip = Load<Sprite>(OrderCardClipPath);
        public readonly Sprite OrderChipFill = Load<Sprite>(OrderCardChipFillPath);
        public readonly Sprite OrderChipRim = Load<Sprite>(OrderCardChipRimPath);
        public readonly Sprite SettingsPanel = Load<Sprite>(SettingsPanelPath);
        public readonly Sprite SettingsFrame = Load<Sprite>(SettingsFramePath);
        public readonly Sprite SettingsIcon = Load<Sprite>(SettingsIconPath);
        public readonly Sprite SettingsSound = Load<Sprite>(SettingsSoundPath);
        public readonly Sprite SettingsMusic = Load<Sprite>(SettingsMusicPath);
        public readonly Sprite SettingsVibration = Load<Sprite>(SettingsVibrationPath);
        public readonly Sprite SettingsExit = Load<Sprite>(SettingsExitPath);
        public readonly Sprite SettingsMuteSlash = Load<Sprite>(SettingsMuteSlashPath);
        public readonly Sprite ExitConfirmationFrame =
            Load<Sprite>(ExitConfirmationFramePath);
        public readonly Sprite ExitConfirmationDoor =
            Load<Sprite>(ExitConfirmationDoorPath);
        public readonly Sprite ExitConfirmationQuestion =
            Load<Sprite>(ExitConfirmationQuestionPath);
        public readonly Sprite ExitConfirmationConfirmButton =
            Load<Sprite>(ExitConfirmationConfirmButtonPath);
        public readonly Sprite ExitConfirmationConfirmText =
            Load<Sprite>(ExitConfirmationConfirmTextPath);
        public readonly Sprite ExitConfirmationCancelButton =
            Load<Sprite>(ExitConfirmationCancelButtonPath);
        public readonly Sprite ExitConfirmationCancelText =
            Load<Sprite>(ExitConfirmationCancelTextPath);
        public readonly Sprite CardPanel =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardPanelPath);
        public readonly Sprite CardEdge =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardEdgePath);
        public readonly Sprite InteriorPlaceholder =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.CardClipPath);
        public readonly Sprite Pill =
            BartenderUiArtFactory.Load(BartenderUiArtFactory.PillPath);
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
    private static GameObject BuildEventSystem()
    {
        var go = new GameObject("Event System - Host Provided");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
        return go;
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
    /// Builds three identical planks and four posts. With two active rows, the spare pair
    /// becomes the uprights above the top shelf; with three rows both pairs span shelves.
    /// Their heights are not authored here:
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
        var postScale = new Vector2(PostScaleX, PostCapScaleY);
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
        ConfigureSlicedPost(pieces.UpperLeftPost);
        ConfigureSlicedPost(pieces.UpperRightPost);
        ConfigureSlicedPost(pieces.LowerLeftPost);
        ConfigureSlicedPost(pieces.LowerRightPost);
        AttachPlankShadow(pieces.TopPlank, art.Plank);
        AttachPlankShadow(pieces.MiddlePlank, art.Plank);
        AttachPlankShadow(pieces.BottomPlank, art.Plank);

        pieces.TopSeatAnchor = BuildSeatAnchor(pieces.TopPlank,
            "Glass Seat Anchor Row 01 - Direct Link");
        pieces.MiddleSeatAnchor = BuildSeatAnchor(pieces.MiddlePlank,
            "Glass Seat Anchor Row 02 - Direct Link");
        pieces.BottomSeatAnchor = BuildSeatAnchor(pieces.BottomPlank,
            "Glass Seat Anchor Row 03 - Direct Link");
        return pieces;
    }

    /// <summary>
    /// The post artwork has fixed gold collars at both ends. Sliced mode honours the
    /// sprite's top/bottom borders so runtime fitting stretches only its purple shaft.
    /// </summary>
    private static void ConfigureSlicedPost(SpriteRenderer post)
    {
        post.drawMode = SpriteDrawMode.Sliced;
        post.size = post.sprite.bounds.size;
    }

    /// <summary>
    /// Parents the shadow to the plank ON PURPOSE. ApplyShelfLayout moves a row whenever
    /// the level changes its shelf count, and a shadow that is a child simply travels
    /// with it - so nothing in the level system ever has to learn that shadows exist.
    /// </summary>
    private static void AttachPlankShadow(SpriteRenderer plank, Sprite sprite)
    {
        SpriteRenderer shadow = BuildSprite(plank.transform,
            plank.gameObject.name.Replace("Direct Plank Asset", "Drop Shadow"),
            sprite, new Vector2(0f, PlankShadowDrop), Vector2.one, PlankShadowOrder);
        shadow.transform.localScale = new Vector3(PlankShadowScale.x, PlankShadowScale.y, 1f);
        shadow.color = new Color(0f, 0f, 0f, PlankShadowAlpha);
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
        public Transform Mouth;
        public Transform Throat;
    }

    /// <summary>
    /// The service stage above the shelf: sky, arch, curtains, the rail a served glass
    /// travels along, and the gold gate it disappears into.
    ///
    /// The supplied three-quarter portal is kept at a uniform scale so its dome and side
    /// silhouette remain intact. The existing depth and occluder sprites are fitted only
    /// into its doorway, preserving the delivery animation without deforming the visible
    /// gold frame.
    /// </summary>
    private static DeliveryStage BuildDeliveryStage(Transform parent, StageArt art,
        ShelfSolve solve, Material portalCutoutMaterial)
    {
        var stage = new DeliveryStage();

        // The sky, curtains and bridge are one painted piece of architecture. Bake them
        // at 2048 px before placing the scene so their top and side crops cannot change
        // relative to one another on a phone. The counter and tower stay separate and
        // draw over this plate, which is what seats the tower inside the bridge instead
        // of leaving a partial arch beside it.
        Sprite bakedUpperStage = BakeUpperStageBackground(art);
        SpriteRenderer upperStage = BuildSprite(parent,
            "Upper Stage - Baked Sky + Bridge + Curtains", bakedUpperStage,
            Vector2.zero, Vector2.one, BakedUpperStageOrder);
        FitWidth(upperStage, 2f * FrameHalfWidth);
        PlaceByTop(upperStage, 0f, CameraHalfHeight);

        // The post STANDS ON the rail: it is a piece of furniture on the service counter,
        // not a pillar holding the building up. Sizing it from the frame height instead
        // of from its own drawing is what made it tower over the arch.
        SpriteRenderer column = BuildSprite(parent, "Delivery Column Left - Manual",
            art.Column, Vector2.zero, Vector2.one, ColumnOrder);
        FitHeight(column, PostHeight);
        // Pinned to the LEFT WALL by the same inset the gate keeps from the right one.
        // Authoring its CENTRE was the bug: how far the post stood from the wall then
        // depended on how wide its drawing happened to be, which is not a decision
        // anybody made.
        float columnWidth = SpriteVisualBounds(art.Column).width
                          * column.transform.localScale.x;
        PlaceByBottom(column, -FrameHalfWidth + StageWallInset + columnWidth * 0.5f,
            DeliveryRailY - PostSinkIntoRail);

        SpriteRenderer rail = BuildSprite(parent, "Delivery Rail Base - Manual",
            art.RailBase, Vector2.zero, Vector2.one, RailBaseOrder);
        FitWidth(rail, 2f * FrameHalfWidth);
        PlaceByTop(rail, 0f, DeliveryRailY);

        // The reference gate is a compact three-quarter drawing, not the former square
        // front view squeezed to half width. The new art is therefore scaled UNIFORMLY:
        // its dome stays round and its narrow right side remains readable.
        Transform pivot = Folder(parent, "04 Portal - Three Quarter Side View");
        Rect towerVisual = SpriteChromaBounds(art.PortalSide);
        float towerScale = TowerHeight / towerVisual.height;
        float towerWidth = towerVisual.width * towerScale;
        // The doorway follows the tower instead of being authored beside it, so resizing
        // the gate can never again leave the purple interior floating at its old size.
        float openingWidth = towerWidth * PortalOpeningWidthRatio;
        float openingHeight = TowerHeight * PortalOpeningHeightRatio;
        var openingCenter = new Vector2(
            towerWidth * PortalOpeningCenterRatio.x,
            TowerHeight * PortalOpeningCenterRatio.y);
        var portalCenter = new Vector2(
            TowerRightEdgeX - towerWidth * 0.5f,
            DeliveryRailY - TowerSinkIntoRail + TowerHeight * 0.5f);
        pivot.localPosition = new Vector3(portalCenter.x, portalCenter.y, 0f);
        stage.Pivot = pivot;

        SpriteRenderer front = BuildSprite(pivot,
            "03 Portal Gold Frame - Supplied Side Art", art.PortalSide,
            Vector2.zero, Vector2.one, PortalFrontOrder);
        front.transform.localScale = Vector3.one * towerScale;
        front.transform.localPosition = new Vector3(
            -towerVisual.center.x * towerScale,
            -towerVisual.center.y * towerScale, 0f);
        front.sharedMaterial = portalCutoutMaterial;

        // The established purple depth/occluder art remains as the animation sandwich,
        // but is fitted only inside the new doorway. Its deformation is invisible chrome;
        // the player-facing gold frame above is never distorted.
        Rect interiorVisual = SpriteVisualBounds(art.PortalBack);
        Rect occluderVisual = SpriteVisualBounds(art.PortalOccluder);
        SpriteRenderer back = BuildSprite(pivot, "01 Portal Interior - Behind Glass",
            art.PortalBack, Vector2.zero, Vector2.one, PortalBackOrder);
        FitVisibleRect(back, interiorVisual, openingCenter,
            new Vector2(openingWidth, openingHeight));
        stage.Glow = BuildSprite(pivot, "02 Portal Glow - Behind Glass",
            art.PortalBack, Vector2.zero, Vector2.one, PortalGlowOrder);
        FitVisibleRect(stage.Glow, interiorVisual, openingCenter,
            new Vector2(openingWidth, openingHeight));
        stage.Glow.color = new Color(0.42f, 0.92f, 1f, 0f);

        float occluderWidth = openingWidth * 0.58f;
        var occluderCenter = new Vector2(
            openingCenter.x + (openingWidth - occluderWidth) * 0.5f,
            openingCenter.y);
        SpriteRenderer occluder = BuildSprite(pivot,
            "04 Portal Right Occluder - In Front Of Glass", art.PortalOccluder,
            Vector2.zero, Vector2.one, PortalOccluderOrder);
        FitVisibleRect(occluder, occluderVisual, occluderCenter,
            new Vector2(occluderWidth, openingHeight));

        stage.BackLayers = new[] { back, stage.Glow };
        stage.FrontLayers = new[] { occluder, front };

        float servedWidth = solve.WidestGlass * solve.SafeTwoRowScale;
        float servedHeight = solve.TallestGlass * solve.SafeTwoRowScale;
        float liftScale = Mathf.Clamp(PortalFit * Mathf.Min(
            openingWidth / servedWidth,
            openingHeight / servedHeight), 0.30f, 1f);
        float entryScale = Mathf.Clamp(liftScale * 0.85f, 0.05f, 1f);
        float occluderReach = occluderWidth * 0.5f;
        float hideScale = Mathf.Clamp(Mathf.Min(entryScale * 0.55f,
            0.95f * 2f * occluderReach / servedWidth), 0.01f, entryScale);

        float towerLeftEdge = portalCenter.x - towerWidth * 0.5f;
        float mouthX = towerLeftEdge - servedWidth * liftScale * 0.5f
                     - PortalMouthClearance;
        float throatX = portalCenter.x + openingCenter.x
                      + openingWidth * 0.22f;

        stage.Mouth = Anchor(parent, "Mouth Anchor - Manual",
            new Vector2(mouthX, DeliveryRailY));
        stage.Throat = Anchor(parent, "Throat Anchor - Manual",
            new Vector2(throatX, DeliveryRailY + 0.18f));

        stage.Portal = parent.gameObject.AddComponent<PortalDeliveryAnimator>();
        stage.Portal.ConfigureSceneBindings(stage.BackLayers, stage.FrontLayers,
            pivot, stage.Glow, null, null, stage.Mouth, stage.Throat);
        stage.Portal.ConfigureGeometry(
            new Vector2(openingWidth, openingHeight),
            Mathf.Abs(towerLeftEdge - mouthX), PortalMouthClearance, PortalFit);
        var portalSerialized = new SerializedObject(stage.Portal);
        SetFloat(portalSerialized, "liftScale", liftScale);
        portalSerialized.ApplyModifiedPropertiesWithoutUndo();

        if (openingWidth < servedWidth * 0.30f)
            throw new InvalidOperationException(
                $"The delivery gate opening is only {openingWidth:0.###} wide against a "
              + $"{servedWidth:0.###} glass; the vessel would have to shrink past the "
              + "point where it still reads as the glass that was served.");

        Debug.Log($"LiquidSort: side-view delivery gate {towerWidth:0.##}x"
                + $"{TowerHeight:0.##} (uniform scale), opening "
                + $"{openingWidth:0.###}x{openingHeight:0.###}; served glass "
                + $"{servedWidth:0.###}x{servedHeight:0.###} steps down "
                + $"{liftScale:0.###} -> {entryScale:0.###} -> {hideScale:0.###}; "
                + $"stands at x={mouthX:0.###}, vanishes at x={throatX:0.###}.");

        stage.Portal.ConfigureTiming(PortalLiftDuration,
            // Lift timing scales with distance; a full height is bottom shelf to rail.
            DeliveryRailY - SurfaceY(3, 2, SpriteVisualBounds(art.Plank).height * ShelfScaleY),
            PortalApproachDuration, PortalEntryDuration, PortalHideDuration,
            PortalBounceDuration, PortalEntryDepth, entryScale, hideScale,
            PortalEntryTilt, PortalAnticipationDuration, PortalMinimumApproachLead);

        ValidateStageFraming(parent);
        return stage;
    }

    /// <summary>
    /// Renders the immovable top architecture into one high-resolution sprite. This is
    /// intentionally an editor-time bake: the game receives one plate, so the bridge's
    /// crop and the two curtain edges cannot move independently on a narrow phone.
    /// </summary>
    private static Sprite BakeUpperStageBackground(StageArt art)
    {
        Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(BakedUpperStagePath);
        if (SystemInfo.graphicsDeviceType
            == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            if (existing != null) return existing;
            throw new InvalidOperationException(
                "Upper-stage bake needs a graphics device the first time it is generated.");
        }

        float bakeHeight = CameraHalfHeight - BakedUpperStageBottomY;
        int bakeHeightPixels = Mathf.CeilToInt(BakedUpperStageWidthPixels
            * bakeHeight / (2f * FrameHalfWidth));
        var root = new GameObject("TEMP - Bake Sky Bridge Curtains")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        GameObject cameraObject = null;
        RenderTexture target = null;
        Texture2D pixels = null;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            BuildBakedUpperStageSource(root.transform, art);
            SetLayerRecursively(root.transform, BakedUpperStageLayer);

            cameraObject = new GameObject("TEMP - Upper Stage Bake Camera")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = BakedUpperStageLayer
            };
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = bakeHeight * 0.5f;
            camera.aspect = (float)BakedUpperStageWidthPixels / bakeHeightPixels;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << BakedUpperStageLayer;
            camera.transform.position = new Vector3(0f,
                (CameraHalfHeight + BakedUpperStageBottomY) * 0.5f, -10f);

            target = new RenderTexture(BakedUpperStageWidthPixels, bakeHeightPixels, 24,
                RenderTextureFormat.ARGB32)
            {
                name = "UpperStageSkyBridgeCurtainsBake",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear
            };
            target.Create();
            pixels = new Texture2D(BakedUpperStageWidthPixels, bakeHeightPixels,
                TextureFormat.RGBA32, false, false);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, BakedUpperStageWidthPixels,
                bakeHeightPixels), 0, 0, false);
            pixels.Apply(false, false);

            string directory = Path.GetDirectoryName(BakedUpperStagePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory)) EnsureAssetFolder(directory);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new DirectoryNotFoundException("Could not resolve the Unity project root.");
            File.WriteAllBytes(Path.Combine(projectRoot, BakedUpperStagePath),
                pixels.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
            if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(root);
        }

        AssetDatabase.ImportAsset(BakedUpperStagePath,
            ImportAssetOptions.ForceSynchronousImport);
        ConfigureStageSprite(BakedUpperStagePath);
        return Load<Sprite>(BakedUpperStagePath);
    }

    /// <summary>
    /// The only three drawings that belong to the baked plate. The cloud sky is the
    /// back layer, the curtain reaches the physical top edge, and the enlarged bridge
    /// sits in front of the fabric. The right-side tower is deliberately absent: it is
    /// a foreground portal that is seated into this architecture by the live scene.
    /// </summary>
    private static void BuildBakedUpperStageSource(Transform parent, StageArt art)
    {
        SpriteRenderer sky = BuildSprite(parent, "Bake - Cloud Sky", art.Sky,
            Vector2.zero, Vector2.one, 0);
        FitWidth(sky, 2f * FrameHalfWidth + 0.15f);
        PlaceByBottom(sky, 0f, BakedUpperStageBottomY);

        SpriteRenderer bridge = BuildSprite(parent, "Bake - Stone Bridge", art.Arch,
            Vector2.zero, Vector2.one, 10);
        FitWidth(bridge, BakedBridgeVisibleSpan);
        Vector3 bridgeScale = bridge.transform.localScale;
        bridgeScale.y *= BakedBridgeVerticalScale;
        bridge.transform.localScale = bridgeScale;
        PlaceByTop(bridge, 0f, CameraHalfHeight + BakedBridgeTopBleed);

        // The fabric is the final edge mask: it wraps over the enlarged bridge at the
        // two top corners, exactly as the reference hides the bridge under the purple
        // folds rather than leaving a seam at either screen edge.
        SpriteRenderer curtain = BuildSprite(parent, "Bake - Purple Curtains",
            art.Curtain, Vector2.zero, Vector2.one, 20);
        FitWidth(curtain, 2f * FrameHalfWidth + 0.15f);
        Vector3 curtainScale = curtain.transform.localScale;
        curtainScale.x *= CurtainHorizontalScale;
        curtainScale.y *= CurtainVerticalScale;
        curtain.transform.localScale = curtainScale;
        float curtainDrape = SpriteVisualBounds(art.Curtain).height
                           * curtain.transform.localScale.y;
        PlaceByTop(curtain, 0f,
            CameraHalfHeight + curtainDrape - CurtainVisibleDrop);
    }

    /// <summary>
    /// Nothing on the service stage may leave the frame, except the backdrop and the
    /// curtain which bleed off the edges by design. This measures the REAL renderers
    /// after they are placed rather than re-deriving where they should have gone, which
    /// is the only way it could have caught what it was written for: the arch was held
    /// by its span above the rail, its own aspect decided where the crown landed, and
    /// the crown landed 0.71 units above the frame with no number anywhere saying so.
    ///
    /// Measured in <paramref name="stageRoot"/>'s local space, not in world space. The
    /// safe-area fitter scales the whole composition and runs in edit mode, so world
    /// coordinates here would be whatever the current Game View aspect happens to make
    /// them; the stage folder sits unrotated at the composition origin, so its local
    /// space IS the authored 720x1280 design space.
    /// </summary>
    private static void ValidateStageFraming(Transform stageRoot)
    {
        foreach (SpriteRenderer renderer in
                 stageRoot.GetComponentsInChildren<SpriteRenderer>(true))
        {
            string name = renderer.gameObject.name;
            // These two are drawn oversized on purpose so no seam can appear at the edge.
            if (name == "Upper Sky Backdrop - Manual"
                || name == "Upper Curtain Pair - Manual") continue;
            if (renderer.sprite == null) continue;

            // The gate art carries no alpha at all - it is matted by chroma in the
            // shader - so measuring it by alpha would return the whole opaque neutral
            // sheet it was generated on and fail this check on a tower that is nowhere
            // near the frame edge. Measure whatever the shader will actually draw.
            bool chromaMatted = renderer.sharedMaterial != null
                && renderer.sharedMaterial.shader != null
                && renderer.sharedMaterial.shader.name
                    == "LiquidSort/GeneratedPortalCutout";
            Rect visual = chromaMatted
                ? SpriteChromaBounds(renderer.sprite)
                : SpriteVisualBounds(renderer.sprite);
            Transform t = renderer.transform;
            Vector3 min = stageRoot.InverseTransformPoint(
                t.TransformPoint(new Vector3(visual.xMin, visual.yMin, 0f)));
            Vector3 max = stageRoot.InverseTransformPoint(
                t.TransformPoint(new Vector3(visual.xMax, visual.yMax, 0f)));

            float allowedTop = name == "Upper Stone Arch - Manual"
                ? CameraHalfHeight + ArchTopBleed
                : CameraHalfHeight;
            if (max.y > allowedTop + 0.001f)
                throw new InvalidOperationException(
                    $"'{name}' reaches y={max.y:0.###}, past its allowed top at "
                  + $"{allowedTop:0.###}. The bezel would cut it off.");
            if (min.x < -FrameHalfWidth - 0.001f || max.x > FrameHalfWidth + 0.001f)
                throw new InvalidOperationException(
                    $"'{name}' spans x=[{min.x:0.###}, {max.x:0.###}], outside the "
                  + $"frame's [{-FrameHalfWidth:0.###}, {FrameHalfWidth:0.###}].");
        }
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

        // A level clone inherits component/material configuration from RoyalGlassLab,
        // while its canonical rest pose comes from the same VesselProfile asset used by
        // the lab. Source-scene transforms are intentionally not part of the contract.
        go.transform.localScale = bottle.profile.ShelfReferenceLocalScale;
        go.transform.localRotation = bottle.profile.ShelfReferenceLocalRotation;

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
        Vector2 support = bottle.profile.SupportLocal;
        foot.transform.localPosition = new Vector3(support.x, support.y, 0f);

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
            authoredLocalScale = bottle.profile.ShelfReferenceLocalScale,
            authoredLocalRotation = bottle.profile.ShelfReferenceLocalRotation
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
            float inherited = badge.bottle.profile.ShelfReferenceScale
                              * solve.SafeTwoRowScale;
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

        // No rail over the cards. The strip used to hang from a red-and-gold bar, but a
        // card is pinned to the back wall by its own gold clip - it is not suspended from
        // anything, so the bar was drawing a support that the art does not need.

        for (int i = 0; i < 3; i++)
        {
            float x = (i - 1) * OrderCardSpacing;
            strip.Cards.Add(BuildOrderCard(strip.Canvas.transform,
                $"Order Card Slot {i + 1:00}", new Vector2(x, OrderStripCenterY), art));
        }

        for (int i = 0; i < strip.Cards.Count; i++)
        {
            strip.Cards[i].SetGlassIcons(icons);
            // Read back by OrderCardView as its authored scale, so the pop animations
            // return to this size instead of snapping the card back to 1.
            strip.Cards[i].transform.localScale = Vector3.one * OrderCardScale;
        }
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
        public Image LevelLabelSprite;
        public Text LevelLabelFallback;
        public GameObject LevelBadgeRoot;
        public Button SettingsButton;
        public Button UndoButton;
        public Button AddTimeButton;
        public Button ShuffleButton;
        public GameObject PauseOverlay;
        public GameObject SettingsCard;
        public GameObject ExitCard;
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
        Transform screenCanvas = controls.Canvas.transform;
        Transform safeArea = BuildSafeArea(screenCanvas).transform;

        Transform topBar = BuildEdgeBar(safeArea, "01 Top Bar", true,
            Px(CameraHalfHeight - TopBarCenterY), Px(TopBarHeight));
        RectTransform badge = BuildRect(topBar, "Level Badge - Level Controlled",
            Vector2.zero, new Vector2(Px(LevelBadgeWidth), Px(TopBarHeight)));
        Image badgeArtwork = BuildImage(badge, "Badge Artwork - Clean",
            art.LevelBadge, Color.white,
            new Vector2(0f, Px(LevelBadgeArtOffsetY)),
            new Vector2(Px(LevelBadgeWidth), Px(LevelBadgeArtHeight)), Image.Type.Simple);
        badgeArtwork.preserveAspect = true;
        controls.LevelLabelSprite = BuildImage(badge, "Badge Label Sprite - Atlas",
            art.LevelLabels[0], Color.white, Vector2.zero,
            new Vector2(Px(LevelLabelSize.x), Px(LevelLabelSize.y)), Image.Type.Simple);
        controls.LevelLabelSprite.preserveAspect = true;
        controls.LevelLabelFallback = BuildText(badge, "Badge Label Fallback", font,
            "BARTENDER", Vector2.zero,
            new Vector2(Px(LevelLabelSize.x), Px(LevelLabelSize.y)),
            Mathf.RoundToInt(Px(0.22f * LevelBadgeScale)), BadgeText);
        controls.LevelLabelFallback.enabled = false;
        controls.LevelBadgeRoot = badge.gameObject;

        controls.SettingsButton = BuildSettingsArtButtonSized(screenCanvas,
            "PauseButton", art, art.SettingsIcon, -TopSettingsButtonInset,
            TopSettingsButtonSize, false, 1.06f, 0.92f, out _);

        // The lower stage is deliberately a separate, non-interactive art group. Its
        // centre is pinned to the safe-area bottom: the 300 px tile base therefore
        // reaches 150 px upward to meet the lowest shelf and 150 px downward underneath
        // the iPhone gesture inset. The oval sits above it while the buttons remain in
        // their own bar, so draw order and input can never become coupled.
        RectTransform bottomFloorArt = BuildRect(safeArea, "00 Bottom Floor Artwork",
            Vector2.zero, new Vector2(760f, 300f));
        Canvas bottomFloorSorting = bottomFloorArt.gameObject.AddComponent<Canvas>();
        bottomFloorSorting.overrideSorting = true;
        bottomFloorSorting.sortingOrder = BottomFloorOrder;
        bottomFloorArt.anchorMin = new Vector2(0.5f, 0f);
        bottomFloorArt.anchorMax = new Vector2(0.5f, 0f);
        bottomFloorArt.pivot = new Vector2(0.5f, 0.5f);
        bottomFloorArt.anchoredPosition = Vector2.zero;

        Image bottomFloor = BuildImage(bottomFloorArt, "00 Bottom Floor - Tan Tiles",
            art.BottomFloor, Color.white, Vector2.zero, bottomFloorArt.sizeDelta,
            Image.Type.Simple);
        bottomFloor.preserveAspect = false;

        Image floorMedallion = BuildImage(bottomFloorArt,
            "01 Floor Medallion - Oval", art.FloorMedallion,
            new Color(0.84f, 0.76f, 0.70f, 0.92f),
            new Vector2(0f, 25f), new Vector2(760f, 396f), Image.Type.Simple);
        floorMedallion.preserveAspect = true;

        Transform bottomBar = BuildEdgeBar(safeArea, "02 Bottom Controls", false,
            Px(BottomBarCenterY + CameraHalfHeight), Px(BottomButtonDiameter));
        controls.UndoButton = BuildArtworkButton(bottomBar, "Undo Button", art.UndoButton,
            Vector2.zero, BottomButtonDiameter,
            new Vector2(-Px(BottomButtonSpacing), 0f));
        controls.AddTimeButton = BuildArtworkButton(bottomBar, "Add Time Button",
            art.AddTimeButton, Vector2.zero, BottomButtonDiameter, Vector2.zero);
        controls.ShuffleButton = BuildArtworkButton(bottomBar, "Shuffle Button",
            art.ShuffleButton, Vector2.zero, BottomButtonDiameter,
            new Vector2(Px(BottomButtonSpacing), 0f));

        ValidateArtworkButton(controls.UndoButton, art.UndoButton);
        ValidateArtworkButton(controls.AddTimeButton, art.AddTimeButton);
        ValidateArtworkButton(controls.ShuffleButton, art.ShuffleButton);

        BuildPauseOverlay(screenCanvas, art, controls);
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
    ///
    /// <paramref name="centreInsetFromEdge"/> is the distance from the screen edge to the
    /// bar's CENTRE LINE, because that is what the callers hand over: the same
    /// TopBarCenterY / BottomBarCenterY the vertical budget is written in. Adding half the
    /// height on top of it pushed both bars a third of a unit inboard, which is what put
    /// the boosters on the lowest shelf.
    /// </summary>
    private static Transform BuildEdgeBar(Transform parent, string name, bool top,
                                          float centreInsetFromEdge, float height)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, top ? 1f : 0f);
        rect.anchorMax = new Vector2(1f, top ? 1f : 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(0f, height);
        rect.anchoredPosition = new Vector2(0f,
            top ? -centreInsetFromEdge : centreInsetFromEdge);
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
    /// Exact scaled port of GameplayPauseCanvas from BartenderSort-Simay.  Closed, the
    /// settings disc sits at the top-right.  Open, the same disc is replaced in place by
    /// Close and four 92 px options descend at a 104 px step.  There is deliberately no
    /// visible card or dim behind the stack in the source design.
    /// </summary>
    private static void BuildPauseOverlay(Transform canvas, StageArt art,
                                          ScreenControls controls)
    {
        RectTransform overlay = Stretch(BuildRect(canvas,
            "PauseSettingsOverlay", Vector2.zero, Vector2.zero));
        controls.PauseOverlay = overlay.gameObject;
        RectTransform blocker = Stretch(BuildImage(overlay, "RaycastBlocker", null,
            Color.clear, Vector2.zero, Vector2.zero, Image.Type.Simple).rectTransform);
        blocker.GetComponent<Image>().raycastTarget = true;

        var cardSize = new Vector2(SettingsButtonSize, SettingsCardHeight);
        RectTransform card = BuildRect(overlay, "SettingsCard",
            new Vector2(-SettingsRightInset, -SettingsTopInset), cardSize);
        card.anchorMin = Vector2.one;
        card.anchorMax = Vector2.one;
        card.pivot = Vector2.one;
        controls.SettingsCard = card.gameObject;

        controls.CloseButton = BuildSettingsArtButton(card, "CloseButton", art,
            art.SettingsIcon, Vector2.zero, false, 1.05f, 0.90f, out _);
        controls.SoundButton = BuildSettingsArtButton(card, "SoundButton", art,
            art.SettingsSound, new Vector2(0f, -SettingsButtonStep), true,
            1.05f, 0.90f, out controls.SoundOffMark);
        controls.MusicButton = BuildSettingsArtButton(card, "MusicButton", art,
            art.SettingsMusic, new Vector2(0f, -SettingsButtonStep * 2f), true,
            1.05f, 0.90f, out controls.MusicOffMark);
        controls.VibrationButton = BuildSettingsArtButton(card, "VibrationButton", art,
            art.SettingsVibration, new Vector2(0f, -SettingsButtonStep * 3f), false,
            1.05f, 0.90f, out controls.VibrationOffMark);
        controls.ExitButton = BuildSettingsArtButton(card, "ExitButton", art,
            art.SettingsExit, new Vector2(0f, -SettingsButtonStep * 4f), false,
            1.05f, 0.90f, out _);

        var confirmSize = new Vector2(600f, 900f);
        RectTransform confirm = BuildRect(overlay, "Exit Confirmation Card", Vector2.zero,
            confirmSize);
        controls.ExitCard = confirm.gameObject;
        Image frame = BuildImage(confirm, "Card Rim", art.ExitConfirmationFrame,
            Color.white, Vector2.zero, confirmSize, Image.Type.Simple);
        frame.preserveAspect = true;

        Image door = BuildImage(confirm, "Door Icon", art.ExitConfirmationDoor,
            Color.white, new Vector2(0f, 25f), new Vector2(190f, 180f),
            Image.Type.Simple);
        door.preserveAspect = true;

        Image question = BuildImage(confirm, "Question", art.ExitConfirmationQuestion,
            Color.white, new Vector2(0f, 270f), new Vector2(480f, 270f),
            Image.Type.Simple);
        question.preserveAspect = true;

        controls.ConfirmExitButton = BuildExitConfirmationButton(confirm, "Confirm Exit",
            art.ExitConfirmationConfirmButton, art.ExitConfirmationConfirmText,
            new Vector2(0f, -165f), new Vector2(480f, 135f),
            new Vector2(180f, 75f));
        controls.CancelExitButton = BuildExitConfirmationButton(confirm, "Cancel Exit",
            art.ExitConfirmationCancelButton, art.ExitConfirmationCancelText,
            new Vector2(0f, -325f), new Vector2(480f, 135f),
            new Vector2(275f, 90f));

        // Closed at rest. BartenderPausePresenter is the only thing that opens them, and
        // it opens them from the flow state, never from the button press.
        controls.ExitCard.SetActive(false);
        controls.PauseOverlay.SetActive(false);
    }

    private static Button BuildSettingsArtButton(Transform parent, string name,
        StageArt art, Sprite icon, Vector2 anchoredPosition, bool includeMuteSlash,
        float hoverScale, float pressedScale, out GameObject muteSlash)
        => BuildSettingsArtButtonSized(parent, name, art, icon, anchoredPosition,
            SettingsButtonSize, includeMuteSlash, hoverScale, pressedScale,
            out muteSlash);

    private static Button BuildSettingsArtButtonSized(Transform parent, string name,
        StageArt art, Sprite icon, Vector2 anchoredPosition, float buttonSize,
        bool includeMuteSlash, float hoverScale, float pressedScale,
        out GameObject muteSlash)
    {
        var size = new Vector2(buttonSize, buttonSize);
        RectTransform rect = BuildRect(parent, name, anchoredPosition, size);
        rect.anchorMin = Vector2.one;
        rect.anchorMax = Vector2.one;
        rect.pivot = Vector2.one;
        // Changing anchors/pivot can preserve the old world rectangle by rewriting the
        // anchored position. Reapply the approved top-right geometry afterwards.
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        var hitTarget = rect.gameObject.AddComponent<Image>();
        hitTarget.color = Color.clear;
        hitTarget.raycastTarget = true;

        AddSettingsLayer(rect, "PanelBlue", art.SettingsPanel, size);
        AddSettingsLayer(rect, "FrameGold", art.SettingsFrame, size);
        AddSettingsLayer(rect, "Icon", icon, size);

        muteSlash = null;
        if (includeMuteSlash)
        {
            Image slash = AddSettingsLayer(rect, "MuteSlash", art.SettingsMuteSlash, size);
            muteSlash = slash.gameObject;
            muteSlash.SetActive(false);
        }

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = hitTarget;
        button.transition = Selectable.Transition.ColorTint;
        Navigation navigation = button.navigation;
        navigation.mode = Navigation.Mode.None;
        button.navigation = navigation;

        var feedback = rect.gameObject.AddComponent<BartenderSettingsButtonFeedback>();
        feedback.Configure(hoverScale, pressedScale, 0.15f);
        return button;
    }

    private static Image AddSettingsLayer(Transform parent, string name, Sprite sprite,
                                          Vector2 size)
    {
        Image image = BuildImage(parent, name, sprite, Color.white, Vector2.zero, size,
            Image.Type.Simple);
        Stretch(image.rectTransform);
        image.preserveAspect = true;
        image.raycastTarget = false;
        return image;
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
        public AudioSource SettingsAudio;
        public BartenderPausePresenter Pause;
        public ScreenControls Controls;
    }

    private static LevelRig BuildLevelRig(Transform systems, Transform layoutSpace,
        StageArt art, BsPalette palette, ShelfPieces shelf, DeliveryStage delivery,
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
        rig.Controls = controls;
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
            OpticalSeatInset, ThreeRowCompositionScale);
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
        rig.LevelBadge.ConfigureSceneBindings(rig.Controller, controls.LevelLabelSprite,
            art.LevelLabels, controls.LevelLabelFallback, controls.LevelBadgeRoot);

        rig.Boosters = host.AddComponent<BoosterBarPresenter>();
        rig.Boosters.ConfigureSceneBindings(rig.Controller, rig.ShelfView, rig.Interaction,
            controls.UndoButton, controls.AddTimeButton, controls.ShuffleButton);

        rig.SettingsAudio = host.AddComponent<AudioSource>();
        rig.SettingsAudio.playOnAwake = false;
        rig.SettingsAudio.loop = false;
        rig.SettingsAudio.spatialBlend = 0f;

        rig.Pause = host.AddComponent<BartenderPausePresenter>();
        SerializedObject pause = new SerializedObject(rig.Pause);
        SetRef(pause, "session", rig.Session);
        SetRef(pause, "controller", rig.Controller);
        SetRef(pause, "pauseButton", controls.SettingsButton);
        SetRef(pause, "settingsOverlay", controls.PauseOverlay);
        SetRef(pause, "settingsCard", controls.SettingsCard);
        SetRef(pause, "closeButton", controls.CloseButton);
        SetRef(pause, "exitButton", controls.ExitButton);
        SetRef(pause, "musicButton", controls.MusicButton);
        SetRef(pause, "soundButton", controls.SoundButton);
        SetRef(pause, "vibrationButton", controls.VibrationButton);
        SetRef(pause, "musicOffMark", controls.MusicOffMark);
        SetRef(pause, "soundOffMark", controls.SoundOffMark);
        SetRef(pause, "vibrationOffMark", controls.VibrationOffMark);
        SetRef(pause, "settingsAudioSource", rig.SettingsAudio);
        SetRef(pause, "buttonClick", Load<AudioClip>(SettingsClickPath));
        SetRef(pause, "exitConfirmationCard", controls.ExitCard);
        SetRef(pause, "confirmExitButton", controls.ConfirmExitButton);
        SetRef(pause, "cancelExitButton", controls.CancelExitButton);
        pause.ApplyModifiedPropertiesWithoutUndo();

        // Both camera-space canvases resolve the host camera at runtime. Writing the
        // authoring camera into the portable prefab would create an out-of-root link and
        // Unity drops it while saving.
        strip.Canvas.GetComponent<WorldCanvasCameraBinder>().ConfigureSceneBindings(null);
        controls.Canvas.GetComponent<WorldCanvasCameraBinder>()
            .ConfigureSceneBindings(null);
        return rig;
    }

    private static void AttachMainMenu(Scene scene,
                                       BartenderLevelController controller)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            throw new InvalidOperationException(
                "Main menu requires a loaded destination scene.");
        if (controller == null)
            throw new ArgumentNullException(nameof(controller));

        var controllerSerialized = new SerializedObject(controller);
        Find(controllerSerialized, "loadOnStart").boolValue = false;
        Find(controllerSerialized, "resumeSavedProgress").boolValue = true;
        controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

        GameObject prefab = Load<GameObject>(MainMenuPrefabPath);
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
        if (instance == null)
            throw new InvalidOperationException(
                "Bartender main-menu prefab could not be instantiated.");

        BartenderMainMenuPresenter presenter =
            instance.GetComponent<BartenderMainMenuPresenter>();
        if (presenter == null)
            throw new InvalidOperationException(
                "Bartender main-menu prefab is missing its presenter.");
        SetRefAndApply(presenter, "controller", controller);
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
        int labelSlot = level.Index - 1;
        if (labelSlot >= 0 && labelSlot < 30)
        {
            controls.LevelLabelSprite.sprite =
                Load<Sprite>(LevelBadgeLabelPath(level.Index));
            controls.LevelLabelSprite.enabled = true;
            controls.LevelLabelFallback.enabled = false;
        }
        else
        {
            controls.LevelLabelSprite.enabled = false;
            controls.LevelLabelFallback.text = "SEVİYE " + level.Index;
            controls.LevelLabelFallback.enabled = true;
        }
    }

    private static WorldSpaceSafeAreaFitter AttachSafeAreaFitter(
        Transform host, Camera camera, Transform compositionRoot)
    {
        var fitter = host.gameObject.AddComponent<WorldSpaceSafeAreaFitter>();
        SerializedObject serialized = new SerializedObject(fitter);
        SetRef(serialized, "targetCamera", camera);
        serialized.FindProperty("autoResolveMainCamera").boolValue = true;
        SetRef(serialized, "compositionRoot", compositionRoot);
        SerializedProperty resolution = serialized.FindProperty("referenceResolution");
        resolution.vector2IntValue = new Vector2Int(DesignWidth, DesignHeight);
        SetFloat(serialized, "referenceOrthographicSize", CameraHalfHeight);
        // Decorative world art bleeds under the notch; only the interactive HUD uses
        // BsSafeArea. Width-fit + top alignment keeps the approved upper stage at the
        // physical top on iPhone 13 instead of centring it 200+ pixels down the screen.
        serialized.FindProperty("respectSafeArea").boolValue = false;
        serialized.FindProperty("contentAlignment").vector2Value =
            new Vector2(0.5f, 1f);
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
        compositionRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        compositionRoot.localScale = Vector3.one;
        return fitter;
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

    private static void FitVisibleRect(SpriteRenderer renderer, Rect visual,
                                       Vector2 targetCenter, Vector2 targetSize)
    {
        float scaleX = targetSize.x / Mathf.Max(0.0001f, visual.width);
        float scaleY = targetSize.y / Mathf.Max(0.0001f, visual.height);
        renderer.transform.localScale = new Vector3(scaleX, scaleY, 1f);
        renderer.transform.localPosition = new Vector3(
            targetCenter.x - visual.center.x * scaleX,
            targetCenter.y - visual.center.y * scaleY, 0f);
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

    /// <summary>
    /// Artist-authored booster art already contains its face, rim, glyph and shadow.
    /// Keeping the old generated layers would double every edge and tint the supplied
    /// colours purple, so these three controls intentionally contain one Image only.
    /// </summary>
    private static Button BuildArtworkButton(Transform parent, string name, Sprite artwork,
                                             Vector2 worldCenter, float worldDiameter,
                                             Vector2? explicitAnchoredPosition = null)
    {
        if (artwork == null)
            throw new ArgumentNullException(nameof(artwork), name + " artwork is missing.");

        float diameter = Px(worldDiameter);
        RectTransform rect = BuildRect(parent, name,
            explicitAnchoredPosition ?? PxPoint(worldCenter),
            new Vector2(diameter, diameter));

        Image face = rect.gameObject.AddComponent<Image>();
        face.sprite = artwork;
        face.color = Color.white;
        face.preserveAspect = true;
        face.raycastTarget = true;

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = face;
        ApplyButtonColors(button);
        return button;
    }

    private static void ValidateArtworkButton(Button button, Sprite expected)
    {
        Image face = button != null ? button.targetGraphic as Image : null;
        if (face == null || face.sprite != expected || face.color != Color.white
            || button.transform.childCount != 0)
            throw new InvalidOperationException(
                (button != null ? button.name : "Booster button")
                + " must use one untinted composite artwork Image with no placeholder layers.");
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

    private static Button BuildExitConfirmationButton(Transform parent, string name,
        Sprite buttonSprite, Sprite labelSprite, Vector2 anchoredPosition, Vector2 size,
        Vector2 labelSize)
    {
        Image face = BuildImage(parent, name, buttonSprite, Color.white,
            anchoredPosition, size, Image.Type.Simple);
        face.raycastTarget = true;

        Image label = BuildImage(face.rectTransform, "Label", labelSprite, Color.white,
            Vector2.zero, labelSize, Image.Type.Simple);
        label.preserveAspect = true;

        var button = face.gameObject.AddComponent<Button>();
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
    /// Visible bounds for generated art whose neutral preview field arrived opaque.
    /// Gold and purple are chromatic; the white/grey outside and doorway are not.
    /// </summary>
    private static Rect SpriteChromaBounds(Sprite sprite)
    {
        int key = sprite.GetInstanceID();
        if (ChromaBoundsCache.TryGetValue(key, out Rect cached)) return cached;

        Bounds fallback = sprite.bounds;
        Rect result = Rect.MinMaxRect(
            fallback.min.x, fallback.min.y, fallback.max.x, fallback.max.y);
        string assetPath = AssetDatabase.GetAssetPath(sprite.texture);
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(projectRoot))
            return result;

        string absolutePath = Path.Combine(projectRoot, assetPath);
        var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            if (!File.Exists(absolutePath)
                || !readable.LoadImage(File.ReadAllBytes(absolutePath), false))
                return result;

            Rect rect = sprite.rect;
            int xStart = Mathf.Clamp(Mathf.FloorToInt(rect.xMin), 0, readable.width - 1);
            int xEnd = Mathf.Clamp(Mathf.CeilToInt(rect.xMax), 1, readable.width);
            int yStart = Mathf.Clamp(Mathf.FloorToInt(rect.yMin), 0, readable.height - 1);
            int yEnd = Mathf.Clamp(Mathf.CeilToInt(rect.yMax), 1, readable.height);
            Color32[] pixels = readable.GetPixels32();
            int minX = xEnd, minY = yEnd, maxX = xStart - 1, maxY = yStart - 1;
            for (int y = yStart; y < yEnd; y++)
            {
                int row = y * readable.width;
                for (int x = xStart; x < xEnd; x++)
                {
                    Color32 pixel = pixels[row + x];
                    int maximum = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b));
                    int minimum = Mathf.Min(pixel.r, Mathf.Min(pixel.g, pixel.b));
                    if (maximum - minimum < 16) continue;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (maxX >= minX && maxY >= minY)
            {
                float ppu = Mathf.Max(1f, sprite.pixelsPerUnit);
                result = Rect.MinMaxRect(
                    (minX - rect.xMin - sprite.pivot.x) / ppu,
                    (minY - rect.yMin - sprite.pivot.y) / ppu,
                    (maxX + 1f - rect.xMin - sprite.pivot.x) / ppu,
                    (maxY + 1f - rect.yMin - sprite.pivot.y) / ppu);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(readable);
        }

        ChromaBoundsCache[key] = result;
        return result;
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

    private static Material EnsurePortalCutoutMaterial()
    {
        Shader shader = Shader.Find("LiquidSort/GeneratedPortalCutout");
        if (shader == null)
            throw new InvalidOperationException(
                "GeneratedPortalCutout shader is unavailable.");

        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(PortalCutoutMaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "Generated Portal Cutout" };
            AssetDatabase.CreateAsset(material, PortalCutoutMaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetFloat("_ChromaLow", 0.025f);
        material.SetFloat("_ChromaHigh", 0.085f);
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

    /// <summary>Lossless import recipe for the clean badge and its bitmap label set.</summary>
    private static void ConfigureUiSprite(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException("Missing badge artwork", path);

        bool needsImport = importer.textureType != TextureImporterType.Sprite
                           || importer.spriteImportMode != SpriteImportMode.Single
                           || importer.mipmapEnabled
                           || !importer.alphaIsTransparency
                           || importer.isReadable
                           || importer.textureCompression != TextureImporterCompression.Uncompressed
                           || importer.wrapMode != TextureWrapMode.Clamp
                           || importer.filterMode != FilterMode.Bilinear;
        if (!needsImport) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
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
        if (rig.Controls.LevelLabelSprite == null
            || rig.Controls.LevelLabelSprite.sprite != art.LevelLabels[3]
            || !rig.Controls.LevelLabelSprite.enabled
            || (rig.Controls.LevelLabelFallback != null
                && rig.Controls.LevelLabelFallback.enabled))
            throw new InvalidOperationException(
                "The saved Level 4 preview must use atlas label slot 4, not live text.");
        if (!rig.Boosters.ValidateBindings(out string boosterError))
            throw new InvalidOperationException(boosterError);
        if (rig.SettingsAudio == null || rig.SettingsAudio.playOnAwake
            || rig.SettingsAudio.spatialBlend != 0f
            || rig.Pause == null || rig.Pause.SettingsAudioSource != rig.SettingsAudio
            || rig.Pause.ButtonClick != Load<AudioClip>(SettingsClickPath))
            throw new InvalidOperationException(
                "The minimal settings-button audio link is incomplete.");
        ValidateSettingsStack(rig.Controls, art);
        if (!rig.ShelfView.Ready || rig.ShelfView.ActiveGlassCount != 6
            || rig.ShelfView.VisibleShelfRows != 2)
            throw new InvalidOperationException(
                "The saved authoring preview must be Level 4's six-glass 3+3 layout.");

        ValidateVerticalBudget(solve);
        ValidateHorizontalBudget(solve);

        // The service rail is a solid shelf roughly two thirds of a unit thick, and the
        // order strip sits directly under it. Nothing in the budget above knows that, so
        // the two silently overlapped and the rail read as a thin red line with its gold
        // finials swallowed. Measured here, from the drawing, for the same reason every
        // other number in this file is measured.
        float railBand = SpriteVisualBounds(art.RailBase).height
                         * (2f * FrameHalfWidth)
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
                // The soft under-board shadow deliberately reuses the plank sprite as
                // a child renderer.  Count only the authored direct furniture links;
                // otherwise an entirely valid three-row shelf reads as six planks.
                if (renderer.sprite == art.Plank
                    && item.name.Contains("Direct Plank Asset")) linkedPlanks++;
                if (renderer.sprite == art.Post
                    && item.name.Contains("Direct Post Asset")) linkedPosts++;
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

    private static void ValidateSettingsStack(ScreenControls controls, StageArt art)
    {
        if (controls == null || controls.SettingsButton == null
            || controls.PauseOverlay == null || controls.SettingsCard == null
            || controls.CloseButton == null || controls.SoundButton == null
            || controls.MusicButton == null || controls.VibrationButton == null
            || controls.ExitButton == null)
            throw new InvalidOperationException(
                "The Bartender settings stack is missing a required control.");

        AssertTopRightRect((RectTransform)controls.SettingsButton.transform,
            -TopSettingsButtonInset,
            new Vector2(TopSettingsButtonSize, TopSettingsButtonSize), "PauseButton");
        AssertTopRightRect((RectTransform)controls.SettingsCard.transform,
            new Vector2(-SettingsRightInset, -SettingsTopInset),
            new Vector2(SettingsButtonSize, SettingsCardHeight), "SettingsCard");
        AssertTopRightRect((RectTransform)controls.CloseButton.transform, Vector2.zero,
            new Vector2(SettingsButtonSize, SettingsButtonSize), "CloseButton");
        AssertTopRightRect((RectTransform)controls.SoundButton.transform,
            new Vector2(0f, -SettingsButtonStep),
            new Vector2(SettingsButtonSize, SettingsButtonSize), "SoundButton");
        AssertTopRightRect((RectTransform)controls.MusicButton.transform,
            new Vector2(0f, -SettingsButtonStep * 2f),
            new Vector2(SettingsButtonSize, SettingsButtonSize), "MusicButton");
        AssertTopRightRect((RectTransform)controls.VibrationButton.transform,
            new Vector2(0f, -SettingsButtonStep * 3f),
            new Vector2(SettingsButtonSize, SettingsButtonSize), "VibrationButton");
        AssertTopRightRect((RectTransform)controls.ExitButton.transform,
            new Vector2(0f, -SettingsButtonStep * 4f),
            new Vector2(SettingsButtonSize, SettingsButtonSize), "ExitButton");

        ValidateSettingsButtonArt(controls.SettingsButton, art.SettingsIcon, null, art);
        ValidateSettingsButtonArt(controls.CloseButton, art.SettingsIcon, null, art);
        ValidateSettingsButtonArt(controls.SoundButton, art.SettingsSound,
            controls.SoundOffMark, art);
        ValidateSettingsButtonArt(controls.MusicButton, art.SettingsMusic,
            controls.MusicOffMark, art);
        ValidateSettingsButtonArt(controls.VibrationButton, art.SettingsVibration,
            controls.VibrationOffMark, art);
        ValidateSettingsButtonArt(controls.ExitButton, art.SettingsExit, null, art);

        Image blocker = controls.PauseOverlay.transform.Find("RaycastBlocker")
            ?.GetComponent<Image>();
        if (blocker == null || !blocker.raycastTarget || blocker.color.a != 0f
            || controls.SettingsCard.GetComponent<Graphic>() != null
            || controls.PauseOverlay.activeSelf || controls.ExitCard.activeSelf)
            throw new InvalidOperationException(
                "The settings stack must rest closed over a transparent raycast blocker.");
    }

    private static void ValidateSettingsButtonArt(Button button, Sprite expectedIcon,
        GameObject expectedMuteSlash, StageArt art)
    {
        Transform root = button.transform;
        Image panel = root.Find("PanelBlue")?.GetComponent<Image>();
        Image frame = root.Find("FrameGold")?.GetComponent<Image>();
        Image icon = root.Find("Icon")?.GetComponent<Image>();
        Transform slash = root.Find("MuteSlash");
        if (panel == null || panel.sprite != art.SettingsPanel
            || frame == null || frame.sprite != art.SettingsFrame
            || icon == null || icon.sprite != expectedIcon
            || button.GetComponent<BartenderSettingsButtonFeedback>() == null
            || (expectedMuteSlash == null) != (slash == null)
            || (expectedMuteSlash != null
                && (slash.gameObject != expectedMuteSlash
                    || slash.GetComponent<Image>().sprite != art.SettingsMuteSlash
                    || slash.gameObject.activeSelf)))
            throw new InvalidOperationException(
                button.name + " lost a required source-art layer or feedback component.");
    }

    private static void AssertTopRightRect(RectTransform rect, Vector2 position,
        Vector2 size, string label)
    {
        const float tolerance = 0.001f;
        if (Vector2.Distance(rect.anchorMin, Vector2.one) > tolerance
            || Vector2.Distance(rect.anchorMax, Vector2.one) > tolerance
            || Vector2.Distance(rect.pivot, Vector2.one) > tolerance
            || Vector2.Distance(rect.anchoredPosition, position) > tolerance
            || Vector2.Distance(rect.sizeDelta, size) > tolerance)
            throw new InvalidOperationException(
                label + " no longer matches the scaled 1080x1920 source geometry.");
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

    /// <summary>
    /// The horizontal half of the budget, which the vertical pass above never had a
    /// counterpart for. Width used to be safe only by construction; now every row the
    /// level system can actually ask for is measured at the exact scale that row would
    /// wear, so widening a vessel or moving a post fails the build instead of quietly
    /// hanging a glass over the end of the plank.
    /// </summary>
    private static void ValidateHorizontalBudget(ShelfSolve solve)
    {
        void Check(int rows, int across, float scale)
        {
            float span = (across - 1) * (solve.InnerWidth / across)
                       + solve.WidestGlass * scale;
            if (span > solve.InnerWidth + 0.001f)
                throw new InvalidOperationException(
                    $"Solved layout overflows: {rows}-row {across}-across spans "
                  + $"{span:0.###} inside an inner shelf of {solve.InnerWidth:0.###}. "
                  + "Move the posts apart or lower GlassCellFill.");
        }

        for (int across = 1; across <= BartenderShelfLevelView.MaximumColumnsPerRow; across++)
        {
            bool compact = across >= 4;
            Check(2, across, compact ? solve.ScaleFourInTwoRows : solve.ScaleTwoRow);
            Check(3, across, compact ? solve.ScaleFourInThreeRows : solve.ScaleThreeRow);
        }
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
        RenderPreview(camera, null, null, DesignWidth, DesignHeight, PreviewPath);
    }

    /// <summary>
    /// Renders a preview at an explicit device frame. When a responsive fitter is
    /// supplied, it is applied after the target texture is assigned, which makes its
    /// width-fit/top-align math use this device rather than the editor Game View.
    /// </summary>
    private static void RenderPreview(Camera camera, WorldSpaceSafeAreaFitter fitter,
                                      BartenderShelfLevelView shelfView,
                                      int width, int height, string previewPath)
    {
        var target = new RenderTexture(width, height, 24,
            RenderTextureFormat.ARGB32)
        {
            name = "SortingShelfShowcasePreview",
            antiAliasing = 1,
            filterMode = FilterMode.Bilinear
        };
        target.Create();

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        Transform compositionRoot = fitter != null ? fitter.CompositionRoot : null;
        Vector3 previousRootPosition = compositionRoot != null
            ? compositionRoot.position : Vector3.zero;
        Quaternion previousRootRotation = compositionRoot != null
            ? compositionRoot.rotation : Quaternion.identity;
        Vector3 previousRootScale = compositionRoot != null
            ? compositionRoot.localScale : Vector3.one;
        bool restoreShelfLayout = false;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32,
            false, false);
        try
        {
            camera.targetTexture = target;
            if (fitter != null && !fitter.ApplyNow())
                throw new InvalidOperationException(
                    "Could not apply the phone preview's responsive composition.");
            if (fitter != null && shelfView != null)
            {
                restoreShelfLayout = true;
                if (!shelfView.RefreshResponsiveLayoutForAuthoring(true))
                    throw new InvalidOperationException(
                        "Could not apply the phone preview's responsive shelf layout.");
            }
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            texture.Apply(false, false);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new DirectoryNotFoundException("Could not resolve the Unity project root.");
            string absolutePreview = Path.Combine(projectRoot, previewPath);
            string previewDirectory = Path.GetDirectoryName(absolutePreview);
            if (!string.IsNullOrEmpty(previewDirectory))
                Directory.CreateDirectory(previewDirectory);
            File.WriteAllBytes(absolutePreview, texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            // The responsive pose is only for this render. The builder persists the
            // neutral authored pose so the fitter can resolve the real device at run time.
            if (compositionRoot != null)
            {
                compositionRoot.SetPositionAndRotation(previousRootPosition,
                    previousRootRotation);
                compositionRoot.localScale = previousRootScale;
            }
            UnityEngine.Object.DestroyImmediate(texture);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            if (restoreShelfLayout)
                shelfView.RefreshResponsiveLayoutForAuthoring(false);
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
