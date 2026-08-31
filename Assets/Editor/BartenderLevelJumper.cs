using System.Collections.Generic;
using BartenderSort.Core;
using LiquidSort.Levels;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Sunum/test aracı: kampanyanın herhangi bir bölümünü normal level sunum zinciriyle
/// tek tıkla açar.
///
/// Edit modunda seçim sahneyi kirletmez: tek kullanımlık istek SessionState'te tutulur,
/// Play açılır ve controller/presenter Start sırası tamamlandıktan sonra uygulanır.
/// Play modunda etkin tur Editor-only olarak hedef slota taşınır; sahte abandon makbuzu
/// yazılmaz ve can harcanmaz. Her iki yol da controller'ın gerçek LevelLoaded / board /
/// order / state event sırasını kullanır; raf, menü ve giriş animasyonu normal çalışır.
/// </summary>
public sealed class BartenderLevelJumper : EditorWindow
{
    private const string SessionPrefix = "GlassPourMathDemo.LevelJumper.";
    private const string PendingLevelKey = SessionPrefix + "PendingLevel";
    private const string PendingSceneKey = SessionPrefix + "PendingScene";
    private const string DebugSessionKey = SessionPrefix + "DebugSession";
    private const string DebugAttemptIdKey = SessionPrefix + "DebugAttemptId";
    private const string DebugAttemptSlotKey = SessionPrefix + "DebugAttemptSlot";
    private const string LastMessageKey = SessionPrefix + "LastMessage";
    private const string LastMessageTypeKey = SessionPrefix + "LastMessageType";
    private const int NoPendingLevel = 0;
    private const double PendingTimeoutSeconds = 8d;

    private static double pendingDeadline;
    private static int pendingStartFrame;

    private BartenderLevelController controller;
    private List<BsLevel> campaign = new List<BsLevel>();
    private Vector2 scroll;
    [SerializeField]
    private int quickLevel = 1;
    private string lastMessage;
    private MessageType lastMessageType = MessageType.Info;

    [MenuItem("Tools/LiquidSort/Level Jumper")]
    private static void Open()
    {
        BartenderLevelJumper window = GetWindow<BartenderLevelJumper>();
        window.titleContent = new GUIContent("Level Jumper");
        window.minSize = new Vector2(420f, 430f);
        window.Rescan();
    }

    [InitializeOnLoadMethod]
    private static void HookPlayModeLifecycle()
    {
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        StopPendingWatch();
        if (EditorApplication.isPlaying && PendingLevel > NoPendingLevel)
            BeginPendingWatch();
    }

    private void OnEnable()
    {
        Rescan();
        lastMessage = SessionState.GetString(LastMessageKey, string.Empty);
        lastMessageType = (MessageType)Mathf.Clamp(
            SessionState.GetInt(LastMessageTypeKey, (int)MessageType.Info),
            (int)MessageType.None, (int)MessageType.Error);
    }

    // Play modunda durum her karede değişebilir; pencere kendi kendine tazelensin.
    private void OnInspectorUpdate() => Repaint();

    private static int PendingLevel =>
        SessionState.GetInt(PendingLevelKey, NoPendingLevel);

    private static void HandlePlayModeStateChanged(PlayModeStateChange change)
    {
        switch (change)
        {
            case PlayModeStateChange.EnteredPlayMode:
                if (PendingLevel > NoPendingLevel) BeginPendingWatch();
                break;

            case PlayModeStateChange.ExitingPlayMode:
                StopPendingWatch();
                CloseDebugAttempt("Play çıkışı");
                if (PendingLevel > NoPendingLevel)
                {
                    ClearPendingRequest();
                    BroadcastReport("Play modu level açılmadan kapandı.",
                        MessageType.Warning);
                }
                break;

            case PlayModeStateChange.EnteredEditMode:
                StopPendingWatch();
                if (PendingLevel > NoPendingLevel)
                {
                    ClearPendingRequest();
                    BroadcastReport("Play açılmadı; bekleyen level isteği temizlendi.",
                        MessageType.Warning);
                }
                CloseDebugAttempt("Edit moda dönüş");
                break;
        }
    }

    private static void HandleBeforeAssemblyReload()
    {
        if (EditorApplication.isPlaying)
            CloseDebugAttempt("assembly reload");
    }

    private static void BeginPendingWatch()
    {
        pendingDeadline = EditorApplication.timeSinceStartup + PendingTimeoutSeconds;
        pendingStartFrame = Time.frameCount;
        EditorApplication.update -= TryRunPendingJump;
        EditorApplication.update += TryRunPendingJump;
    }

