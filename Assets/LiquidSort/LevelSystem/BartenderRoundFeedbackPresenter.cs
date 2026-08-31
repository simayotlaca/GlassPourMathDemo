using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Terminal presentation that sits downstream of the round FSM. It is installed on
    /// the existing session at runtime so the hand-authored scene and prefab do not need
    /// to be regenerated. <see cref="BartenderSession.TerminalReady"/> is intentionally
    /// used instead of StateChanged: the event opens only after the final pour, portal
    /// flight, shelf reseat and presentation lock have all settled.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(520)]
    public sealed class BartenderRoundFeedbackPresenter : MonoBehaviour
    {
        private const string SuccessPopupResourcePath = "Ui/Result/BartenderResultPopup";
        private const string FailurePopupResourcePath = "Ui/Result/BartenderFailurePopup";
        private const int FailureContinueCoinCost =
            BartenderProgressService.FailureContinueCoinCost;

        private static readonly Color RoyalPurple = new Color32(0x2F, 0x18, 0x66, 0xFA);
        private static readonly Color RoyalPurpleDark = new Color32(0x17, 0x09, 0x32, 0xF2);
        private static readonly Color WarmCream = new Color32(0xFF, 0xF1, 0xC8, 0xFF);
        private static readonly Color Gold = new Color32(0xFF, 0xC8, 0x3D, 0xFF);
        private static readonly Color FailureRed = new Color32(0xE7, 0x4E, 0x54, 0xFF);

        [SerializeField] private BartenderSession session;
        [SerializeField] private BartenderLevelController controller;

        [Header("Motion")]
        [SerializeField, Min(0.01f)] private float scrimFadeDuration = 0.22f;
        [SerializeField, Min(0.01f)] private float cardEntranceDuration = 0.40f;
        [SerializeField, Range(0.5f, 1f)] private float cardStartScale = 0.78f;
        [SerializeField, Min(0f)] private float cardStartOffset = 54f;

        private Canvas feedbackCanvas;
        private CanvasGroup canvasGroup;
        private Image scrim;
        private Image flash;
        private RectTransform card;
        private BartenderResultPopupView successView;
        private BartenderFailurePopupView failureView;
        private GameObject failureOverlay;
        private CanvasGroup failureCanvasGroup;
        private Image failureScrim;
        private Image failureFlash;
        private RectTransform failureCard;
        private Image cardImage;
        private Outline cardOutline;
        private Text titleLabel;
        private Text detailLabel;
        private Button actionButton;
        private Button failureActionButton;
        private Button paidContinueButton;
        private Button closeButton;
        private Button rewardButton;
        private Text actionLabel;
        private Sequence activeSequence;
        private BsRoundOutcome shownOutcome;
        private BsRoundToken shownToken;
        private bool terminalCommandPending;

        private BartenderUiConfetti confetti;
        private static bool sceneHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstallerStatics()
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
                BartenderSession[] sessions =
                    roots[i].GetComponentsInChildren<BartenderSession>(true);
                for (int j = 0; j < sessions.Length; j++)
                {
                    BartenderSession found = sessions[j];
                    if (found != null
                        && found.GetComponent<BartenderRoundFeedbackPresenter>() == null)
                        found.gameObject.AddComponent<BartenderRoundFeedbackPresenter>();
                }
            }
        }

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            EnsureView();

            // Covers a component that was toggled back on after the readiness event.
            if (session != null && session.CanContinueAfterWin)
                Present(BsRoundOutcome.Won);
            else if (session != null && session.CanRetryAfterFailure)
                Present(BsRoundOutcome.Failed);
        }

        private void OnDisable()
        {
            Unsubscribe();
            ResetPresentation();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (session != null)
            {
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalReady += HandleTerminalReady;
                session.TerminalCommandCompleted -= HandleTerminalCommandCompleted;
                session.TerminalCommandCompleted += HandleTerminalCommandCompleted;
            }
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.LevelLoaded += HandleLevelLoaded;
            }
            BartenderProgressService.LivesChanged -= HandleLivesChanged;
            BartenderProgressService.LivesChanged += HandleLivesChanged;
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
        }

        private void Unsubscribe()
        {
            if (session != null)
            {
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalCommandCompleted -= HandleTerminalCommandCompleted;
            }
            if (controller != null) controller.LevelLoaded -= HandleLevelLoaded;
            BartenderProgressService.LivesChanged -= HandleLivesChanged;
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
        }

        private void HandleTerminalReady(BsRoundOutcome outcome) => Present(outcome);

        private void HandleLevelLoaded(BsLevel level) => ResetPresentation();

        private void HandleLivesChanged(int lives)
        {
            RefreshFailureButtons();
        }

        private void HandleCoinsChanged(int coins)
        {
            RefreshFailureButtons();
        }

        private void RefreshFailureButtons()
        {
            if (shownOutcome != BsRoundOutcome.Failed || feedbackCanvas == null
                || !feedbackCanvas.gameObject.activeSelf || canvasGroup == null
                || !canvasGroup.interactable || terminalCommandPending)
                return;
            SetActiveButtonsInteractable(true);
        }

        private void HandleTerminalCommandCompleted(BartenderTerminalCommandResult result)
        {
            terminalCommandPending = false;
            if (result == BartenderTerminalCommandResult.Rejected)
            {
                SetActiveButtonsInteractable(true);
                ShakeCard();
                return;
            }

            // Campaign completion and the X route both expose the existing main menu.
            // Keeping this overlay alive would leave an invisible input blocker above it.
            ResetPresentation();
        }

        private void Present(BsRoundOutcome outcome)
        {
            if (!isActiveAndEnabled || session == null) return;
            EnsureView();
            if (feedbackCanvas == null) return;

            shownOutcome = outcome;
            shownToken = session.CurrentToken;
            terminalCommandPending = false;
            bool won = outcome == BsRoundOutcome.Won;
            if (!won && confetti != null) confetti.StopAndClear();
            bool authoredView = SelectPresentationView(won);
            if (canvasGroup == null || scrim == null || flash == null
                || card == null || actionButton == null) return;

            // The authored result hierarchies use the supplied Turkish raster labels
            // directly. Dynamic text remains only as a safe load/import fallback.
            if (!authoredView)
            {
                titleLabel.text = won ? "SEVİYE TAMAMLANDI!" : FailureTitle();
                detailLabel.text = won ? "Harika servis!" : FailureDetail();
                actionLabel.text = won ? "DEVAM ET" : "TEKRAR DENE";
            }

            if (cardImage != null) cardImage.color = won ? RoyalPurple : RoyalPurpleDark;
            if (cardOutline != null) cardOutline.effectColor = won ? Gold : FailureRed;
            scrim.color = won
                ? new Color(0.07f, 0.02f, 0.15f, 0.58f)
                : new Color(0.12f, 0.01f, 0.03f, 0.66f);
            flash.color = won
                ? new Color(1f, 0.78f, 0.20f, 0f)
                : new Color(0.95f, 0.12f, 0.15f, 0f);

            feedbackCanvas.gameObject.SetActive(true);
            canvasGroup.alpha = 0f;
            // The full-screen scrim must consume world taps while the card enters. Leaving
            // this false lets BartenderPourInteraction's terminal tap fallback queue the
            // next level before this presenter's disabled button becomes usable.
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = false;
            card.anchoredPosition = new Vector2(0f, -cardStartOffset);
            card.localScale = Vector3.one * cardStartScale;
            SetActiveButtonsInteractable(false);

            KillActiveSequence();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            activeSequence = sequence;
            sequence.Append(canvasGroup.DOFade(1f, scrimFadeDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(card.DOAnchorPos(Vector2.zero, cardEntranceDuration)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            sequence.Join(card.DOScale(Vector3.one, cardEntranceDuration)
                .SetEase(Ease.OutBack).SetRecyclable(true));
            sequence.Insert(0f, flash.DOFade(won ? 0.30f : 0.22f, 0.09f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(0.09f, flash.DOFade(0f, 0.34f)
                .SetEase(Ease.InSine).SetRecyclable(true));
            if (!won)
                sequence.Insert(0.18f, card.DOShakeAnchorPos(
                        0.26f, new Vector2(14f, 0f), 11, 0f, false, true)
                    .SetRecyclable(true));
            sequence.OnComplete(() =>
            {
                if (!ReferenceEquals(activeSequence, sequence)) return;
                // Recyclable tween objects must not remain cached after auto-kill; DOTween
                // may lend the same instance to an unrelated animation immediately after.
                activeSequence = null;
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;
                SetActiveButtonsInteractable(true);
            });
            sequence.OnKill(() =>
            {
                if (ReferenceEquals(activeSequence, sequence)) activeSequence = null;
            });

            if (won) EmitConfetti();
        }

        private bool SelectPresentationView(bool won)
        {
            bool useAuthoredSuccess = won && successView != null && successView.IsReady;
            bool useAuthoredFailure = !won && failureView != null && failureView.IsReady;
            if (successView != null) successView.SetVisible(useAuthoredSuccess);
            if (failureView != null) failureView.SetVisible(useAuthoredFailure);
            if (failureOverlay != null)
                failureOverlay.SetActive(!useAuthoredSuccess && !useAuthoredFailure);

            if (useAuthoredSuccess)
            {
                canvasGroup = successView.OverlayGroup;
                scrim = successView.Dimmer;
                flash = successView.Flash;
                card = successView.CardPivot;
                actionButton = successView.ContinueButton;
                paidContinueButton = null;
                closeButton = successView.CloseButton;
                rewardButton = successView.RewardButton;
                RebindButton(actionButton, HandleActionPressed);
                RebindButton(closeButton, HandleClosePressed);
                return true;
            }

            if (useAuthoredFailure)
            {
                canvasGroup = failureView.OverlayGroup;
                scrim = failureView.Dimmer;
                flash = failureView.Flash;
                card = failureView.CardPivot;
                actionButton = failureView.RetryButton;
                paidContinueButton = failureView.PaidContinueButton;
                closeButton = failureView.CloseButton;
                rewardButton = null;
                RebindButton(actionButton, HandleActionPressed);
                RebindButton(paidContinueButton, HandlePaidContinuePressed);
                RebindButton(closeButton, HandleClosePressed);
                return true;
            }

            canvasGroup = failureCanvasGroup;
            scrim = failureScrim;
            flash = failureFlash;
            card = failureCard;
            actionButton = failureActionButton;
            paidContinueButton = null;
            closeButton = null;
            rewardButton = null;
            RebindButton(actionButton, HandleActionPressed);
            return false;
        }

        private string FailureTitle()
        {
            if (controller == null) return "SERVİS DURDU";
            return controller.FailureReason == BartenderFailureReason.OrderTimedOut
                ? "SÜRE DOLDU!"
                : "HAMLE KALMADI";
        }

        private string FailureDetail()
        {
            if (controller == null) return "Bir kez daha deneyelim.";
            if (controller.FailureReason != BartenderFailureReason.OrderTimedOut)
                return "Yeni bir düzenle tekrar deneyebilirsin.";
            return controller.TimedOutOrderSlot >= 0
                ? $"{controller.TimedOutOrderSlot + 1}. sipariş bekleyemedi."
                : "Bir sipariş bekleyemedi.";
        }

        private void HandleActionPressed()
        {
            if (session == null || actionButton == null || !actionButton.interactable) return;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = shownOutcome == BsRoundOutcome.Won
                ? session.RequestContinueAfterWin(shownToken)
                : session.RequestRetryAfterFailure(shownToken);
            if (accepted) return;
            terminalCommandPending = false;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private void HandlePaidContinuePressed()
        {
            if (session == null || paidContinueButton == null
                || !paidContinueButton.interactable) return;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session.RequestPaidRetryAfterFailure(
                shownToken, FailureContinueCoinCost);
            if (accepted) return;
            terminalCommandPending = false;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private void HandleClosePressed()
        {
            if (session == null || closeButton == null || !closeButton.interactable) return;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session.RequestReturnToMainMenuFromTerminal(shownToken);
            if (accepted) return;
            terminalCommandPending = false;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private void SetActiveButtonsInteractable(bool interactable)
        {
            bool canUseAction = interactable;
            bool failed = shownOutcome == BsRoundOutcome.Failed;
            int lives = failed ? BartenderProgressService.Lives : 0;
            if (failed) canUseAction = canUseAction && lives > 0;
            if (actionButton != null) actionButton.interactable = canUseAction;
            if (paidContinueButton != null)
            {
                paidContinueButton.interactable = interactable && failed
                    && lives < BartenderProgressService.MaxLives
                    && BartenderProgressService.CanAfford(FailureContinueCoinCost);
            }
            if (closeButton != null) closeButton.interactable = interactable;

            // The reference rewarded-video artwork is present in the hierarchy, but this
            // project currently has no rewarded-ad adapter. It stays visibly authored and
            // intentionally non-interactable so a tap cannot grant an unverified reward.
            if (rewardButton != null) rewardButton.interactable = false;
        }

        private void ShakeCard()
        {
            if (card == null) return;
            card.DOKill(false);
            card.anchoredPosition = Vector2.zero;
            card.DOShakeAnchorPos(0.22f, new Vector2(12f, 0f), 10, 0f, false, true)
                .SetUpdate(true).SetRecyclable(true);
        }

        private void ResetPresentation()
        {
            terminalCommandPending = false;
            KillActiveSequence();
            ResetCardTransform(card);
            ResetCardTransform(failureCard);
            if (successView != null) ResetCardTransform(successView.CardPivot);
            if (failureView != null) ResetCardTransform(failureView.CardPivot);
            if (flash != null) flash.DOKill(false);
            if (successView != null && successView.Flash != null)
                successView.Flash.DOKill(false);
            if (failureView != null && failureView.Flash != null)
                failureView.Flash.DOKill(false);
            if (confetti != null) confetti.StopAndClear();

            if (failureActionButton != null)
            {
                RebindButton(failureActionButton, HandleActionPressed);
                failureActionButton.gameObject.SetActive(true);
                failureActionButton.interactable = false;
            }
            if (failureCanvasGroup != null)
            {
                failureCanvasGroup.alpha = 0f;
                failureCanvasGroup.blocksRaycasts = false;
                failureCanvasGroup.interactable = false;
            }
            if (failureOverlay != null) failureOverlay.SetActive(false);

            if (successView != null)
            {
                RebindButton(successView.ContinueButton, HandleActionPressed);
                RebindButton(successView.CloseButton, HandleClosePressed);
                successView.SetButtonsInteractable(false, false, false);
                successView.SetVisible(false);
            }
            if (failureView != null)
            {
                RebindButton(failureView.PaidContinueButton,
                    HandlePaidContinuePressed);
                RebindButton(failureView.RetryButton, HandleActionPressed);
                RebindButton(failureView.CloseButton, HandleClosePressed);
                failureView.SetButtonsInteractable(false, false, false);
                failureView.SetVisible(false);
            }

            if (feedbackCanvas != null) feedbackCanvas.gameObject.SetActive(false);
        }

        private static void ResetCardTransform(RectTransform target)
        {
            if (target == null) return;
            target.DOKill(false);
            target.anchoredPosition = Vector2.zero;
            target.localScale = Vector3.one;
        }

        private static void RebindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null) return;
            button.onClick.RemoveListener(action);
            button.onClick.AddListener(action);
        }

        private void KillActiveSequence()
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
        }

        private void EnsureView()
        {
            if (feedbackCanvas != null || !Application.isPlaying) return;

            var canvasObject = new GameObject(
                "Round Feedback Canvas (Runtime)",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.hideFlags = HideFlags.DontSave;
            canvasObject.transform.SetParent(transform, false);

            feedbackCanvas = canvasObject.GetComponent<Canvas>();
            feedbackCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            feedbackCanvas.overrideSorting = true;
            feedbackCanvas.sortingOrder = 32000;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(720f, 1280f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            Stretch(canvasRect);

            LoadAuthoredSuccessView(canvasRect);
            LoadAuthoredFailureView(canvasRect);
            CreateLegacyFailureView(canvasRect);
            EnsureConfetti();
            feedbackCanvas.gameObject.SetActive(false);
        }

        private void LoadAuthoredSuccessView(RectTransform canvasRect)
        {
            GameObject prefab = Resources.Load<GameObject>(SuccessPopupResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"Sonuç popup prefabı Resources/{SuccessPopupResourcePath} altında bulunamadı; "
                    + "güvenli metin yedeği kullanılacak.", this);
                return;
            }

            GameObject instance = Instantiate(prefab, canvasRect, false);
            instance.name = "Result Overlay (Success)";
            instance.hideFlags = HideFlags.DontSave;
            successView = instance.GetComponent<BartenderResultPopupView>();
            if (successView == null)
            {
                Debug.LogError("Sonuç popup prefabında BartenderResultPopupView eksik.", instance);
                instance.SetActive(false);
                return;
            }
            RectTransform successRect = successView.transform as RectTransform;
            if (successRect != null) Stretch(successRect);

            if (!successView.IsReady)
            {
                Debug.LogError("Sonuç popup prefabının zorunlu UI referansları eksik.", successView);
                successView.SetVisible(false);
                return;
            }

            BsButtonSound.Ensure(successView.ContinueButton.gameObject);
            BsButtonSound.Ensure(successView.CloseButton.gameObject);
            BsButtonSound.Ensure(successView.RewardButton.gameObject);
            RebindButton(successView.ContinueButton, HandleActionPressed);
            RebindButton(successView.CloseButton, HandleClosePressed);

            // Input is locked during the entrance; keep the supplied art vibrant while
            // disabled rather than applying Unity's default grey multiplier.
            PreserveArtworkWhenDisabled(successView.ContinueButton);
            PreserveArtworkWhenDisabled(successView.CloseButton);
            PreserveArtworkWhenDisabled(successView.RewardButton);
            successView.SetButtonsInteractable(false, false, false);
            successView.SetVisible(false);
        }

        private void LoadAuthoredFailureView(RectTransform canvasRect)
        {
            GameObject prefab = Resources.Load<GameObject>(FailurePopupResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"Başarısızlık popup prefabı Resources/{FailurePopupResourcePath} "
                    + "altında bulunamadı; güvenli metin yedeği kullanılacak.", this);
                return;
            }

            GameObject instance = Instantiate(prefab, canvasRect, false);
            instance.name = "Result Overlay (Failure)";
            instance.hideFlags = HideFlags.DontSave;
            failureView = instance.GetComponent<BartenderFailurePopupView>();
            if (failureView == null)
            {
                Debug.LogError(
                    "Başarısızlık popup prefabında BartenderFailurePopupView eksik.", instance);
                instance.SetActive(false);
                return;
            }

            RectTransform failureRect = failureView.transform as RectTransform;
            if (failureRect != null) Stretch(failureRect);
            if (!failureView.IsReady)
            {
                Debug.LogError(
                    "Başarısızlık popup prefabının zorunlu UI referansları eksik.",
                    failureView);
                failureView.SetVisible(false);
                return;
            }

            BsButtonSound.Ensure(failureView.PaidContinueButton.gameObject);
            BsButtonSound.Ensure(failureView.RetryButton.gameObject);
            BsButtonSound.Ensure(failureView.CloseButton.gameObject);
            RebindButton(failureView.PaidContinueButton,
                HandlePaidContinuePressed);
            RebindButton(failureView.RetryButton, HandleActionPressed);
            RebindButton(failureView.CloseButton, HandleClosePressed);
            failureView.SetPurchaseCost(FailureContinueCoinCost);
            PreserveArtworkWhenDisabled(failureView.CloseButton);
            failureView.SetButtonsInteractable(false, false, false);
            failureView.SetVisible(false);
        }

        private static void PreserveArtworkWhenDisabled(Button button)
        {
            if (button == null) return;
            ColorBlock colours = button.colors;
            colours.disabledColor = Color.white;
            button.colors = colours;
        }

        private void CreateLegacyFailureView(RectTransform canvasRect)
        {
            failureOverlay = new GameObject(
                "Result Overlay (Failure Fallback)", typeof(RectTransform),
                typeof(CanvasGroup));
            failureOverlay.hideFlags = HideFlags.DontSave;
            failureOverlay.transform.SetParent(canvasRect, false);
            RectTransform overlayRect = failureOverlay.GetComponent<RectTransform>();
            Stretch(overlayRect);
            failureCanvasGroup = failureOverlay.GetComponent<CanvasGroup>();

            failureScrim = CreateImage("Dimmer", overlayRect, Color.clear);
            Stretch(failureScrim.rectTransform);

            failureFlash = CreateImage("Outcome Flash", overlayRect, Color.clear);
            Stretch(failureFlash.rectTransform);
            failureFlash.raycastTarget = false;

            cardImage = CreateImage("Result Card", overlayRect, RoyalPurple);
            failureCard = cardImage.rectTransform;
            failureCard.anchorMin = failureCard.anchorMax = new Vector2(0.5f, 0.5f);
            failureCard.pivot = new Vector2(0.5f, 0.5f);
            failureCard.sizeDelta = new Vector2(560f, 330f);
            cardOutline = cardImage.gameObject.AddComponent<Outline>();
            cardOutline.effectColor = Gold;
            cardOutline.effectDistance = new Vector2(5f, -5f);

            Font font = ResolveUiFont();
            titleLabel = CreateText(
                "Title", failureCard, font, 42, FontStyle.Bold, WarmCream);
            RectTransform titleRect = titleLabel.rectTransform;
            titleRect.anchorMin = new Vector2(0.06f, 0.60f);
            titleRect.anchorMax = new Vector2(0.94f, 0.91f);
            titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;

            detailLabel = CreateText(
                "Detail", failureCard, font, 24, FontStyle.Normal, WarmCream);
            RectTransform detailRect = detailLabel.rectTransform;
            detailRect.anchorMin = new Vector2(0.08f, 0.40f);
            detailRect.anchorMax = new Vector2(0.92f, 0.62f);
            detailRect.offsetMin = detailRect.offsetMax = Vector2.zero;

            Image buttonImage = CreateImage("Action Button", failureCard, Gold);
            RectTransform buttonRect = buttonImage.rectTransform;
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.5f, 0.20f);
            buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.sizeDelta = new Vector2(330f, 72f);
            failureActionButton = buttonImage.gameObject.AddComponent<Button>();
            BsButtonSound.Ensure(failureActionButton.gameObject);
            failureActionButton.targetGraphic = buttonImage;
            ColorBlock colours = failureActionButton.colors;
            colours.normalColor = Gold;
            colours.highlightedColor = new Color(1f, 0.88f, 0.43f);
            colours.pressedColor = new Color(0.82f, 0.54f, 0.10f);
            colours.disabledColor = new Color(0.50f, 0.40f, 0.22f, 0.75f);
            colours.fadeDuration = 0.08f;
            failureActionButton.colors = colours;

            actionLabel = CreateText(
                "Label", buttonRect, font, 27, FontStyle.Bold, RoyalPurpleDark);
            Stretch(actionLabel.rectTransform);
            actionLabel.raycastTarget = false;
            RebindButton(failureActionButton, HandleActionPressed);

            failureCanvasGroup.alpha = 0f;
            failureCanvasGroup.blocksRaycasts = false;
            failureCanvasGroup.interactable = false;
            failureOverlay.SetActive(false);

            // Safe initial references; Present selects the authored success view when ready.
            canvasGroup = failureCanvasGroup;
            scrim = failureScrim;
            flash = failureFlash;
            card = failureCard;
            actionButton = failureActionButton;
        }

        private static Image CreateImage(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = colour;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Font font,
                                       int size, FontStyle style, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = colour;
            return text;
        }

        private static Font ResolveUiFont()
        {
            // Reuse the scene's authored bitmap-compatible UI font when possible. This
            // also keeps Turkish glyph coverage identical to the level badge and pause UI.
            Text[] labels = Object.FindObjectsByType<Text>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null && labels[i].font != null)
                    return labels[i].font;
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void EmitConfetti()
        {
            EnsureConfetti();
            if (confetti == null) return;
            confetti.Play();
        }

        private void EnsureConfetti()
        {
            if (confetti != null || !Application.isPlaying || feedbackCanvas == null)
                return;
            confetti = BartenderUiConfetti.AttachTo(
                feedbackCanvas.transform as RectTransform);
        }
    }
}
