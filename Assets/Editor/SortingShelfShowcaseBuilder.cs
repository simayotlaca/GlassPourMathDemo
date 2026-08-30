using System;
using System.Collections.Generic;
using System.IO;
using BartenderSort.Core;
using LiquidSort;
using LiquidSort.Levels;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Creates the sorting-game art scene as ordinary, serialized scene objects. There are
/// deliberately no prefab instances and no runtime object generator: all Royal glass pool
/// roots, three possible shelf planks, four posts and level-system links are saved directly
/// into the scene so an artist can continue arranging the hierarchy by hand.
/// </summary>
[InitializeOnLoad]
public static class SortingShelfShowcaseBuilder
{
    public const string ScenePath = "Assets/LiquidSort/SortingShelfShowcase.unity";

    private const string RequestPath = "Temp/sorting-shelf-showcase.req";
    private const string DonePath = "Temp/sorting-shelf-showcase.done";
    private const string PreviewPath = "Temp/SortingShelfShowcase.png";
    private const string BackdropMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/SortingShelfBackdrop.mat";
    private const string GlassLightMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/RoyalGlassLight.mat";
    private const string SourceGlassScenePath =
        "Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity";

    private const string ShelfPlankPath =
        "Assets/LiquidSort/RoyalGlassLab/Art/ShelfParts/ShelfPlank_Burgundy_v1.png";
    private const string ShelfPostPath =
        "Assets/LiquidSort/RoyalGlassLab/Art/ShelfParts/ShelfPost_BurgundyGold_v1.png";

    private const string ShotProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/ShotRoyal.asset";
    private const string CocktailProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/CocktailRoyal.asset";
    private const string MugProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/MugRoyal.asset";
    private const string TallProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/TumblerRoyal.asset";
    private const string PalettePath =
        "Assets/LiquidSort/LevelSystem/Resources/BsPalette.asset";
    private const string PreviewLevelPath =
        "Assets/LiquidSort/LevelSystem/Resources/Levels/Level_004.asset";

    // Kept separate from the canonical Royal lab layer (29).
    private const int StageLayer = 28;
    private const float CameraHalfHeight = 6.00f;
    private const float UpperShelfSurfaceY = 0.80f;
    private const float LowerShelfSurfaceY = -4.15f;
    private const float ShelfScaleX = 1.55f;
    private const float TopShelfScaleY = 1.78f;
    private const float MiddleShelfScaleY = 1.50f;
    private const float BottomShelfScaleY = 1.40f;
    private const float OpticalSeatInset = 0.02f;

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
            Build();
            File.WriteAllText(DonePath,
                "ok\nscene=" + ScenePath
                + "\npreview=" + PreviewPath
                + "\nactivePreviewGlasses=6\nmanualGlassPool=23\nlayout=3+3\n"
                + "shelfRows=3\nposts=4\nprefabInstances=0\nlevelSystem=connected\n"
                + "plank=" + ShelfPlankPath + "\npost=" + ShelfPostPath + "\n");
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

