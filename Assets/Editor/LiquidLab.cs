using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LiquidSort;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Offscreen preview renderer, so a glass can be looked at without opening a scene,
/// entering play mode or touching anything the editor already has open.
///
/// It watches <c>Temp/liquidlab.req</c> and, when that file appears, builds a throwaway
/// rig far outside the current scene, renders one PNG per requested pose and deletes the
/// request. Nothing it creates is saveable, so the open scene never goes dirty.
///
/// Request file, one directive per line:
///   out=&lt;absolute directory&gt;
///   width=560            pixel width of every shot
///   height=760           pixel height
///   ortho=2.2            camera half height in world units
///   bg=0B1030            clear colour
///   art=Assets/Art/GlassFront.png
///   set=brimHeadroom:0.08   any public float/bool on LiquidBottle or BottleShell
///   shot=&lt;name&gt;;&lt;capacity&gt;;&lt;volume&gt;;&lt;angleDeg&gt;;&lt;#hex,#hex,... bottom to top&gt;
///
/// A <c>set</c> applies to every shot after it, so a sweep is one request file.
/// </summary>
[InitializeOnLoad]
public static class LiquidLab
{
    private const string RequestPath = "Temp/liquidlab.req";
    private const string DonePath = "Temp/liquidlab.done";
    // Unity clears Temp before command-line entry points run, so batch hand-off lives at
    // the project root and is deleted immediately after it is consumed.
    private const string BatchRequestPath = ".liquidlab.batch.req";
    private const string BatchDonePath = ".liquidlab.batch.done";

    /// Bumped by hand whenever this file changes, so a reply can be told apart from one
    /// the previous assembly answered before the editor got round to recompiling.
    private const string BuildStamp = "s13";

    static LiquidLab() => EditorApplication.update += Poll;

    /// <summary>
    /// Renders the same request format from a headless Unity invocation. Keeping this path
    /// separate from the editor watcher prevents the update callback from consuming a CI
    /// request before <c>-executeMethod LiquidLab.RunBatchRequest</c> is reached.
    /// </summary>
    public static void RunBatchRequest()
    {
        if (!File.Exists(BatchRequestPath))
            throw new FileNotFoundException("LiquidLab batch request not found", BatchRequestPath);

        string[] lines = File.ReadAllLines(BatchRequestPath);
        File.Delete(BatchRequestPath);
        Run(lines);
        File.WriteAllText(BatchDonePath, "ok " + BuildStamp + "\n" + Report);
    }

    /// <summary>
    /// SessionState survives an assembly/domain reload, unlike a static bool. Keeping the
    /// refresh marker there prevents the still-present request from starting another
    /// refresh as soon as Unity recreates this type.
    /// </summary>
    private const string RefreshPendingSessionKey =
        "GlassPourMathDemo.LiquidLab.RefreshPending";

