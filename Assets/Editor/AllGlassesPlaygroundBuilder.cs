using System;
using System.Collections.Generic;
using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a self-contained animation sandbox containing one instance of every baked
/// vessel. It intentionally does not use WaterSortBoard.Generate(): the four profiles
/// have different capacities, and a global generated capacity would overwrite them.
/// </summary>
[InitializeOnLoad]
public static class AllGlassesPlaygroundBuilder
{
    public const string ScenePath = "Assets/LiquidSort/AllGlassesPlayground.unity";
    private const string RequestPath = "Temp/all-glasses-playground.req";
    private const string DonePath = "Temp/all-glasses-playground.done";

    private const string ShotPath = "Assets/LiquidSort/Profiles/Shot.asset";
    private const string CocktailPath = "Assets/LiquidSort/Profiles/CocktailGlass.asset";
    private const string MugPath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/MugRoyal.asset";
    private const string TumblerPath = "Assets/LiquidSort/Profiles/Tumbler.asset";
    private const string TumblerAuthoredFrontPath = "Assets/Art/Uzundeneme2.png";
    private const string GlassThemePath =
        "Assets/LiquidSort/Themes/PremiumCasualGlassTheme.asset";
    private const string AuthoredSpriteMaterialPath =
        "Assets/LiquidSort/Materials/AuthoredGlassSprite.mat";
    private const string BackdropMaterialPath =
        "Assets/LiquidSort/Materials/PlaygroundBackdrop.mat";
    private const int PlaygroundLayer = 30;

    private static bool refreshed;

    static AllGlassesPlaygroundBuilder() => EditorApplication.update += PollRequest;

    private static void PollRequest()
    {
        if (!File.Exists(RequestPath)) { refreshed = false; return; }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating
            || EditorApplication.isPlayingOrWillChangePlaymode) return;

        // First let Unity import/recompile every file that belongs to the rig. Building on
        // the following editor tick guarantees that the saved scene uses the current code.
        if (!refreshed)
        {
            refreshed = true;
            AssetDatabase.Refresh();
            return;
        }

