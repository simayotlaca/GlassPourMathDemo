using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Runtime-only Royal coach-mark view: four raycast scrims leave a real hole over the
    /// expected world bottle, while every decorative element remains non-interactive.
    /// The hand, halo and copy therefore guide the existing hit-test instead of replacing it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderTutorialOverlayView : MonoBehaviour
    {
        private const string ThemeResourcePath = "Ui/MainMenu/BartenderMainMenuTheme";
        private const string TutorialFontResourcePath =
            "Ui/Tutorial/Nunito_Turkish_ExtraBold";
        private const string CardResourcePath =
            "Ui/Tutorial/TutorialCard_Royal_Simple_v1";
        private const string HandResourcePath =
            "Ui/Tutorial/CoachHand_Royal_Tap_Simple_v1";
        private const string FallbackHandResourcePath = "Ui/Tutorial/CoachHand_Royal_v1";
        private const string HaloResourcePath =
            "Ui/Tutorial/TutorialHalo_Royal_Simple_v1";
        private const string FxResourcePath =
            "Ui/Tutorial/TutorialFxSheet_Royal_Simple_v1";
        private const int FxColumns = 4;
        private const int FxRows = 2;
        private const int FxCellCount = FxColumns * FxRows;
        private const int FxSparkleCellCount = 6;
        private const int FxFirstDropletCell = 6;
        // Keep the short tutorial success beat above the terminal result canvas (32000),
        // then release input so that presenter's normal continue/retry action is exposed.
        private const int SortingOrder = 32500;

        private static readonly Color ScrimColour = new Color32(0x12, 0x05, 0x26, 0xB8);
        private static readonly Color CompletionScrimColour =
            new Color32(0x12, 0x05, 0x26, 0x66);
        private static readonly Color RoyalPurple = new Color32(0x2F, 0x18, 0x66, 0xFF);
        private static readonly Color RoyalPurpleDark = new Color32(0x17, 0x09, 0x32, 0xFF);
        private static readonly Color WarmCream = new Color32(0xFF, 0xF1, 0xC8, 0xFF);
        private static readonly Color Gold = new Color32(0xFF, 0xC8, 0x3D, 0xFF);
        private static readonly Color Lavender = new Color32(0x9B, 0x75, 0xF2, 0xFF);

        private readonly Image[] scrims = new Image[4];
        private readonly Image[] orbitJewels = new Image[6];
        private readonly Image[] trailDots = new Image[6];
        private readonly List<Sparkle> sparkles = new List<Sparkle>(16);
        private readonly List<Tween> sparkleTweens = new List<Tween>(16);

        private sealed class Sparkle
        {
            public RectTransform Rect;
            public CanvasGroup Group;
            public Image Image;
        }

        private Canvas tutorialCanvas;
        private GameObject canvasObject;
        private CanvasGroup canvasGroup;
        private RectTransform canvasRect;
        private RectTransform haloRoot;
        private Image haloOuter;
        private Image haloInner;
        private RectTransform handRect;
        private CanvasGroup handGroup;
        private RectTransform card;
        private Image cardImage;
        private Text eyebrowLabel;
        private Text titleLabel;
        private Text detailLabel;
        private RectTransform skipRect;
        private Button skipButton;
        private Image fullFlash;

        private BartenderMainMenuTheme theme;
        private Font tutorialFont;
        private Sprite roundedSprite;
        private Sprite ringSprite;
        private Sprite tutorialCardSprite;
        private Sprite tutorialHandSprite;
        private Sprite tutorialHaloSprite;
        private Texture2D tutorialFxTexture;
        private readonly Sprite[] tutorialFxSprites = new Sprite[FxCellCount];
        private Texture2D roundedTexture;
        private Texture2D ringTexture;
        private bool usingDedicatedHand;
        private bool usingDedicatedHalo;
        private bool usingDedicatedFx;

        private LiquidBottle targetBottle;
        private LiquidBottle travelFromBottle;
        private Camera targetCamera;
        private Rect targetRect;
        private Vector2 targetPoint;
        private Vector2 travelFromPoint;
        private Vector2 desiredCardPosition;
        private float haloPulse;
        private float orbitPhase;
        private float handPhase;
        private bool visible;
        private bool completing;
        private bool cardPositionTweened;

        private Sequence transitionSequence;
        private Tween haloPulseTween;
        private Tween haloRotationTween;
        private Tween handPhaseTween;
        private Tween flashTween;

        public bool Visible => visible;
        public event Action SkipRequested;

        private void LateUpdate()
        {
            if (!visible || tutorialCanvas == null || !tutorialCanvas.gameObject.activeSelf)
                return;

            if (targetBottle != null)
            {
                UpdateTargetGeometry();
                UpdateHandGeometry();
                UpdateTrailGeometry();
            }

            float follow = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
            if (card != null && !cardPositionTweened)
                card.anchoredPosition = Vector2.Lerp(
                    card.anchoredPosition, desiredCardPosition, follow);
        }

        private void OnDisable() => HideImmediate();

        private void OnDestroy()
        {
            KillOwnedTweens();
            if (canvasObject != null) Destroy(canvasObject);
            if (roundedSprite != null) Destroy(roundedSprite);
            if (ringSprite != null) Destroy(ringSprite);
            for (int i = 0; i < tutorialFxSprites.Length; i++)
            {
                if (tutorialFxSprites[i] != null) Destroy(tutorialFxSprites[i]);
                tutorialFxSprites[i] = null;
            }
            if (roundedTexture != null) Destroy(roundedTexture);
            if (ringTexture != null) Destroy(ringTexture);
        }

        public void ShowStep(BartenderTutorialStep step, int stepIndex, int stepCount,
                             LiquidBottle target, LiquidBottle travelFrom,
                             Camera worldCamera, bool celebratePrevious)
        {
            if (step == null || target == null) return;
            EnsureView();
            if (tutorialCanvas == null) return;

            bool entering = !visible || !tutorialCanvas.gameObject.activeSelf;
            targetBottle = target;
            travelFromBottle = travelFrom;
            targetCamera = worldCamera != null ? worldCamera : Camera.main;
            completing = false;
            visible = true;

            tutorialCanvas.gameObject.SetActive(true);
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;
            skipButton.interactable = true;
            skipRect.gameObject.SetActive(true);
            eyebrowLabel.gameObject.SetActive(true);
            titleLabel.gameObject.SetActive(true);
            detailLabel.gameObject.SetActive(true);
            card.sizeDelta = new Vector2(620f, 292f);
            SetRect(titleLabel.rectTransform, -245f, -43f, 245f, 9f);
            SetScrimColour(ScrimColour);
            KillTween(ref flashTween);
            fullFlash.color = new Color(1f, 0.78f, 0.20f, 0f);

            eyebrowLabel.text = string.IsNullOrWhiteSpace(step.Eyebrow)
                ? $"KRALİYET DERSİ  •  {stepIndex + 1}/{Mathf.Max(1, stepCount)}"
                : $"{step.Eyebrow}  •  {stepIndex + 1}/{Mathf.Max(1, stepCount)}";
            titleLabel.text = step.Title ?? string.Empty;
            detailLabel.text = step.Detail ?? string.Empty;

            Canvas.ForceUpdateCanvases();
            UpdateTargetGeometry();
            UpdateHandGeometry();
            UpdateTrailGeometry();
            StartGuidanceLoops();

            KillTransition();
            transitionSequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true);

            if (entering)
            {
                canvasGroup.alpha = 0f;
                card.localScale = Vector3.one * 0.78f;
                card.anchoredPosition = desiredCardPosition + Vector2.up * 54f;
                transitionSequence
                    .Append(canvasGroup.DOFade(1f, 0.22f).SetEase(Ease.OutQuad))
                    .Join(card.DOScale(1f, 0.40f).SetEase(Ease.OutBack))
                    .Join(card.DOAnchorPos(desiredCardPosition, 0.36f)
                        .SetEase(Ease.OutCubic));
                TrackCardPosition(transitionSequence);
            }
            else
            {
                canvasGroup.alpha = 1f;
                if (celebratePrevious) EmitSparkles(targetPoint, false);
                transitionSequence
                    .Append(card.DOScale(0.92f, 0.10f).SetEase(Ease.InQuad))
                    .Append(card.DOScale(1f, 0.28f).SetEase(Ease.OutBack));
            }
        }

        public void SuspendForPresentation()
        {
            if (!visible || tutorialCanvas == null) return;
            StopGuidanceLoops();
            targetBottle = null;
            travelFromBottle = null;
            KillTransition();
            transitionSequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true)
                .Append(canvasGroup.DOFade(0f, 0.16f).SetEase(Ease.OutQuad))
                .AppendCallback(() =>
                {
                    if (!completing && tutorialCanvas != null)
                        tutorialCanvas.gameObject.SetActive(false);
                });
        }

        public void Nudge()
        {
            if (!visible || card == null || targetBottle == null) return;
            BsAudio.Instance?.Play(BsSfx.Invalid);
            EmitSparkles(targetPoint, true);
            KillTransition();
            // A very early wrong tap may interrupt the entrance fade/scale. Snap only
            // those presentation baselines before playing the nudge so the modal cannot
            // remain permanently translucent or undersized.
            canvasGroup.alpha = 1f;
            card.localScale = Vector3.one;
            Vector2 home = desiredCardPosition;
            transitionSequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true)
                .Append(card.DOAnchorPos(home + Vector2.left * 18f, 0.065f))
                .Append(card.DOAnchorPos(home + Vector2.right * 16f, 0.065f))
                .Append(card.DOAnchorPos(home + Vector2.left * 8f, 0.055f))
                .Append(card.DOAnchorPos(home, 0.085f).SetEase(Ease.OutBack));
            TrackCardPosition(transitionSequence);
        }

        public void ShowCompletion(string eyebrow, string title, string detail,
                                   Action finished)
        {
            EnsureView();
            if (tutorialCanvas == null)
            {
                finished?.Invoke();
                return;
            }

            completing = true;
            visible = true;
            targetBottle = null;
            travelFromBottle = null;
            StopGuidanceLoops();
            tutorialCanvas.gameObject.SetActive(true);
            canvasGroup.alpha = 1f;
            canvasGroup.blocksRaycasts = true;
            skipButton.interactable = false;
            skipRect.gameObject.SetActive(false);
            SetScrimColour(CompletionScrimColour);
            SetFullScrim(true);
            bool showCompletionHalo = usingDedicatedHalo && haloRoot != null;
            haloRoot.gameObject.SetActive(showCompletionHalo);
            if (showCompletionHalo)
            {
                haloRoot.anchoredPosition = Vector2.zero;
                haloRoot.sizeDelta = new Vector2(560f, 280f);
                haloRoot.localRotation = Quaternion.identity;
                haloRoot.localScale = Vector3.one * 0.94f;
                if (haloOuter != null)
                    haloOuter.color = new Color(1f, 1f, 1f, 0.38f);
            }
            handRect.gameObject.SetActive(false);
            SetTrailVisible(false);

            eyebrowLabel.text = string.Empty;
            eyebrowLabel.gameObject.SetActive(false);
            titleLabel.text = string.IsNullOrWhiteSpace(title)
                ? "HAZIRSIN!"
                : title.Trim();
            detailLabel.text = string.Empty;
            detailLabel.gameObject.SetActive(false);
            card.sizeDelta = new Vector2(520f, 248f);
            SetRect(titleLabel.rectTransform, -220f, -38f, 220f, 38f);
            desiredCardPosition = Vector2.zero;
            card.anchoredPosition = Vector2.zero;
            card.localScale = Vector3.one * 0.92f;

            EmitSparkles(Vector2.zero, false, 6, 160f, false);
            KillTween(ref flashTween);
            fullFlash.color = new Color(1f, 0.78f, 0.20f, 0f);
            // ButtonClick is the shipped, guaranteed compact UI tick.
            BsAudio.Instance?.Play(BsSfx.ButtonClick, 0.78f, 1.12f);
            KillTransition();
            transitionSequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true)
                .Append(card.DOScale(1.01f, 0.22f).SetEase(Ease.OutBack));
            if (showCompletionHalo)
                transitionSequence.Join(
                    haloRoot.DOScale(1f, 0.24f).SetEase(Ease.OutCubic));
            transitionSequence
                .Append(card.DOScale(1f, 0.10f).SetEase(Ease.OutQuad))
                .AppendInterval(0.85f)
                .Append(canvasGroup.DOFade(0f, 0.24f).SetEase(Ease.InQuad))
                .AppendCallback(() =>
                {
                    HideImmediate();
                    finished?.Invoke();
                });
        }

        public void HideImmediate()
        {
            KillOwnedTweens();
            visible = false;
            completing = false;
            targetBottle = null;
            travelFromBottle = null;
            targetCamera = null;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
            }
            if (tutorialCanvas != null) tutorialCanvas.gameObject.SetActive(false);
        }

        private void EnsureView()
        {
            if (tutorialCanvas != null || !Application.isPlaying) return;

            theme = Resources.Load<BartenderMainMenuTheme>(ThemeResourcePath);
            LoadTutorialArt();
            roundedSprite = CreateRoundedSprite(out roundedTexture);
            ringSprite = CreateRingSprite(out ringTexture);

            canvasObject = new GameObject(
                "Royal Tutorial Canvas (Runtime)", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasObject.hideFlags = HideFlags.DontSave;
            canvasObject.layer = UiLayer();
            Scene ownerScene = gameObject.scene;
            if (ownerScene.IsValid() && ownerScene.isLoaded)
                SceneManager.MoveGameObjectToScene(canvasObject, ownerScene);

            tutorialCanvas = canvasObject.GetComponent<Canvas>();
            tutorialCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            tutorialCanvas.overrideSorting = true;
            tutorialCanvas.sortingOrder = SortingOrder;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(720f, 1280f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGroup = canvasObject.GetComponent<CanvasGroup>();
            canvasRect = canvasObject.GetComponent<RectTransform>();
            Stretch(canvasRect);

            for (int i = 0; i < scrims.Length; i++)
            {
                scrims[i] = CreateImage("Spotlight Scrim " + i, canvasRect, null,
                    ScrimColour);
                scrims[i].raycastTarget = true;
                Button blocker = scrims[i].gameObject.AddComponent<Button>();
                blocker.targetGraphic = scrims[i];
                blocker.transition = Selectable.Transition.None;
                blocker.onClick.AddListener(Nudge);
            }

            fullFlash = CreateImage("Royal Step Flash", canvasRect, null,
                new Color(1f, 0.78f, 0.20f, 0f));
            Stretch(fullFlash.rectTransform);
            fullFlash.raycastTarget = false;

            BuildHalo();
            BuildTrail();
            BuildHand();
            BuildCard();
            BuildSkipButton();
            BuildSparkles();

            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
            tutorialCanvas.gameObject.SetActive(false);
        }

        private void LoadTutorialArt()
        {
            // Fredoka's bundled face omits several Turkish glyphs (Ğ, İ and Ş).
            // Keep the completion copy editable, but render it with a static,
            // Turkish-complete face so no platform falls back to tofu boxes.
            tutorialFont = Resources.Load<Font>(TutorialFontResourcePath);
            tutorialCardSprite = Resources.Load<Sprite>(CardResourcePath);

            tutorialHandSprite = Resources.Load<Sprite>(HandResourcePath);
            usingDedicatedHand = tutorialHandSprite != null;
            if (tutorialHandSprite == null)
                tutorialHandSprite = Resources.Load<Sprite>(FallbackHandResourcePath);

            tutorialHaloSprite = Resources.Load<Sprite>(HaloResourcePath);
            usingDedicatedHalo = tutorialHaloSprite != null;

            tutorialFxTexture = Resources.Load<Texture2D>(FxResourcePath);
            if (tutorialFxTexture == null
                || tutorialFxTexture.width < FxColumns
                || tutorialFxTexture.height < FxRows)
                return;

            // The authored sheet is specified visually from top to bottom: gold glints
            // on row zero, then purple sparkles and droplets on row one. Sprite.Create's
            // texture rect uses a bottom-left origin, so invert only the row coordinate.
            for (int rowFromTop = 0; rowFromTop < FxRows; rowFromTop++)
            for (int column = 0; column < FxColumns; column++)
            {
                int xMin = Mathf.RoundToInt(
                    tutorialFxTexture.width * column / (float)FxColumns);
                int xMax = Mathf.RoundToInt(
                    tutorialFxTexture.width * (column + 1) / (float)FxColumns);
                int rowFromBottom = FxRows - rowFromTop - 1;
                int yMin = Mathf.RoundToInt(
                    tutorialFxTexture.height * rowFromBottom / (float)FxRows);
                int yMax = Mathf.RoundToInt(
                    tutorialFxTexture.height * (rowFromBottom + 1) / (float)FxRows);
                int index = rowFromTop * FxColumns + column;
                Sprite sprite = Sprite.Create(tutorialFxTexture,
                    new Rect(xMin, yMin, xMax - xMin, yMax - yMin),
                    new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect);
                sprite.name = $"Royal Tutorial FX {index} (Runtime)";
                sprite.hideFlags = HideFlags.HideAndDontSave;
                tutorialFxSprites[index] = sprite;
            }

            usingDedicatedFx = true;
        }

        private void BuildHalo()
        {
            haloRoot = NewUiObject("Royal Spotlight", canvasRect).GetComponent<RectTransform>();
            haloRoot.anchorMin = haloRoot.anchorMax = new Vector2(0.5f, 0.5f);
            haloRoot.pivot = new Vector2(0.5f, 0.5f);

            if (usingDedicatedHalo)
            {
                haloOuter = CreateImage("Royal Focus Halo", haloRoot,
                    tutorialHaloSprite, Color.white);
                Stretch(haloOuter.rectTransform);
                // The production halo is tightly cropped and intentionally stretches
                // with the target bottle's rectangular spotlight.
                haloOuter.preserveAspect = false;
                haloOuter.raycastTarget = false;
                return;
            }

            haloOuter = CreateImage("Purple Glow", haloRoot, ringSprite, Lavender);
            Stretch(haloOuter.rectTransform);
            haloOuter.color = new Color(Lavender.r, Lavender.g, Lavender.b, 0.48f);
            haloOuter.raycastTarget = false;

            haloInner = CreateImage("Gold Ring", haloRoot, ringSprite, Gold);
            Stretch(haloInner.rectTransform, 10f);
            haloInner.raycastTarget = false;

            for (int i = 0; i < orbitJewels.Length; i++)
            {
                Image jewel = CreateImage("Orbit Jewel " + i, haloRoot, roundedSprite,
                    i % 2 == 0 ? Gold : WarmCream);
                jewel.type = Image.Type.Sliced;
                jewel.raycastTarget = false;
                jewel.rectTransform.anchorMin = jewel.rectTransform.anchorMax =
                    new Vector2(0.5f, 0.5f);
                jewel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                jewel.rectTransform.sizeDelta = new Vector2(11f, 11f);
                jewel.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                orbitJewels[i] = jewel;
            }
        }

        private void BuildTrail()
        {
            bool useDroplets = usingDedicatedFx
                && tutorialFxSprites[FxFirstDropletCell] != null
                && tutorialFxSprites[FxFirstDropletCell + 1] != null;
            for (int i = 0; i < trailDots.Length; i++)
            {
                Sprite sprite = useDroplets
                    ? tutorialFxSprites[FxFirstDropletCell + i % 2]
                    : roundedSprite;
                Image dot = CreateImage("Pour Trail " + i, canvasRect, sprite,
                    useDroplets ? Color.white : (i % 2 == 0 ? Gold : WarmCream));
                dot.type = useDroplets ? Image.Type.Simple : Image.Type.Sliced;
                dot.preserveAspect = useDroplets;
                dot.raycastTarget = false;
                RectTransform rect = dot.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                float size = 10f + i * 1.4f;
                // The source cells intentionally have generous transparent padding.
                // These rects keep the painted droplets at roughly 14-20 UI pixels.
                rect.sizeDelta = useDroplets
                    ? new Vector2(46f + i, 84f + i * 1.8f)
                    : new Vector2(size, size);
                trailDots[i] = dot;
            }
            SetTrailVisible(false);
        }

        private void BuildHand()
        {
            Image handImage = CreateImage("Royal Coach Hand", canvasRect,
                tutorialHandSprite,
                Color.white);
            handRect = handImage.rectTransform;
            handRect.anchorMin = handRect.anchorMax = new Vector2(0.5f, 0.5f);
            // The new tap pose points down and bakes its contact ripple near the bottom.
            // Keep the old, top-edge hotspot only when the legacy upright hand is used.
            handRect.pivot = usingDedicatedHand
                ? new Vector2(0.428f, 0.076f)
                : new Vector2(0.371f, 0.970f);
            handRect.sizeDelta = new Vector2(102f, 153f);
            handRect.localRotation = Quaternion.Euler(
                0f, 0f, usingDedicatedHand ? 0f : -8f);
            handImage.preserveAspect = true;
            handImage.raycastTarget = false;
            handGroup = handImage.gameObject.AddComponent<CanvasGroup>();
            handGroup.blocksRaycasts = false;
            handGroup.interactable = false;
        }

        private void BuildCard()
        {
            Sprite themedFallback = theme != null ? theme.PlayFrame : null;
            Sprite frame = tutorialCardSprite != null
                ? tutorialCardSprite
                : themedFallback;
            cardImage = CreateImage("Royal Tutorial Card", canvasRect,
                frame != null ? frame : roundedSprite,
                frame != null ? Color.white : RoyalPurple);
            card = cardImage.rectTransform;
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(620f, 292f);
            cardImage.preserveAspect = frame != null;
            cardImage.type = frame == null ? Image.Type.Sliced : Image.Type.Simple;
            cardImage.raycastTarget = true;
            Button cardBlocker = cardImage.gameObject.AddComponent<Button>();
            cardBlocker.targetGraphic = cardImage;
            cardBlocker.transition = Selectable.Transition.None;
            cardBlocker.onClick.AddListener(Nudge);

            Shadow shadow = cardImage.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0.08f, 0.015f, 0.16f, 0.72f);
            shadow.effectDistance = new Vector2(0f, -10f);

            Font font = ResolveFont();
            eyebrowLabel = CreateText("Step", card, font, 20, FontStyle.Bold, Gold);
            SetRect(eyebrowLabel.rectTransform, -225f, 8f, 225f, 38f);
            AddOutline(eyebrowLabel, RoyalPurpleDark, 1.6f);

            titleLabel = CreateText("Title", card, font, 35, FontStyle.Bold, WarmCream);
            SetRect(titleLabel.rectTransform, -245f, -43f, 245f, 9f);
            titleLabel.resizeTextForBestFit = true;
            titleLabel.resizeTextMinSize = 24;
            titleLabel.resizeTextMaxSize = 35;
            AddOutline(titleLabel, RoyalPurpleDark, 2.2f);

            detailLabel = CreateText("Detail", card, font, 21, FontStyle.Normal, WarmCream);
            SetRect(detailLabel.rectTransform, -235f, -100f, 235f, -42f);
            detailLabel.resizeTextForBestFit = true;
            detailLabel.resizeTextMinSize = 17;
            detailLabel.resizeTextMaxSize = 21;
            AddOutline(detailLabel, RoyalPurpleDark, 1.2f);
        }

        private void BuildSkipButton()
        {
            Image image = CreateImage("Skip Tutorial", canvasRect, roundedSprite,
                new Color(RoyalPurple.r, RoyalPurple.g, RoyalPurple.b, 0.94f));
            image.type = Image.Type.Sliced;
            skipRect = image.rectTransform;
            skipRect.anchorMin = skipRect.anchorMax = new Vector2(0.5f, 0.5f);
            skipRect.pivot = new Vector2(0.5f, 0.5f);
            skipRect.sizeDelta = new Vector2(118f, 48f);

            Outline outline = image.gameObject.AddComponent<Outline>();
            outline.effectColor = Gold;
            outline.effectDistance = new Vector2(2f, -2f);

            skipButton = image.gameObject.AddComponent<Button>();
            skipButton.targetGraphic = image;
            ColorBlock colours = skipButton.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = new Color(1f, 0.92f, 0.70f, 1f);
            colours.pressedColor = new Color(0.78f, 0.70f, 0.90f, 1f);
            colours.fadeDuration = 0.08f;
            skipButton.colors = colours;
            BsButtonSound.Ensure(skipButton.gameObject);

            Text label = CreateText("Label", skipRect, ResolveFont(), 20,
                FontStyle.Bold, WarmCream);
            Stretch(label.rectTransform);
            label.text = "ATLA";
            label.raycastTarget = false;
            AddOutline(label, RoyalPurpleDark, 1.2f);
            skipButton.onClick.AddListener(HandleSkipPressed);
        }

        private void BuildSparkles()
        {
            for (int i = 0; i < 16; i++)
            {
                bool useFxSprite = usingDedicatedFx
                    && tutorialFxSprites[i % FxSparkleCellCount] != null;
                Sprite sprite = useFxSprite
                    ? tutorialFxSprites[i % FxSparkleCellCount]
                    : roundedSprite;
                Image image = CreateImage("Royal Sparkle " + i, canvasRect,
                    sprite, useFxSprite
                        ? Color.white
                        : (i % 3 == 0 ? Lavender : (i % 2 == 0 ? Gold : WarmCream)));
                image.type = useFxSprite ? Image.Type.Simple : Image.Type.Sliced;
                image.preserveAspect = useFxSprite;
                image.raycastTarget = false;
                RectTransform rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                float fxHeight = 72f;
                if (useFxSprite)
                {
                    // Normalise the six motifs' different painted bounds without
                    // trimming the shared 4x2 texture at runtime.
                    switch (i % FxSparkleCellCount)
                    {
                        case 1: fxHeight = 84f; break;
                        case 2: fxHeight = 96f; break;
                        case 3: fxHeight = 90f; break;
                        case 5: fxHeight = 90f; break;
                    }
                }
                rect.sizeDelta = useFxSprite
                    ? new Vector2(fxHeight * 0.56f, fxHeight)
                    : new Vector2(12f, 12f);
                rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
                CanvasGroup group = image.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                sparkles.Add(new Sparkle { Rect = rect, Group = group, Image = image });
            }
        }

        private void StartGuidanceLoops()
        {
            StopGuidanceLoops();
            if (haloRoot != null)
            {
                haloRoot.gameObject.SetActive(true);
                haloRoot.localScale = Vector3.one;
            }
            if (usingDedicatedHalo && haloOuter != null)
                haloOuter.color = Color.white;
            if (handRect != null) handRect.gameObject.SetActive(true);

            haloPulseTween = DOVirtual.Float(0f, 1f, 0.75f, value => haloPulse = value)
                .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine)
                .SetTarget(this).SetUpdate(true);
            // Keep the rectangular spotlight rings fixed. Only the small jewels orbit
            // along the ellipse, so a tall bottle never turns into a wide rotating halo.
            haloRoot.localRotation = Quaternion.identity;
            if (!usingDedicatedHalo)
            {
                haloRotationTween = DOVirtual.Float(0f, Mathf.PI * 2f, 5.2f,
                        value => orbitPhase = value)
                    .SetLoops(-1, LoopType.Restart).SetEase(Ease.Linear)
                    .SetTarget(this).SetUpdate(true);
            }
            handPhaseTween = DOVirtual.Float(0f, 1f, 1.32f, value => handPhase = value)
                .SetLoops(-1, LoopType.Restart).SetEase(Ease.Linear)
                .SetTarget(this).SetUpdate(true);
        }

        private void StopGuidanceLoops()
        {
            KillTween(ref haloPulseTween);
            KillTween(ref haloRotationTween);
            KillTween(ref handPhaseTween);
            haloPulse = 0f;
            orbitPhase = 0f;
            handPhase = 0f;
        }

        private void UpdateTargetGeometry()
        {
            if (canvasRect == null || targetBottle == null) return;
            Rect full = canvasRect.rect;
            if (full.width <= 1f || full.height <= 1f) return;

            if (!TryGetBottleRect(targetBottle, targetCamera, out Rect found))
            {
                targetPoint = Vector2.zero;
                found = new Rect(-75f, -100f, 150f, 200f);
            }

            const float padding = 28f;
            Vector2 foundCentre = found.center;
            float availableWidth = Mathf.Max(1f, full.width - 8f);
            float availableHeight = Mathf.Max(1f, full.height - 8f);
            float foundWidth = Mathf.Min(availableWidth,
                Mathf.Max(128f, found.width + padding * 2f));
            float foundHeight = Mathf.Min(availableHeight,
                Mathf.Max(158f, found.height + padding * 2f));
            float centreX = Mathf.Clamp(foundCentre.x,
                full.xMin + 4f + foundWidth * 0.5f,
                full.xMax - 4f - foundWidth * 0.5f);
            float centreY = Mathf.Clamp(foundCentre.y,
                full.yMin + 4f + foundHeight * 0.5f,
                full.yMax - 4f - foundHeight * 0.5f);
            found = Rect.MinMaxRect(
                centreX - foundWidth * 0.5f, centreY - foundHeight * 0.5f,
                centreX + foundWidth * 0.5f, centreY + foundHeight * 0.5f);
            targetRect = found;
            // The hole must remain fully on-screen to preserve the input mask, but the
            // fingertip should still point at the visible part of the real bottle.
            targetPoint = new Vector2(
                Mathf.Clamp(foundCentre.x, full.xMin + 12f, full.xMax - 12f),
                Mathf.Clamp(foundCentre.y, full.yMin + 12f, full.yMax - 12f));

            SetFullScrim(false);
            SetRect(scrims[0].rectTransform, full.xMin, full.yMin, full.xMax, found.yMin);
            SetRect(scrims[1].rectTransform, full.xMin, found.yMax, full.xMax, full.yMax);
            SetRect(scrims[2].rectTransform, full.xMin, found.yMin, found.xMin, found.yMax);
            SetRect(scrims[3].rectTransform, found.xMax, found.yMin, full.xMax, found.yMax);

            float pulse = haloPulse * 16f;
            haloRoot.anchoredPosition = found.center;
            haloRoot.sizeDelta = new Vector2(found.width + 34f + pulse,
                found.height + 34f + pulse);
            PositionOrbitJewels();

            Rect safe = SafeCanvasRect(full);
            float halfCard = card != null ? card.sizeDelta.y * 0.5f : 146f;
            desiredCardPosition = found.center.y > 0f
                ? new Vector2(0f, safe.yMin + halfCard + 64f)
                : new Vector2(0f, safe.yMax - halfCard - 64f);
            if (skipRect != null)
                skipRect.anchoredPosition = new Vector2(safe.xMax - 70f, safe.yMax - 30f);

            if (travelFromBottle != null
                && TryGetBottleRect(travelFromBottle, targetCamera, out Rect fromRect))
                travelFromPoint = fromRect.center;
            else
                travelFromPoint = targetPoint;
        }

        private void UpdateHandGeometry()
        {
            if (handRect == null || handGroup == null) return;

            float bob = Mathf.Sin(handPhase * Mathf.PI * 2f) * 3f;
            float press = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.30f, 0.46f, handPhase))
                * (1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(0.46f, 0.64f, handPhase)));
            // This game expects a second tap, not a drag. The trail explains source → target,
            // while the fingertip stays on the target and performs a clear tap beat.
            Vector2 position = targetPoint + Vector2.up * (12f + bob - press * 12f);
            float scale = Mathf.Lerp(1f, 0.86f, press);
            handRect.localRotation = Quaternion.Euler(
                0f, 0f, usingDedicatedHand ? 0f : -8f);

            handRect.anchoredPosition = position;
            handRect.localScale = Vector3.one * scale;
            handGroup.alpha = 1f;
        }

        private void UpdateTrailGeometry()
        {
            bool show = travelFromBottle != null;
            SetTrailVisible(show);
            if (!show) return;

            for (int i = 0; i < trailDots.Length; i++)
            {
                float t = (i + 1f) / (trailDots.Length + 1f);
                Vector2 arc = Vector2.up * Mathf.Sin(t * Mathf.PI) * 46f;
                RectTransform rect = trailDots[i].rectTransform;
                rect.anchoredPosition = Vector2.Lerp(travelFromPoint, targetPoint, t) + arc;
                float wave = 0.78f + 0.24f * Mathf.Sin(
                    Time.unscaledTime * 7f - i * 0.75f);
                rect.localScale = Vector3.one * wave;
                Color colour = trailDots[i].color;
                colour.a = 0.52f + 0.36f * wave;
                trailDots[i].color = colour;
            }
        }

        private void PositionOrbitJewels()
        {
            if (haloRoot == null) return;
            float rx = haloRoot.sizeDelta.x * 0.5f;
            float ry = haloRoot.sizeDelta.y * 0.5f;
            for (int i = 0; i < orbitJewels.Length; i++)
            {
                if (orbitJewels[i] == null) continue;
                float angle = Mathf.PI * 2f * i / orbitJewels.Length + orbitPhase;
                RectTransform rect = orbitJewels[i].rectTransform;
                rect.anchoredPosition = new Vector2(
                    Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry);
                float scale = i % 2 == 0 ? 1f : 0.72f;
                rect.localScale = Vector3.one * scale;
            }
        }

        private bool TryGetBottleRect(LiquidBottle bottle, Camera camera,
                                      out Rect localRect)
        {
            localRect = default;
            if (bottle == null || camera == null || canvasRect == null) return false;

            SpriteRenderer[] renderers = bottle.GetComponentsInChildren<SpriteRenderer>(false);
            bool found = false;
            Bounds bounds = default;

            // BottleShell's FrontGlass is a stable full-vessel silhouette. Prefer it so
            // temporary reveal/rejection overlays and wide ground shadows cannot make the
            // tutorial spotlight jump while they animate.
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (renderer == null || renderer.name != "FrontGlass" || !renderer.enabled
                    || !renderer.gameObject.activeInHierarchy || renderer.sprite == null)
                    continue;
                bounds = renderer.bounds;
                found = true;
                break;
            }

            bool collectFallback = !found;
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (!collectFallback) break;
                if (renderer == null || !renderer.enabled
                    || !renderer.gameObject.activeInHierarchy || renderer.sprite == null
                    || renderer.name == "Shadow"
                    || renderer.name == "InvalidMoveHighlight"
                    || renderer.name == "MechanicRevealFeedback")
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }
            if (!found) return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, bounds.center.z),
                new Vector3(min.x, max.y, bounds.center.z),
                new Vector3(max.x, min.y, bounds.center.z),
                new Vector3(max.x, max.y, bounds.center.z),
            };

            Vector2 localMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 localMax = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 screen = camera.WorldToScreenPoint(corners[i]);
                if (screen.z <= 0f) return false;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        canvasRect, screen, null, out Vector2 local))
                    return false;
                localMin = Vector2.Min(localMin, local);
                localMax = Vector2.Max(localMax, local);
            }

            localRect = Rect.MinMaxRect(localMin.x, localMin.y, localMax.x, localMax.y);
            return true;
        }

        private Rect SafeCanvasRect(Rect fallback)
        {
            Rect safe = Screen.safeArea;
            if (safe.width <= 1f || safe.height <= 1f || canvasRect == null) return fallback;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, safe.min, null, out Vector2 min)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, safe.max, null, out Vector2 max))
                return fallback;
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private void SetFullScrim(bool full)
        {
            if (canvasRect == null) return;
            Rect rect = canvasRect.rect;
            for (int i = 0; i < scrims.Length; i++)
                scrims[i].gameObject.SetActive(full ? i == 0 : true);
            if (full) SetRect(scrims[0].rectTransform, rect.xMin, rect.yMin,
                rect.xMax, rect.yMax);
        }

        private void SetScrimColour(Color colour)
        {
            for (int i = 0; i < scrims.Length; i++)
                if (scrims[i] != null) scrims[i].color = colour;
        }

        private void SetTrailVisible(bool shown)
        {
            for (int i = 0; i < trailDots.Length; i++)
                if (trailDots[i] != null) trailDots[i].gameObject.SetActive(shown);
        }

        private void EmitSparkles(Vector2 centre, bool restrained,
                                  int countOverride = -1,
                                  float distanceOverride = -1f,
                                  bool includeFlash = true)
        {
            KillSparkles();
            int count = countOverride > 0
                ? Mathf.Min(countOverride, sparkles.Count)
                : (restrained ? 7 : sparkles.Count);
            float distance = distanceOverride > 0f
                ? distanceOverride
                : (restrained ? 76f : 170f);
            for (int i = 0; i < count; i++)
            {
                Sparkle sparkle = sparkles[i];
                float angle = (Mathf.PI * 2f * i / count) + (i % 2) * 0.13f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 end = centre + direction * distance * (0.72f + (i % 4) * 0.09f);
                sparkle.Rect.anchoredPosition = centre;
                sparkle.Rect.localScale = Vector3.zero;
                sparkle.Group.alpha = 0f;
                sparkle.Rect.localRotation = Quaternion.Euler(0f, 0f, 45f);

                float delay = i * 0.012f;
                Sequence sequence = DOTween.Sequence()
                    .SetTarget(this).SetUpdate(true)
                    .AppendInterval(delay)
                    .AppendCallback(() => sparkle.Group.alpha = 1f)
                    .Join(sparkle.Rect.DOAnchorPos(end, 0.52f).SetEase(Ease.OutCubic))
                    .Join(sparkle.Rect.DOScale(1.25f, 0.18f).SetEase(Ease.OutBack))
                    .Join(sparkle.Rect.DORotate(
                        new Vector3(0f, 0f, 225f), 0.52f, RotateMode.FastBeyond360))
                    .Append(sparkle.Group.DOFade(0f, 0.20f).SetEase(Ease.InQuad));
                sparkleTweens.Add(sequence);
            }

            if (includeFlash && fullFlash != null)
            {
                KillTween(ref flashTween);
                fullFlash.color = new Color(1f, 0.78f, 0.20f, 0f);
                flashTween = DOTween.Sequence()
                    .SetTarget(this).SetUpdate(true)
                    .Append(fullFlash.DOFade(restrained ? 0.08f : 0.16f, 0.08f))
                    .Append(fullFlash.DOFade(0f, 0.25f));
            }
        }

        private void HandleSkipPressed()
        {
            if (!visible || completing) return;
            SkipRequested?.Invoke();
        }

        private void KillOwnedTweens()
        {
            KillTransition();
            StopGuidanceLoops();
            KillTween(ref flashTween);
            KillSparkles();
        }

        private void KillTransition()
        {
            Sequence sequence = transitionSequence;
            transitionSequence = null;
            cardPositionTweened = false;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
        }

        private void TrackCardPosition(Sequence sequence)
        {
            if (sequence == null) return;
            cardPositionTweened = true;
            sequence.OnComplete(() =>
            {
                if (ReferenceEquals(transitionSequence, sequence))
                    cardPositionTweened = false;
            });
        }

        private void KillSparkles()
        {
            for (int i = 0; i < sparkleTweens.Count; i++)
            {
                Tween tween = sparkleTweens[i];
                if (tween != null && tween.IsActive()) tween.Kill(false);
            }
            sparkleTweens.Clear();
            for (int i = 0; i < sparkles.Count; i++)
            {
                sparkles[i].Group.alpha = 0f;
                sparkles[i].Rect.localScale = Vector3.zero;
            }
        }

        private static void KillTween(ref Tween tween)
        {
            Tween current = tween;
            tween = null;
            if (current != null && current.IsActive()) current.Kill(false);
        }

        private Font ResolveFont()
        {
            if (tutorialFont != null) return tutorialFont;
            if (theme != null && theme.UiFont != null) return theme.UiFont;
            Text[] labels = FindObjectsByType<Text>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null && labels[i].font != null) return labels[i].font;
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static Sprite CreateRoundedSprite(out Texture2D texture)
        {
            const int size = 64;
            const float radius = 15f;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Royal Tutorial Rounded UI (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            Vector2 centre = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            Vector2 box = new Vector2(size * 0.5f - radius - 1f,
                size * 0.5f - radius - 1f);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 point = new Vector2(Mathf.Abs(x - centre.x),
                    Mathf.Abs(y - centre.y));
                Vector2 delta = Vector2.Max(point - box, Vector2.zero);
                float distance = delta.magnitude - radius;
                byte alpha = (byte)Mathf.RoundToInt(
                    255f * (1f - Mathf.Clamp01(distance + 0.5f)));
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(18f, 18f, 18f, 18f));
            sprite.name = "Royal Tutorial Rounded UI (Runtime)";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static Sprite CreateRingSprite(out Texture2D texture)
        {
            const int size = 128;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Royal Tutorial Ring (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - centre) / centre;
                float dy = (y - centre) / centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float core = 1f - Mathf.Clamp01(Mathf.Abs(distance - 0.79f) / 0.045f);
                float glow = 1f - Mathf.Clamp01(Mathf.Abs(distance - 0.79f) / 0.19f);
                float alpha = Mathf.Clamp01(core * 0.86f + glow * 0.34f);
                pixels[y * size + x] = new Color32(255, 255, 255,
                    (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "Royal Tutorial Ring (Runtime)";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static Image CreateImage(string name, Transform parent, Sprite sprite,
                                         Color colour)
        {
            GameObject go = NewUiObject(name, parent, typeof(Image));
            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Font font,
                                       int size, FontStyle style, Color colour)
        {
            GameObject go = NewUiObject(name, parent, typeof(Text));
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = colour;
            text.raycastTarget = false;
            return text;
        }

        private static void AddOutline(Text text, Color colour, float distance)
        {
            Outline outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = colour;
            outline.effectDistance = new Vector2(distance, -distance);
            outline.useGraphicAlpha = true;
        }

        private static GameObject NewUiObject(string name, Transform parent,
                                              params Type[] components)
        {
            var types = new Type[components.Length + 2];
            types[0] = typeof(RectTransform);
            types[1] = typeof(CanvasRenderer);
            Array.Copy(components, 0, types, 2, components.Length);
            var go = new GameObject(name, types);
            go.layer = UiLayer();
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        private static int UiLayer()
        {
            int layer = LayerMask.NameToLayer("UI");
            return layer >= 0 ? layer : 0;
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void SetRect(RectTransform rect, float xMin, float yMin,
                                    float xMax, float yMax)
        {
            if (rect == null) return;
            float left = Mathf.Min(xMin, xMax);
            float right = Mathf.Max(xMin, xMax);
            float bottom = Mathf.Min(yMin, yMax);
            float top = Mathf.Max(yMin, yMax);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2((left + right) * 0.5f,
                (bottom + top) * 0.5f);
            rect.sizeDelta = new Vector2(Mathf.Max(0f, right - left),
                Mathf.Max(0f, top - bottom));
        }
    }
}