    private static void Build()
    {
        VisualBoundsCache.Clear();
        ConfigureShelfSprite(ShelfPlankPath);
        ConfigureShelfSprite(ShelfPostPath);

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
            Sprite plank = Load<Sprite>(ShelfPlankPath);
            Sprite post = Load<Sprite>(ShelfPostPath);
            VesselProfile shot = Load<VesselProfile>(ShotProfilePath);
            VesselProfile cocktail = Load<VesselProfile>(CocktailProfilePath);
            VesselProfile mug = Load<VesselProfile>(MugProfilePath);
            VesselProfile tall = Load<VesselProfile>(TallProfilePath);
            BsPalette palette = Load<BsPalette>(PalettePath);
            BsLevel previewLevel = Load<BsLevel>(PreviewLevelPath);
            Material backdropMaterial = EnsureBackdropMaterial();
            Material glassLight = Load<Material>(GlassLightMaterialPath);

            GameObject shotSource = FindSceneObject(sourceScene, "01 Shot Royal");
            GameObject cocktailSource = FindSceneObject(sourceScene, "02 Cocktail Royal");
            GameObject mugSource = FindSceneObject(sourceScene, "03 Mug Royal");
            GameObject tallSource = FindSceneObject(sourceScene, "04 Tumbler Royal");
            ValidateSourceGlass(shotSource, shot);
            ValidateSourceGlass(cocktailSource, cocktail);
            ValidateSourceGlass(mugSource, mug);
            ValidateSourceGlass(tallSource, tall);

            Camera camera = BuildCamera();
            var root = new GameObject("Sorting Game - Hand Authored");

            Transform environment = Folder(root.transform, "01 Environment - Hand Authored");
            BuildBackdrop(environment, backdropMaterial);
            ShelfPieces shelf = BuildShelf(environment, plank, post);

            Transform orders = Folder(root.transform, "02 Orders Area - Reserved (Top)");
            BuildOrderAnchors(orders);

            Transform glasses = Folder(root.transform, "03 Glasses - Manual Royal Pools (23)");
            List<BartenderShelfLevelView.GlassBinding> shots = BuildVesselPool(shotSource, glasses,
                "01 Shot Pool - 4 Direct Scene Objects",
                "Shot", BartenderShelfLevelView.FullCampaignShotPoolSize, null, 0f);
            List<BartenderShelfLevelView.GlassBinding> cocktails = BuildVesselPool(cocktailSource, glasses,
                "02 Cocktail Pool - 5 Direct Scene Objects",
                "Cocktail", BartenderShelfLevelView.FullCampaignCocktailPoolSize,
                glassLight, 0.32f);
            List<BartenderShelfLevelView.GlassBinding> lattes = BuildVesselPool(mugSource, glasses,
                "03 Latte Mug Pool - 6 Direct Scene Objects",
                "Latte Mug", BartenderShelfLevelView.FullCampaignLattePoolSize, null, 0f);
            List<BartenderShelfLevelView.GlassBinding> tumblers = BuildVesselPool(tallSource, glasses,
                "04 Tumbler Pool - 8 Direct Scene Objects",
                "Tumbler", BartenderShelfLevelView.FullCampaignTumblerPoolSize,
                glassLight, 0.26f);

            Transform systems = Folder(root.transform, "04 Level System - Serialized References");
            var levelController = systems.gameObject.AddComponent<BartenderLevelController>();
            ConfigureLevelController(levelController, palette);
            var shelfView = systems.gameObject.AddComponent<BartenderShelfLevelView>();
            shelfView.ConfigureSceneBindings(levelController, root.transform,
                shots, cocktails, lattes, tumblers,
                shelf.TopPlank, shelf.MiddlePlank, shelf.BottomPlank,
                shelf.TopSeatAnchor, shelf.MiddleSeatAnchor, shelf.BottomSeatAnchor,
                shelf.UpperLeftPost, shelf.UpperRightPost,
                shelf.LowerLeftPost, shelf.LowerRightPost);
            shelfView.ConfigureLayout(
                new Vector2(UpperShelfSurfaceY, LowerShelfSurfaceY),
                new Vector3(1.65f, -1.30f, -4.25f),
                2.60f, 2.18f, 1.60f,
                1.27f, 1.00f, 0.90f,
                OpticalSeatInset);

            var note = new GameObject(
                "NOTE - Level, 23 glasses, 3 planks and 4 posts are direct scene links");
            note.transform.SetParent(root.transform, false);

            SetLayerRecursively(root.transform, StageLayer);

            if (!shelfView.ValidateFullCampaignBindings(out string bindingError))
                throw new InvalidOperationException(bindingError);
            if (!shelfView.TryPresent(previewLevel, BsBoard.FromLevel(previewLevel), palette))
                throw new InvalidOperationException(shelfView.LastError);

            Validate(scene, shelfView, levelController, plank, post,
                cocktail, tall, glassLight);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);

