using System;
using System.Collections.Generic;
using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the independent four-vessel Royal art lab. The authored sprites own every
/// glass highlight; the cloned BottleLiquid material still owns the moving liquid,
/// curved caps, band junctions and pour animation. Nothing here changes the original
/// four-glass profiles or their shared liquid material.
/// </summary>
[InitializeOnLoad]
public static class RoyalGlassLabBuilder
{
    private const int LabLayer = 29;
    public const string ScenePath =
        "Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity";
    private const string RequestPath = "Temp/royal-glass-lab.req";
    private const string DonePath = "Temp/royal-glass-lab.done";
    private const string PreviewPath = "Temp/RoyalGlassLab.png";

    private const string ArtRoot = "Assets/LiquidSort/RoyalGlassLab/Art/";
    private const string ProfileRoot = "Assets/LiquidSort/RoyalGlassLab/Profiles/";
    private const string MaterialRoot = "Assets/LiquidSort/RoyalGlassLab/Materials/";
    private const string ThemePath =
        "Assets/LiquidSort/RoyalGlassLab/Themes/RoyalGlassLabTheme.asset";

    private const string ShotSprite = ArtRoot + "ShotRoyalFrameEmpty.png";
    private const string ShotTraceSprite = ArtRoot + "ShotRoyalFrameGeometry.png";
    private const string CocktailSprite = ArtRoot + "CocktailRoyalFrameEmpty.png";
    private const string CocktailTraceSprite = ArtRoot + "CocktailRoyalFrameGeometry.png";
    private const string MugSprite = ArtRoot + "MugRoyalFrameEmpty.png";
    private const string MugTraceSprite = ArtRoot + "MugRoyalFrameGeometry.png";
    private const string TallSprite = ArtRoot + "TallRoyalFrameEmpty.png";
    private const string TallTraceSprite = ArtRoot + "TallRoyalFrameGeometry.png";

    private const string ShotSource = "Assets/LiquidSort/Profiles/Shot.asset";
    private const string CocktailSource =
        "Assets/LiquidSort/Profiles/CocktailGlass.asset";
    private const string MugSource = "Assets/LiquidSort/Profiles/Mug.asset";
    private const string TallSource = "Assets/LiquidSort/Profiles/Tumbler.asset";

    private const string ShotProfile = ProfileRoot + "ShotRoyal.asset";
    private const string CocktailProfile = ProfileRoot + "CocktailRoyal.asset";
    private const string MugProfile = ProfileRoot + "MugRoyal.asset";
    private const string TallProfile = ProfileRoot + "TumblerRoyal.asset";

    private const string SourceLiquidMaterial =
        "Assets/LiquidSort/Materials/BottleLiquid.mat";
    private const string RoyalLiquidMaterial =
        MaterialRoot + "RoyalBottleLiquid.mat";
    private const string SpriteMaterial = MaterialRoot + "RoyalGlassSprite.mat";
    private const string BackdropMaterial = MaterialRoot + "RoyalBackdrop.mat";
    private const string PourStreamMaterial =
        "Assets/LiquidSort/Materials/PourStream.mat";

    private static bool refreshed;

    static RoyalGlassLabBuilder() => EditorApplication.update += PollRequest;

