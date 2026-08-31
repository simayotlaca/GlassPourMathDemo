using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;

/// <summary>
/// Creates the Unity 6 Sprite Atlas V2 asset for the hand-authored level-label set.
/// Runtime code still keeps direct Sprite references; the atlas is solely the packing
/// and batching layer, so a rename cannot silently break a level lookup.
/// </summary>
[InitializeOnLoad]
public static class LevelBadgeSpriteAtlasSetup
{
    public const string LabelFolder =
        "Assets/LiquidSort/RoyalGlassLab/Art/Ui/LevelBadge/Labels";
    public const string AtlasPath =
        "Assets/LiquidSort/RoyalGlassLab/Art/Ui/LevelBadge/LevelBadgeText.spriteatlasv2";

    static LevelBadgeSpriteAtlasSetup()
    {
        EditorApplication.delayCall += EnsureAfterRefresh;
    }

    [MenuItem("Tools/Liquid Sort/Ensure Level Badge Sprite Atlas")]
    public static void EnsureAtlas()
    {
        if (!AssetDatabase.IsValidFolder(LabelFolder))
            throw new DirectoryNotFoundException(LabelFolder);

        if (EditorSettings.spritePackerMode == SpritePackerMode.Disabled
            || EditorSettings.spritePackerMode == SpritePackerMode.SpriteAtlasV2Build)
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;

        if (File.Exists(AtlasPath)) return;

        DefaultAsset labelFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(LabelFolder);
        if (labelFolder == null)
            throw new FileNotFoundException("Level label folder is not imported", LabelFolder);

        var atlas = new SpriteAtlasAsset();
        atlas.Add(new Object[] { labelFolder });
        SpriteAtlasPackingSettings packing = new SpriteAtlasPackingSettings
        {
            padding = 8,
            enableRotation = false,
            enableTightPacking = false,
            enableAlphaDilation = true,
            blockOffset = 1
        };
        SpriteAtlasTextureSettings texture = new SpriteAtlasTextureSettings
        {
            readable = false,
            generateMipMaps = false,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 1
        };
        TextureImporterPlatformSettings platform = new TextureImporterPlatformSettings
        {
            name = "DefaultTexturePlatform",
            overridden = true,
            maxTextureSize = 2048,
            format = TextureImporterFormat.RGBA32,
            compressionQuality = 100
        };

        SpriteAtlasAsset.Save(atlas, AtlasPath);
        AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(AtlasPath) as SpriteAtlasImporter;
        if (importer == null)
            throw new InvalidDataException("Unity could not import " + AtlasPath);
        importer.includeInBuild = true;
        importer.packingSettings = packing;
        importer.textureSettings = texture;
        importer.SetPlatformSettings(platform);
        // SpriteAtlasTextureSettings.sRGB is read-only in Unity 6's public API,
        // even though the importer serializes the value. Keep this UI atlas in
        // the same sRGB space as its source PNGs.
        var serializedImporter = new SerializedObject(importer);
        SerializedProperty sRgb =
            serializedImporter.FindProperty("textureSettings.sRGB");
        if (sRgb != null)
        {
            sRgb.boolValue = true;
            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
        }
        importer.SaveAndReimport();
        Debug.Log("LiquidSort: created LevelBadgeText Sprite Atlas V2 (30 labels).");
    }

    private static void EnsureAfterRefresh()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += EnsureAfterRefresh;
            return;
        }

        try
        {
            EnsureAtlas();
        }
        catch (DirectoryNotFoundException)
        {
            // The PNG folder can arrive one AssetDatabase refresh after this script.
            EditorApplication.delayCall += EnsureAfterRefresh;
        }
    }
}
