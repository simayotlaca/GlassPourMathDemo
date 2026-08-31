using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Orthogonal FTUE state machine. It never mutates the board or the round FSM: accepted
    /// controller receipts advance steps, while the central interaction policy only narrows
    /// which otherwise-legal player command can be attempted.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderTutorialDirector : MonoBehaviour, IBartenderInputPolicy
    {
        private const string SequenceResourcePath = "Tutorials/RoyalFirstPour";
        private const string FallbackTutorialId = "royal_first_pour";
        private const int FallbackVersion = 2;
        private const float CompletionWatchdogSeconds = 3.5f;
        private const string DefaultCompletionEyebrow = "";
        private const string DefaultCompletionTitle = "HAZIRSIN!";
        private const string DefaultCompletionDetail = "";

        [SerializeField] private BartenderPourInteraction interaction;
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private BartenderSession session;
        [SerializeField] private BartenderTutorialOverlayView overlay;

        private readonly List<BartenderTutorialStep> activeSteps =
            new List<BartenderTutorialStep>(8);

        private BartenderLevelController subscribedController;
        private BartenderPourInteraction subscribedInteraction;
        private BartenderTutorialOverlayView subscribedOverlay;
        private BartenderTutorialSequence authoredSequence;
        private string activeTutorialId;
        private string completionEyebrow = DefaultCompletionEyebrow;
        private string completionTitle = DefaultCompletionTitle;
        private string completionDetail = DefaultCompletionDetail;
        private int activeVersion;
        private int stepIndex = -1;
        private bool active;
        private bool waitingForPresentation;
        private bool completionPending;
        private bool completing;
        private bool visualSuspended;
        private float completionDeadline;
        private BsRoundToken roundToken;
        private bool hasRoundToken;

        public bool Active => active;
        public bool BlocksTerminalPresentation =>
            active && (completionPending || completing);
        public int CurrentStepIndex => stepIndex;
        public int StepCount => activeSteps.Count;

        private BartenderTutorialStep CurrentStep =>
            stepIndex >= 0 && stepIndex < activeSteps.Count
                ? activeSteps[stepIndex]
                : null;

        private void Awake()
        {
            ResolveDependencies();
            authoredSequence = Resources.Load<BartenderTutorialSequence>(
                SequenceResourcePath);
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            TryStartForCurrentLevel();
        }

        private void OnDisable()
        {
            Unsubscribe();
            StopTutorial();
        }

        private void LateUpdate()
        {
            BartenderPourInteraction previousInteraction = interaction;
            BartenderLevelController previousController = controller;
            BartenderShelfLevelView previousShelf = shelfView;
            BartenderSession previousSession = session;
            BartenderTutorialOverlayView previousOverlay = overlay;
            ResolveDependencies();
            Subscribe();

            bool dependenciesChanged = !ReferenceEquals(previousInteraction, interaction)
                || !ReferenceEquals(previousController, controller)
                || !ReferenceEquals(previousShelf, shelfView)
                || !ReferenceEquals(previousSession, session)
                || !ReferenceEquals(previousOverlay, overlay);
            if (dependenciesChanged)
            {
                if (active) StopTutorial();
                TryStartForCurrentLevel();
                return;
            }

            if (!active) return;
            if (interaction == null)
            {
                StopTutorial();
                return;
            }
            if (overlay == null || !overlay.isActiveAndEnabled)
            {
                StopTutorial();
                return;
            }
            if (!ReferenceEquals(interaction.InputPolicy, this)
                && !interaction.TrySetInputPolicy(this))
            {
                StopTutorial();
                return;
            }

            bool terminalCompletion = completionPending || completing;
            if (completing && completionDeadline > 0f
                && Time.unscaledTime >= completionDeadline)
            {
                // Fail open if a third party kills the completion tween without callbacks.
                StopTutorial();
                return;
            }
            if (!terminalCompletion && !TryCaptureOrValidateRoundToken())
            {
                StopTutorial();
                return;
            }

            bool stateCanPresent = controller != null
                && (controller.State == BartenderLevelState.Playing
                    || (terminalCompletion
                        && (controller.State == BartenderLevelState.Won
                            || controller.State == BartenderLevelState.Failed)));
            if (!stateCanPresent)
                return;

            if (visualSuspended)
            {
                visualSuspended = false;
                waitingForPresentation = true;
            }

            BartenderTutorialStep step = CurrentStep;
            if (!waitingForPresentation && !completing && step != null
                && step.Action == BartenderTutorialAction.PourIntoBottle
                && interaction != null
                && interaction.SelectedGlassId != step.PrimaryGlassId)
            {
                int selectStep = FindPreviousSelectStep(stepIndex, step.PrimaryGlassId);
                if (selectStep >= 0)
                {
                    stepIndex = selectStep;
                    waitingForPresentation = true;
                    overlay?.HideImmediate();
                }
            }

            if (!waitingForPresentation || !PresentationReady()) return;
            waitingForPresentation = false;

            if (completionPending)
            {
                completing = true;
                completionDeadline = Time.unscaledTime + CompletionWatchdogSeconds;
                overlay.ShowCompletion(completionEyebrow, completionTitle,
                    completionDetail, HandleCompletionAnimationFinished);
                return;
            }

            ShowCurrentStep(true);
        }

        public bool Allows(BartenderInputRequest request, out string rejectionReason)
        {
            rejectionReason = null;
            if (!active) return true;
            if (waitingForPresentation || completing || visualSuspended)
            {
                rejectionReason = "Bir saniye, servis hazırlanıyor.";
                return false;
            }

            BartenderTutorialStep step = CurrentStep;
            if (step == null)
            {
                rejectionReason = "Eğitim adımı hazırlanıyor.";
                return false;
            }

            switch (step.Action)
            {
                case BartenderTutorialAction.SelectBottle:
                    if (request.Intent == BartenderInputIntent.BottleTap
                        && request.PrimaryGlassId == step.PrimaryGlassId)
                        return true;
                    rejectionReason = "Önce parlayan bardağı seç.";
                    return false;

                case BartenderTutorialAction.PourIntoBottle:
                    if (request.Intent == BartenderInputIntent.BottleTap)
                    {
                        bool allowedTap = request.PrimaryGlassId == step.SecondaryGlassId
                            && request.SelectedGlassId == step.PrimaryGlassId;
                        if (allowedTap) return true;
                    }
                    else if (request.Intent == BartenderInputIntent.Pour
                        && request.PrimaryGlassId == step.PrimaryGlassId
                        && request.SecondaryGlassId == step.SecondaryGlassId)
                        return true;
                    rejectionReason = "Seçili içeceği parlayan boş bardağa dök.";
                    return false;

                case BartenderTutorialAction.DeliverBottle:
                    if (request.Intent == BartenderInputIntent.BottleTap
                        && request.PrimaryGlassId == step.PrimaryGlassId)
                        return true;
                    if (request.Intent == BartenderInputIntent.Delivery
                        && request.PrimaryGlassId == step.PrimaryGlassId)
                        return true;
                    rejectionReason = "Hazır siparişi göndermek için parlayan bardağa dokun.";
                    return false;

                default:
                    rejectionReason = "Parlayan hedefi takip et.";
                    return false;
            }
        }

        public void HandleRejected(BartenderInputRequest request, string rejectionReason)
        {
            if (!active || completing) return;
            overlay?.Nudge();
        }

        /// <summary>Useful for a replay button or an editor console without touching saves.</summary>
        public static void ResetRoyalFirstPourProgress()
        {
            BartenderTutorialProgress.Reset(FallbackTutorialId, FallbackVersion);
        }

        [ContextMenu("Reset Royal Tutorial Progress")]
        private void ResetProgressFromContextMenu()
        {
            string id = string.IsNullOrEmpty(activeTutorialId)
                ? FallbackTutorialId
                : activeTutorialId;
            int version = activeVersion > 0 ? activeVersion : FallbackVersion;
            BartenderTutorialProgress.Reset(id, version);
            StopTutorial();
            TryStartForCurrentLevel();
        }

        private void TryStartForCurrentLevel()
        {
            ResolveDependencies();
            Subscribe();
            if (!isActiveAndEnabled || controller == null || interaction == null
                || shelfView == null || session == null || controller.CurrentLevel == null)
                return;

            StopTutorial();
            if (!TryLoadSequenceForCurrentLevel()) return;
            if (BartenderTutorialProgress.IsCompleted(activeTutorialId, activeVersion))
                return;
            if (!ValidateSequence(out string validationReason))
            {
                Debug.LogWarning("[BartenderTutorial] " + validationReason, this);
                activeSteps.Clear();
                return;
            }
            if (!interaction.TrySetInputPolicy(this))
            {
                Debug.LogWarning(
                    "[BartenderTutorial] Başka bir modal input policy etkin; eğitim açılmadı.",
                    this);
                activeSteps.Clear();
                return;
            }

            interaction.ClearSelectionForModal();
            active = true;
            stepIndex = 0;
            waitingForPresentation = true;
            completionPending = false;
            completing = false;
            visualSuspended = false;
            completionDeadline = 0f;
            hasRoundToken = false;
            overlay.HideImmediate();
            TryCaptureOrValidateRoundToken();
        }

        private bool TryLoadSequenceForCurrentLevel()
        {
            activeSteps.Clear();
            activeTutorialId = null;
            activeVersion = 0;
            completionEyebrow = DefaultCompletionEyebrow;
            completionTitle = DefaultCompletionTitle;
            completionDetail = DefaultCompletionDetail;

            if (authoredSequence != null && authoredSequence.Matches(controller)
                && authoredSequence.Steps != null && authoredSequence.Steps.Count > 0)
            {
                activeTutorialId = authoredSequence.TutorialId;
                activeVersion = authoredSequence.Version;
                completionEyebrow = CopyOrDefault(authoredSequence.CompletionEyebrow,
                    DefaultCompletionEyebrow);
                completionTitle = CopyOrDefault(authoredSequence.CompletionTitle,
                    DefaultCompletionTitle);
                completionDetail = CopyOrDefault(authoredSequence.CompletionDetail,
                    DefaultCompletionDetail);
                for (int i = 0; i < authoredSequence.Steps.Count; i++)
                {
                    BartenderTutorialStep step = authoredSequence.Steps[i];
                    if (step != null) activeSteps.Add(step.Clone());
                }
                return activeSteps.Count > 0;
            }

            if (controller.CurrentCampaignSlot != 0 || controller.CurrentLevel.Index != 1)
                return false;

            activeTutorialId = FallbackTutorialId;
            activeVersion = FallbackVersion;
            activeSteps.Add(new BartenderTutorialStep
            {
                StepId = "select_red_coupe",
                Action = BartenderTutorialAction.SelectBottle,
                PrimaryGlassId = 3,
                SecondaryGlassId = -1,
                Eyebrow = "KRALİYET DERSİ",
                Title = "DOLU KADEHE DOKUN",
                Detail = "Üstteki kırmızıyı servis için ayıralım.",
            });
            activeSteps.Add(new BartenderTutorialStep
            {
                StepId = "pour_into_shot",
                Action = BartenderTutorialAction.PourIntoBottle,
                PrimaryGlassId = 3,
                SecondaryGlassId = 1,
                Eyebrow = "USTA DÖKÜŞÜ",
                Title = "BOŞ SHOT'A DÖK",
                Detail = "Seçili kadehten parlayan shot bardağına dokun.",
            });
            activeSteps.Add(new BartenderTutorialStep
            {
                StepId = "deliver_shot",
                Action = BartenderTutorialAction.DeliverBottle,
                PrimaryGlassId = 1,
                SecondaryGlassId = -1,
                Eyebrow = "KRALİYET SERVİSİ",
                Title = "SİPARİŞİ GÖNDER",
                Detail = "Hazır shot'a tekrar dokun ve servisi tamamla!",
            });
            return true;
        }

        private static string CopyOrDefault(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private bool ValidateSequence(out string reason)
        {
            reason = null;
            if (activeSteps.Count == 0)
            {
                reason = "Tutorial sequence boş.";
                return false;
            }

            BsBoard simulated = controller.Board;
            if (simulated == null)
            {
                reason = "Tutorial doğrulaması için board snapshot alınamadı.";
                return false;
            }
            if (simulated.IsWin() || simulated.IsFail())
            {
                reason = "Tutorial terminal durumdaki bir board üzerinde başlatılamaz.";
                return false;
            }

            int simulatedSelection = -1;
            for (int i = 0; i < activeSteps.Count; i++)
            {
                BartenderTutorialStep step = activeSteps[i];
                RtGlass primary = step != null
                    ? simulated.GlassById(step.PrimaryGlassId)
                    : null;
                if (step == null || primary == null)
                {
                    reason = $"Adım {i + 1} geçersiz primary glass id kullanıyor.";
                    return false;
                }

                switch (step.Action)
                {
                    case BartenderTutorialAction.SelectBottle:
                        if (simulatedSelection >= 0)
                        {
                            reason = $"Adım {i + 1}: yeni seçimden önce önceki seçim tüketilmedi.";
                            return false;
                        }
                        if (!CanSelectAsSource(simulated, primary))
                        {
                            reason = $"Adım {i + 1}: hedef seçilebilir bir kaynak değil.";
                            return false;
                        }
                        if (simulated.MatchedSlot(primary) >= 0)
                        {
                            reason = $"Adım {i + 1}: hazır sipariş pointer tarafından seçilmez, teslim edilir.";
                            return false;
                        }
                        simulatedSelection = primary.Id;
                        break;

                    case BartenderTutorialAction.PourIntoBottle:
                        RtGlass target = simulated.GlassById(step.SecondaryGlassId);
                        if (target == null || target.Id == primary.Id)
                        {
                            reason = $"Adım {i + 1}: pour target id geçersiz.";
                            return false;
                        }
                        if (simulatedSelection != primary.Id)
                        {
                            reason = $"Adım {i + 1}: kaynak önceki seçim adımıyla seçilmedi.";
                            return false;
                        }
                        if (simulated.MatchedSlot(target) >= 0)
                        {
                            reason = $"Adım {i + 1}: pour hedefi pointer tarafından teslimata yönlenir.";
                            return false;
                        }
                        PourResult pour = simulated.Pour(primary, target);
                        if (!pour.Success)
                        {
                            reason = $"Adım {i + 1}: beklenen dökme yasal değil ({pour.Reason}).";
                            return false;
                        }
                        simulatedSelection = -1;
                        break;

                    case BartenderTutorialAction.DeliverBottle:
                        if (simulated.MatchedSlot(primary) < 0
                            || !simulated.Deliver(primary, out _))
                        {
                            reason = $"Adım {i + 1}: hedef bardak bu noktada teslim edilemiyor.";
                            return false;
                        }
                        simulatedSelection = -1;
                        break;

                    default:
                        reason = $"Adım {i + 1}: bilinmeyen tutorial aksiyonu.";
                        return false;
                }

                bool finalStep = i == activeSteps.Count - 1;
                bool simulatedWin = simulated.IsWin();
                bool simulatedFail = simulated.IsFail();
                if (!finalStep && (simulatedWin || simulatedFail))
                {
                    reason = $"Adım {i + 1}: tur tutorial bitmeden terminal duruma giriyor.";
                    return false;
                }
                if (finalStep && simulatedFail)
                {
                    reason = $"Adım {i + 1}: tutorial başarı yerine başarısız tur üretiyor.";
                    return false;
                }
            }
            return true;
        }

        private static bool CanSelectAsSource(BsBoard board, RtGlass glass)
        {
            return board != null && glass != null && !glass.IsEmpty
                && !glass.IsChained(board.Delivered)
                && glass.TopChainLength(board.Delivered) > 0;
        }

        private void ShowCurrentStep(bool celebratePrevious)
        {
            BartenderTutorialStep step = CurrentStep;
            if (step == null)
            {
                StopTutorial();
                return;
            }

            int targetId = step.Action == BartenderTutorialAction.PourIntoBottle
                ? step.SecondaryGlassId
                : step.PrimaryGlassId;
            if (!shelfView.TryGetBottle(targetId, out LiquidBottle target)
                || target == null || !target.gameObject.activeInHierarchy)
            {
                waitingForPresentation = true;
                return;
            }

            LiquidBottle from = null;
            if (step.Action == BartenderTutorialAction.PourIntoBottle)
                shelfView.TryGetBottle(step.PrimaryGlassId, out from);

            overlay.ShowStep(step, stepIndex, activeSteps.Count, target, from,
                interaction.InputCamera, celebratePrevious);
        }

        private void HandleSelectionChanged(int selectedGlassId)
        {
            if (!active || waitingForPresentation || completing) return;
            BartenderTutorialStep step = CurrentStep;
            if (step == null || step.Action != BartenderTutorialAction.SelectBottle
                || selectedGlassId != step.PrimaryGlassId)
                return;

            AdvanceToNextStep(false);
        }

        private void HandlePoured(BartenderPourReceipt receipt)
        {
            if (!active || completing || receipt == null) return;
            BartenderTutorialStep step = CurrentStep;
            if (step == null || step.Action != BartenderTutorialAction.PourIntoBottle
                || receipt.SourceBefore == null || receipt.TargetBefore == null
                || receipt.SourceBefore.Id != step.PrimaryGlassId
                || receipt.TargetBefore.Id != step.SecondaryGlassId)
                return;

            AdvanceToNextStep(true);
        }

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            if (!active || completing || receipt == null || receipt.DeliveredGlass == null)
                return;
            BartenderTutorialStep step = CurrentStep;
            if (step == null || step.Action != BartenderTutorialAction.DeliverBottle
                || receipt.DeliveredGlass.Id != step.PrimaryGlassId)
                return;

            if (stepIndex + 1 < activeSteps.Count)
            {
                AdvanceToNextStep(true);
                return;
            }

            BartenderTutorialProgress.Complete(activeTutorialId, activeVersion);
            completionPending = true;
            waitingForPresentation = true;
            overlay.SuspendForPresentation();
        }

        private void AdvanceToNextStep(bool waitForPresentation)
        {
            stepIndex++;
            if (stepIndex >= activeSteps.Count)
            {
                BartenderTutorialProgress.Complete(activeTutorialId, activeVersion);
                completionPending = true;
                waitingForPresentation = true;
                overlay.SuspendForPresentation();
                return;
            }

            waitingForPresentation = waitForPresentation;
            if (waitForPresentation)
                overlay.SuspendForPresentation();
            else
                ShowCurrentStep(true);
        }

        private bool PresentationReady()
        {
            bool allowedState = controller != null
                && (controller.State == BartenderLevelState.Playing
                    || (completionPending
                        && (controller.State == BartenderLevelState.Won
                            || controller.State == BartenderLevelState.Failed)));
            return active && allowedState && interaction != null && shelfView != null
                && !controller.PresentationLocked
                && !interaction.Busy
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.DeliveryPlaying
                && !shelfView.SynchronizationDeferred;
        }

        private int FindPreviousSelectStep(int beforeIndex, int glassId)
        {
            for (int i = beforeIndex - 1; i >= 0; i--)
            {
                BartenderTutorialStep candidate = activeSteps[i];
                if (candidate.Action == BartenderTutorialAction.SelectBottle
                    && candidate.PrimaryGlassId == glassId)
                    return i;
            }
            return -1;
        }

        private bool TryCaptureOrValidateRoundToken()
        {
            if (session == null) return false;
            if (hasRoundToken) return session.IsTokenCurrent(roundToken);
            if (!session.TryGetPlayingToken(out roundToken)) return true;
            hasRoundToken = true;
            return true;
        }

        private void HandleLevelLoaded(BsLevel _) => TryStartForCurrentLevel();

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (!active) return;
            if (state == BartenderLevelState.Paused)
            {
                visualSuspended = true;
                overlay.HideImmediate();
                return;
            }
            if (state == BartenderLevelState.Playing)
            {
                if (visualSuspended) waitingForPresentation = true;
                return;
            }
            if ((state == BartenderLevelState.Won || state == BartenderLevelState.Failed)
                && (completionPending || completing))
            {
                visualSuspended = false;
                waitingForPresentation = completionPending;
                hasRoundToken = false;
                return;
            }
            StopTutorial();
        }

        private void HandleSkipRequested()
        {
            if (!active || completing) return;
            BartenderTutorialProgress.Complete(activeTutorialId, activeVersion);
            StopTutorial();
        }

        private void HandleCompletionAnimationFinished()
        {
            if (!active) return;
            StopTutorial();
        }

        private void StopTutorial()
        {
            if (interaction != null) interaction.ClearInputPolicy(this);
            overlay?.HideImmediate();
            active = false;
            waitingForPresentation = false;
            completionPending = false;
            completing = false;
            visualSuspended = false;
            completionDeadline = 0f;
            hasRoundToken = false;
            roundToken = default;
            stepIndex = -1;
            activeSteps.Clear();
            activeTutorialId = null;
            completionEyebrow = DefaultCompletionEyebrow;
            completionTitle = DefaultCompletionTitle;
            completionDetail = DefaultCompletionDetail;
            activeVersion = 0;
        }

        private void ResolveDependencies()
        {
            if (interaction == null) interaction = GetComponent<BartenderPourInteraction>();
            shelfView = interaction != null ? interaction.ShelfView : null;
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            controller = interaction != null ? interaction.Controller : null;
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            session = interaction != null ? interaction.Session : null;
            if (session == null) session = GetComponent<BartenderSession>();
            if (overlay == null)
            {
                overlay = GetComponent<BartenderTutorialOverlayView>();
                if (overlay == null && Application.isPlaying)
                    overlay = gameObject.AddComponent<BartenderTutorialOverlayView>();
            }
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                    subscribedController.Poured -= HandlePoured;
                    subscribedController.Delivered -= HandleDelivered;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                    subscribedController.Poured += HandlePoured;
                    subscribedController.Delivered += HandleDelivered;
                }
            }

            if (subscribedInteraction != interaction)
            {
                if (subscribedInteraction != null)
                    subscribedInteraction.SelectionChanged -= HandleSelectionChanged;
                subscribedInteraction = interaction;
                if (subscribedInteraction != null)
                    subscribedInteraction.SelectionChanged += HandleSelectionChanged;
            }

            if (subscribedOverlay != overlay)
            {
                if (subscribedOverlay != null)
                    subscribedOverlay.SkipRequested -= HandleSkipRequested;
                subscribedOverlay = overlay;
                if (subscribedOverlay != null)
                    subscribedOverlay.SkipRequested += HandleSkipRequested;
            }
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.Poured -= HandlePoured;
                subscribedController.Delivered -= HandleDelivered;
            }
            if (subscribedInteraction != null)
                subscribedInteraction.SelectionChanged -= HandleSelectionChanged;
            if (subscribedOverlay != null)
                subscribedOverlay.SkipRequested -= HandleSkipRequested;
            subscribedController = null;
            subscribedInteraction = null;
            subscribedOverlay = null;
        }
    }

    /// <summary>Adds the tutorial beside authored gameplay rigs without rebuilding scenes.</summary>
    internal static class BartenderTutorialInstaller
    {
        private static bool sceneHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            sceneHooked = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForLoadedScenes()
        {
            if (!sceneHooked)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
                sceneHooked = true;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
                InstallInScene(SceneManager.GetSceneAt(i));
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode _) =>
            InstallInScene(scene);

        private static void InstallInScene(Scene scene)
        {
            if (!Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                BartenderPourInteraction[] interactions =
                    roots[i].GetComponentsInChildren<BartenderPourInteraction>(true);
                for (int j = 0; j < interactions.Length; j++)
                {
                    BartenderPourInteraction found = interactions[j];
                    if (found != null
                        && found.GetComponent<BartenderTutorialDirector>() == null)
                        found.gameObject.AddComponent<BartenderTutorialDirector>();
                }
            }
        }
    }
}
