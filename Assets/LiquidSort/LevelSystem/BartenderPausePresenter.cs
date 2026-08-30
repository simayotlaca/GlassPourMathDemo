using System;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Oyun ekranındaki dişli düğmesi ve arkasındaki ayarlar kartı.
    ///
    /// İki FSM birlikte çalışır ve karışmazlar:
    ///   BartenderSession  — oyun duruyor mu (Playing / Paused)
    ///   BsPauseOverlayStateMachine — durduğunda hangi kart görünüyor
    ///                                (Settings / ExitConfirmation)
    ///
    /// OPTİMİSTİK UI YOK. Dişliye basmak kartı açmaz; yalnızca controller'a Pause
    /// komutunu yollar. Kart, komut kabul edilip durum değişikliği geri geldiğinde
    /// açılır. Reddedilen bir pause'da (örneğin sunum kilidi tutulurken) ekran hiç
    /// kıpırdamaz — yarım açılmış bir kart kalmaz.
    ///
    /// Bütün görsel referanslar opsiyoneldir: art gelmeden de bileşen ayakta durur ve
    /// bağlı olan neyse onu yönetir.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderPausePresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderSession session;
        [Tooltip("Boşsa session üzerinden çözülür. Pause/Resume komutlarının sahibi.")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Oyun ekranı")]
        [Tooltip("Dişli. Yalnız oyun oynanırken görünür ve tıklanabilir.")]
        [SerializeField] private Button pauseButton = null;

        [Header("Ayarlar kartı")]
        [Tooltip("Karartma dahil bütün overlay kökü. Oyun duraklarken açılır.")]
        [SerializeField] private GameObject settingsOverlay = null;
        [Tooltip("Kartın kendisi. Overlay ile aynı obje ise boş bırakılabilir.")]
        [SerializeField] private GameObject settingsCard = null;
        [SerializeField] private Button closeButton = null;
        [SerializeField] private Button resumeButton = null;
        [SerializeField] private Button exitButton = null;

        [Header("Ayar düğmeleri")]
        [SerializeField] private Button musicButton = null;
        [SerializeField] private Button soundButton = null;
        [SerializeField] private Button vibrationButton = null;
        [Tooltip("Kapalıyken açılan üstü çizili işaret. Opsiyonel.")]
        [SerializeField] private GameObject musicOffMark = null;
        [SerializeField] private GameObject soundOffMark = null;
        [SerializeField] private GameObject vibrationOffMark = null;

        [Header("Çıkış onayı")]
        [SerializeField] private GameObject exitConfirmationCard = null;
        [SerializeField] private Button confirmExitButton = null;
        [SerializeField] private Button cancelExitButton = null;

        private const string MusicKey = "LiquidSort.Bartender.Settings.Music";
        private const string SoundKey = "LiquidSort.Bartender.Settings.Sound";
        private const string VibrationKey = "LiquidSort.Bartender.Settings.Vibration";

        private readonly BsPauseOverlayStateMachine overlayFlow =
            new BsPauseOverlayStateMachine();

        private BartenderSession subscribedSession;
        private bool buttonsHooked;

        public BsPauseOverlayState OverlayState => overlayFlow.State;
        public bool MusicOn { get; private set; } = true;
        public bool SoundOn { get; private set; } = true;
        public bool VibrationOn { get; private set; } = true;

        /// <summary>
        /// Ayar değerleri değişti. Bu projede henüz ses sistemi yok; değer saklanıyor
        /// ve yayımlanıyor, tüketen tarafı sonra bağlanacak.
        /// </summary>
        public event Action SettingsChanged;

        private void Awake()
        {
            ResolveDependencies();
            LoadSettings();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            HookButtons();
            Subscribe();
            ApplySettingsMarks();
            Project(session != null ? session.State : BsFlowState.Menu);
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnhookButtons();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        // ---- Komutlar -------------------------------------------------------------

        /// <summary>
        /// Dişli. Komutu yollar, kartı AÇMAZ — kart durum değişikliğiyle açılır.
        /// </summary>
        public void OpenPauseMenu()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Playing
                || overlayFlow.State != BsPauseOverlayState.Closed) return;
            controller.Pause();
        }

        /// <summary>X ve Devam aynı Paused -> Playing komutuna gider.</summary>
        public void ResumeGameplay()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.Settings) return;
            controller.Resume();
        }

        /// <summary>Kart içi geçiş: oyun durumu değişmez, yalnız görünen kart değişir.</summary>
        public void RequestExitToMainMenu()
        {
            if (session == null || session.State != BsFlowState.Paused) return;
            if (!overlayFlow.Dispatch(BsPauseOverlayTrigger.ExitRequested)) return;
            ApplyProjection(BsFlowState.Paused);
        }

        public void CancelExitToMainMenu()
        {
            if (session == null || session.State != BsFlowState.Paused) return;
            if (!overlayFlow.Dispatch(BsPauseOverlayTrigger.ExitCancelled)) return;
            ApplyProjection(BsFlowState.Paused);
        }

        /// <summary>
        /// Levelı boşaltır; akış oradan menüye düşer. Komut reddedilirse onay kartı
        /// açık kalır, çünkü durum değişikliği hiç gelmez.
        /// </summary>
        public void ConfirmExitToMainMenu()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.ExitConfirmation) return;
            controller.UnloadLevel();
        }

        // ---- Ayar düğmeleri -------------------------------------------------------

        public void ToggleMusic()
        {
            MusicOn = !MusicOn;
            PlayerPrefs.SetInt(MusicKey, MusicOn ? 1 : 0);
            CommitSettings();
        }

        public void ToggleSound()
        {
            SoundOn = !SoundOn;
            PlayerPrefs.SetInt(SoundKey, SoundOn ? 1 : 0);
            CommitSettings();
        }

        public void ToggleVibration()
        {
            VibrationOn = !VibrationOn;
            PlayerPrefs.SetInt(VibrationKey, VibrationOn ? 1 : 0);
            CommitSettings();
        }

        private void CommitSettings()
        {
            PlayerPrefs.Save();
            ApplySettingsMarks();
            SettingsChanged?.Invoke();
        }

        private void LoadSettings()
        {
            MusicOn = PlayerPrefs.GetInt(MusicKey, 1) != 0;
            SoundOn = PlayerPrefs.GetInt(SoundKey, 1) != 0;
            VibrationOn = PlayerPrefs.GetInt(VibrationKey, 1) != 0;
        }

        private void ApplySettingsMarks()
        {
            if (musicOffMark) musicOffMark.SetActive(!MusicOn);
            if (soundOffMark) soundOffMark.SetActive(!SoundOn);
            if (vibrationOffMark) vibrationOffMark.SetActive(!VibrationOn);
        }

        // ---- Projeksiyon ----------------------------------------------------------

        private void Subscribe()
        {
            if (subscribedSession == session) return;
            Unsubscribe();
            subscribedSession = session;
            if (subscribedSession != null) subscribedSession.FlowChanged += HandleFlowChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedSession != null) subscribedSession.FlowChanged -= HandleFlowChanged;
            subscribedSession = null;
        }

        private void HandleFlowChanged(BsTransitionResult transition) =>
            Project(transition.To);

        private void Project(BsFlowState state)
        {
            if (state == BsFlowState.Paused)
            {
                if (overlayFlow.State == BsPauseOverlayState.Closed)
                    overlayFlow.Dispatch(BsPauseOverlayTrigger.PauseAccepted);
            }
            else if (overlayFlow.State != BsPauseOverlayState.Closed)
            {
                overlayFlow.Dispatch(BsPauseOverlayTrigger.PauseEnded);
            }

            ApplyProjection(state);
        }

        private void ApplyProjection(BsFlowState state)
        {
            bool playing = state == BsFlowState.Playing;
            bool paused = state == BsFlowState.Paused;
            bool settings = paused && overlayFlow.State == BsPauseOverlayState.Settings;
            bool confirmation = paused
                                && overlayFlow.State == BsPauseOverlayState.ExitConfirmation;

            if (pauseButton)
            {
                pauseButton.interactable = playing;
                pauseButton.gameObject.SetActive(playing);
            }

            if (settingsOverlay) settingsOverlay.SetActive(paused);
            if (settingsCard && settingsCard != settingsOverlay)
                settingsCard.SetActive(settings);
            if (exitConfirmationCard) exitConfirmationCard.SetActive(confirmation);

            SetInteractable(closeButton, settings);
            SetInteractable(resumeButton, settings);
            SetInteractable(exitButton, settings);
            SetInteractable(musicButton, settings);
            SetInteractable(soundButton, settings);
            SetInteractable(vibrationButton, settings);
            SetInteractable(confirmExitButton, confirmation);
            SetInteractable(cancelExitButton, confirmation);
        }

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button) button.interactable = interactable;
        }

        // ---- Düğme bağlama --------------------------------------------------------

        private void HookButtons()
        {
            if (buttonsHooked) return;
            if (pauseButton) pauseButton.onClick.AddListener(OpenPauseMenu);
            if (closeButton) closeButton.onClick.AddListener(ResumeGameplay);
            if (resumeButton) resumeButton.onClick.AddListener(ResumeGameplay);
            if (exitButton) exitButton.onClick.AddListener(RequestExitToMainMenu);
            if (confirmExitButton)
                confirmExitButton.onClick.AddListener(ConfirmExitToMainMenu);
            if (cancelExitButton)
                cancelExitButton.onClick.AddListener(CancelExitToMainMenu);
            // Kaynak projede bu üçü hiçbir şeye bağlı değildi, yalnız interactable
            // yapılıyordu. Burada bağlandılar; değeri saklanıyor, tüketeni sonra gelecek.
            if (musicButton) musicButton.onClick.AddListener(ToggleMusic);
            if (soundButton) soundButton.onClick.AddListener(ToggleSound);
            if (vibrationButton) vibrationButton.onClick.AddListener(ToggleVibration);
            buttonsHooked = true;
        }

        private void UnhookButtons()
        {
            if (!buttonsHooked) return;
            if (pauseButton) pauseButton.onClick.RemoveListener(OpenPauseMenu);
            if (closeButton) closeButton.onClick.RemoveListener(ResumeGameplay);
            if (resumeButton) resumeButton.onClick.RemoveListener(ResumeGameplay);
            if (exitButton) exitButton.onClick.RemoveListener(RequestExitToMainMenu);
            if (confirmExitButton)
                confirmExitButton.onClick.RemoveListener(ConfirmExitToMainMenu);
            if (cancelExitButton)
                cancelExitButton.onClick.RemoveListener(CancelExitToMainMenu);
            if (musicButton) musicButton.onClick.RemoveListener(ToggleMusic);
            if (soundButton) soundButton.onClick.RemoveListener(ToggleSound);
            if (vibrationButton) vibrationButton.onClick.RemoveListener(ToggleVibration);
            buttonsHooked = false;
        }
    }
}
