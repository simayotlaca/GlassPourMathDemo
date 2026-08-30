using System.Collections.Generic;
using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns a drawing into a <see cref="VesselProfile"/>: traces the interior, rasterises
/// the mask the liquid shader clips against, and samples the two tables that used to be
/// searched for at runtime.
///
/// This is the only place any of that work happens. A built player never traces a sprite,
/// never allocates a texture and never bisects a polygon, because every answer it needs
/// is already in the asset. Adding a glass is a profile plus a bake; no C# involved.
/// </summary>
[InitializeOnLoad]
public static class VesselProfileBaker
{
    private const int AngleSteps = 97;    // 2.5 degrees apart over the tilt range
    private const int FillSteps = 65;
    private const int UprightSteps = 129;
    private const float MaxAngle = 120f;
    private const string RequestPath = "Temp/liquid-family-rebake.req";
    private const string DonePath = "Temp/liquid-family-rebake.done";
    private static bool refreshed;

    static VesselProfileBaker() => EditorApplication.update += PollRequest;

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
            BakeAllProfiles();
            string[] guids = AssetDatabase.FindAssets("t:VesselProfile");
            var report = new System.Text.StringBuilder("ok\n");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(path);
                if (profile == null || !profile.IsBaked
                    || profile.upright == null || !profile.upright.HasVisibleHeightMap)
                    throw new System.InvalidOperationException(path + " family bake is incomplete");
                report.Append(profile.name).Append(" visibleFloor=")
                    .Append(profile.visibleLiquidFloor.ToString("F6",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" visibleHeight=")
                    .Append(profile.upright.totalVisibleHeight.ToString("F6",
                        System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(DonePath, report.ToString());
        }
        catch (System.Exception exception)
        {
            File.WriteAllText(DonePath, "error\n" + exception);
            Debug.LogException(exception);
        }
    }

    [MenuItem("Tools/LiquidSort/Bake Selected Vessel Profiles %#b")]
    public static void BakeSelection()
    {
        var profiles = new List<VesselProfile>();
        foreach (Object o in Selection.objects)
            if (o is VesselProfile profile) profiles.Add(profile);

        if (profiles.Count == 0)
        {
            EditorUtility.DisplayDialog("Bake Vessel Profile",
                "Select one or more VesselProfile assets first.", "OK");
            return;
        }

        foreach (VesselProfile profile in profiles) Bake(profile);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/LiquidSort/Bake Selected Vessel Profiles %#b", true)]
    private static bool BakeSelectionEnabled()
    {
        foreach (Object o in Selection.objects)
            if (o is VesselProfile) return true;
        return false;
    }

    /// <summary>
    /// Rebuilds every vessel profile in the project. Besides being useful from the menu,
    /// the public entry point makes CI/batch validation deterministic:
    /// <c>-executeMethod VesselProfileBaker.BakeAllProfiles</c>.
    /// </summary>
    [MenuItem("Tools/LiquidSort/Bake All Vessel Profiles")]
    public static void BakeAllProfiles()
    {
        string[] guids = AssetDatabase.FindAssets("t:VesselProfile");
        int baked = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            VesselProfile profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(path);
            if (profile != null && Bake(profile)) baked++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"LiquidSort: baked {baked}/{guids.Length} vessel profiles.");
    }

    public static bool Bake(VesselProfile profile)
    {
        if (profile == null) return false;
        if (profile.front == null)
        {
            Debug.LogError($"{profile.name}: no front sprite to draw or bake optical visibility from.", profile);
            return false;
        }

        Sprite traceSource = profile.traceSource != null ? profile.traceSource : profile.front;
        Sprite opticalSource = profile.front;
        string tracePath = AssetDatabase.GetAssetPath(traceSource);
        string opticalPath = AssetDatabase.GetAssetPath(opticalSource);
        bool sharedTexture = !string.IsNullOrEmpty(tracePath)
            && string.Equals(tracePath, opticalPath, System.StringComparison.Ordinal);
        var traceImporter = AssetImporter.GetAtPath(tracePath) as TextureImporter;
        var opticalImporter = sharedTexture
            ? traceImporter
            : AssetImporter.GetAtPath(opticalPath) as TextureImporter;
        bool restoreTraceUnreadable = traceImporter != null && !traceImporter.isReadable;
        bool restoreOpticalUnreadable = !sharedTexture
            && opticalImporter != null && !opticalImporter.isReadable;
        string traceName = traceSource.name;
        string opticalName = opticalSource.name;
        try
        {
            if (restoreTraceUnreadable)
            {
                traceImporter.isReadable = true;
                traceImporter.SaveAndReimport();
            }
            if (restoreOpticalUnreadable)
            {
                // Never reaches this branch when both sprites live in the same texture;
                // one import is enough and avoids toggling the same importer twice.
                opticalImporter.isReadable = true;
                opticalImporter.SaveAndReimport();
            }

            Sprite readableTrace = traceImporter != null
                ? LoadSprite(tracePath, traceName)
                : traceSource;
            Sprite readableOptical = opticalImporter != null
                ? LoadSprite(opticalPath, opticalName)
                : opticalSource;
            if (readableTrace == null || readableOptical == null)
            {
                Debug.LogError($"{profile.name}: could not reload trace '{traceName}' "
                    + $"and front '{opticalName}' for baking.", profile);
                return false;
            }
            return BakeReadable(profile, readableTrace, readableOptical);
        }
        finally
        {
            // Neither source keeps a CPU copy in Play Mode or a player build. Reacquire
            // importers after every reimport; Unity may replace the importer instance.
            if (restoreOpticalUnreadable)
            {
                opticalImporter = AssetImporter.GetAtPath(opticalPath) as TextureImporter;
                if (opticalImporter != null)
                {
                    opticalImporter.isReadable = false;
                    opticalImporter.SaveAndReimport();
                }
            }
            if (restoreTraceUnreadable)
            {
                traceImporter = AssetImporter.GetAtPath(tracePath) as TextureImporter;
                if (traceImporter != null)
                {
                    traceImporter.isReadable = false;
                    traceImporter.SaveAndReimport();
                }
            }
        }
    }

    private static bool BakeReadable(VesselProfile profile, Sprite readableTrace,
        Sprite readableFront)
    {
        GlassInteriorFitter.Fit fit = GlassInteriorFitter.FitSprite(readableTrace,
            GlassInteriorFitter.Settings.Default);
        if (fit == null)
        {
            Debug.LogError($"{profile.name}: could not trace '{readableTrace.name}'.", profile);
            return false;
        }

        Vector2[] polygon = profile.clipRightInterior
            ? ClipRightInterior(fit.Polygon, profile.rightInteriorXAtY0,
                profile.rightInteriorSlope)
            : fit.Polygon;
        profile.interiorPolygon = polygon;
        profile.interiorBounds = PolygonBounds(polygon);
        profile.mouthLocal = fit.Mouth;
        profile.mouthHalfWidth = fit.MouthHalfWidth;
        profile.visibleBottomLocal = fit.VisibleBottom;
        profile.polygonArea = VesselFillMath.Area(polygon);

        BakeMask(profile);
        profile.upright = BakeUpright(profile, readableFront);
        profile.hasVisibleLiquidFloor = profile.upright.HasVisibleHeightMap;
        profile.visibleLiquidFloor = profile.hasVisibleLiquidFloor
            ? profile.upright.LevelAtVisibleHeight(0f)
            : profile.upright.floorY;
        profile.tilted = BakeTilted(profile);

        EditorUtility.SetDirty(profile);
        Debug.Log($"{profile.name}: baked {profile.interiorPolygon.Length} interior points, " +
                  $"{AngleSteps}x{FillSteps} tilt table, {UprightSteps} upright samples.", profile);
        return true;
    }

    private static Sprite LoadSprite(string path, string spriteName)
    {
        Sprite readable = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (readable != null && readable.name == spriteName) return readable;

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is Sprite candidate && candidate.name == spriteName)
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// A handle hole can join the main transparent cavity through antialiased pixels, so
    /// the generic flood fill correctly sees one region but incorrectly gives liquid the
    /// handle lobe as volume. A profile can opt into a sloped half-plane that follows its
    /// real body wall. Sutherland-Hodgman clipping inserts clean crossings instead of
    /// collapsing handle vertices into backtracking/zero-length edges.
    /// </summary>
    private static Vector2[] ClipRightInterior(Vector2[] source, float xAtY0, float slope)
    {
        var result = new List<Vector2>(source.Length + 2);
        Vector2 previous = source[source.Length - 1];
        float previousDistance = xAtY0 + slope * previous.y - previous.x;
        bool previousInside = previousDistance >= 0f;

        for (int i = 0; i < source.Length; i++)
        {
            Vector2 current = source[i];
            float currentDistance = xAtY0 + slope * current.y - current.x;
            bool currentInside = currentDistance >= 0f;

            if (currentInside != previousInside)
            {
                float denominator = previousDistance - currentDistance;
                float t = Mathf.Abs(denominator) > 1e-6f
                    ? Mathf.Clamp01(previousDistance / denominator)
                    : 0f;
                result.Add(Vector2.Lerp(previous, current, t));
            }
            if (currentInside) result.Add(current);

            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }
        return result.Count >= 3 ? result.ToArray() : source;
    }

    private static Rect PolygonBounds(Vector2[] polygon)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 p = polygon[i];
            minX = Mathf.Min(minX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x);
            maxY = Mathf.Max(maxY, p.y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static void BakeMask(VesselProfile profile)
    {
        string path = AssetDatabase.GetAssetPath(profile);
        if (profile.interiorMask != null)
        {
            Object.DestroyImmediate(profile.interiorMask, true);
            profile.interiorMask = null;
        }

        Texture2D mask = BottleArtFactory.MaskTexture(profile.interiorPolygon, profile.QuadRect, 160f);
        mask.name = profile.name + " Interior Mask";
        mask.hideFlags = HideFlags.None;

        AssetDatabase.AddObjectToAsset(mask, path);
        profile.interiorMask = mask;
    }

    private static VesselProfile.UprightTable BakeUpright(VesselProfile profile,
        Sprite readableFront)
    {
        Vector2[] polygon = profile.interiorPolygon;
        float area = Mathf.Max(profile.polygonArea, 1e-5f);
        VesselFillMath.VerticalExtent(polygon, out float minY, out float maxY);

        var table = new VesselProfile.UprightTable
        {
            steps = UprightSteps,
            minY = minY,
            maxY = maxY,
            areaFraction = new float[UprightSteps],
            capHalfDepth = new float[UprightSteps],
            spillAngle = new float[UprightSteps]
        };

        for (int i = 0; i < UprightSteps; i++)
        {
            float level = Mathf.Lerp(minY, maxY, i / (float)(UprightSteps - 1));
            table.areaFraction[i] = Mathf.Clamp01(VesselFillMath.AreaBelow(polygon, level) / area);

            float half = VesselFillMath.HalfWidthAt(polygon, level, out _);
            table.capHalfDepth[i] = Mathf.Min(2f * half * profile.surfaceBulge,
                profile.interiorBounds.height * profile.maxCapDepth);
        }

        BakeVisibleHeightMap(profile, readableFront, table);

        table.ceilingY = SurfaceCeiling(profile, polygon, minY, maxY);

        // The mapping floor has to sit where the liquid actually starts being drawn, and
        // that is the bottom of the interior outline. Starting it higher — at the chord
        // based "visible bottom" — hands the bottom colour every pixel between the two
        // for free: measured on this glass the floor sat at 0.058 while the outline
        // reaches -0.336, so two equal units came out 220px and 120px instead of level.
        // The taper at the bottom of a cone is not wasted space, it is where the first
        // unit lives.
        table.floorY = minY;

        // The tilt a vessel of each fill first pours at. Sampled by area fraction so the
        // runtime can look it up with the same number it already has.
        Vector2 spillMouth = profile.mouthHalfWidth > 0.0001f
            ? new Vector2(-profile.mouthHalfWidth, profile.mouthLocal.y)
            : profile.mouthLocal;
        for (int i = 0; i < UprightSteps; i++)
        {
            float fill = i / (float)(UprightSteps - 1);
            table.spillAngle[i] = VesselFillMath.SpillAngle(polygon, spillMouth, fill, MaxAngle);
        }

        return table;
    }

    /// <summary>
    /// Builds the shared family rule for how tall liquid looks through authored glass.
    /// Each row is evaluated inside its own local chord, so a transparent tapered bowl
    /// remains valid while an opaque tumbler base contributes almost no visible height.
    /// The result is stored both forward and inverse; Play Mode only interpolates arrays.
    /// </summary>
    private static void BakeVisibleHeightMap(VesselProfile profile, Sprite front,
        VesselProfile.UprightTable table)
    {
        Texture2D texture = front != null ? front.texture : null;
        if (texture == null)
            throw new System.InvalidOperationException(profile.name
                + ": front art has no texture for the visibility bake");

        Color32[] pixels = texture.GetPixels32();
        Rect spriteRect = front.rect;
        Vector2 pivot = front.pivot;
        float ppu = Mathf.Max(1e-5f, front.pixelsPerUnit);
        int count = table.steps;
        var transmission = new float[count];
        var smoothed = new float[count];

        for (int i = 0; i < count; i++)
        {
            float level = Mathf.Lerp(table.minY, table.maxY,
                i / (float)(count - 1));
            float half = VesselFillMath.HalfWidthAt(profile.interiorPolygon,
                level, out float centre);
            if (half <= 1e-5f)
            {
                transmission[i] = 0f;
                continue;
            }

            // The middle 55% of this row excludes the authored side strokes without
            // comparing a narrow row with the vessel's global widest row. That local
            // normalisation is what lets the same rule serve a coupe and a tumbler.
            float coreHalf = half * 0.55f;
            int samples = Mathf.Clamp(
                Mathf.CeilToInt(coreHalf * 2f * ppu), 5, 96);
            float sum = 0f;
            for (int sample = 0; sample < samples; sample++)
            {
                float t = (sample + 0.5f) / samples;
                float localX = Mathf.Lerp(centre - coreHalf,
                    centre + coreHalf, t);
                float pixelX = spriteRect.x + pivot.x + localX * ppu;
                float pixelY = spriteRect.y + pivot.y + level * ppu;
                sum += 1f - SampleAlphaBilinear(pixels, texture.width,
                    texture.height, spriteRect, pixelX, pixelY);
            }
            transmission[i] = Mathf.Clamp01(sum / samples);
        }

        // One tiny editor-only filter rejects a single antialiasing row at a thick base.
        // It does not affect runtime cost and keeps the cumulative map monotonic.
        smoothed[0] = transmission[0];
        smoothed[count - 1] = transmission[count - 1];
        for (int i = 1; i < count - 1; i++)
            smoothed[i] = (transmission[i - 1] + 2f * transmission[i]
                + transmission[i + 1]) * 0.25f;

        table.visibleHeight = new float[count];
        float step = (table.maxY - table.minY) / (count - 1);
        for (int i = 1; i < count; i++)
            table.visibleHeight[i] = table.visibleHeight[i - 1]
                + step * (smoothed[i - 1] + smoothed[i]) * 0.5f;

        table.totalVisibleHeight = table.visibleHeight[count - 1];
        if (table.totalVisibleHeight <= 1e-5f)
            throw new System.InvalidOperationException(profile.name
                + ": authored front art leaves no visible liquid height");

        table.levelAtVisibleFraction = new float[count];
        int upper = 1;
        for (int i = 0; i < count; i++)
        {
            float target = table.totalVisibleHeight * i / (count - 1f);
            if (i == 0)
            {
                // Skip an opaque zero-height plateau. Starting at its upper edge means
                // the first unit is never charged for pixels the player cannot see.
                while (upper < count && table.visibleHeight[upper] <= 1e-6f)
                    upper++;
                table.levelAtVisibleFraction[0] = LevelAtIndex(table,
                    Mathf.Max(0, upper - 1));
                continue;
            }

            while (upper < count - 1 && table.visibleHeight[upper] < target)
                upper++;
            int lower = Mathf.Max(0, upper - 1);
            float lowVisible = table.visibleHeight[lower];
            float highVisible = table.visibleHeight[upper];
            float blend = highVisible > lowVisible + 1e-7f
                ? Mathf.Clamp01((target - lowVisible) / (highVisible - lowVisible))
                : 0f;
            table.levelAtVisibleFraction[i] = Mathf.Lerp(
                LevelAtIndex(table, lower), LevelAtIndex(table, upper), blend);
        }
    }

    private static float LevelAtIndex(VesselProfile.UprightTable table, int index) =>
        Mathf.Lerp(table.minY, table.maxY,
            Mathf.Clamp(index, 0, table.steps - 1) / (float)(table.steps - 1));

    private static float SampleAlphaBilinear(Color32[] pixels, int width, int height,
        Rect spriteRect, float x, float y)
    {
        float maxX = Mathf.Min(width - 1f, spriteRect.xMax - 1f);
        float maxY = Mathf.Min(height - 1f, spriteRect.yMax - 1f);
        x = Mathf.Clamp(x, Mathf.Max(0f, spriteRect.xMin), maxX);
        y = Mathf.Clamp(y, Mathf.Max(0f, spriteRect.yMin), maxY);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, height - 1);
        int x1 = Mathf.Min(x0 + 1, width - 1);
        int y1 = Mathf.Min(y0 + 1, height - 1);
        float tx = x - x0;
        float ty = y - y0;

        float a00 = pixels[y0 * width + x0].a / 255f;
        float a10 = pixels[y0 * width + x1].a / 255f;
        float a01 = pixels[y1 * width + x0].a / 255f;
        float a11 = pixels[y1 * width + x1].a / 255f;
        return Mathf.Lerp(Mathf.Lerp(a00, a10, tx),
            Mathf.Lerp(a01, a11, tx), ty);
    }

    private static VesselProfile.TiltTable BakeTilted(VesselProfile profile)
    {
        Vector2[] polygon = profile.interiorPolygon;
        float area = Mathf.Max(profile.polygonArea, 1e-5f);

        var table = new VesselProfile.TiltTable
        {
            angleSteps = AngleSteps,
            fillSteps = FillSteps,
            maxAngle = MaxAngle,
            level = new float[AngleSteps * FillSteps],
            centreX = new float[AngleSteps * FillSteps],
            halfChord = new float[AngleSteps * FillSteps],
            ceilingFill = new float[AngleSteps]
        };

        var rotated = new List<Vector2>(polygon.Length);
        for (int a = 0; a < AngleSteps; a++)
        {
            float angle = Mathf.Lerp(-MaxAngle, MaxAngle, a / (float)(AngleSteps - 1));
            VesselFillMath.Rotate(polygon, angle, rotated);
            VesselFillMath.VerticalExtent(rotated, out float lowY, out float highY);
            float ceilingLevel = SurfaceCeiling(profile, rotated, lowY, highY);
            table.ceilingFill[a] = Mathf.Clamp01(VesselFillMath.AreaBelow(rotated, ceilingLevel) / area);

            for (int f = 0; f < FillSteps; f++)
            {
                float fraction = f / (float)(FillSteps - 1);
                float level = VesselFillMath.LevelForFraction(rotated, area, fraction);
                float half = VesselFillMath.HalfWidthAt(rotated, level, out float centre);

                int index = a * FillSteps + f;
                table.level[index] = level;
                table.centreX[index] = centre;
                table.halfChord[index] = half;
            }
        }

        return table;
    }

    /// <summary>
    /// Same rule the runtime used to apply every frame: the highest waterline whose top
    /// face still clears the brim, or the fixed headroom, whichever leaves more room.
    /// </summary>
    private static float SurfaceCeiling(VesselProfile profile, IList<Vector2> polygon, float minY, float maxY)
    {
        float height = profile.interiorBounds.height;
        float span = Mathf.Max(0.001f, maxY - minY);
        float limit = Mathf.Min(height * profile.maxCapDepth, span * 0.45f);
        float depth = limit;

        for (int i = 0; i < 3; i++)
        {
            float probe = Mathf.Clamp(maxY - depth, minY, maxY);
            float half = VesselFillMath.HalfWidthAt(polygon, probe, out _);
            depth = Mathf.Min(2f * half * profile.surfaceBulge, limit);
        }

        float reserved = depth * (1f - Mathf.Clamp01(profile.surfaceAllowance)) * 1.06f;
        // Measured in top-face depths, not in vessel heights. A share of height cannot be
        // consistent across a set: the same 30% leaves a thin sliver over a tall tumbler
        // and half a bowl over a squat coupe, because what the eye reads is the dark
        // crescent against the *width* of the opening. The cap depth already tracks the
        // chord, so counting the gap in cap depths keeps that crescent the same shape in
        // every vessel. brimHeadroom stays as an optional floor.
        float headroom = Mathf.Max(depth * profile.brimGapCaps, reserved);
        headroom = Mathf.Max(headroom, height * profile.brimHeadroom);
        return Mathf.Max(minY, maxY - headroom);
    }
}