    private static void StopPendingWatch() =>
        EditorApplication.update -= TryRunPendingJump;

    private static void TryRunPendingJump()
    {
        int levelNumber = PendingLevel;
        if (levelNumber <= NoPendingLevel)
        {
            StopPendingWatch();
            return;
        }
        if (!EditorApplication.isPlaying) return;
        bool bootstrapWaiting = EditorApplication.isCompiling
                             || EditorApplication.isUpdating
                             || Time.frameCount <= pendingStartFrame;
        if (bootstrapWaiting)
        {
            if (EditorApplication.timeSinceStartup < pendingDeadline) return;
            FinishPendingJump(false,
                "Level sunumu zamanında hazırlanamadı.", MessageType.Error, null);
            return;
        }

        string expectedScene = SessionState.GetString(PendingSceneKey, string.Empty);
        BartenderLevelController target = FindControllerInScene(expectedScene,
            out string findReason);
        if (target == null || !target.EditorLevelJumpReady)
        {
            if (EditorApplication.timeSinceStartup < pendingDeadline) return;
            FinishPendingJump(false,
                string.IsNullOrEmpty(findReason)
                    ? "Level sunumu zamanında hazırlanamadı."
                    : findReason,
                MessageType.Error, null);
            return;
        }

        bool loaded = target.EditorTryJumpToLevelNumber(
            levelNumber, out bool ownershipTouched, out string ownedAttemptId,
            out int ownedAttemptSlot, out string rejectionReason);
        ApplyDebugAttemptOwnership(ownershipTouched, ownedAttemptId,
            ownedAttemptSlot);
        if (loaded)
        {
            FinishPendingJump(true,
                "Level " + levelNumber
              + " normal giriş sunumuyla açıldı. Can ve ilerleme korundu.",
                MessageType.Info, target);
            return;
        }

        if (BartenderProgressService.Lives <= 0)
        {
            FinishPendingJump(false, rejectionReason, MessageType.Warning, target);
            return;
        }
        if (EditorApplication.timeSinceStartup < pendingDeadline) return;
        FinishPendingJump(false,
            "Level " + levelNumber + " açılamadı: " + rejectionReason,
            MessageType.Error, target);
    }

    private static BartenderLevelController FindControllerInScene(
        string expectedScene, out string rejectionReason)
    {
        rejectionReason = null;
        BartenderLevelController[] found =
            Object.FindObjectsByType<BartenderLevelController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        BartenderLevelController match = null;
        int matches = 0;
        for (int i = 0; i < found.Length; i++)
        {
            BartenderLevelController candidate = found[i];
            if (candidate == null) continue;
            string candidateScene = candidate.gameObject.scene.path;
            if (!string.IsNullOrEmpty(expectedScene)
                && !string.Equals(candidateScene, expectedScene,
                    System.StringComparison.Ordinal))
                continue;
            match = candidate;
            matches++;
        }

        if (matches == 1) return match;
        rejectionReason = matches == 0
            ? "İstek yapılan sahnede BartenderLevelController bulunamadı."
            : "İstek yapılan sahnede birden fazla BartenderLevelController var.";
        return null;
    }

    private static void FinishPendingJump(bool success, string message,
                                          MessageType type,
                                          BartenderLevelController target)
    {
        ClearPendingRequest();
        StopPendingWatch();
        if (success)
            Debug.Log("[Level Jumper] " + message, target);
        BroadcastReport(message, type);
    }

    private static void ApplyDebugAttemptOwnership(bool ownershipTouched,
                                                   string attemptId, int campaignSlot)
    {
        if (!ownershipTouched) return;
        if (string.IsNullOrEmpty(attemptId) || campaignSlot < 0)
        {
            ClearDebugAttemptOwnership();
            return;
        }

        SessionState.SetBool(DebugSessionKey, true);
        SessionState.SetString(DebugAttemptIdKey, attemptId);
        SessionState.SetInt(DebugAttemptSlotKey, campaignSlot);
    }

    private static bool CloseDebugAttempt(string context)
    {
        if (!SessionState.GetBool(DebugSessionKey, false)) return true;
        string attemptId = SessionState.GetString(DebugAttemptIdKey, string.Empty);
        int campaignSlot = SessionState.GetInt(DebugAttemptSlotKey, -1);
        if (string.IsNullOrEmpty(attemptId) || campaignSlot < 0)
        {
            Debug.LogWarning("[Level Jumper] " + context
                           + " sırasında test turu kimliği bulunamadı.");
            return false;
        }
        if (!BartenderProgressService.EditorTryDiscardActiveAttempt(
                attemptId, campaignSlot, out string rejectionReason))
        {
            Debug.LogWarning("[Level Jumper] " + context
                           + " sırasında test turu kapatılamadı: " + rejectionReason);
            return false;
        }

        ClearDebugAttemptOwnership();
        return true;
    }

