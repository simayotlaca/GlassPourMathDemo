using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LiquidSort;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Produces the neutral master-art used where liquid meets the authored glass base.
///
/// The source drawings contain an opaque navy trough at the inner floor. Because the
/// front sprite is drawn over the liquid, that trough reads as a black cut-out for every
/// liquid colour. This editor-only pass follows the trough's real alpha transition and
/// replaces only a thin depth of it with the same clean blue glass ramp as the side wall.
/// It never paints a horizontal strip and it never changes the source PNGs.
///
/// The liquid fitter classifies alpha >= 0.35 as ink. The repair preserves that boolean
/// classification for every pixel, validates the imported sprite against the original
/// fit, then re-bakes the four profiles. A failed validation restores every old profile.
/// </summary>
[InitializeOnLoad]
public static class GlassBottomAssetBaker
{
    private const string RequestPath = "Temp/glass-bottom-assets.req";
    private const string DonePath = "Temp/glass-bottom-assets.done";
    private const string MarkerPrefix = "LiquidSort.GlassBottomAssetBaker/v1;source=";
    private const byte FitterCut = 89; // round(GlassInteriorFitter.Settings.Default.alphaThreshold * 255)

    private static readonly Entry[] Entries =
    {
        // Expected floor ranges are source-texture coordinates (origin at bottom-left).
        // They came from an independent pixel inspection and act only as a guardrail;
        // the curve itself is still reconstructed from the enclosed transparent region.
        new Entry("Shot", "Assets/LiquidSort/Profiles/Shot.asset",
            "Assets/Art/ShotGlass.png", "Assets/Art/ShotGlass_v2.png",
            new RectInt(734, 232, 580, 96), new Color32(96, 154, 220, 238), 84),
        new Entry("Cocktail", "Assets/LiquidSort/Profiles/CocktailGlass.asset",
            "Assets/Art/CocktailGlassLine.png", "Assets/Art/CocktailGlassLine_v2.png",
            new RectInt(364, 737, 646, 221), new Color32(132, 190, 232, 232), 44),
        new Entry("Mug", "Assets/LiquidSort/Profiles/Mug.asset",
            "Assets/Art/MugGlass.png", "Assets/Art/MugGlass_v2.png",
            new RectInt(639, 317, 714, 104), new Color32(96, 154, 220, 238), 84),
        new Entry("Tumbler", "Assets/LiquidSort/Profiles/Tumbler.asset",
            "Assets/Art/TumblerGlass.png", "Assets/Art/TumblerGlass_v2.png",
            new RectInt(741, 220, 566, 97), new Color32(96, 154, 220, 238), 84)
    };

    private static bool refreshed;

    static GlassBottomAssetBaker() => EditorApplication.update += PollRequest;

    private readonly struct Entry
    {
        public readonly string label;
        public readonly string profilePath;
        public readonly string sourcePath;
        public readonly string outputPath;
        public readonly RectInt expectedFloorPixels;
        public readonly Color32 contactGlass;
        public readonly int paintDepthPixels;

        public Entry(string label, string profilePath, string sourcePath, string outputPath,
            RectInt expectedFloorPixels, Color32 contactGlass, int paintDepthPixels)
        {
            this.label = label;
            this.profilePath = profilePath;
            this.sourcePath = sourcePath;
            this.outputPath = outputPath;
            this.expectedFloorPixels = expectedFloorPixels;
            this.contactGlass = contactGlass;
            this.paintDepthPixels = paintDepthPixels;
        }
    }

    private sealed class Prepared
    {
        public Entry entry;
        public VesselProfile profile;
        public Sprite oldFront;
        public ProfileShape oldShape;
        public GlassInteriorFitter.Fit sourceFit;
        public RepairStats stats;
        public Sprite repairedFront;
    }

    private readonly struct ProfileShape
    {
        public readonly Vector2[] polygon;
        public readonly Rect bounds;
        public readonly float area;
        public readonly Vector2 mouth;
        public readonly float mouthHalfWidth;
        public readonly float visibleBottom;
        public readonly int points;

        public ProfileShape(VesselProfile profile)
        {
            polygon = profile.interiorPolygon != null
                ? (Vector2[])profile.interiorPolygon.Clone()
                : Array.Empty<Vector2>();
            bounds = profile.interiorBounds;
            area = profile.polygonArea;
            mouth = profile.mouthLocal;
            mouthHalfWidth = profile.mouthHalfWidth;
            visibleBottom = profile.visibleBottomLocal;
            points = profile.interiorPolygon != null ? profile.interiorPolygon.Length : 0;
        }
    }

