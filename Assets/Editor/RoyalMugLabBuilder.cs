using System;
using System.Collections.Generic;
using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Imports and verifies the new authored mug without touching the original Mug profile
/// or AllGlassesPlayground scene. The profile is baked from the new alpha silhouette,
/// so the dynamic liquid geometry follows this artwork instead of the retired drawing.
/// </summary>
[InitializeOnLoad]
public static class RoyalMugLabBuilder
{
    private const int LabLayer = 31;
    public const string ScenePath = "Assets/LiquidSort/RoyalGlassLab/RoyalMugLab.unity";
    private const string RequestPath = "Temp/royal-mug-lab.req";
    private const string DonePath = "Temp/royal-mug-lab.done";
    private const string PreviewPath = "Temp/RoyalMugLab.png";

    private const string SpritePath =
        "Assets/LiquidSort/RoyalGlassLab/Art/MugRoyalFrameEmpty.png";
    private const string SourceProfilePath = "Assets/LiquidSort/Profiles/Mug.asset";
    private const string ProfilePath =
        "Assets/LiquidSort/RoyalGlassLab/Profiles/MugRoyal.asset";
    private const string ThemePath =
        "Assets/LiquidSort/RoyalGlassLab/Themes/RoyalGlassLabTheme.asset";
    private const string SpriteMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/RoyalGlassSprite.mat";
    private const string SharedLiquidMaterialPath =
        "Assets/LiquidSort/Materials/BottleLiquid.mat";
    private const string RoyalLiquidMaterialPath =
        "Assets/LiquidSort/RoyalGlassLab/Materials/RoyalBottleLiquid.mat";

    private static bool refreshed;