    private static void ClearDebugAttemptOwnership()
    {
        SessionState.EraseBool(DebugSessionKey);
        SessionState.EraseString(DebugAttemptIdKey);
        SessionState.EraseInt(DebugAttemptSlotKey);
    }

    private static void ClearPendingRequest()
    {
        SessionState.EraseInt(PendingLevelKey);
        SessionState.EraseString(PendingSceneKey);
    }

    private static void BroadcastReport(string message, MessageType type)
    {
        SessionState.SetString(LastMessageKey, message ?? string.Empty);
        SessionState.SetInt(LastMessageTypeKey, (int)type);
        BartenderLevelJumper[] windows =
            Resources.FindObjectsOfTypeAll<BartenderLevelJumper>();
        for (int i = 0; i < windows.Length; i++)
            windows[i].SetReport(message, type);
    }

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
            if (PendingLevel > NoPendingLevel)
                Row("Bekleyen atlama", "Level " + PendingLevel);

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
                using (new EditorGUI.DisabledScope(
                           BartenderProgressService.Lives <= 0
                        || (!EditorApplication.isPlaying
                            && EditorApplication.isPlayingOrWillChangePlaymode)))
                {
                    if (GUILayout.Button(
                            EditorApplication.isPlaying ? "Şimdi Git" : "Play'de Aç",
                            GUILayout.Width(105f)))
                        Go(quickLevel);
                }
                if (PendingLevel > NoPendingLevel
                    && GUILayout.Button("İptal", GUILayout.Width(48f)))
                    CancelPendingJump();
            }

            EditorGUILayout.LabelField(
                EditorApplication.isPlaying
                    ? "Sunum kilitliyse sıraya alınır; atlama can harcamaz."
                    : "Tek tıkla Play açılır; sahne kaydı değiştirilmez.",
                EditorStyles.miniLabel);
        }
    }

    private void DrawCampaignList()
    {
        EditorGUILayout.LabelField("Kampanya", EditorStyles.boldLabel);
        bool jumpDisabled = BartenderProgressService.Lives <= 0
                         || (!EditorApplication.isPlaying
                             && EditorApplication.isPlayingOrWillChangePlaymode);
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
                using (new EditorGUI.DisabledScope(
                           jumpDisabled))
                {
                    if (GUILayout.Button(
                            EditorApplication.isPlaying ? "Git" : "Aç",
                            GUILayout.Width(60f)))
                        Go(level.Index);
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Level başlatmak için pozitif can hâlâ gerekir; Editor atlamasının kendisi can
    /// harcamaz. Doldurma, production ile aynı atomik commit ve UI eventlerini kullanır.
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
        if (controller == null)
        {
            Report("Açık sahnede BartenderLevelController bulunamadı.", MessageType.Error);
            return;
        }
        bool levelExists = false;
        for (int i = 0; i < campaign.Count; i++)
        {
            if (campaign[i] == null || campaign[i].Index != levelNumber) continue;
            levelExists = true;
            break;
        }
        if (!levelExists)
        {
            Report("Level " + levelNumber + " kampanyada yok.", MessageType.Error);
            return;
        }

        Scene scene = controller.gameObject.scene;
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
        {
            Report("Level Jumper yalnız kaydedilmiş bir gameplay sahnesinde çalışır.",
                MessageType.Error);
            return;
        }

        quickLevel = levelNumber;
        SessionState.SetInt(PendingLevelKey, levelNumber);
        SessionState.SetString(PendingSceneKey, scene.path);
        if (EditorApplication.isPlaying)
        {
            Report("Level " + levelNumber
                 + " sıraya alındı; sunum güvenli noktada değişecek.",
                MessageType.Info);
            BeginPendingWatch();
            return;
        }

        Report("Level " + levelNumber
             + " hazırlandı; Play açılıyor ve normal giriş sunumu çalışacak.",
            MessageType.Info);
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.EnterPlaymode();
    }

    private void CancelPendingJump()
    {
        if (PendingLevel <= NoPendingLevel) return;
        ClearPendingRequest();
        StopPendingWatch();
        Report("Bekleyen level atlaması iptal edildi.", MessageType.Info);
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

    private void Report(string message, MessageType type) =>
        BroadcastReport(message, type);

    private void SetReport(string message, MessageType type)
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