    private struct RepairStats
    {
        public int changedPixels;
        public int strongMaskPixels;
        public int detectedColumns;
        public int candidateColumns;
        public int originalInkPixels;
        public int repairedInkPixels;
        public int classificationMismatches;
        public int sourceClassAlignmentPixels;
        public int importedClassificationMismatches;
        public int compressionRetryMismatches;
        public bool usedUncompressedFallback;
        public int edgeMinX;
        public int edgeMaxX;
        public int edgeMinY;
        public int edgeMaxY;
    }

    private static void PollRequest()
    {
        if (!File.Exists(RequestPath))
        {
            refreshed = false;
            return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating
            || EditorApplication.isPlayingOrWillChangePlaymode) return;

        // Refresh once first. This guarantees a request made immediately after this file
        // changed is served by the newly compiled assembly rather than the old one.
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
            string report = BuildAndBake();
            File.WriteAllText(DonePath, "ok\n" + report);
        }
        catch (Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    [MenuItem("Tools/LiquidSort/Rebuild Neutral Glass Bottom Assets")]
    public static void BuildAndBakeFromMenu()
    {
        try
        {
            string report = BuildAndBake();
            Debug.Log("LiquidSort: neutral glass bottom assets rebuilt.\n" + report);
            EditorUtility.DisplayDialog("Neutral Glass Bottoms",
                "Four non-destructive v2 assets were rebuilt and their profiles re-baked.\n\n"
                + report, "OK");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Neutral Glass Bottoms",
                "Nothing was linked because validation failed.\n\n" + exception.Message, "OK");
            throw;
        }
    }

    /// <summary>
    /// Deterministic entry point for the request watcher and command-line verification.
    /// The returned report includes the mask and alpha-classification evidence for every
    /// generated sprite.
    /// </summary>
    public static string BuildAndBake()
    {
        var prepared = new List<Prepared>(Entries.Length);

        // Generate and validate every output before changing a single profile reference.
        for (int i = 0; i < Entries.Length; i++)
            prepared.Add(Prepare(Entries[i]));

        var objects = new UnityEngine.Object[prepared.Count];
        for (int i = 0; i < prepared.Count; i++) objects[i] = prepared[i].profile;
        Undo.RecordObjects(objects, "Link neutral glass bottom assets");

        bool profilesTouched = false;
        try
        {
            profilesTouched = true;
            for (int i = 0; i < prepared.Count; i++)
            {
                Prepared item = prepared[i];
                item.profile.front = item.repairedFront;
                EditorUtility.SetDirty(item.profile);
                if (!VesselProfileBaker.Bake(item.profile))
                    throw new InvalidOperationException(item.entry.label
                        + ": VesselProfileBaker rejected the repaired sprite");
                ValidateProfileShape(item.entry.label, item.oldShape, item.profile);
            }

            AssetDatabase.SaveAssets();
        }
        catch
        {
            if (profilesTouched) RestoreProfiles(prepared);
            throw;
        }

        var report = new StringBuilder();
        for (int i = 0; i < prepared.Count; i++)
        {
            Prepared item = prepared[i];
            RepairStats s = item.stats;
            report.Append(item.entry.label).Append(" -> ").Append(item.entry.outputPath)
                .Append(" changed=").Append(s.changedPixels)
                .Append(" strongMask=").Append(s.strongMaskPixels)
                .Append(" edgeColumns=").Append(s.detectedColumns).Append('/')
                .Append(s.candidateColumns)
                .Append(" ink=").Append(s.originalInkPixels).Append("->")
                .Append(s.repairedInkPixels)
                .Append(" classMismatch=").Append(s.classificationMismatches)
                .Append(" sourceClassAlign=").Append(s.sourceClassAlignmentPixels)
                .Append(" importedClassMismatch=").Append(s.importedClassificationMismatches)
                .Append(" compressionRetryMismatch=").Append(s.compressionRetryMismatches)
                .Append(" uncompressedFallback=").Append(s.usedUncompressedFallback ? 1 : 0)
                .Append(" curve=x").Append(s.edgeMinX).Append("..").Append(s.edgeMaxX)
                .Append(" y").Append(s.edgeMinY).Append("..").Append(s.edgeMaxY)
                .Append(" area=").Append(item.profile.polygonArea.ToString("F6",
                    CultureInfo.InvariantCulture))
                .Append(" visibleBottom=").Append(item.profile.visibleBottomLocal.ToString("F6",
                    CultureInfo.InvariantCulture)).Append('\n');
        }
        return report.ToString();
    }