    private static void Poll()
    {
        if (!File.Exists(RequestPath))
        {
            if (SessionState.GetBool(RefreshPendingSessionKey, false))
                SessionState.EraseBool(RefreshPendingSessionKey);
            return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        if (!SessionState.GetBool(RefreshPendingSessionKey, false))
        {
            SessionState.SetBool(RefreshPendingSessionKey, true);
            AssetDatabase.Refresh();
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(RequestPath); }
        catch (IOException) { return; }   // still being written

        try { File.Delete(RequestPath); }
        catch (IOException) { return; }
        catch (System.UnauthorizedAccessException) { return; }
        SessionState.EraseBool(RefreshPendingSessionKey);

        try { Run(lines); }
        catch (System.Exception e) { File.WriteAllText(DonePath, "error: " + e); return; }

        File.WriteAllText(DonePath, "ok " + BuildStamp + "\n" + Report);
    }

    private struct Shot
    {
        public string name;
        public int capacity;
        public float volume;
        public float angle;
        public Color[] colors;
        public List<KeyValuePair<string, string>> overrides;
    }

    private static readonly System.Text.StringBuilder Report = new System.Text.StringBuilder();

    private static void Run(string[] lines)
    {
        Report.Clear();
        string outDir = "Temp/liquidlab";
        int width = 560, height = 760;
        float ortho = 2.2f;
        Color background = Hex("0B1030");
        string artPath = "Assets/Art/GlassFront.png";
        string profilePath = null;
        var shots = new List<Shot>();
        var pending = new List<KeyValuePair<string, string>>();

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int split = line.IndexOf('=');
            if (split < 0) continue;
            string key = line.Substring(0, split).Trim();
            string value = line.Substring(split + 1).Trim();

            switch (key)
            {
                case "out": outDir = value; break;
                case "width": width = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "height": height = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "ortho": ortho = Number(value); break;
                case "bg": background = Hex(value); break;
                case "art": artPath = value; break;
                case "profile": profilePath = value; break;
                case "bake": BakeProfile(value); break;
                case "set":
                {
                    int colon = value.IndexOf(':');
                    if (colon > 0)
                        pending.Add(new KeyValuePair<string, string>(
                            value.Substring(0, colon).Trim(), value.Substring(colon + 1).Trim()));
                    break;
                }
                case "shot":
                {
                    Shot shot = ParseShot(value);
                    shot.overrides = new List<KeyValuePair<string, string>>(pending);
                    shots.Add(shot);
                    break;
                }
            }
        }

        Directory.CreateDirectory(outDir);
        Sprite art = AssetDatabase.LoadAssetAtPath<Sprite>(artPath);
        if (art == null) throw new System.IO.FileNotFoundException("no sprite at " + artPath);

        VesselProfile profile = string.IsNullOrEmpty(profilePath)
            ? null
            : AssetDatabase.LoadAssetAtPath<VesselProfile>(profilePath);
        if (!string.IsNullOrEmpty(profilePath) && profile == null)
            throw new System.IO.FileNotFoundException("no profile at " + profilePath);

        // Far enough out that nothing in the open scene can wander into frame.
        var origin = new Vector3(5000f, 5000f, 0f);
        var rig = new GameObject("LiquidLabRig") { hideFlags = HideFlags.HideAndDontSave };
        rig.transform.position = origin;

        var cameraGo = new GameObject("LiquidLabCamera") { hideFlags = HideFlags.HideAndDontSave };
        cameraGo.transform.position = origin + new Vector3(0f, 0f, -10f);
        var camera = cameraGo.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = ortho;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = background;
        camera.enabled = false;

        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 8,
            hideFlags = HideFlags.HideAndDontSave
        };

