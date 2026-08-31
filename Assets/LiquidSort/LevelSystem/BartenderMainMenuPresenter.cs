using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Projects campaign, economy and settings state onto the hand-authored main-menu
    /// hierarchy. It never creates visual GameObjects: every visible element is serialized
    /// in BartenderMainMenuCanvas.prefab and can be inspected or adjusted in Hierarchy.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class BartenderMainMenuPresenter : MonoBehaviour
    {
        private static readonly CultureInfo TurkishCulture =
            CultureInfo.GetCultureInfo("tr-TR");

        [Header("Scene owner")]
        [Tooltip("Boşsa aynı rig kökünde aranır.")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Serialized hierarchy")]
        [SerializeField] private GameObject menuRoot;
        [SerializeField] private RectTransform layoutAreaRect;
        [SerializeField] private RectTransform primaryActionRect;
        [SerializeField] private RectTransform noticeRect;
        [SerializeField] private GameObject settingsOverlay;

        [Header("Top HUD")]
        [SerializeField] private Text lifeCountLabel;
        [SerializeField] private Text lifeTimerLabel;
        [SerializeField] private Text coinLabel;
        [SerializeField] private Button addLifeButton;
        [SerializeField] private Button addCoinButton;
        [SerializeField] private Button settingsButton;

        [Header("Primary action")]
        [SerializeField] private Button playButton;
        [SerializeField] private Text playLabel;
        [SerializeField] private Text noticeLabel;

        [Header("Bottom navigation")]
        [SerializeField] private RectTransform bottomNavigationRect;
        [SerializeField] private Button shopButton;
        [SerializeField] private Button homeButton;
        [SerializeField] private Button recipesButton;

        [Header("Settings")]
        [SerializeField] private Button musicButton;
        [SerializeField] private Button soundButton;
        [SerializeField] private Button vibrationButton;
        [SerializeField] private Button closeSettingsButton;
        [SerializeField] private GameObject musicSlash;
        [SerializeField] private GameObject soundSlash;
        [SerializeField] private GameObject vibrationSlash;

        private BartenderLevelController subscribedController;
        private bool buttonsHooked;
        private float noticeUntil;
        private float lastLayoutHeight = float.NaN;

        public bool Visible => menuRoot != null && menuRoot.activeSelf;

        private void Awake()
        {
            ResolveController();
            // Defensive for hand-authored variants. The production scene also serializes
            // loadOnStart=false, so there is no script execution-order race.
            if (controller != null) controller.DisableAutomaticLoadAtRuntime();
        }

        private void OnEnable()
        {
            ResolveController();
            if (controller != null) controller.DisableAutomaticLoadAtRuntime();
            HookButtons();

            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
            BartenderProgressService.LivesChanged += HandleLivesChanged;
            BartenderProgressService.LifeTimerChanged += HandleLifeTimerChanged;
            BartenderProgressService.ProgressChanged += HandleProgressChanged;
            BartenderSettingsStore.SettingsChanged += HandleSettingsChanged;
            SubscribeController();

            ProjectControllerState();
            RefreshAll();
            RefreshResponsiveLayout(true);
        }

        private void OnDisable()
        {
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            BartenderProgressService.LivesChanged -= HandleLivesChanged;
            BartenderProgressService.LifeTimerChanged -= HandleLifeTimerChanged;
            BartenderProgressService.ProgressChanged -= HandleProgressChanged;
            BartenderSettingsStore.SettingsChanged -= HandleSettingsChanged;
            UnsubscribeController();
            UnhookButtons();
        }

        private void Update()
        {
            RefreshResponsiveLayout();
            if (noticeLabel != null && noticeLabel.gameObject.activeSelf
                && Time.unscaledTime >= noticeUntil)
                noticeLabel.gameObject.SetActive(false);
        }

        /// <summary>Compatibility hook for tests or hosts that replace the controller.</summary>
        public void Bind(BartenderLevelController nextController)
        {
            if (controller == nextController)
            {
                ProjectControllerState();
                RefreshAll();
                return;
            }

            UnsubscribeController();
            controller = nextController;
            if (controller != null) controller.DisableAutomaticLoadAtRuntime();
            if (isActiveAndEnabled) SubscribeController();
            ProjectControllerState();
            RefreshAll();
        }

        private void ResolveController()
        {
            if (controller != null) return;
            Transform root = transform.root;
            if (root != null)
                controller = root.GetComponentInChildren<BartenderLevelController>(true);
            if (controller == null)
                controller = FindFirstObjectByType<BartenderLevelController>(
                    FindObjectsInactive.Include);
        }

        private void SubscribeController()
        {
            if (subscribedController == controller) return;
            UnsubscribeController();
            subscribedController = controller;
            if (subscribedController != null)
                subscribedController.StateChanged += HandleControllerStateChanged;
        }

        private void UnsubscribeController()
        {
            if (subscribedController != null)
                subscribedController.StateChanged -= HandleControllerStateChanged;
            subscribedController = null;
        }

        private void HookButtons()
        {
            if (buttonsHooked) return;
            if (playButton != null) playButton.onClick.AddListener(StartGame);
            if (shopButton != null) shopButton.onClick.AddListener(ShowShopNotice);
            if (homeButton != null) homeButton.onClick.AddListener(SelectHome);
            if (recipesButton != null)
                recipesButton.onClick.AddListener(ShowRecipesNotice);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
            if (addLifeButton != null)
                addLifeButton.onClick.AddListener(ShowLifeStoreNotice);
            if (addCoinButton != null)
                addCoinButton.onClick.AddListener(ShowCoinStoreNotice);
            if (musicButton != null) musicButton.onClick.AddListener(ToggleMusic);
            if (soundButton != null) soundButton.onClick.AddListener(ToggleSound);
            if (vibrationButton != null)
                vibrationButton.onClick.AddListener(ToggleVibration);
            if (closeSettingsButton != null)
                closeSettingsButton.onClick.AddListener(CloseSettings);
            buttonsHooked = true;
        }

        private void UnhookButtons()
        {
            if (!buttonsHooked) return;
            if (playButton != null) playButton.onClick.RemoveListener(StartGame);
            if (shopButton != null) shopButton.onClick.RemoveListener(ShowShopNotice);
            if (homeButton != null) homeButton.onClick.RemoveListener(SelectHome);
            if (recipesButton != null)
                recipesButton.onClick.RemoveListener(ShowRecipesNotice);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
            if (addLifeButton != null)
                addLifeButton.onClick.RemoveListener(ShowLifeStoreNotice);
            if (addCoinButton != null)
                addCoinButton.onClick.RemoveListener(ShowCoinStoreNotice);
            if (musicButton != null) musicButton.onClick.RemoveListener(ToggleMusic);
            if (soundButton != null) soundButton.onClick.RemoveListener(ToggleSound);
            if (vibrationButton != null)
                vibrationButton.onClick.RemoveListener(ToggleVibration);
            if (closeSettingsButton != null)
                closeSettingsButton.onClick.RemoveListener(CloseSettings);
            buttonsHooked = false;
        }

        private void StartGame()
        {
            ResolveController();
            SubscribeController();
            if (controller == null)
            {
                ShowNotice("OYUN SAHNESİ HAZIR DEĞİL");
                return;
            }

            if (controller.TryStartSavedCampaign(out string rejectionReason)) return;
            ShowNotice(string.IsNullOrWhiteSpace(rejectionReason)
                ? "BÖLÜM BAŞLATILAMADI"
                : rejectionReason.ToUpper(TurkishCulture));
            RefreshAll();
        }

        private void OpenSettings()
        {
            if (settingsOverlay == null) return;
            RefreshSettings();
            settingsOverlay.SetActive(true);
        }

        private void CloseSettings()
        {
            if (settingsOverlay != null) settingsOverlay.SetActive(false);
        }

        private void ShowLifeStoreNotice() => ShowNotice("CAN MAĞAZASI YAKINDA");
        private void ShowCoinStoreNotice() => ShowNotice("JETON MAĞAZASI YAKINDA");
        private void ShowShopNotice() => ShowNotice("DÜKKAN YAKINDA");
        private void ShowRecipesNotice() => ShowNotice("TARİFLER YAKINDA");

        private void SelectHome()
        {
            if (noticeLabel != null) noticeLabel.gameObject.SetActive(false);
        }

        private void ToggleMusic()
        {
            BartenderSettingsStore.ToggleMusic();
            RefreshSettings();
        }

        private void ToggleSound()
        {
            bool wasOn = BartenderSettingsStore.SoundOn;
            if (wasOn) BsAudio.UI(BsSfx.ButtonClick);
            bool enabled = BartenderSettingsStore.ToggleSound();
            if (!wasOn && enabled) BsAudio.UI(BsSfx.ButtonClick);
            RefreshSettings();
        }

        private void ToggleVibration()
        {
            BartenderSettingsStore.ToggleVibration();
            RefreshSettings();
        }

        private void ShowNotice(string message)
        {
            if (noticeLabel == null) return;
            noticeLabel.text = message ?? string.Empty;
            noticeLabel.gameObject.SetActive(true);
            noticeUntil = Time.unscaledTime + 2.6f;
        }

        private void HandleControllerStateChanged(BartenderLevelState _) =>
            ProjectControllerState();

        private void HandleCoinsChanged(int value)
        {
            if (coinLabel != null)
                coinLabel.text = value.ToString(CultureInfo.InvariantCulture);
        }

        private void HandleLivesChanged(int value)
        {
            if (lifeCountLabel != null)
                lifeCountLabel.text = value.ToString(CultureInfo.InvariantCulture);
            RefreshLevel();
        }

        private void HandleLifeTimerChanged(TimeSpan remaining) =>
            RefreshLifeTimer(remaining);

        private void HandleProgressChanged(int _) => RefreshLevel();
        private void HandleSettingsChanged() => RefreshSettings();

        private void ProjectControllerState()
        {
            if (menuRoot == null) return;
            bool show = controller == null
                || controller.State == BartenderLevelState.Unloaded
                || controller.State == BartenderLevelState.CampaignComplete;
            menuRoot.SetActive(show);
            if (!show) CloseSettings();
            if (show) RefreshAll();
        }

        private void RefreshAll()
        {
            HandleCoinsChanged(BartenderProgressService.Coins);
            HandleLivesChanged(BartenderProgressService.Lives);
            RefreshLifeTimer(BartenderProgressService.LifeTimer);
            RefreshLevel();
            RefreshSettings();
        }

        private void RefreshLifeTimer(TimeSpan remaining)
        {
            if (lifeTimerLabel == null) return;
            if (BartenderProgressService.IsLifeFull)
            {
                lifeTimerLabel.text = "DOLU";
                return;
            }

            long totalSeconds = Math.Max(0L, (long)Math.Ceiling(remaining.TotalSeconds));
            long totalMinutes = totalSeconds / 60L;
            long seconds = totalSeconds % 60L;
            lifeTimerLabel.text = totalMinutes.ToString("00", CultureInfo.InvariantCulture)
                                + ":"
                                + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void RefreshLevel()
        {
            if (playButton == null || playLabel == null) return;
            bool complete = controller != null
                && controller.State == BartenderLevelState.CampaignComplete;
            if (complete)
            {
                playLabel.text = "TAMAMLANDI";
                playButton.interactable = false;
                return;
            }

            int levelNumber = controller != null
                ? controller.NextUnlockedLevelNumber
                : BartenderProgressService.NextUnlockedCampaignSlot + 1;
            playLabel.text = "BÖLÜM "
                           + Mathf.Max(1, levelNumber).ToString(CultureInfo.InvariantCulture);
            // Zero lives remains tappable so the timer/reason can be explained.
            playButton.interactable = true;
        }

        private void RefreshSettings()
        {
            if (musicSlash != null) musicSlash.SetActive(!BartenderSettingsStore.MusicOn);
            if (soundSlash != null) soundSlash.SetActive(!BartenderSettingsStore.SoundOn);
            if (vibrationSlash != null)
                vibrationSlash.SetActive(!BartenderSettingsStore.VibrationOn);
        }

        private void RefreshResponsiveLayout(bool force = false)
        {
            if (layoutAreaRect == null || primaryActionRect == null || noticeRect == null)
                return;

            float height = layoutAreaRect.rect.height;
            if (height <= 0f) return;
            if (!force && Mathf.Approximately(height, lastLayoutHeight)) return;
            lastLayoutHeight = height;

            bool compact = height < 1680f;
            if (bottomNavigationRect != null)
                bottomNavigationRect.localScale = Vector3.one;

            primaryActionRect.anchoredPosition = new Vector2(0f, compact ? 480f : 520f);
            primaryActionRect.sizeDelta = compact
                ? new Vector2(460f, 171f)
                : new Vector2(560f, 220f);
            noticeRect.anchoredPosition = new Vector2(0f, compact ? 600f : 670f);
        }
    }
}