    private static Prepared Prepare(Entry entry)
    {
        VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(entry.profilePath);
        if (profile == null) throw new FileNotFoundException("No VesselProfile", entry.profilePath);
        if (!profile.IsBaked || profile.interiorPolygon == null || profile.interiorPolygon.Length < 3)
            throw new InvalidOperationException(entry.label + ": profile must be baked before repair");

        TextureImporter sourceImporter = AssetImporter.GetAtPath(entry.sourcePath) as TextureImporter;
        if (sourceImporter == null) throw new FileNotFoundException("No source texture", entry.sourcePath);
        EnsureOutputIsToolOwned(entry);

        byte[] pngBytes = null;
        GlassInteriorFitter.Fit sourceFit = null;
        RepairStats stats = default;
        bool restoreUnreadable = !sourceImporter.isReadable;
        try
        {
            if (restoreUnreadable)
            {
                sourceImporter.isReadable = true;
                sourceImporter.SaveAndReimport();
            }

            Sprite source = AssetDatabase.LoadAssetAtPath<Sprite>(entry.sourcePath);
            if (source == null) throw new InvalidOperationException(entry.label + ": source is not a Sprite");
            sourceFit = GlassInteriorFitter.FitSprite(source, GlassInteriorFitter.Settings.Default);
            if (sourceFit == null) throw new InvalidOperationException(entry.label + ": source fit failed");

            Color32[] original = LoadRawPngPixels(entry.sourcePath,
                out int pngWidth, out int pngHeight);
            if (pngWidth != source.texture.width || pngHeight != source.texture.height)
                throw new InvalidOperationException(entry.label + ": raw and imported dimensions differ");
            int sourceClassAlignmentPixels = 0;
            AlignRawFitterClassification(source.texture.GetPixels32(), original,
                ref sourceClassAlignmentPixels);
            Color32[] repaired = (Color32[])original.Clone();
            RepairPixels(entry, source, profile, original, repaired, out stats);
            stats.sourceClassAlignmentPixels = sourceClassAlignmentPixels;
            ValidateAlphaClassification(entry.label, original, repaired, ref stats);
            pngBytes = EncodePng(pngWidth, pngHeight, repaired);
        }
        finally
        {
            sourceImporter = AssetImporter.GetAtPath(entry.sourcePath) as TextureImporter;
            if (restoreUnreadable && sourceImporter != null)
            {
                sourceImporter.isReadable = false;
                sourceImporter.SaveAndReimport();
            }
        }

        WriteAtomically(entry.outputPath, pngBytes);
        sourceImporter = AssetImporter.GetAtPath(entry.sourcePath) as TextureImporter;
        ConfigureCloneImporter(sourceImporter, entry);

        ValidateImportedClassification(entry, ref stats);

        TextureImporter repairedImporter = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
        if (repairedImporter == null)
            throw new InvalidOperationException(entry.label + ": repaired texture did not import");

        repairedImporter.isReadable = true;
        repairedImporter.SaveAndReimport();
        Sprite repairedFront = AssetDatabase.LoadAssetAtPath<Sprite>(entry.outputPath);
        if (repairedFront == null)
            throw new InvalidOperationException(entry.label + ": repaired texture is not a Sprite");

        GlassInteriorFitter.Fit repairedFit = GlassInteriorFitter.FitSprite(repairedFront,
            GlassInteriorFitter.Settings.Default);
        ValidateFit(entry.label, sourceFit, repairedFit);

        repairedImporter = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
        if (repairedImporter != null)
        {
            repairedImporter.isReadable = false;
            repairedImporter.SaveAndReimport();
        }
        repairedFront = AssetDatabase.LoadAssetAtPath<Sprite>(entry.outputPath);

        return new Prepared
        {
            entry = entry,
            profile = profile,
            oldFront = profile.front,
            oldShape = new ProfileShape(profile),
            sourceFit = sourceFit,
            stats = stats,
            repairedFront = repairedFront
        };
    }

