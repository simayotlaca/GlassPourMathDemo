using System.Collections.Generic;
using BartenderSort.Core;
using LiquidSort.Levels;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Test aracı: kampanyanın herhangi bir bölümüne tek tıkla gitmek için.
///
/// İki ayrı yol vardır, çünkü controller'ın kendi kuralları ikisini ayırır:
///
/// - Play modunda DEĞİLKEN "Başlat", sahnedeki controller'ın startingLevelNumber /
///   resumeSavedProgress / loadOnStart alanlarını yazar. Play'e basıldığında oyun o
///   bölümden açılır.
/// - Play modundayken "Git", etkin turu kapatıp istenen bölümü yükler. Controller etkin
///   bir tur sonuçlanmadan başka bölüm yüklemeyi reddettiği için önce duraklatıp terk
///   etmek gerekir; bu da oyunun kendi sözleşmesi gereği BİR CAN harcar. Araç bunu
///   gizlemez, can sayısını üstte gösterir.
///
/// resumeSavedProgress açıkken controller yalnız kayıtlı açık bölümden başlamaya izin
/// verir, yani serbest atlama imkânsızdır. Her iki yol da bu alanı kapatır; Play modunda
/// yapılan değişiklik Play bitince kendiliğinden geri döner.
/// </summary>
public sealed class BartenderLevelJumper : EditorWindow
{
    private const string ResumeField = "resumeSavedProgress";
    private const string StartingLevelField = "startingLevelNumber";
    private const string LoadOnStartField = "loadOnStart";

    private BartenderLevelController controller;
    private List<BsLevel> campaign = new List<BsLevel>();
    private Vector2 scroll;
    private int quickLevel = 1;
    private string lastMessage;
    private MessageType lastMessageType = MessageType.Info;

    [MenuItem("Tools/LiquidSort/Level Jumper")]
    private static void Open()
    {
        BartenderLevelJumper window = GetWindow<BartenderLevelJumper>();
        window.titleContent = new GUIContent("Level Jumper");
        window.minSize = new Vector2(380f, 340f);
        window.Rescan();
    }

    private void OnEnable() => Rescan();

    // Play modunda durum her karede değişebilir; pencere kendi kendine tazelensin.
    private void OnInspectorUpdate() => Repaint();

    private void Rescan()
    {
        controller = FindFirstObjectByType<BartenderLevelController>(
            FindObjectsInactive.Include);

        BsLevel[] found = Resources.LoadAll<BsLevel>("Levels");
        campaign = new List<BsLevel>(found);
        campaign.RemoveAll(level => level == null);
        campaign.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    private void OnGUI()
    {
        DrawHeader();
        if (controller == null)
        {
            EditorGUILayout.HelpBox(
                "Açık sahnede BartenderLevelController yok. Level'lı sahneyi aç "
              + "(Assets/LiquidSort/SortingShelfShowcase.unity) ve Yenile'ye bas.",
                MessageType.Warning);
            return;
        }

        DrawStatus();
        DrawQuickJump();
        DrawCampaignList();
        DrawLifeTools();

        if (!string.IsNullOrEmpty(lastMessage))
            EditorGUILayout.HelpBox(lastMessage, lastMessageType);
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(
                controller != null ? controller.gameObject.name : "controller bulunamadı",
                EditorStyles.boldLabel);
            if (GUILayout.Button("Yenile", GUILayout.Width(70f))) Rescan();
        }
        EditorGUILayout.Space(2f);
    }

    private void DrawStatus()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Durum", EditorStyles.boldLabel);
            Row("Oyun durumu", controller.State.ToString());
            Row("Açık level", controller.CurrentLevel != null
                ? controller.CurrentLevel.Index + "  (slot "
                  + controller.CurrentCampaignSlot + ")"
                : "-");
            Row("Kayıtlı açık level", controller.NextUnlockedLevelNumber.ToString());
            Row("Can", BartenderProgressService.Lives + " / "
                     + BartenderProgressService.MaxLives);
            Row("Coin", BartenderProgressService.Coins.ToString());
            Row("Kampanya", campaign.Count + " level");

