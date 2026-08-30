using System.IO;
using GlassPourDemo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The first pass demo, kept for comparison. <see cref="GlassPourSceneBinder"/> now owns
/// <c>Assets/Scenes/GlassPourDemo.unity</c> and builds it on the LiquidSort glasses, so
/// this one no longer creates the scene on import: it would race the binder for the same
/// file. Use the menu item below to go back to the GlassVessel version on purpose.
/// </summary>
public static class GlassPourDemoBuilder
{
    private const string ScenePath = "Assets/Scenes/GlassPourDemo.unity";
    private const string FrameFxPath = "Assets/Art/GlassFront.png";

    [MenuItem("Tools/Glass Pour Demo/Rebuild With Old GlassVessel")]
    public static void RebuildDemo() => BuildDemo(true);

    [MenuItem("Tools/Glass Pour Demo/Validate Liquid Math")]
    public static void ValidateMath()
    {
        float full = PolygonFillMath.Area(GlassVessel.InteriorPolygon);
        float halfY = PolygonFillMath.FindWaterline(GlassVessel.InteriorPolygon, 0.5f);
        float half = PolygonFillMath.Area(PolygonFillMath.ClipBelowY(GlassVessel.InteriorPolygon, halfY));
        var enclosingQuad = new[]
        {
            new Vector2(-3f, -3f), new Vector2(3f, -3f),
            new Vector2(3f, 3f), new Vector2(-3f, 3f)
        };
        float intersection = PolygonFillMath.Area(
            PolygonFillMath.IntersectConvex(enclosingQuad, GlassVessel.InteriorPolygon));
        float halfError = Mathf.Abs(half / full - 0.5f);
        float clipError = Mathf.Abs(intersection / full - 1f);
        Debug.Assert(halfError < 0.000001f, "Liquid area solver drifted.");
        Debug.Assert(clipError < 0.000001f, "Convex cap clipping drifted.");
        Debug.Log(
            $"Glass math OK — full area: {full:F4}, half error: {halfError:F7}, " +
            $"cap clip error: {clipError:F7}");
    }

    private static void BuildDemo(bool openScene)
    {
        ConfigureTexture();
        Directory.CreateDirectory("Assets/Scenes");
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cameraObject = new GameObject("Main Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        camera.orthographic = true;
        camera.orthographicSize = 4.3f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.015f, 0.02f, 0.10f, 1f);
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);

        var controllerObject = new GameObject("GlassPourDemo");
        var controller = controllerObject.AddComponent<GlassPourController>();
        controller.frameFx = AssetDatabase.LoadAssetAtPath<Sprite>(FrameFxPath);

        EditorSceneManager.SaveScene(scene, ScenePath);
        var buildScenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
            EditorBuildSettings.scenes);
        buildScenes.RemoveAll(entry => entry.path == ScenePath);
        buildScenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = buildScenes.ToArray();
        AssetDatabase.SaveAssets();
        if (openScene) EditorSceneManager.OpenScene(ScenePath);
        Debug.Log("Glass Pour Demo hazır. GlassPourDemo sahnesini açıp Play'e basın.");
    }

    private static void ConfigureTexture()
    {
        var importer = AssetImporter.GetAtPath(FrameFxPath) as TextureImporter;
        if (importer == null) return;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 512f;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }
}