        try
        {
            foreach (Shot shot in shots)
            {
                var go = new GameObject("LabGlass") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(rig.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.Euler(0f, 0f, shot.angle);

                var bottle = go.AddComponent<LiquidBottle>();
                bottle.capacity = Mathf.Max(1, shot.capacity);
                bottle.profile = profile;
                if (profile == null) bottle.glassArt = art;
                bottle.sortingOrder = 1;

                var shell = go.AddComponent<BottleShell>();
                if (profile != null) shell.backOverride = profile.back;
                shell.drawNeck = false;
                // Off, so a preview renders the same path the game does. The stroke is
                // recoloured by the contour material now; the CPU repaint would read the
                // source texture back and needs Read/Write, which the art no longer has.
                shell.restyleLine = false;

                if (shot.overrides != null)
                    foreach (var pair in shot.overrides) Apply(bottle, shell, pair.Key, pair.Value);

                bottle.SetUnits(shot.colors);
                bottle.DisplayVolume = shot.volume < 0f ? shot.colors.Length : shot.volume;

                // ExecuteAlways gives us OnEnable, but LateUpdate does not tick for a
                // freshly built object inside one editor update, so drive both by hand.
                shell.Build();
                bottle.Refresh();

                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = null;

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                var readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply();
                RenderTexture.active = previous;

                Report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: baked={7} interiorH={1:F3} bottom={2:F3} surfaceY={3:F3} brim={4:F3} visBottom={5:F3} mouthY={6:F3}",
                    shot.name, bottle.interiorHeight, bottle.interiorBottom, bottle.SurfaceWorldY - go.transform.position.y,
                    bottle.brimHeadroom, bottle.visibleBottomLocal, bottle.mouthLocal.y, bottle.Profiled));

                File.WriteAllBytes(Path.Combine(outDir, shot.name + ".png"), readback.EncodeToPNG());
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(go);
            }
        }
        finally
        {
            Object.DestroyImmediate(cameraGo);
            Object.DestroyImmediate(rig);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }

    /// <summary>
    /// "assetPath;spritePath;capacity" - creates the profile if it is not there yet,
    /// points it at the sprite and bakes it. The same thing the menu item does, driven
    /// from the request file so a sweep can rebake between shots.
    /// </summary>
    private static void BakeProfile(string value)
    {
        string[] parts = value.Split(';');
        string assetPath = parts[0].Trim();
        var profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(assetPath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VesselProfile>();
            AssetDatabase.CreateAsset(profile, assetPath);
        }

        if (parts.Length > 1)
            profile.front = AssetDatabase.LoadAssetAtPath<Sprite>(parts[1].Trim());
        if (parts.Length > 2)
            profile.capacity = int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
        if (parts.Length > 3)
            profile.back = parts[3].Trim().Length == 0
                ? null
                : AssetDatabase.LoadAssetAtPath<Sprite>(parts[3].Trim());

        VesselProfileBaker.Bake(profile);
        AssetDatabase.SaveAssets();
        Report.AppendLine($"baked {assetPath}: polygon={profile.interiorPolygon?.Length} isBaked={profile.IsBaked}");
    }

    private static Shot ParseShot(string value)
    {
        string[] parts = value.Split(';');
        var colors = new List<Color>();
        if (parts.Length > 4)
            foreach (string hex in parts[4].Split(','))
                if (hex.Trim().Length > 0) colors.Add(Hex(hex));

        return new Shot
        {
            name = parts[0].Trim(),
            capacity = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 2,
            volume = parts.Length > 2 ? Number(parts[2]) : -1f,
            angle = parts.Length > 3 ? Number(parts[3]) : 0f,
            colors = colors.ToArray()
        };
    }

    /// <summary>
    /// Writes one public field on either component, so a sweep needs no recompile.
    /// A dotted path reaches one level into a struct field — <c>lineStyle.shadeLow</c> —
    /// which is where most of the look actually lives.
    /// </summary>
    private static void Apply(LiquidBottle bottle, BottleShell shell, string path, string value)
    {
        string[] parts = path.Split('.');
        foreach (object root in new object[] { bottle, shell })
        {
            var field = root.GetType().GetField(parts[0]);
            if (field == null) continue;

            if (parts.Length == 1)
            {
                if (!Write(root, field, value)) break;
                return;
            }

            // Structs come back boxed, so the copy has to be written back afterwards.
            object container = field.GetValue(root);
            var inner = container.GetType().GetField(parts[1]);
            if (inner == null || !Write(container, inner, value)) break;
            field.SetValue(root, container);
            return;
        }
        Debug.LogWarning("LiquidLab: cannot set " + path);
    }

    private static bool Write(object target, System.Reflection.FieldInfo field, string value)
    {
        if (field.FieldType == typeof(float)) field.SetValue(target, Number(value));
        else if (field.FieldType == typeof(int)) field.SetValue(target, int.Parse(value, CultureInfo.InvariantCulture));
        else if (field.FieldType == typeof(bool)) field.SetValue(target, value == "1" || value.ToLowerInvariant() == "true");
        else if (field.FieldType == typeof(Color)) field.SetValue(target, Hex(value));
        else if (field.FieldType == typeof(Vector2))
        {
            string[] xy = value.Split(',');
            field.SetValue(target, new Vector2(Number(xy[0]), Number(xy[1])));
        }
        else return false;
        return true;
    }

    private static float Number(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Color Hex(string value)
    {
        string hex = value.Trim().TrimStart('#');
        return ColorUtility.TryParseHtmlString("#" + hex, out Color c) ? c : Color.magenta;
    }
}