    private static void PollRequest()
    {
        if (!File.Exists(RequestPath)) { refreshed = false; return; }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating
            || EditorApplication.isPlayingOrWillChangePlaymode) return;

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
                "ok\nscene=" + ScenePath +
                "\npreview=" + PreviewPath +
                "\nvessels=4\ninteractive=true\n" +
                "liquidMaterial=" + RoyalLiquidMaterial + "\n");
        }
        catch (Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    [MenuItem("Tools/LiquidSort/Rebuild Royal Glass Lab")]
    public static void RebuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    private static void Build()
    {
        ConfigureSpriteImporter(ShotSprite);
        ConfigureSpriteImporter(ShotTraceSprite);
        ConfigureSpriteImporter(CocktailSprite);
        ConfigureSpriteImporter(CocktailTraceSprite);
        ConfigureSpriteImporter(MugSprite);
        ConfigureSpriteImporter(MugTraceSprite);
        ConfigureSpriteImporter(TallSprite);
        ConfigureSpriteImporter(TallTraceSprite);

        Material liquid = EnsureRoyalLiquidMaterial();
        VesselProfile shot = EnsureProfile("Shot Royal", ShotSource,
            ShotProfile, ShotSprite, liquid, ShotTraceSprite);
        VesselProfile cocktail = EnsureProfile("Cocktail Royal", CocktailSource,
            CocktailProfile, CocktailSprite, liquid, CocktailTraceSprite);
        VesselProfile mug = EnsureProfile("Mug Royal", MugSource,
            MugProfile, MugSprite, liquid, MugTraceSprite);
        VesselProfile tall = EnsureProfile("Tumbler Royal", TallSource,
            TallProfile, TallSprite, liquid, TallTraceSprite);
        GlassVisualTheme theme = EnsureTheme();

        Scene previous = SceneManager.GetActiveScene();
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
                NewSceneMode.Additive);
        }
        EditorSceneManager.SetActiveScene(scene);

        try
        {
            Camera camera = BuildCamera();
            // Keep the Royal lab on the camera's clean solid background. The former
            // procedural showcase quad drew the cabinet, shelves and cocktail plinth.
            var root = new GameObject("Royal Glass Lab");
            var animator = root.AddComponent<PourAnimator>();
            var streamObject = new GameObject("PourStream");
            streamObject.transform.SetParent(root.transform, false);
            var stream = streamObject.AddComponent<PourStream>();
            stream.material = AssetDatabase.LoadAssetAtPath<Material>(PourStreamMaterial);
            if (stream.material == null)
                throw new FileNotFoundException("Missing pour stream material");
            animator.stream = stream;

            var board = root.AddComponent<WaterSortBoard>();
            board.boardCamera = camera;
            board.pourAnimator = animator;
            board.generateOnStart = false;
            board.requireMatchingColors = false;
            board.selectionLift = 0.16f;
            board.selectionSpeed = 14f;
            board.pickPadding = 0.22f;

            // Royal Smash-inspired saturated palette. These are still dynamic liquid
            // colours; none of them is baked into the glass sprites.
            // Match the visible All Glasses Playground composition. Royal sprites use
            // different pixels-per-unit values and transparent canvas bounds, so these
            // compensated transforms align the rendered silhouettes rather than merely
            // copying numerically identical Transform values.
            board.bottles.Add(BuildVessel(root.transform, "01 Shot Royal", shot,
                new Vector2(-1.20f, 1.574f), 0.654f, theme,
                new[] { Hex(0xF39A12) }));
            LiquidBottle cocktailBottle = BuildVessel(root.transform,
                "02 Cocktail Royal", cocktail,
                new Vector2(1.20f, 2.091f), 0.539f, theme,
                new[] { Hex(0x008E57), Hex(0xF44F8D) });
            board.bottles.Add(cocktailBottle);
            board.bottles.Add(BuildVessel(root.transform, "03 Mug Royal", mug,
                new Vector2(-1.20f, -2.10f), 0.66f, theme,
                new[] { Hex(0xF39A12), Hex(0x008E57) }));
            board.bottles.Add(BuildVessel(root.transform, "04 Tumbler Royal", tall,
                new Vector2(1.20f, -2.202f), 0.783f, theme,
                new[] { Hex(0x09A9E6), Hex(0x792DC4) }));

            var note = new GameObject("HELP - Click source, then click target");
            note.transform.SetParent(root.transform, false);
            SetLayerRecursively(root.transform, LabLayer);

            Validate(board, liquid);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);
            RenderPreview(camera);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidSort: Royal Glass Lab imported, baked and rendered.");
        }
        finally
        {
            if (previous.IsValid() && previous.isLoaded)
                EditorSceneManager.SetActiveScene(previous);
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static void ConfigureSpriteImporter(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            throw new FileNotFoundException("Missing cleaned Royal sprite", path);

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

    private static VesselProfile EnsureProfile(string displayName, string sourcePath,
        string profilePath, string spritePath, Material liquid,
        string traceSpritePath = null)
    {
        VesselProfile source = AssetDatabase.LoadAssetAtPath<VesselProfile>(sourcePath);
        if (source == null) throw new FileNotFoundException("Missing source profile", sourcePath);

        VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(profilePath);
        if (profile == null)
        {
            if (!AssetDatabase.CopyAsset(sourcePath, profilePath))
                throw new IOException("Could not clone profile to " + profilePath);
            AssetDatabase.ImportAsset(profilePath, ImportAssetOptions.ForceUpdate);
            profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(profilePath);
        }
        if (profile == null)
            throw new InvalidOperationException(displayName + " profile did not load.");

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        if (sprite == null)
            throw new InvalidOperationException(spritePath + " did not import as a Sprite.");
        Sprite traceSprite = string.IsNullOrEmpty(traceSpritePath)
            ? sprite
            : AssetDatabase.LoadAssetAtPath<Sprite>(traceSpritePath);
        if (traceSprite == null)
            throw new InvalidOperationException(traceSpritePath + " did not import as a Sprite.");

        profile.name = displayName;
        profile.front = sprite;
        // Neutral vertical reflections are baked into the visible front sprites.
        // Keep each clean pre-reflection alpha as the geometry source so liquid
        // capacity, bands, tilt and pour math are unchanged.
        profile.traceSource = traceSprite;
        profile.back = null;
        profile.frame = null;
        profile.liquidMaterial = liquid;
        profile.thinGlassFxMaterial = null;
        profile.handleGlassLight = 0f;
        profile.stemFootGlassLight = 0f;
        profile.stemFootToonStrength = 0f;
        profile.bottomRimGlassLight = 0f;
        profile.liquidBounceScale = 0f;
        profile.clipRightInterior = false;
        profile.capacity = source.capacity;
        EditorUtility.SetDirty(profile);

        if (!VesselProfileBaker.Bake(profile))
            throw new InvalidOperationException("Could not bake " + displayName
                                                + " from its new alpha silhouette.");
        AssetDatabase.SaveAssets();
        return profile;
    }

    private static Material EnsureRoyalLiquidMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(RoyalLiquidMaterial);
        if (material == null)
        {
            if (!AssetDatabase.CopyAsset(SourceLiquidMaterial, RoyalLiquidMaterial))
                throw new IOException("Could not clone BottleLiquid material.");
            AssetDatabase.ImportAsset(RoyalLiquidMaterial, ImportAssetOptions.ForceUpdate);
            material = AssetDatabase.LoadAssetAtPath<Material>(RoyalLiquidMaterial);
        }
        if (material == null)
            throw new InvalidOperationException("Royal liquid material did not load.");

        material.name = "Royal Bottle Liquid";
        // Keep the proven liquid-light recipe but isolate it from the original scene.
        // The palette itself arrives per bottle through MaterialPropertyBlock values.
        material.SetFloat("_CapRim", 0.38f);
        material.SetFloat("_Shine", 0.30f);
        material.SetFloat("_Overbright", 1.35f);
        material.SetFloat("_CapValue", 1.22f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material EnsureSpriteMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(SpriteMaterial);
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) throw new InvalidOperationException("Sprites/Default unavailable.");
        if (material == null)
        {
            material = new Material(shader) { name = "Royal Glass Sprite" };
            AssetDatabase.CreateAsset(material, SpriteMaterial);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }
        material.color = Color.white;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GlassVisualTheme EnsureTheme()
    {
        GlassVisualTheme theme = AssetDatabase.LoadAssetAtPath<GlassVisualTheme>(ThemePath);
        if (theme == null)
        {
            theme = ScriptableObject.CreateInstance<GlassVisualTheme>();
            theme.name = "Royal Glass Lab Theme";
            AssetDatabase.CreateAsset(theme, ThemePath);
        }

        GlassVisualTheme.Settings settings = GlassVisualTheme.Settings.Default;
        settings.preserveAuthoredFront = true;
        settings.authoredFrontMaterial = EnsureSpriteMaterial();
        settings.backAlpha = 0f;
        settings.shoulderStrength = 0f;
        settings.sideFxStrength = 0f;
        settings.rimHotspotStrength = 0f;
        settings.bottomLensStrength = 0f;
        settings.liquidBounceStrength = 0f;
        settings.paintedToyStrength = 0f;
        settings.shadowColor = Hex(0x351653);
        settings.shadowStrength = 0.42f;
        settings.wideShadowStrength = 0f;
        settings.groundGlowStrength = 0f;
        settings.panelAlpha = 0f;
        theme.settings = settings;
        EditorUtility.SetDirty(theme);
        return theme;
    }

    private static Camera BuildCamera()
    {
        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);
        var camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 5.05f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex(0x090D20);
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 100f;
        camera.allowHDR = false;
        camera.cullingMask = 1 << LabLayer;
        cameraObject.AddComponent<AudioListener>();
        return camera;
    }

    private static void BuildBackdrop()
    {
        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backdrop.name = "Royal Showcase Background";
        backdrop.layer = LabLayer;
        backdrop.transform.position = new Vector3(0f, 0f, 5f);
        backdrop.transform.localScale = new Vector3(40f, 20f, 1f);
        Collider collider = backdrop.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        MeshRenderer renderer = backdrop.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = EnsureBackdropMaterial();
        renderer.sortingLayerName = "Default";
        renderer.sortingOrder = -100;
    }

    private static Material EnsureBackdropMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(BackdropMaterial);
        Shader shader = Shader.Find("LiquidSort/PlaygroundBackdrop");
        if (shader == null)
            throw new InvalidOperationException("Playground backdrop shader unavailable.");
        if (material == null)
        {
            material = new Material(shader) { name = "Royal Backdrop" };
            AssetDatabase.CreateAsset(material, BackdropMaterial);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetFloat("_WorldHeight", 10.1f);
        material.SetColor("_TopColor", Hex(0x071A3E));
        material.SetColor("_BottomColor", Hex(0x260B35));
        material.SetColor("_BayTopColor", Hex(0x0B2148));
        material.SetColor("_BayBottomColor", Hex(0x1C1139));
        material.SetColor("_PillarColor", Hex(0x4B123D));
        material.SetColor("_BevelColor", Hex(0xA93268));
        material.SetColor("_CanopyColor", Hex(0x32103F));
        material.SetColor("_AlcoveUpper", Hex(0x0A2045));
        material.SetColor("_AlcoveLower", Hex(0x1C1038));
        material.SetColor("_ArchColor", Hex(0x92507F));
        material.SetColor("_CeilingColor", Hex(0x59D9EF));
        material.SetColor("_ShelfShadow", Hex(0x25091D));
        material.SetColor("_ShelfBody", Hex(0x74173D));
        material.SetColor("_ShelfLip", Hex(0xC33268));
        material.SetColor("_ShelfHighlight", Hex(0xF0648B));
        material.SetColor("_ShelfUnderlight", Hex(0x12BADA));
        EditorUtility.SetDirty(material);
        return material;
    }

    private static LiquidBottle BuildVessel(Transform parent, string name,
        VesselProfile profile, Vector2 localPosition, float scale,
        GlassVisualTheme theme, IReadOnlyList<Color> contents)
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
        shell.drawNeck = false;
        shell.restyleLine = false;
        shell.drawShadow = true;
        shell.theme = theme;
        shell.thinFxIntensity = 0f;
        shell.thinFxSelectionBoost = 0f;
        shell.drawGlassLight = false;
        shell.lightIntensity = 0f;
        shell.selectionBoost = 0f;
        shell.shadowStrength = 0.34f;
        shell.shadowWidth = 0.76f;
        shell.shadowHeight = 0.10f;
        shell.shadowOffsetY = 0f;

        bottle.SetUnits(contents);
        bottle.Refresh();
        shell.Build();
        return bottle;
    }

    private static void Validate(WaterSortBoard board, Material liquid)
    {
        if (board.bottles.Count != 4)
            throw new InvalidOperationException("Expected four Royal vessels.");
        foreach (LiquidBottle bottle in board.bottles)
        {
            if (bottle == null || bottle.profile == null || !bottle.profile.IsBaked)
                throw new InvalidOperationException("A Royal vessel is not baked.");
            if (bottle.profile.front == null || bottle.profile.interiorMask == null)
                throw new InvalidOperationException(bottle.name + " has incomplete art data.");
            if (bottle.profile.liquidMaterial != liquid)
                throw new InvalidOperationException(bottle.name + " is not isolated to Royal liquid.");
            if (bottle.profile.interiorBounds.width <= 0.20f
                || bottle.profile.interiorBounds.height <= 0.35f)
                throw new InvalidOperationException(bottle.name + " traced an implausible interior.");
        }
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }

    private static void RenderPreview(Camera camera)
    {
        // Export the art review at a real 9:16 HD resolution. The former 675x1200
        // target made the authored glass highlights look pixelated even though the
        // source sprites were imported uncompressed at their full resolution.
        const int width = 1080;
        const int height = 1920;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "RoyalGlassLabPreview",
            hideFlags = HideFlags.HideAndDontSave,
            antiAliasing = 4,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
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
            File.WriteAllBytes(PreviewPath, pixels.EncodeToPNG());
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

    private static Color Hex(int rgb) => new Color(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);
}