    static RoyalMugLabBuilder() => EditorApplication.update += PollRequest;

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
                "\nprofile=" + ProfilePath +
                "\nsprite=" + SpritePath +
                "\npreview=" + PreviewPath + "\n");
        }
        catch (Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    [MenuItem("Tools/LiquidSort/Rebuild Royal Mug Lab")]
    public static void RebuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    private static void Build()
    {
        ConfigureSpriteImporter();
        VesselProfile profile = EnsureProfile();
        GlassVisualTheme theme = EnsureTheme();

        Scene previous = SceneManager.GetActiveScene();
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool alreadyOpen = scene.IsValid() && scene.isLoaded;
        if (alreadyOpen)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                UnityEngine.Object.DestroyImmediate(root);
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
            LiquidBottle bottle = BuildMug(profile, theme);
            Validate(profile, bottle);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Could not save " + ScenePath);
            RenderPreview(camera);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidSort: Royal Mug Lab imported, baked and rendered.");
        }
        finally
        {
            if (previous.IsValid() && previous.isLoaded)
                EditorSceneManager.SetActiveScene(previous);
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static void ConfigureSpriteImporter()
    {
        TextureImporter importer = AssetImporter.GetAtPath(SpritePath) as TextureImporter;
        if (importer == null)
            throw new FileNotFoundException("Missing cleaned Royal mug sprite", SpritePath);

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

    private static VesselProfile EnsureProfile()
    {
        VesselProfile source = AssetDatabase.LoadAssetAtPath<VesselProfile>(SourceProfilePath);
        if (source == null) throw new FileNotFoundException("Missing source Mug profile");

        VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(ProfilePath);
        if (profile == null)
        {
            if (!AssetDatabase.CopyAsset(SourceProfilePath, ProfilePath))
                throw new IOException("Could not clone Mug profile to " + ProfilePath);
            AssetDatabase.ImportAsset(ProfilePath, ImportAssetOptions.ForceUpdate);
            profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(ProfilePath);
        }
        if (profile == null) throw new InvalidOperationException("Royal Mug profile did not load.");

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpritePath);
        if (sprite == null) throw new InvalidOperationException("Royal mug did not import as a Sprite.");

        profile.name = "Mug Royal";
        profile.front = sprite;
        profile.traceSource = sprite;
        profile.back = null;
        profile.frame = null;
        profile.liquidMaterial = EnsureRoyalLiquidMaterial();
        profile.thinGlassFxMaterial = null;
        profile.handleGlassLight = 0f;
        profile.stemFootGlassLight = 0f;
        profile.stemFootToonStrength = 0f;
        profile.bottomRimGlassLight = 0f;
        profile.liquidBounceScale = 0f;
        profile.clipRightInterior = false;
        profile.capacity = 3;
        EditorUtility.SetDirty(profile);

        if (!VesselProfileBaker.Bake(profile))
            throw new InvalidOperationException("Could not bake liquid geometry from Royal mug alpha.");
        AssetDatabase.SaveAssets();
        return profile;
    }

    private static Material EnsureRoyalLiquidMaterial()
    {
        Material source = AssetDatabase.LoadAssetAtPath<Material>(
            SharedLiquidMaterialPath);
        if (source == null)
            throw new FileNotFoundException("Missing shared BottleLiquid material");

        Material material = AssetDatabase.LoadAssetAtPath<Material>(
            RoyalLiquidMaterialPath);
        if (material == null)
        {
            material = new Material(source) { name = "Royal Bottle Liquid" };
            AssetDatabase.CreateAsset(material, RoyalLiquidMaterialPath);
        }
        else
        {
            material.shader = source.shader;
            material.CopyPropertiesFromMaterial(source);
            material.name = "Royal Bottle Liquid";
        }

        // This is purely a Royal-scene art direction layer. LiquidBottle continues to
        // supply masks, band heights, cap geometry, tilt, wave and splash through its
        // property block; these values only shape colour inside those exact pixels.
        material.SetFloat("_CylinderKey", 0.84f);
        material.SetFloat("_CylinderShade", 0.12f);
        material.SetFloat("_EdgeShade", 0.08f);
        material.SetFloat("_WallShade", 0.10f);
        material.SetFloat("_WallWidth", 0.06f);
        material.SetFloat("_WallBias", 0.28f);
        material.SetFloat("_RoundShade", 0.04f);
        material.SetFloat("_CapFalloff", 0.58f);
        material.SetFloat("_CapRim", 0.62f);
        material.SetFloat("_FarRim", 0.24f);
        material.SetFloat("_Shine", 0.58f);
        material.SetFloat("_ShineX", -0.42f);
        material.SetFloat("_ShineWidth", 0.38f);
        material.SetFloat("_Overbright", 1.22f);
        material.SetFloat("_BoundaryShade", 0.16f);
        material.SetFloat("_FloorShade", 0f);
        material.SetFloat("_FloorGlow", 0f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material EnsureSpriteMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(SpriteMaterialPath);
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) throw new InvalidOperationException("Sprites/Default is unavailable.");

        if (material == null)
        {
            material = new Material(shader) { name = "Royal Glass Sprite" };
            AssetDatabase.CreateAsset(material, SpriteMaterialPath);
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
        cameraObject.transform.position = new Vector3(0f, 0.05f, -10f);
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 2.45f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex(0x927DB8);
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 100f;
        camera.allowHDR = false;
        camera.cullingMask = 1 << LabLayer;
        cameraObject.AddComponent<AudioListener>();
        return camera;
    }

    private static LiquidBottle BuildMug(VesselProfile profile, GlassVisualTheme theme)
    {
        var root = new GameObject("Royal Mug Lab");
        var go = new GameObject("Mug Royal");
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = new Vector3(-0.10f, 0f, 0f);
        go.transform.localScale = Vector3.one * 0.96f;

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
        shell.shadowWidth = 0.78f;
        shell.shadowHeight = 0.10f;
        shell.shadowOffsetY = 0f;

        bottle.SetUnits(new List<Color>
        {
            LiquidPalette.BodyOf("Tangerine Orange"),
            LiquidPalette.BodyOf("Deep Teal"),
            LiquidPalette.BodyOf("Candy Pink")
        });
        bottle.Refresh();
        shell.Build();
        SetLayerRecursively(root.transform, LabLayer);
        return bottle;
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }

    private static void Validate(VesselProfile profile, LiquidBottle bottle)
    {
        if (profile == null || !profile.IsBaked || profile.interiorMask == null)
            throw new InvalidOperationException("Royal Mug profile is not fully baked.");
        if (profile.front == null)
            throw new InvalidOperationException("Royal mug sprite reference is missing.");
        // Read/Write must be restored to off after the editor-only bake.
        TextureImporter importer = AssetImporter.GetAtPath(SpritePath) as TextureImporter;
        if (importer == null || importer.isReadable)
            throw new InvalidOperationException("Royal mug sprite import state is invalid.");
        if (bottle == null || bottle.profile != profile || bottle.UnitCount != 3)
            throw new InvalidOperationException("Royal Mug scene state is invalid.");
        if (profile.interiorBounds.width <= 0.5f || profile.interiorBounds.height <= 1f)
            throw new InvalidOperationException("Royal Mug interior trace is implausibly small.");
    }

    private static void RenderPreview(Camera camera)
    {
        const int width = 700;
        const int height = 1000;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "RoyalMugLabPreview",
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