            RenderPreview(camera);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidSort: hand-authored sorting shelf showcase created.");
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
        renderer.sortingOrder = -100;
    }

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

    private static ShelfPieces BuildShelf(Transform parent, Sprite plank, Sprite post)
    {
        Transform structure = Folder(parent,
            "Shelf Structure - 3 Rows + 2 Spans (Direct Links)");
        Transform shelves = Folder(structure, "01 Planks - Level Controlled");
        Transform spans = Folder(structure, "02 Post Spans - Level Controlled");
        Transform upperSpan = Folder(spans, "Span 01 - Between Row 1 and Row 2");
        Transform lowerSpan = Folder(spans, "Span 02 - Between Row 2 and Row 3");

        float topY = UpperShelfSurfaceY
                   - plank.bounds.size.y * TopShelfScaleY * 0.5f;
        float middleY = LowerShelfSurfaceY
                      - plank.bounds.size.y * MiddleShelfScaleY * 0.5f;
        float bottomSurface = -4.25f;
        float bottomY = bottomSurface
                      - plank.bounds.size.y * BottomShelfScaleY * 0.5f;

        var pieces = new ShelfPieces
        {
            TopPlank = BuildSprite(shelves,
                "Shelf Row 01 - Direct Plank Asset", plank,
                new Vector2(0f, topY),
                new Vector2(ShelfScaleX, TopShelfScaleY), 10),
            MiddlePlank = BuildSprite(shelves,
                "Shelf Row 02 - Direct Plank Asset", plank,
                new Vector2(0f, middleY),
                new Vector2(ShelfScaleX, MiddleShelfScaleY), 10),
            BottomPlank = BuildSprite(shelves,
                "Shelf Row 03 - Direct Plank Asset", plank,
                new Vector2(0f, bottomY),
                new Vector2(ShelfScaleX, BottomShelfScaleY), 10),
            UpperLeftPost = BuildSprite(upperSpan,
                "Post Span 01 Left - Direct Post Asset", post,
                new Vector2(-3.00f, 0f), new Vector2(0.72f, 1f), 5),
            UpperRightPost = BuildSprite(upperSpan,
                "Post Span 01 Right - Direct Post Asset", post,
                new Vector2(3.00f, 0f), new Vector2(0.72f, 1f), 5),
            LowerLeftPost = BuildSprite(lowerSpan,
                "Post Span 02 Left - Direct Post Asset", post,
                new Vector2(-3.00f, 0f), new Vector2(0.72f, 1f), 5),
            LowerRightPost = BuildSprite(lowerSpan,
                "Post Span 02 Right - Direct Post Asset", post,
                new Vector2(3.00f, 0f), new Vector2(0.72f, 1f), 5)
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

    private static void BuildOrderAnchors(Transform parent)
    {
        parent.localPosition = new Vector3(0f, 5.05f, 0f);
        EmptyAnchor(parent, "Order Slot 01 - Future", -2.10f);
        EmptyAnchor(parent, "Order Slot 02 - Future", 0f);
        EmptyAnchor(parent, "Order Slot 03 - Future", 2.10f);
    }

    private static void EmptyAnchor(Transform parent, string name, float x)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
    }

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

    private static List<BartenderShelfLevelView.GlassBinding> BuildVesselPool(
        GameObject source, Transform parent, string folderName, string vesselName, int count,
        Material glassLight, float glassLightIntensity)
    {
        Transform poolRoot = Folder(parent, folderName);
        var pool = new List<BartenderShelfLevelView.GlassBinding>(count);
        for (int i = 0; i < count; i++)
            pool.Add(ClonePoolVessel(source, poolRoot,
                $"{vesselName} Scene Glass {i + 1:00}",
                glassLight, glassLightIntensity));
        return pool;
    }

    private static BartenderShelfLevelView.GlassBinding ClonePoolVessel(
        GameObject source, Transform parent, string name,
        Material glassLight, float glassLightIntensity)
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

        var foot = new GameObject("Glass Foot Anchor - Direct Link");
        foot.transform.SetParent(go.transform, false);
        foot.transform.localPosition = new Vector3(
            bottle.mouthLocal.x, SpriteVisualBounds(bottle.profile.front).yMin, 0f);

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

    private static void ConfigureLevelController(BartenderLevelController controller,
        BsPalette palette)
    {
        var serialized = new SerializedObject(controller);
        SerializedProperty paletteProperty = serialized.FindProperty("palette");
        if (paletteProperty == null)
            throw new InvalidOperationException(
                "BartenderLevelController palette field could not be serialized.");
        paletteProperty.objectReferenceValue = palette;
        serialized.ApplyModifiedPropertiesWithoutUndo();
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

    private static void ConfigureShelfSprite(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException("Missing shelf artwork", path);

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

    private static void Validate(Scene scene, BartenderShelfLevelView shelfView,
        BartenderLevelController levelController, Sprite plank, Sprite post,
        VesselProfile cocktail, VesselProfile tall, Material glassLight)
    {
        if (shelfView == null || levelController == null
            || shelfView.Controller != levelController)
            throw new InvalidOperationException(
                "The level controller and shelf view are not directly linked.");
        if (!shelfView.ValidateFullCampaignBindings(out string bindingError))
            throw new InvalidOperationException(bindingError);
        if (!shelfView.Ready || shelfView.ActiveGlassCount != 6
            || shelfView.VisibleShelfRows != 2)
            throw new InvalidOperationException(
                "The saved authoring preview must be Level 4's six-glass 3+3 layout.");

        int linkedPlanks = 0;
        int linkedPosts = 0;
        int glassPoolSize = 0;
        int activePreviewGlasses = 0;
        int reflectedGlasses = 0;
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
                bool expectsReflection = bottle.profile == cocktail || bottle.profile == tall;
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
                if (renderer.sprite == plank) linkedPlanks++;
                if (renderer.sprite == post) linkedPosts++;
            }
        }

        if (glassPoolSize != 23 || activePreviewGlasses != 6)
            throw new InvalidOperationException(
                $"Expected 23 direct Royal pool objects and 6 active preview glasses; "
              + $"got {glassPoolSize}/{activePreviewGlasses}.");
        if (reflectedGlasses != 13)
            throw new InvalidOperationException(
                $"Expected 13 reflected Cocktail/Tumbler glasses; got {reflectedGlasses}.");
        if (linkedPlanks != 3 || linkedPosts != 4)
            throw new InvalidOperationException(
                $"Expected three direct planks and four direct posts; got "
              + $"{linkedPlanks}/{linkedPosts}.");
        if (oldSandboxBoards != 0)
            throw new InvalidOperationException(
                "WaterSortBoard must not coexist with the Bartender level-system view.");
    }

    private static void RenderPreview(Camera camera)
    {
        const int width = 720;
        const int height = 1280;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "SortingShelfShowcasePreview",
            antiAliasing = 1,
            filterMode = FilterMode.Bilinear
        };
        target.Create();

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
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
