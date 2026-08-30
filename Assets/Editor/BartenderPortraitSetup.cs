using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Portre kurulumu: Game view boyutu ve oyuncu ayarlarındaki yönelim kilidi.
///
/// BartenderSort projesindeki BsGameViewUtil'den taşındı; oradaki tek sabit 720x1280
/// yerine burada ölçü parametreli ve iPhone 13 ön ayarı eklendi. GameViewSizes API'si
/// hâlâ internal olduğu için reflection zorunlu.
///
/// Yönelim ayarı dosyaya değil PlayerSettings API'sine yazılıyor. Editör açıkken
/// ProjectSettings.asset'i elle düzenlemek işe yaramaz: Unity o dosyayı bellekteki
/// kopyasından yeniden serialize eder ve düzenleme sessizce kaybolur.
/// </summary>
public static class BartenderPortraitSetup
{
    private const string MenuRoot = "Bartender Sort/Portre/";

    // iPhone 13 / 13 Pro / 14: 1170x2532, 19.5:9.
    private const int IPhone13Width = 1170;
    private const int IPhone13Height = 2532;

    [MenuItem(MenuRoot + "Game View: iPhone 13 (1170x2532)", false, 10)]
    public static void SetIPhone13()
    {
        Apply(IPhone13Width, IPhone13Height, "Bartender iPhone 13 1170x2532");
    }

    [MenuItem(MenuRoot + "Game View: 720x1280 (9:16)", false, 11)]
    public static void SetPortrait720()
    {
        Apply(720, 1280, "Bartender 720x1280");
    }

    /// <summary>
    /// Ekip projesindeki yönelim ayarlarının aynısı: yalnız portre, ters portre ve
    /// iki yatay kapalı.
    /// </summary>
    [MenuItem(MenuRoot + "Oyuncu Ayarları: portreye kilitle", false, 30)]
    public static void LockToPortrait()
    {
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.allowedAutorotateToPortrait = true;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = false;
        PlayerSettings.allowedAutorotateToLandscapeRight = false;
        AssetDatabase.SaveAssets();
        Debug.Log("[Bartender] Yönelim portreye kilitlendi.");
    }

    /// <summary>
    /// Kamerayı DEĞİŞTİRMEZ, yalnız ölçer. En-boy değişince ortografik kameranın
    /// gördüğü genişlik değişir ve elle dizilmiş raf düzeni kenarlardan taşabilir;
    /// bu rapor kararı vermeden önce sayıyı verir.
    /// </summary>
    [MenuItem(MenuRoot + "Rapor: kamera genişliği ne olur?", false, 50)]
    public static void ReportCameraWidth()
    {
        Camera camera = Camera.main;
        if (camera == null || !camera.orthographic)
        {
            Debug.LogWarning("[Bartender] Ortografik bir Main Camera bulunamadı.");
            return;
        }

        float size = camera.orthographicSize;
        float current = 2f * size * camera.aspect;
        float wanted = (float)IPhone13Width / IPhone13Height;
        float atIPhone13 = 2f * size * wanted;
        float sizeForSameWidth = current * 0.5f / wanted;

        Debug.Log($"[Bartender] Ortho size {size:0.##}\n"
                + $"  şu anki en-boy {camera.aspect:0.0000} -> görünen genişlik {current:0.00} birim\n"
                + $"  iPhone 13 en-boy {wanted:0.0000} -> görünen genişlik {atIPhone13:0.00} birim\n"
                + $"  aynı genişliği korumak için ortho size ~{sizeForSameWidth:0.00}", camera);
    }

    private static void Apply(int width, int height, string name)
    {
        if (TryApply(width, height, name, out string error))
            Debug.Log($"[Bartender] Game view {width}x{height} portreye alındı.");
        else
            Debug.LogWarning("[Bartender] Game view ayarlanamadı: " + error);
    }

    public static bool TryApply(int width, int height, string name, out string error)
    {
        error = null;
        try
        {
            Assembly editorAsm = typeof(Editor).Assembly;
            Type sizesType = editorAsm.GetType("UnityEditor.GameViewSizes");
            Type sizeType = editorAsm.GetType("UnityEditor.GameViewSize");
            Type sizeTypeEnum = editorAsm.GetType("UnityEditor.GameViewSizeType");
            Type gameViewType = editorAsm.GetType("UnityEditor.GameView");
            if (sizesType == null || sizeType == null || sizeTypeEnum == null
                || gameViewType == null)
            {
                error = "Tipler bulunamadı.";
                return false;
            }

            Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            object instance = singleton
                .GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (instance == null) { error = "GameViewSizes instance yok."; return false; }

            object group = sizesType
                .GetProperty("currentGroup", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(instance);
            if (group == null) { error = "currentGroup yok."; return false; }
            Type groupType = group.GetType();

            int index = FindIndex(groupType, group, name);
            if (index < 0)
            {
                ConstructorInfo ctor = sizeType.GetConstructor(
                    new[] { sizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                if (ctor == null) { error = "GameViewSize ctor yok."; return false; }
                // GameViewSizeType.FixedResolution = 1
                object newSize = ctor.Invoke(new[]
                {
                    Enum.ToObject(sizeTypeEnum, 1), width, height, (object)name
                });
                groupType.GetMethod("AddCustomSize")?.Invoke(group, new[] { newSize });
                index = FindIndex(groupType, group, name);
            }
            if (index < 0) { error = "Boyut eklenemedi."; return false; }

            EditorWindow win = null;
            UnityEngine.Object[] openViews = Resources.FindObjectsOfTypeAll(gameViewType);
            for (int i = 0; i < openViews.Length; i++)
            {
                // Device Simulator inherits from GameView. Selecting that window makes a
                // fixed-resolution preset appear to do nothing, so target only the real
                // Game View tab used by the source BartenderSort project.
                if (openViews[i] is EditorWindow candidate
                    && candidate.GetType() == gameViewType)
                {
                    win = candidate;
                    break;
                }
            }
            if (win == null)
                win = EditorWindow.GetWindow(gameViewType, false, null, false);
            if (win == null) { error = "Game view penceresi yok."; return false; }
            MethodInfo setSize = gameViewType.GetMethod("SizeSelectionCallback",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (setSize == null) { error = "SizeSelectionCallback yok."; return false; }
            setSize.Invoke(win, new object[] { index, null });
            win.Repaint();
            win.Focus();
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static int FindIndex(Type groupType, object group, string name)
    {
        int total = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null);
        MethodInfo getSize = groupType.GetMethod("GetGameViewSize", new[] { typeof(int) });
        for (int i = 0; i < total; i++)
        {
            object size = getSize.Invoke(group, new object[] { i });
            string text = size.GetType().GetProperty("baseText")?.GetValue(size) as string;
            if (text == name) return i;
        }
        return -1;
    }
}