    private static void RepairPixels(Entry entry, Sprite sprite, VesselProfile profile,
        Color32[] original, Color32[] repaired, out RepairStats stats)
    {
        stats = new RepairStats
        {
            edgeMinX = int.MaxValue,
            edgeMinY = int.MaxValue,
            edgeMaxX = int.MinValue,
            edgeMaxY = int.MinValue
        };
        Texture2D texture = sprite.texture;
        int textureWidth = texture.width;
        int textureHeight = texture.height;
        Rect rect = sprite.rect;
        int rectX = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, textureWidth - 1);
        int rectY = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, textureHeight - 1);
        int rectWidth = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, textureWidth - rectX);
        int rectHeight = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, textureHeight - rectY);
        Vector2 pivot = sprite.pivot;
        float ppu = Mathf.Max(1f, sprite.pixelsPerUnit);

        bool[] interior = LargestEnclosedTransparentRegion(original, textureWidth,
            rectX, rectY, rectWidth, rectHeight);
        if (interior == null)
            throw new InvalidOperationException(profile.name + ": source has no enclosed transparent interior");

        float floor = profile.visibleBottomLocal;
        float halfWidth = VesselFillMath.HalfWidthAt(profile.interiorPolygon,
            floor + 1f / ppu, out float centreX);
        if (halfWidth <= 0.01f)
            throw new InvalidOperationException(profile.name + ": no floor span at visibleBottomLocal");

        float floorPixel = rectY + pivot.y + floor * ppu;
        int searchRadius = Mathf.Clamp(Mathf.RoundToInt(
            Mathf.Clamp(profile.interiorBounds.width * 0.075f, 0.085f, 0.19f) * ppu), 24, 112);
        int maxDepth = Mathf.Clamp(entry.paintDepthPixels, 18, 96);
        int riseDepth = Mathf.Clamp(Mathf.RoundToInt(ppu * 0.012f), 4, 6);
        int fadeStart = Mathf.Max(riseDepth + 1, Mathf.RoundToInt(maxDepth * 0.67f));

        for (int localX = 0; localX < rectWidth; localX++)
        {
            int bottomInteriorY = -1;
            for (int localY = 0; localY < rectHeight; localY++)
            {
                if (!interior[localY * rectWidth + localX]) continue;
                bottomInteriorY = localY;
                break;
            }
            if (bottomInteriorY < 1) continue;

            int x = rectX + localX;
            int boundaryY = rectY + bottomInteriorY - 1; // first classified-ink pixel below cavity
            float localXUnits = ((x + 0.5f) - rectX - pivot.x) / ppu;
            float xNormal = Mathf.Abs(localXUnits - centreX) / Mathf.Max(halfWidth, 1e-5f);
            if (xNormal > 0.98f) continue;
            stats.candidateColumns++;

            // The largest component gives the bowl; the visible-floor band removes its
            // vertical walls and, on the cocktail asset, the separate stem/base drawing.
            if (Mathf.Abs((boundaryY + 0.5f) - floorPixel) > searchRadius) continue;
            if (original[boundaryY * textureWidth + x].a < FitterCut) continue;

            stats.detectedColumns++;
            stats.edgeMinX = Mathf.Min(stats.edgeMinX, x);
            stats.edgeMaxX = Mathf.Max(stats.edgeMaxX, x);
            stats.edgeMinY = Mathf.Min(stats.edgeMinY, boundaryY);
            stats.edgeMaxY = Mathf.Max(stats.edgeMaxY, boundaryY);

            // Edit only the first uninterrupted fitted-ink run under the cavity. This is
            // the actual curved trough, never a horizontal rectangle. Transparent/AA
            // pixels are untouched and the maximum reach cannot enter the outer base.
            for (int depth = 0; depth < maxDepth; depth++)
            {
                int y = boundaryY - depth;
                if (y < rectY) break;
                int index = y * textureWidth + x;
                Color32 source = original[index];
                if (source.a < FitterCut) break;

                float rise = SmoothStep(0f, riseDepth, depth);
                float fall = 1f - SmoothStep(fadeStart, maxDepth - 1f, depth);
                float vertical = (0.82f + 0.18f * rise) * fall;
                float across = 1f - SmoothStep(0.86f, 0.98f, xNormal);
                float mask = Mathf.Clamp01(vertical * across);
                if (mask <= 1f / 255f) continue;

                // This stays inside pixels the fitter already classifies as glass, so we
                // can make the brush as solid as the side wall without changing geometry.
                byte targetAlpha = entry.contactGlass.a;
                var target = new Color32(entry.contactGlass.r, entry.contactGlass.g,
                    entry.contactGlass.b, targetAlpha);
                Color32 result = LerpRgbAndAlpha(source, target, mask * 0.90f, mask);
                repaired[index] = result;

                if (!Same(source, result)) stats.changedPixels++;
                if (mask >= 0.75f) stats.strongMaskPixels++;
            }
        }

        if (stats.detectedColumns < Mathf.Max(24, stats.candidateColumns / 4))
            throw new InvalidOperationException(profile.name + ": enclosed-floor detector found only "
                + stats.detectedColumns + "/" + stats.candidateColumns + " columns");
        ValidateMeasuredFloorRange(entry, stats);
        if (stats.changedPixels < 64 || stats.strongMaskPixels < 16)
            throw new InvalidOperationException(profile.name + ": repair mask was unexpectedly empty (changed="
                + stats.changedPixels + ", strong=" + stats.strongMaskPixels + ")");
    }

    private static bool[] LargestEnclosedTransparentRegion(Color32[] pixels, int textureWidth,
        int rectX, int rectY, int width, int height)
    {
        int length = width * height;
        var ink = new bool[length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                ink[y * width + x] = pixels[(rectY + y) * textureWidth + rectX + x].a
                    >= FitterCut;

        var outside = new bool[length];
        var queue = new int[length];
        int head = 0, tail = 0;
        void Seed(int index)
        {
            if (ink[index] || outside[index]) return;
            outside[index] = true;
            queue[tail++] = index;
        }

        for (int x = 0; x < width; x++)
        {
            Seed(x);
            Seed((height - 1) * width + x);
        }
        for (int y = 1; y < height - 1; y++)
        {
            Seed(y * width);
            Seed(y * width + width - 1);
        }
        Flood(queue, ref head, ref tail, outside, ink, width, height);

        var visited = new bool[length];
        bool[] largest = null;
        int largestCount = 0;
        for (int start = 0; start < length; start++)
        {
            if (ink[start] || outside[start] || visited[start]) continue;
            head = 0;
            tail = 0;
            visited[start] = true;
            queue[tail++] = start;
            Flood(queue, ref head, ref tail, visited, ink, width, height, outside);
            if (tail <= largestCount) continue;

            largestCount = tail;
            largest = new bool[length];
            for (int i = 0; i < tail; i++) largest[queue[i]] = true;
        }
        return largest;
    }

    private static void Flood(int[] queue, ref int head, ref int tail, bool[] marked,
        bool[] ink, int width, int height, bool[] forbidden = null)
    {
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            TryVisit(index - 1, x > 0, queue, ref tail, marked, ink, forbidden);
            TryVisit(index + 1, x + 1 < width, queue, ref tail, marked, ink, forbidden);
            TryVisit(index - width, y > 0, queue, ref tail, marked, ink, forbidden);
            TryVisit(index + width, y + 1 < height, queue, ref tail, marked, ink, forbidden);
        }
    }

    private static void TryVisit(int next, bool inBounds, int[] queue, ref int tail,
        bool[] marked, bool[] ink, bool[] forbidden)
    {
        if (!inBounds || ink[next] || marked[next]
            || (forbidden != null && forbidden[next])) return;
        marked[next] = true;
        queue[tail++] = next;
    }

    private static void ValidateMeasuredFloorRange(Entry entry, RepairStats stats)
    {
        RectInt expected = entry.expectedFloorPixels;
        int overlapMin = Mathf.Max(stats.edgeMinX, expected.xMin);
        int overlapMax = Mathf.Min(stats.edgeMaxX + 1, expected.xMax);
        float overlap = Mathf.Max(0, overlapMax - overlapMin)
            / (float)Mathf.Max(1, stats.edgeMaxX - stats.edgeMinX + 1);
        int medianY = (stats.edgeMinY + stats.edgeMaxY) / 2;
        if (overlap < 0.75f || medianY < expected.yMin || medianY >= expected.yMax)
            throw new InvalidOperationException(entry.label + ": detected floor x"
                + stats.edgeMinX + ".." + stats.edgeMaxX + " y" + stats.edgeMinY + ".."
                + stats.edgeMaxY + " does not agree with inspected range " + expected);
    }

    private static void ValidateAlphaClassification(string label, Color32[] original,
        Color32[] repaired, ref RepairStats stats)
    {
        if (original.Length != repaired.Length)
            throw new InvalidOperationException(label + ": pixel array size changed");

        for (int i = 0; i < original.Length; i++)
        {
            bool before = original[i].a >= FitterCut;
            bool after = repaired[i].a >= FitterCut;
            if (before) stats.originalInkPixels++;
            if (after) stats.repairedInkPixels++;
            if (before != after) stats.classificationMismatches++;
        }
        if (stats.classificationMismatches != 0
            || stats.originalInkPixels != stats.repairedInkPixels)
            throw new InvalidOperationException(label + ": repair changed the fitter alpha class for "
                + stats.classificationMismatches + " pixels");
    }

    private static void AlignRawFitterClassification(Color32[] imported,
        Color32[] raw, ref int changed)
    {
        if (imported.Length != raw.Length)
            throw new InvalidOperationException("Raw and imported source sizes differ");
        for (int i = 0; i < raw.Length; i++)
        {
            bool wantedInk = imported[i].a >= FitterCut;
            bool rawInk = raw[i].a >= FitterCut;
            if (wantedInk == rawInk) continue;
            Color32 pixel = raw[i];
            pixel.a = wantedInk ? (byte)104 : (byte)74;
            raw[i] = pixel;
            changed++;
        }
    }

    private static void ValidateImportedClassification(Entry entry, ref RepairStats stats)
    {
        TextureImporter sourceImporter = AssetImporter.GetAtPath(entry.sourcePath) as TextureImporter;
        TextureImporter outputImporter = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
        if (sourceImporter == null || outputImporter == null)
            throw new InvalidOperationException(entry.label + ": importers missing during alpha validation");

        bool restoreSource = !sourceImporter.isReadable;
        try
        {
            if (restoreSource)
            {
                sourceImporter.isReadable = true;
                sourceImporter.SaveAndReimport();
            }
            if (!outputImporter.isReadable)
            {
                outputImporter.isReadable = true;
                outputImporter.SaveAndReimport();
            }

            int mismatches = ImportedClassMismatches(entry);
            stats.compressionRetryMismatches = mismatches;
            if (mismatches > 0)
            {
                // A compressed source was already decompressed before the PNG was made;
                // compressing that result again can move edge alpha across 89. Keep all
                // other importer settings but store the generated master losslessly.
                outputImporter = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
                outputImporter.textureCompression = TextureImporterCompression.Uncompressed;
                outputImporter.SaveAndReimport();
                stats.usedUncompressedFallback = true;
                mismatches = ImportedClassMismatches(entry);
            }
            stats.importedClassificationMismatches = mismatches;
            if (mismatches != 0)
                throw new InvalidOperationException(entry.label + ": imported v2 changed "
                    + mismatches + " fitter alpha classes even without compression");
        }
        finally
        {
            sourceImporter = AssetImporter.GetAtPath(entry.sourcePath) as TextureImporter;
            if (restoreSource && sourceImporter != null)
            {
                sourceImporter.isReadable = false;
                sourceImporter.SaveAndReimport();
            }
        }
    }

    private static int ImportedClassMismatches(Entry entry)
    {
        Sprite source = AssetDatabase.LoadAssetAtPath<Sprite>(entry.sourcePath);
        Sprite output = AssetDatabase.LoadAssetAtPath<Sprite>(entry.outputPath);
        if (source == null || output == null || source.texture == null || output.texture == null)
            throw new InvalidOperationException(entry.label + ": sprites missing during imported comparison");
        Color32[] before = source.texture.GetPixels32();
        Color32[] after = output.texture.GetPixels32();
        if (before.Length != after.Length)
            throw new InvalidOperationException(entry.label + ": output dimensions changed");
        int mismatches = 0;
        for (int i = 0; i < before.Length; i++)
            if ((before[i].a >= FitterCut) != (after[i].a >= FitterCut)) mismatches++;
        return mismatches;
    }

    private static void ValidateFit(string label, GlassInteriorFitter.Fit before,
        GlassInteriorFitter.Fit after)
    {
        if (before == null || after == null)
            throw new InvalidOperationException(label + ": fitted geometry is missing");

        float areaBefore = VesselFillMath.Area(before.Polygon);
        float areaAfter = VesselFillMath.Area(after.Polygon);
        float areaDelta = Mathf.Abs(areaAfter - areaBefore);
        float boundsDelta = MaxRectEdgeDelta(before.Bounds, after.Bounds);
        float mouthDelta = Vector2.Distance(before.Mouth, after.Mouth);
        float mouthWidthDelta = Mathf.Abs(before.MouthHalfWidth - after.MouthHalfWidth);
        float bottomDelta = Mathf.Abs(before.VisibleBottom - after.VisibleBottom);
        float polygonDelta = MaxPolygonDelta(before.Polygon, after.Polygon);

        const float epsilon = 1e-5f;
        if (areaDelta > Mathf.Max(1e-5f, areaBefore * 1e-5f)
            || boundsDelta > epsilon || mouthDelta > epsilon
            || mouthWidthDelta > epsilon || bottomDelta > epsilon || polygonDelta > epsilon)
            throw new InvalidOperationException(label + ": imported v2 geometry drifted: area="
                + areaDelta.ToString("F7", CultureInfo.InvariantCulture)
                + " bounds=" + boundsDelta.ToString("F5", CultureInfo.InvariantCulture)
                + " mouth=" + mouthDelta.ToString("F5", CultureInfo.InvariantCulture)
                + " mouthHalf=" + mouthWidthDelta.ToString("F5", CultureInfo.InvariantCulture)
                + " bottom=" + bottomDelta.ToString("F5", CultureInfo.InvariantCulture)
                + " polygon=" + polygonDelta.ToString("F5", CultureInfo.InvariantCulture));
    }

    private static void ValidateProfileShape(string label, ProfileShape before,
        VesselProfile after)
    {
        float areaDelta = Mathf.Abs(after.polygonArea - before.area);
        float boundsDelta = MaxRectEdgeDelta(before.bounds, after.interiorBounds);
        float mouthDelta = Vector2.Distance(before.mouth, after.mouthLocal);
        float mouthWidthDelta = Mathf.Abs(before.mouthHalfWidth - after.mouthHalfWidth);
        float bottomDelta = Mathf.Abs(before.visibleBottom - after.visibleBottomLocal);
        float polygonDelta = MaxPolygonDelta(before.polygon, after.interiorPolygon);

        const float epsilon = 1e-5f;
        if (areaDelta > Mathf.Max(1e-5f, before.area * 1e-5f)
            || boundsDelta > epsilon || mouthDelta > epsilon
            || mouthWidthDelta > epsilon || bottomDelta > epsilon || polygonDelta > epsilon)
            throw new InvalidOperationException(label + ": profile re-bake drifted beyond tolerance");
    }

    private static float MaxPolygonDelta(Vector2[] before, Vector2[] after)
    {
        if (before == null || after == null || before.Length != after.Length)
            return float.PositiveInfinity;
        float maximum = 0f;
        for (int i = 0; i < before.Length; i++)
            maximum = Mathf.Max(maximum, Vector2.Distance(before[i], after[i]));
        return maximum;
    }

    private static float MaxRectEdgeDelta(Rect a, Rect b) => Mathf.Max(
        Mathf.Max(Mathf.Abs(a.xMin - b.xMin), Mathf.Abs(a.xMax - b.xMax)),
        Mathf.Max(Mathf.Abs(a.yMin - b.yMin), Mathf.Abs(a.yMax - b.yMax)));

    private static void RestoreProfiles(List<Prepared> prepared)
    {
        var errors = new StringBuilder();
        for (int i = 0; i < prepared.Count; i++)
        {
            Prepared item = prepared[i];
            try
            {
                item.profile.front = item.oldFront;
                EditorUtility.SetDirty(item.profile);
                if (!VesselProfileBaker.Bake(item.profile))
                    errors.Append(item.entry.label).Append(" rollback bake failed; ");
            }
            catch (Exception exception)
            {
                errors.Append(item.entry.label).Append(": ").Append(exception.Message).Append("; ");
            }
        }
        AssetDatabase.SaveAssets();
        if (errors.Length > 0)
            Debug.LogError("LiquidSort: profile rollback encountered errors: " + errors);
    }

    private static void EnsureOutputIsToolOwned(Entry entry)
    {
        if (!File.Exists(Path.GetFullPath(entry.outputPath))) return;
        TextureImporter importer = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
        string expected = MarkerPrefix + entry.sourcePath;
        if (importer == null || importer.userData != expected)
            throw new InvalidOperationException(entry.outputPath
                + " already exists but is not a tool-owned v2 asset; refusing to overwrite it");
    }

    private static void ConfigureCloneImporter(TextureImporter source, Entry entry)
    {
        if (source == null) throw new InvalidOperationException(entry.label + ": source importer vanished");
        AssetDatabase.ImportAsset(entry.outputPath,
            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        TextureImporter destination = AssetImporter.GetAtPath(entry.outputPath) as TextureImporter;
        if (destination == null) throw new InvalidOperationException(entry.label + ": no output importer");

        var settings = new TextureImporterSettings();
        source.ReadTextureSettings(settings);
        destination.SetTextureSettings(settings);
        destination.maxTextureSize = source.maxTextureSize;
        destination.textureCompression = source.textureCompression;
        destination.compressionQuality = source.compressionQuality;
        destination.userData = MarkerPrefix + entry.sourcePath;
        destination.isReadable = true;

        CopyPlatformSettings(source, destination, "DefaultTexturePlatform");
        CopyPlatformSettings(source, destination, "Standalone");
        CopyPlatformSettings(source, destination, "Android");
        CopyPlatformSettings(source, destination, "iPhone");
        CopyPlatformSettings(source, destination, "WebGL");
        destination.SaveAndReimport();
    }

    private static void CopyPlatformSettings(TextureImporter source,
        TextureImporter destination, string platform)
    {
        TextureImporterPlatformSettings settings = source.GetPlatformTextureSettings(platform);
        if (!string.IsNullOrEmpty(settings.name)) destination.SetPlatformTextureSettings(settings);
    }

    private static byte[] EncodePng(int width, int height, Color32[] pixels)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
        {
            name = "Neutral glass bottom encode"
        };
        try
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            byte[] bytes = texture.EncodeToPNG();
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException("Unity returned an empty PNG");
            return bytes;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static Color32[] LoadRawPngPixels(string assetPath, out int width, out int height)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
        {
            name = "Neutral glass raw source"
        };
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.GetFullPath(assetPath));
            if (!ImageConversion.LoadImage(texture, bytes, false))
                throw new InvalidOperationException("Could not decode " + assetPath);
            width = texture.width;
            height = texture.height;
            return texture.GetPixels32();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static void WriteAtomically(string assetPath, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("No PNG bytes", nameof(bytes));
        string fullPath = Path.GetFullPath(assetPath);
        string directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("No output directory");
        Directory.CreateDirectory(directory);
        string temporary = fullPath + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        try
        {
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static float Alpha(Color32[] pixels, int width, int height, int x, int y)
    {
        x = Mathf.Clamp(x, 0, width - 1);
        y = Mathf.Clamp(y, 0, height - 1);
        return pixels[y * width + x].a / 255f;
    }

    private static float Luminance(Color32 pixel) =>
        (0.299f * pixel.r + 0.587f * pixel.g + 0.114f * pixel.b) / 255f;

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Mathf.Clamp01((value - from) / Mathf.Max(to - from, 1e-5f));
        return t * t * (3f - 2f * t);
    }

    private static Color32 Lerp(Color32 from, Color32 to, float amount)
    {
        amount = Mathf.Clamp01(amount);
        return new Color32(
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.r, to.r, amount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.g, to.g, amount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.b, to.b, amount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.a, to.a, amount)));
    }

    private static Color32 LerpRgbAndAlpha(Color32 from, Color32 to,
        float rgbAmount, float alphaAmount)
    {
        rgbAmount = Mathf.Clamp01(rgbAmount);
        alphaAmount = Mathf.Clamp01(alphaAmount);
        return new Color32(
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.r, to.r, rgbAmount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.g, to.g, rgbAmount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.b, to.b, rgbAmount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(from.a, to.a, alphaAmount)));
    }

    private static bool Same(Color32 a, Color32 b) =>
        a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
}
