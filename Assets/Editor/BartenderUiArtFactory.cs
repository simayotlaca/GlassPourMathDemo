using System;

using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sipariş kartı, düğme ve rozet görsellerini üretir.
///
/// NEDEN ÜRETİLİYOR: proje bu parçalar için hiç art almadı, ama sahneyi "sonra
/// gelecek" boş kutularla kurmak yerleşimi ölçülemez hâle getirir — bir kartın
/// ne kadar yer kapladığını ancak çizildikten sonra görürsünüz. Üretilen görseller
/// nihai sanat değil, ÖLÇÜSÜ DOĞRU yer tutuculardır: hepsi 9-slice veya dairesel,
/// yani sanatçı aynı isimli dosyayı değiştirdiğinde sahnede tek bir sayı bile
/// oynamaz.
///
/// Hepsi işaretli mesafe alanıyla (SDF) çizilir; kenarlar bu yüzden ölçekten
/// bağımsız temiz kalır. <see cref="BottleArtFactory"/> aynı yaklaşımı bardak
/// maskesi için kullanıyor, çizim disiplini bilerek aynı.
/// </summary>
public static class BartenderUiArtFactory
{
    public const string UiArtFolder = "Assets/LiquidSort/RoyalGlassLab/Art/Ui";
    public const string OrderIconFolder = "Assets/LiquidSort/RoyalGlassLab/Art/OrderIcons";

    /// <summary>
    /// Üretim sürümü. Şekil matematiği değişince artırılır; artmadıkça var olan
    /// dosyalar yeniden yazılmaz, çünkü bir sanatçı onların üstüne çizmiş olabilir.
    /// </summary>
    private const int ArtVersion = 1;
    private const string VersionKey = "LiquidSort.BartenderUiArt.Version";

    public const string CardPanelPath = UiArtFolder + "/Ui_CardPanel.png";
    public const string CardEdgePath = UiArtFolder + "/Ui_CardEdge.png";
    public const string CardClipPath = UiArtFolder + "/Ui_CardClip.png";
    public const string PillPath = UiArtFolder + "/Ui_Pill.png";

    /// <summary>9-slice kenar payları, piksel. Sahne bu sayıları okumaz; importer yazar.</summary>
    private const int CardCorner = 30;
    private const int PillCorner = 34;

    [MenuItem("Tools/LiquidSort/Rebuild Bartender UI Art")]
    public static void RebuildFromMenu()
    {
        EnsureUiArt(true);
        Debug.Log("LiquidSort: Bartender UI art regenerated in " + UiArtFolder);
    }

    /// <summary>
    /// Eksik olan her görseli üretir. <paramref name="force"/> hepsini yeniden yazar.
    /// </summary>
    public static void EnsureUiArt(bool force = false)
    {
        EnsureFolder(UiArtFolder);
        bool stale = force || EditorPrefs.GetInt(VersionKey, 0) != ArtVersion;

        // Kart gövdesi: yumuşak köşeli, dolu beyaz. Renk Image.color'dan gelir, dosyadan
        // değil — aynı dosya hem krem kartı hem yeşil tamamlanmış kartı çizer.
        Write(CardPanelPath, stale, 128, 128, (p, half) =>
            RoundedRect(p, half - new Vector2(2f, 2f), CardCorner));

        // Kart kenarı: aynı köşe yarıçapı, yalnız çerçeve. Eşleşme yanınca boyanır.
        Write(CardEdgePath, stale, 128, 128, (p, half) =>
            Ring(RoundedRect(p, half - new Vector2(4f, 4f), CardCorner - 2), 7f));

        // Kart içi kırpma maskesi: kartın köşelerini taşan hiçbir şey görünmesin diye.
        Write(CardClipPath, stale, 128, 128, (p, half) =>
            RoundedRect(p, half - new Vector2(8f, 8f), CardCorner - 6));

        // Kapsül: level rozeti ve sayaç arka planı.
        Write(PillPath, stale, 128, 72, (p, half) =>
            RoundedRect(p, half - new Vector2(2f, 2f), PillCorner));

        AssetDatabase.Refresh();
        ConfigureSliced(CardPanelPath, CardCorner);
        ConfigureSliced(CardEdgePath, CardCorner);
        ConfigureSliced(CardClipPath, CardCorner);
        ConfigureSliced(PillPath, PillCorner);
        EditorPrefs.SetInt(VersionKey, ArtVersion);
    }