        refreshed = false;
        BuildIfRequested();
    }

    [MenuItem("Tools/LiquidSort/Rebuild All Glasses Playground")]
    public static void RebuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    private static void BuildIfRequested()
    {
        if (!File.Exists(RequestPath)) return;
        File.Delete(RequestPath);

        try
        {
            Build();
            File.WriteAllText(DonePath,
                "ok\nscene=" + ScenePath +
                "\nvessels=4\ninteractive=true\nmatching-colours=false\n");
        }
        catch (Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    private static void Build()
    {
        VesselProfile shot = LoadProfile(ShotPath);
        VesselProfile cocktail = LoadProfile(CocktailPath);
        VesselProfile mug = LoadProfile(MugPath);
        VesselProfile tumbler = LoadProfile(TumblerPath);
        ConfigureAuthoredTumblerFront(tumbler);
        ConfigureProfileLighting(shot, cocktail, mug, tumbler);
        GlassVisualTheme glassTheme = EnsurePremiumGlassTheme();

        Scene previous = SceneManager.GetActiveScene();
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool alreadyOpen = scene.IsValid() && scene.isLoaded;
        if (alreadyOpen)
        {
            // This scene is generated and may be the one the user is currently looking
            // at. Rebuild it in place instead of trying to save a second open scene to
            // the same path (Unity rejects that save).
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                UnityEngine.Object.DestroyImmediate(roots[i]);
        }
        else
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        }
        EditorSceneManager.SetActiveScene(scene);

        try
        {
            Camera camera = BuildCamera();
            BuildShowcaseBackdrop();

            var root = new GameObject("All Glasses Playground");
            var animator = root.AddComponent<PourAnimator>();
            var streamObject = new GameObject("PourStream");
            streamObject.transform.SetParent(root.transform, false);
            var stream = streamObject.AddComponent<PourStream>();
            stream.material = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/LiquidSort/Materials/PourStream.mat");
            if (stream.material == null)
                throw new FileNotFoundException("Missing pour-stream material");
            animator.stream = stream;

            var board = root.AddComponent<WaterSortBoard>();
            board.boardCamera = camera;
            board.pourAnimator = animator;
            board.generateOnStart = false;
            board.requireMatchingColors = false;
            board.selectionLift = 0.16f;
            board.selectionSpeed = 14f;
            board.pickPadding = 0.22f;

            // Two rows keep every silhouette readable in both portrait and landscape
            // Game views. Positions are root-local so the whole rig can be copied or
            // turned into a prefab without snapping back to world-space coordinates.
            board.bottles.Add(BuildVessel(root.transform, "01 Shot", shot,
                new Vector2(-1.20f, 2.10f), 0.76f, glassTheme, 0f,
                new[] { Hex(0xF39A12) }));
            board.bottles.Add(BuildVessel(root.transform, "02 Cocktail", cocktail,
                new Vector2(1.20f, 2.10f), 0.70f, glassTheme, 0f,
                new[] { Hex(0x008E57), Hex(0xF44F8D) }));
            board.bottles.Add(BuildVessel(root.transform, "03 Mug", mug,
                new Vector2(-1.20f, -2.10f), 0.66f, glassTheme, 0f,
                new[] { Hex(0xF39A12), Hex(0x008E57) }));
            board.bottles.Add(BuildVessel(root.transform, "04 Tumbler", tumbler,
                new Vector2(1.20f, -2.10f), 0.72f, glassTheme, 0f,
                new[] { Hex(0x09A9E6), Hex(0x792DC4) }));

            var note = new GameObject("HELP - Click source, then click target");
            note.transform.SetParent(root.transform, false);

            // Unity may keep editor lab scenes loaded additively. Restrict this camera
            // and every generated object to one layer so an unrelated preview vessel
            // can never leak into this scene's render or saved comparison image.
            SetLayerRecursively(root.transform, PlaygroundLayer);

            Validate(board);
            RenderPortraitPreview(camera);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidSort: AllGlassesPlayground hazır — kaynağa, sonra hedefe tıklayın.");
        }
        finally
        {
            if (previous.IsValid() && previous.isLoaded)
                EditorSceneManager.SetActiveScene(previous);
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static Camera BuildCamera()
    {
        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);

        var camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        // 5.05 keeps the widest handled glass inside a 9:16 phone view; wider Game
        // views simply reveal more background without changing the composition.
        camera.orthographicSize = 5.05f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex(0x090D20);
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 100f;
        camera.allowHDR = true;
        camera.cullingMask = 1 << PlaygroundLayer;

        // The authored PNGs already contain their own highlights. Keep the first raw-art
        // baseline free of post-process halos; the liquid shader supplies its crisp cap
        // and band highlights directly.
        cameraObject.AddComponent<AudioListener>();
        return camera;
    }

    private static void BuildShowcaseBackdrop()
    {
        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backdrop.name = "Showcase Background";
        backdrop.layer = PlaygroundLayer;
        backdrop.transform.position = new Vector3(0f, 0f, 5f);
        backdrop.transform.localScale = new Vector3(40f, 20f, 1f);

        // WaterSortBoard picks against bottle bounds rather than physics, and the
        // backdrop never needs a collider. Removing the primitive's default collider
        // also prevents it becoming future input debt if picking changes later.
        Collider collider = backdrop.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);

        MeshRenderer renderer = backdrop.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = EnsureBackdropMaterial();
        renderer.sortingLayerName = "Default";
        renderer.sortingOrder = -100;
    }

    private static Material EnsureBackdropMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(BackdropMaterialPath);
        Shader shader = Shader.Find("LiquidSort/PlaygroundBackdrop");
        if (shader == null)
            throw new InvalidOperationException("Playground backdrop shader is unavailable.");

        if (material == null)
        {
            material = new Material(shader) { name = "Playground Backdrop" };
            AssetDatabase.CreateAsset(material, BackdropMaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetFloat("_WorldHeight", 10.1f);
        material.SetColor("_TopColor", Hex(0x09152B));
        material.SetColor("_BottomColor", Hex(0x250C26));
        material.SetColor("_BayTopColor", Hex(0x111D38));
        material.SetColor("_BayBottomColor", Hex(0x171126));
        material.SetColor("_PillarColor", Hex(0x421029));
        material.SetColor("_BevelColor", Hex(0x8B2E48));
        material.SetColor("_CanopyColor", Hex(0x300E29));
        material.SetColor("_AlcoveUpper", Hex(0x0B1731));
        material.SetColor("_AlcoveLower", Hex(0x151129));
        material.SetColor("_ArchColor", Hex(0x8B4742));
        material.SetColor("_CeilingColor", Hex(0x64DDE2));
        material.SetColor("_ShelfShadow", Hex(0x25091D));
        material.SetColor("_ShelfBody", Hex(0x74172F));
        material.SetColor("_ShelfLip", Hex(0xBC3249));
        material.SetColor("_ShelfHighlight", Hex(0xE8616C));
        material.SetColor("_ShelfUnderlight", Hex(0x0EA8C6));
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GlassVisualTheme EnsurePremiumGlassTheme()
    {
        const string folder = "Assets/LiquidSort/Themes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets/LiquidSort", "Themes");

        GlassVisualTheme theme =
            AssetDatabase.LoadAssetAtPath<GlassVisualTheme>(GlassThemePath);
        if (theme == null)
        {
            theme = ScriptableObject.CreateInstance<GlassVisualTheme>();
            theme.name = "Premium Casual Glass Theme";
            AssetDatabase.CreateAsset(theme, GlassThemePath);
        }

        GlassVisualTheme.Settings settings = GlassVisualTheme.Settings.Default;
        settings.preserveAuthoredFront = true;
        settings.authoredFrontMaterial = EnsureAuthoredSpriteMaterial();
        settings.contourDark = Hex(0x2C174F);
        settings.contourLight = Hex(0x83BFD2);
        settings.lightDirection = 120f;
        settings.backTint = Hex(0x6B91BA);
        settings.backAlpha = 0f;
        settings.glassKeyLight = Hex(0xFFF1C8);
        settings.glassFillLight = Hex(0x45E3F4);
        settings.shoulderStrength = 0f;
        settings.sideFxStrength = 0f;
        settings.rimHotspotStrength = 0f;
        settings.bottomLensStrength = 0f;
        settings.liquidBounceStrength = 0f;
        settings.paintedToyStrength = 0f;
        settings.toyMidColor = Hex(0x397EB8);
        settings.toyFillColor = Hex(0x45E3F4);
        settings.shadowColor = Hex(0x351653);
        settings.shadowStrength = 0.40f;
        settings.wideShadowColor = Hex(0x68418A);
        settings.wideShadowStrength = 0f;
        settings.groundGlowColor = Hex(0xF6BE78);
        settings.groundGlowStrength = 0f;
        // Background design comes later; keep the optional full playfield panel off.
        settings.panelAlpha = 0f;

        theme.settings = settings;
        EditorUtility.SetDirty(theme);
        return theme;
    }

    private static Material EnsureAuthoredSpriteMaterial()
    {
        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(AuthoredSpriteMaterialPath);
        Shader spriteShader = Shader.Find("LiquidSort/AuthoredGlassPalette");
        if (spriteShader == null)
            throw new InvalidOperationException("Authored glass palette shader is unavailable.");

        if (material == null)
        {
            material = new Material(spriteShader) { name = "Authored Glass Sprite" };
            AssetDatabase.CreateAsset(material, AuthoredSpriteMaterialPath);
        }
        else if (material.shader != spriteShader)
        {
            material.shader = spriteShader;
        }

        material.color = Color.white;
        material.SetColor("_ShadowColor", Hex(0x4F5D86));
        material.SetColor("_MidColor", Hex(0x8E92B4));
        material.SetColor("_HighlightColor", Hex(0xD9F0EE));
        material.SetFloat("_ShadowPoint", 0.33f);
        material.SetFloat("_MidPoint", 0.50f);
        material.SetFloat("_HighlightPoint", 0.68f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void ConfigureProfileLighting(VesselProfile shot,
        VesselProfile cocktail, VesselProfile mug, VesselProfile tumbler)
    {
        DisableProfileGlassFx(shot);
        DisableProfileGlassFx(cocktail);
        DisableProfileGlassFx(mug);
        DisableProfileGlassFx(tumbler);

        EditorUtility.SetDirty(shot);
        EditorUtility.SetDirty(cocktail);
        EditorUtility.SetDirty(mug);
        EditorUtility.SetDirty(tumbler);
    }

    private static void ConfigureAuthoredTumblerFront(VesselProfile tumbler)
    {
        TextureImporter importer =
            AssetImporter.GetAtPath(TumblerAuthoredFrontPath) as TextureImporter;
        if (importer == null)
            throw new FileNotFoundException("Missing authored tumbler PNG",
                TumblerAuthoredFrontPath);

        bool needsImport = importer.textureType != TextureImporterType.Sprite
                           || importer.spriteImportMode != SpriteImportMode.Single
                           || Mathf.Abs(importer.spritePixelsPerUnit - 512f) > 0.001f
                           || importer.mipmapEnabled != true
                           || importer.alphaIsTransparency != true
                           || importer.textureCompression != TextureImporterCompression.Uncompressed
                           || importer.wrapMode != TextureWrapMode.Clamp
                           || importer.filterMode != FilterMode.Bilinear
                           || importer.maxTextureSize != 2048;
        if (needsImport)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 512f;

            // Unity 6 no longer exposes spriteAlignment/spritePivot directly on
            // TextureImporter. Round-trip them through TextureImporterSettings so
            // this remains compatible with the editor version used by the project.
            var textureSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(textureSettings);
            textureSettings.spriteAlignment = (int)SpriteAlignment.Center;
            textureSettings.spritePivot = new Vector2(0.5f, 0.5f);
            importer.SetTextureSettings(textureSettings);

            importer.mipmapEnabled = true;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
        }

        Sprite authored =
            AssetDatabase.LoadAssetAtPath<Sprite>(TumblerAuthoredFrontPath);
        if (authored == null)
            throw new InvalidOperationException("Authored tumbler did not import as a Sprite.");

        if (tumbler.front == authored) return;

        // The supplied PNG owns visible colour/alpha. Keep the already baked v2 sprite as
        // the permanent geometry source so later profile bakes cannot move the liquid wall
        // because of the raw artwork's few additional antialiased pixels.
        if (tumbler.traceSource == null) tumbler.traceSource = tumbler.front;
        tumbler.front = authored;
        EditorUtility.SetDirty(tumbler);
    }

    private static void DisableProfileGlassFx(VesselProfile profile)
    {
        profile.handleGlassLight = 0f;
        profile.stemFootGlassLight = 0f;
        profile.stemFootToonStrength = 0f;
        profile.bottomRimGlassLight = 0f;
        profile.liquidBounceScale = 0f;
    }

    private static LiquidBottle BuildVessel(Transform parent, string name,
        VesselProfile profile, Vector2 localPosition, float scale,
        GlassVisualTheme theme, float thinFxIntensity, IReadOnlyList<Color> contents)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(localPosition.x, localPosition.y, 0f);
        go.transform.localScale = Vector3.one * scale;

        var bottle = go.AddComponent<LiquidBottle>();
        bottle.profile = profile;
        bottle.capacity = profile.capacity;
        bottle.sortingOrder = 1;

        var shell = go.AddComponent<BottleShell>();
        shell.backOverride = profile.back;
        shell.drawNeck = false;
        shell.restyleLine = false;
        shell.drawShadow = true;
        shell.theme = theme;
        shell.thinFxIntensity = thinFxIntensity;
        shell.thinFxSelectionBoost = 0f;
        shell.drawGlassLight = false;
        shell.lightIntensity = 0f;
        shell.selectionBoost = 0f;
        shell.shadowStrength = 0.35f;
        shell.shadowWidth = 0.72f;
        shell.shadowHeight = 0.10f;
        shell.shadowOffsetY = 0f;

        bottle.SetUnits(contents);
        bottle.Refresh();
        shell.Build();
        return bottle;
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }

    private static void RenderPortraitPreview(Camera camera)
    {
        const int width = 675;
        const int height = 1200;
        const string path = "Temp/AllGlassesPlayground.png";

        var target = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR)
        {
            name = "AllGlassesPlaygroundPreview",
            hideFlags = HideFlags.HideAndDontSave
        };
        target.Create();

        RenderTexture previousTarget = camera.targetTexture;
        RenderTexture previousActive = RenderTexture.active;
        Texture2D pixels = null;
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            pixels.Apply(false, false);
            byte[] png = pixels.EncodeToPNG();
            File.WriteAllBytes(path, png);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    private static VesselProfile LoadProfile(string path)
    {
        VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(path);
        if (profile == null) throw new FileNotFoundException("Missing vessel profile", path);
        if (!profile.IsBaked) throw new InvalidOperationException(profile.name + " is not baked.");
        if (profile.front == null || profile.interiorMask == null || profile.liquidMaterial == null)
            throw new InvalidOperationException(profile.name + " is missing baked art/material data.");
        return profile;
    }

    private static void Validate(WaterSortBoard board)
    {
        if (board.bottles.Count != 4) throw new InvalidOperationException("Expected four vessels.");
        for (int i = 0; i < board.bottles.Count; i++)
        {
            LiquidBottle bottle = board.bottles[i];
            if (bottle == null || bottle.profile == null || !bottle.profile.IsBaked)
                throw new InvalidOperationException("A playground vessel has no baked profile.");
            if (bottle.capacity != bottle.profile.capacity || bottle.UnitCount > bottle.capacity)
                throw new InvalidOperationException(bottle.name + " has inconsistent capacity/state.");
        }

        // The first user action has at least one legal destination in sandbox mode.
        if (WaterSortBoard.TransferAmount(board.bottles[0], board.bottles[2], false) <= 0)
            throw new InvalidOperationException("Initial playground layout cannot start a pour.");
    }

    private static Color Hex(int rgb) => new Color(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);
}
