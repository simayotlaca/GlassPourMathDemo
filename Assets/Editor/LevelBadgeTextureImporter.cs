using UnityEditor;
using UnityEngine;

/// <summary>Stable, lossless import settings for the clean badge and label sprites.</summary>
public sealed class LevelBadgeTextureImporter : AssetPostprocessor
{
    private const string BadgePath =
        "Assets/LiquidSort/RoyalGlassLab/Art/Ui/LevelBadge_Cute_Empty_Clean.png";
    private const string LabelPrefix =
        "Assets/LiquidSort/RoyalGlassLab/Art/Ui/LevelBadge/Labels/";

    private void OnPreprocessTexture()
    {
        if (assetPath != BadgePath
            && !(assetPath.StartsWith(LabelPrefix)
                 && assetPath.EndsWith(".png"))) return;

        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.isReadable = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 2048;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        settings.spritePivot = new Vector2(0.5f, 0.5f);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
    }
}