    /// <summary>
    /// Bir bardak profilinin iç boşluk maskesini kart görselinin kullanabileceği bir
    /// sprite asset'ine çevirir ve maskenin ön görsel dikdörtgeni içindeki normalize
    /// yerini döner.
    ///
    /// Maske profilde <see cref="VesselProfile.QuadRect"/>'e rasterlenmiş hâlde duruyor
    /// ama alt-asset olduğu için bir Image'e verilemez; PNG'ye yazmak onu sıradan,
    /// sanatçının açıp bakabileceği bir sprite yapar.
    /// </summary>
    public static Sprite EnsureInteriorMaskSprite(VesselProfile profile, string fileName,
                                                  out Rect interiorRectInFront)
    {
        interiorRectInFront = new Rect(0.2f, 0.1f, 0.6f, 0.6f);
        if (profile == null || profile.front == null) return null;

        EnsureFolder(OrderIconFolder);
        string path = OrderIconFolder + "/" + fileName + ".png";

        Texture2D mask = profile.interiorMask;
        if (mask == null)
            throw new InvalidOperationException(
                profile.name + " has no baked interior mask; bake the profile first.");

        if (!File.Exists(ToAbsolute(path)))
        {
            byte[] png = EncodeMask(mask);
            File.WriteAllBytes(ToAbsolute(path), png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
        ConfigureSimple(path);

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null)
            throw new InvalidOperationException("Could not import interior mask " + path);

        interiorRectInFront = NormalizedQuadInFront(profile);
        return sprite;
    }

    /// <summary>
    /// İç boşluk dikdörtgeninin ön görselin TAM dikdörtgeni içindeki 0..1 yeri.
    ///
    /// Ölçü sprite'ın pivotundan ve piksel-birim oranından hesaplanır, mesh'inden
    /// değil: uGUI <c>Image</c> bir sprite'ı çizerken tam dikdörtgeni RectTransform'a
    /// eşler, sıkı mesh'i yalnız onun içine oturtur. İkisini karıştırmak maskeyi
    /// bardağın birkaç piksel yanına kaydırırdı.
    /// </summary>
    public static Rect NormalizedQuadInFront(VesselProfile profile)
    {
        Sprite front = profile.front;
        float ppu = Mathf.Max(0.0001f, front.pixelsPerUnit);
        Vector2 pivotUnits = front.pivot / ppu;
        var fullLocal = new Rect(-pivotUnits.x, -pivotUnits.y,
            front.rect.width / ppu, front.rect.height / ppu);
        Rect quad = profile.QuadRect;
        return new Rect(
            (quad.xMin - fullLocal.xMin) / fullLocal.width,
            (quad.yMin - fullLocal.yMin) / fullLocal.height,
            quad.width / fullLocal.width,
            quad.height / fullLocal.height);
    }

    // ---- Rasterizer -------------------------------------------------------------

    /// <summary>
    /// Signed distance in pixels for a point measured from the image centre.
    /// Negative is inside. <c>half</c> is half the image size, so one shape
    /// definition serves every size the caller asks for.
    /// </summary>
    private delegate float Field(Vector2 point, Vector2 half);

    private static void Write(string path, bool overwriteExisting,
                              int width, int height, Field field)
    {
        string absolute = ToAbsolute(path);
        if (!overwriteExisting && File.Exists(absolute)) return;

        var half = new Vector2(width * 0.5f, height * 0.5f);
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            float py = y + 0.5f - half.y;
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f - half.x;
                float d = field(new Vector2(px, py), half);
                byte a = (byte)(Mathf.Clamp01(0.5f - d) * 255f);
                pixels[y * width + x] = new Color32(255, 255, 255, a);
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        try
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? ".");
            File.WriteAllBytes(absolute, texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static byte[] EncodeMask(Texture2D mask)
    {
        // Alt-asset dokusu okunabilir olarak bake edilir; yine de savunmalı kopyalanır,
        // çünkü okunamayan bir doku burada sessizce siyah bir kart üretirdi.
        try
        {
            Color32[] pixels = mask.GetPixels32();
            var copy = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32,
                false, true);
            try
            {
                copy.SetPixels32(pixels);
                copy.Apply(false, false);
                return copy.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }
        catch (UnityException exception)
        {
            throw new InvalidOperationException(
                "Interior mask texture is not readable: " + exception.Message);
        }
    }

    // ---- Şekiller ---------------------------------------------------------------

    private static float RoundedRect(Vector2 p, Vector2 half, float radius)
    {
        radius = Mathf.Min(radius, Mathf.Min(half.x, half.y));
        Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - half
                    + new Vector2(radius, radius);
        return Mathf.Min(Mathf.Max(q.x, q.y), 0f)
               + new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude - radius;
    }

    /// <summary>Turns any filled field into a band of the given thickness around its edge.</summary>
    private static float Ring(float filled, float thickness) =>
        Mathf.Abs(filled) - thickness * 0.5f;

    // ---- Import -----------------------------------------------------------------

    private static void ConfigureSliced(string path, int border)
    {
        Configure(path, importer =>
        {
            importer.spriteBorder = new Vector4(border, border, border, border);
        });
    }

    private static void ConfigureSimple(string path) => Configure(path, null);

    private static void Configure(string path, Action<TextureImporter> extra)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException("Missing generated art", path);

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.isReadable = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 512;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        settings.spritePivot = new Vector2(0.5f, 0.5f);
        // FullRect keeps the generated rect and the RectTransform in step; a tight mesh
        // would silently trim the transparent margin these shapes are measured against.
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        extra?.Invoke(importer);
        importer.SaveAndReimport();
    }

    private static void EnsureFolder(string assetFolder)
    {
        if (string.IsNullOrEmpty(assetFolder)
            || AssetDatabase.IsValidFolder(assetFolder)) return;
        int slash = assetFolder.LastIndexOf('/');
        if (slash <= 0)
            throw new InvalidOperationException("Invalid asset folder: " + assetFolder);
        string parent = assetFolder.Substring(0, slash);
        EnsureFolder(parent);
        if (string.IsNullOrEmpty(
                AssetDatabase.CreateFolder(parent, assetFolder.Substring(slash + 1))))
            throw new IOException("Could not create asset folder " + assetFolder);
    }

    private static string ToAbsolute(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
            throw new DirectoryNotFoundException("Could not resolve the Unity project root.");
        return Path.Combine(projectRoot, assetPath);
    }

    /// <summary>Loads a generated sprite, failing loudly rather than drawing nothing.</summary>
    public static Sprite Load(string path)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null)
            throw new FileNotFoundException("Missing generated UI sprite", path);
        return sprite;
    }
}
