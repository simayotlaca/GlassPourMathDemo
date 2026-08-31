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
        [Tooltip("Ayar düğmelerini taşıyan şeffaf overlay kökü. Oyun duraklarken açılır.")]
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

        [Header("Eski serialize tıklama referansı")]
        [Tooltip("Sahne/prefab uyumluluğu için korunur; oynatma artık BsAudio üzerinden yapılır.")]
        [SerializeField] private AudioSource settingsAudioSource = null;
        [SerializeField] private AudioClip buttonClick = null;

        [Header("Çıkış onayı")]
        [SerializeField] private GameObject exitConfirmationCard = null;
        [SerializeField] private Button confirmExitButton = null;
        [SerializeField] private Button cancelExitButton = null;

        private readonly BsPauseOverlayStateMachine overlayFlow =
            new BsPauseOverlayStateMachine();

        private BartenderSession subscribedSession;
        private Image overlayBlocker;
        private bool buttonsHooked;

        public BsPauseOverlayState OverlayState => overlayFlow.State;
        public bool MusicOn => BartenderSettingsStore.MusicOn;
        public bool SoundOn => BartenderSettingsStore.SoundOn;
        public bool VibrationOn => BartenderSettingsStore.VibrationOn;
        public AudioSource SettingsAudioSource => settingsAudioSource;
        public AudioClip ButtonClick => buttonClick;

        /// <summary>
        /// Ayar değerleri değişti. Tercihler saklanır; ses köprüsü ayrı SFX ve BGM
        /// kanallarını bu projeksiyona göre günceller.
        /// </summary>
        public event Action SettingsChanged
        {
            add => BartenderSettingsStore.SettingsChanged += value;
            remove => BartenderSettingsStore.SettingsChanged -= value;
        }

        private void Awake()
        {
            ResolveDependencies();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            ConfigureButtonSounds();
            HookButtons();
            Subscribe();
            BartenderSettingsStore.SettingsChanged -= ApplySettingsMarks;
            BartenderSettingsStore.SettingsChanged += ApplySettingsMarks;
            ResolveOffMarks();
            ApplySettingsMarks();
            Project(session != null ? session.State : BsFlowState.Menu);
        }

        private void OnDisable()
        {
            BartenderSettingsStore.SettingsChanged -= ApplySettingsMarks;
            Unsubscribe();
            UnhookButtons();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (settingsAudioSource == null) settingsAudioSource = GetComponent<AudioSource>();
            if (overlayBlocker == null && settingsOverlay != null)
            {
                Transform blocker = settingsOverlay.transform.Find("RaycastBlocker");
                if (blocker != null) overlayBlocker = blocker.GetComponent<Image>();
            }
            if (buttonClick == null)
                buttonClick = Resources.Load<AudioClip>("Audio/SFX_ButtonClick");
        }

        private void ResolveOffMarks()
        {
            if (soundOffMark == null && soundButton != null)
                soundOffMark = FindOffMark(soundButton);
            if (musicOffMark == null && musicButton != null)
                musicOffMark = FindOffMark(musicButton);
            if (vibrationOffMark != null || vibrationButton == null) return;

            vibrationOffMark = FindOffMark(vibrationButton);
            if (vibrationOffMark != null) return;

            GameObject template = soundOffMark != null ? soundOffMark : musicOffMark;
            if (template == null) return;
            vibrationOffMark = Instantiate(template, vibrationButton.transform, false);
            vibrationOffMark.name = "MuteSlash";
            vibrationOffMark.transform.SetAsLastSibling();
            vibrationOffMark.SetActive(false);
        }

        private static GameObject FindOffMark(Button button)
        {
            Transform mark = button.transform.Find("MuteSlash");
            return mark != null ? mark.gameObject : null;
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
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.ExitConfirmation) return;

            // Vazgeç doğrudan oyuna döner. Overlay'i iyimser biçimde kapatma;
            // Resume reddedilirse onay kartı açık kalsın. Kabul edilen durum
            // değişikliği Project(Playing) üzerinden PauseEnded'i yollar.
            controller.Resume();
        }

        /// <summary>
        /// Turu abandon makbuzuyla kapatır ve bir canı tam bir kez harcar; akış oradan
        /// menüye düşer. Kayıt reddedilirse onay kartı açık kalır.
        /// </summary>
        public void ConfirmExitToMainMenu()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.ExitConfirmation) return;
            controller.TryAbandonToMainMenu(out _);
        }

        // ---- Ayar düğmeleri -------------------------------------------------------

        public void ToggleMusic()
        {
            PlaySettingsClickFallback(musicButton);
            BartenderSettingsStore.ToggleMusic();
            ApplySettingsMarks();
        }

        public void ToggleSound()
        {
            // Kapatırken mute uygulanmadan önce, açarken mute kalktıktan sonra bir kez
            // çal: kaynak oyundaki işitsel geri bildirim sırası budur.
            bool wasOn = SoundOn;
            if (wasOn) BsAudio.UI(BsSfx.ButtonClick);
            bool enabled = BartenderSettingsStore.ToggleSound();
            if (!wasOn && enabled) BsAudio.UI(BsSfx.ButtonClick);
            ApplySettingsMarks();
        }

        private void ConfigureButtonSounds()
        {
            EnsureButtonSound(pauseButton);
            EnsureButtonSound(closeButton);
            EnsureButtonSound(resumeButton);
            EnsureButtonSound(exitButton);
            EnsureButtonSound(confirmExitButton);
            EnsureButtonSound(cancelExitButton);
            EnsureButtonSound(musicButton);
            EnsureButtonSound(vibrationButton);

            if (soundButton == null) return;
            BsButtonSound feedback = BsButtonSound.Ensure(soundButton.gameObject);
            if (feedback != null) feedback.EnableClickSound = false;
        }

        private static void EnsureButtonSound(Button button)
        {
            if (button != null) BsButtonSound.Ensure(button.gameObject);
        }

        public void ToggleVibration()
        {
            PlaySettingsClickFallback(vibrationButton);
            BartenderProgressService.HardReset();
            UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }

        private static void PlaySettingsClickFallback(Button source)
        {
            if (source != null && source.GetComponent<BsButtonSound>() != null) return;
            BsAudio.UI(BsSfx.ButtonClick);
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
            if (overlayBlocker)
                overlayBlocker.color = confirmation
                    ? new Color(0.035f, 0.012f, 0.08f, 0.72f)
                    : Color.clear;
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