            if (BartenderProgressService.Lives <= 0)
                EditorGUILayout.HelpBox(
                    "Can 0. Bu haldeyken hiçbir bölüm yüklenemez — aşağıdan canı doldur.",
                    MessageType.Warning);
        }
    }

    private void DrawQuickJump()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Hızlı git", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                quickLevel = Mathf.Max(1, EditorGUILayout.IntField("Level no", quickLevel));
                if (GUILayout.Button(EditorApplication.isPlaying ? "Git" : "Başlat",
                        GUILayout.Width(110f)))
                    Go(quickLevel);
            }

            EditorGUILayout.LabelField(
                EditorApplication.isPlaying
                    ? "Etkin tur terk edilir — bir can harcar."
                    : "Play'e basınca bu bölümden açılır.",
                EditorStyles.miniLabel);
        }
    }

    private void DrawCampaignList()
    {
        EditorGUILayout.LabelField("Kampanya", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < campaign.Count; i++)
        {
            BsLevel level = campaign[i];
            int glasses = level.Glasses != null ? level.Glasses.Count : 0;
            int columns = Mathf.Max(1, level.ColumnsPerRow);
            int rows = Mathf.Max(2, Mathf.CeilToInt(glasses / (float)columns));
            bool isCurrent = controller.CurrentLevel == level;

            using (new EditorGUILayout.HorizontalScope(
                       isCurrent ? EditorStyles.helpBox : GUIStyle.none))
            {
                EditorGUILayout.LabelField(
                    (isCurrent ? "> " : "   ") + "Level " + level.Index,
                    GUILayout.Width(90f));
                EditorGUILayout.LabelField(
                    glasses + " bardak - " + columns + " sutun - " + rows + " sira",
                    EditorStyles.miniLabel);
                if (GUILayout.Button(EditorApplication.isPlaying ? "Git" : "Başlat",
                        GUILayout.Width(60f)))
                    Go(level.Index);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Atlamak can harcadığı için can bitince araç kendi kendini kilitler. Doldurma
    /// işini BartenderProgressService'in kendi editor API'si yapar: üretimle aynı atomik
    /// commit kullanıldığı için Play modunda ekranlar da anında güncellenir.
    /// </summary>
    private void DrawLifeTools()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Can", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(BartenderProgressService.IsLifeFull))
                {
                    if (GUILayout.Button("Canı doldur")) RefillLives();
                }
                if (GUILayout.Button("+1 can")) GrantOneLife();
            }
            EditorGUILayout.LabelField(
                BartenderProgressService.LifeTimer > System.TimeSpan.Zero
                    ? "Sonraki can: " + BartenderProgressService.LifeTimer.ToString(@"mm\:ss")
                    : "Can dolu.",
                EditorStyles.miniLabel);
        }
    }

    // ---- Eylemler -----------------------------------------------------------------

    private void Go(int levelNumber)
    {
        if (EditorApplication.isPlaying) JumpNow(levelNumber);
        else ApplyStartingLevel(levelNumber);
    }

    /// <summary>
    /// Play modu dışı yol: Play'e basıldığında controller'ın hangi bölümü açacağını
    /// belirleyen üç alanı yazar. resumeSavedProgress kapatılmazsa controller kayıtlı
    /// açık bölümden başlar ve buradaki sayı sessizce yok sayılır.
    /// </summary>
    private void ApplyStartingLevel(int levelNumber)
    {
        var serialized = new SerializedObject(controller);
        SerializedProperty starting = serialized.FindProperty(StartingLevelField);
        SerializedProperty resume = serialized.FindProperty(ResumeField);
        SerializedProperty loadOnStart = serialized.FindProperty(LoadOnStartField);
        if (starting == null || resume == null || loadOnStart == null)
        {
            Report("Controller alanları bulunamadı; script değişmiş olabilir.",
                MessageType.Error);
            return;
        }

        starting.intValue = levelNumber;
        resume.boolValue = false;
        loadOnStart.boolValue = true;
        serialized.ApplyModifiedProperties();

        Report("Level " + levelNumber + " başlangıç bölümü olarak ayarlandı. "
             + "Play'e bas. (Sahneyi kaydetmen gerekir.)", MessageType.Info);
    }

    /// <summary>
    /// Play modu yolu. Sıra, controller'ın kabul ettiği tek sıradır: etkin tur önce
    /// duraklatılıp terk edilmeli, terminal durum önce boşaltılmalı, ancak ondan sonra
    /// serbest bir slot yüklenebilir.
    /// </summary>
    private void JumpNow(int levelNumber)
    {
        // Duraklatmayı biz yaptıysak bunu hatırla: terk etme reddedilirse oyunu
        // duraklatılmış bırakmak, kullanıcının bulduğu durumu bozmak olur.
        bool pausedByTool = false;
        if (controller.State == BartenderLevelState.Playing)
        {
            if (!controller.Pause())
            {
                Report("Tur duraklatılamadı; sunum kilidi açık olabilir, biraz sonra dene.",
                    MessageType.Warning);
                return;
            }
            pausedByTool = true;
        }

        if (controller.State == BartenderLevelState.Paused)
        {
            if (!controller.TryAbandonToMainMenu(out string abandonReason))
            {
                if (pausedByTool) controller.Resume();
                Report("Etkin tur kapatılamadı: " + abandonReason, MessageType.Warning);
                return;
            }
        }
        else if (controller.State != BartenderLevelState.Unloaded)
        {
            controller.UnloadLevel();
            if (controller.State != BartenderLevelState.Unloaded)
            {
                Report("Bölüm boşaltılamadı (durum: " + controller.State
                     + "). Sonuç ekranı kapanınca tekrar dene.", MessageType.Warning);
                return;
            }
        }

        SetBool(ResumeField, false);

        if (!controller.LoadLevelNumber(levelNumber))
        {
            Report("Level " + levelNumber + " yüklenemedi. Can 0 olabilir ya da bu numara "
                 + "kampanyada yok — Console gerekçeyi yazar.", MessageType.Warning);
            return;
        }

        Report("Level " + levelNumber + " yüklendi.", MessageType.Info);
    }

    private void RefillLives()
    {
        if (BartenderProgressService.EditorRefillLives(out string rejectionReason))
        {
            Report("Can dolduruldu.", MessageType.Info);
            return;
        }
        Report("Can doldurulamadı: " + rejectionReason, MessageType.Warning);
    }

    private void GrantOneLife()
    {
        int next = BartenderProgressService.Lives + 1;
        if (next > BartenderProgressService.MaxLives)
        {
            Report("Can zaten dolu.", MessageType.Info);
            return;
        }
        if (BartenderProgressService.EditorSetLives(next, out string rejectionReason))
        {
            Report("Can " + next + " oldu.", MessageType.Info);
            return;
        }
        Report("Can eklenemedi: " + rejectionReason, MessageType.Warning);
    }

    // ---- Yardımcılar --------------------------------------------------------------

    private void SetBool(string field, bool value)
    {
        var serialized = new SerializedObject(controller);
        SerializedProperty property = serialized.FindProperty(field);
        if (property == null || property.boolValue == value) return;
        property.boolValue = value;
        serialized.ApplyModifiedProperties();
    }

    private void Report(string message, MessageType type)
    {
        lastMessage = message;
        lastMessageType = type;
        Repaint();
    }

    private static void Row(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(140f));
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
        }
    }
}
