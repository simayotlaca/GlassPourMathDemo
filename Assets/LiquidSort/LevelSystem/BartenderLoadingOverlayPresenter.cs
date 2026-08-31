using System;
using System.Collections;
using DG.Tweening;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Runtime-only, full-screen level loading presentation. The level domain in this
    /// project loads synchronously in the active scene, so callers display this view for
    /// at least one rendered frame before invoking the load command and then advance it
    /// with real lifecycle milestones. The component owns presentation only; it never
    /// mutates campaign or board state.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderLoadingOverlayPresenter : MonoBehaviour
    {
        private const string BackgroundResourcePath =
            "Ui/Loading/RoyalGlass_LoadingBackground_Runtime";
        private const string ThemeResourcePath = "Ui/MainMenu/BartenderMainMenuTheme";
        private const string TurkishFontResourcePath =
            "Ui/Tutorial/Nunito_Turkish_ExtraBold";
        private const int SortingOrder = 32760;
        private const float TrackWidth = 774f;
        private const float TrackHeight = 58f;

        private static readonly Color RoyalPurple =
            new Color32(0x2F, 0x18, 0x66, 0xFF);
        private static readonly Color RoyalPurpleDark =
            new Color32(0x17, 0x09, 0x32, 0xFF);
        private static readonly Color WarmCream =
            new Color32(0xFF, 0xF1, 0xC8, 0xFF);
        private static readonly Color Gold =
            new Color32(0xFF, 0xC8, 0x3D, 0xFF);
        private static readonly Color Turquoise =
            new Color32(0x09, 0xA9, 0xE6, 0xFF);
        private static readonly Color Yellow =
            new Color32(0xF3, 0xC9, 0x28, 0xFF);
        private static readonly Color Coral =
            new Color32(0xE8, 0x45, 0x3C, 0xFF);
        private static readonly Color Pink =
            new Color32(0xF4, 0x4F, 0x8D, 0xFF);

        private Canvas loadingCanvas;
        private GameObject canvasObject;
        private CanvasGroup canvasGroup;
        private RectTransform canvasRect;
        private RectTransform barFrame;
        private RectTransform fillClip;
        private RectTransform frontCap;
        private RectTransform sheen;
        private Image frontCapImage;
        private Text titleLabel;
        private Text percentLabel;

        private Sprite roundedSprite;
        private Texture2D roundedTexture;
        private float displayedProgress;
        private float nextDotsAt;
        private int dotCount;
        private bool visible;

        private Tween fadeTween;
        private Tween progressTween;
        private Tween shimmerTween;
        private Tween pulseTween;

        public bool Visible => visible;
        public float DisplayedProgress => displayedProgress;

        public static BartenderLoadingOverlayPresenter Attach(GameObject host)
        {
            if (host == null) return null;
            BartenderLoadingOverlayPresenter overlay =
                host.GetComponent<BartenderLoadingOverlayPresenter>();
            if (overlay == null)
                overlay = FindFirstObjectByType<BartenderLoadingOverlayPresenter>(
                    FindObjectsInactive.Include);
            return overlay != null
                ? overlay
                : host.AddComponent<BartenderLoadingOverlayPresenter>();
        }

        public void Prewarm()
        {
            EnsureView();
            HideImmediate();
        }

        public bool Begin()
        {
            EnsureView();
            if (loadingCanvas == null || canvasGroup == null) return false;

            KillOwnedTweens();
            visible = true;
            dotCount = 1;
            nextDotsAt = Time.unscaledTime + 0.32f;
            titleLabel.text = "YÜKLENİYOR.";
            SetProgressImmediate(0f);

            loadingCanvas.gameObject.SetActive(true);
            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;
            barFrame.localScale = new Vector3(0.985f, 0.985f, 1f);

            fadeTween = canvasGroup.DOFade(1f, 0.16f)
                .SetEase(Ease.OutQuad).SetUpdate(true).SetTarget(this);
            pulseTween = barFrame.DOScale(1.008f, 0.92f)
                .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine)
                .SetUpdate(true).SetTarget(this);
            StartShimmer();
            return true;
        }

        public void AdvanceTo(float normalizedProgress, float duration)
        {
            if (!visible) return;
            float target = Mathf.Clamp01(normalizedProgress);
            target = Mathf.Max(displayedProgress, target);
            KillTween(ref progressTween);
            if (duration <= 0.001f)
            {
                SetProgressImmediate(target);
                return;
            }

            progressTween = DOVirtual.Float(displayedProgress, target, duration,
                    SetProgressImmediate)
                .SetEase(Ease.OutCubic).SetUpdate(true).SetTarget(this);
        }

        public IEnumerator CompleteAndHide()
        {
            if (!visible) yield break;
            AdvanceTo(1f, 0.28f);
            yield return new WaitForSecondsRealtime(0.34f);
            if (!visible) yield break;
            FadeOut(0.18f);
            yield return new WaitForSecondsRealtime(0.20f);
            if (visible) HideImmediate();
        }

        public IEnumerator CancelAndHide()
        {
            if (!visible) yield break;
            FadeOut(0.12f);
            yield return new WaitForSecondsRealtime(0.14f);
            if (visible) HideImmediate();
        }

        public void HideImmediate()
        {
            KillOwnedTweens();
            visible = false;
            displayedProgress = 0f;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
            }
            if (loadingCanvas != null) loadingCanvas.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!visible || titleLabel == null || Time.unscaledTime < nextDotsAt) return;
            dotCount = dotCount % 3 + 1;
            titleLabel.text = "YÜKLENİYOR" + new string('.', dotCount);
            nextDotsAt = Time.unscaledTime + 0.32f;
        }

        private void OnDisable()
        {
            if (Application.isPlaying) HideImmediate();
        }

        private void OnDestroy()
        {
            KillOwnedTweens();
            if (canvasObject != null) Destroy(canvasObject);
            if (roundedSprite != null) Destroy(roundedSprite);
            if (roundedTexture != null) Destroy(roundedTexture);
        }

        private void EnsureView()
        {
            if (loadingCanvas != null || !Application.isPlaying) return;

            BartenderMainMenuTheme theme =
                Resources.Load<BartenderMainMenuTheme>(ThemeResourcePath);
            Texture2D backgroundTexture =
                Resources.Load<Texture2D>(BackgroundResourcePath);
            Font font = Resources.Load<Font>(TurkishFontResourcePath);
            if (font == null && theme != null) font = theme.UiFont;
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            roundedSprite = CreateRoundedSprite(out roundedTexture);

            canvasObject = new GameObject(
                "Royal Level Loading Canvas (Runtime)", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster),
                typeof(CanvasGroup));
            canvasObject.hideFlags = HideFlags.DontSave;
            canvasObject.layer = UiLayer();
            Scene ownerScene = gameObject.scene;
            if (ownerScene.IsValid() && ownerScene.isLoaded)
                SceneManager.MoveGameObjectToScene(canvasObject, ownerScene);

            loadingCanvas = canvasObject.GetComponent<Canvas>();
            loadingCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            loadingCanvas.overrideSorting = true;
            loadingCanvas.sortingOrder = SortingOrder;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0f;

            canvasGroup = canvasObject.GetComponent<CanvasGroup>();
            canvasRect = canvasObject.GetComponent<RectTransform>();
            Stretch(canvasRect);

            RawImage background = CreateRawImage("Loading Artwork", canvasRect,
                backgroundTexture, Color.white);
            RectTransform backgroundRect = background.rectTransform;
            backgroundRect.anchorMin = backgroundRect.anchorMax = new Vector2(0.5f, 0.5f);
            backgroundRect.pivot = new Vector2(0.5f, 0.5f);
            backgroundRect.sizeDelta = new Vector2(1080f, 1920f);
            AspectRatioFitter fitter = background.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = 9f / 16f;
            background.raycastTarget = true;

            GameObject safeObject = NewUiObject("Loading Safe Area", canvasRect);
            RectTransform safeRect = safeObject.GetComponent<RectTransform>();
            Stretch(safeRect);
            BsSafeArea safeArea = safeObject.AddComponent<BsSafeArea>();
            safeArea.ApplyTop = false;
            safeArea.ApplyBottom = true;
            safeArea.ApplySides = true;

            BuildCopy(safeRect, font);
            BuildProgressBar(safeRect, font);

            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
            loadingCanvas.gameObject.SetActive(false);
        }

        private void BuildCopy(RectTransform parent, Font font)
        {
            titleLabel = CreateText("Loading Title", parent, font, 72,
                FontStyle.Bold, WarmCream);
            RectTransform rect = titleLabel.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, 370f);
            rect.sizeDelta = new Vector2(820f, 116f);
            titleLabel.resizeTextForBestFit = true;
            titleLabel.resizeTextMinSize = 48;
            titleLabel.resizeTextMaxSize = 72;
            AddOutline(titleLabel, RoyalPurpleDark, 3.2f);
            Shadow shadow = titleLabel.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0.05f, 0.01f, 0.12f, 0.72f);
            shadow.effectDistance = new Vector2(0f, -6f);
        }

        private void BuildProgressBar(RectTransform parent, Font font)
        {
            Image glow = CreateImage("Progress Glow", parent, roundedSprite,
                new Color(Gold.r, Gold.g, Gold.b, 0.20f));
            glow.type = Image.Type.Sliced;
            SetBottomRect(glow.rectTransform, 0f, 218f, 854f, 126f);
            glow.raycastTarget = false;

            Image frame = CreateImage("Progress Gold Frame", parent, roundedSprite, Gold);
            frame.type = Image.Type.Sliced;
            barFrame = frame.rectTransform;
            SetBottomRect(barFrame, 0f, 218f, 830f, 106f);
            frame.raycastTarget = false;

            Image inner = CreateImage("Progress Track", barFrame, roundedSprite,
                RoyalPurpleDark);
            inner.type = Image.Type.Sliced;
            RectTransform innerRect = inner.rectTransform;
            innerRect.anchorMin = innerRect.anchorMax = new Vector2(0.5f, 0.5f);
            innerRect.pivot = new Vector2(0.5f, 0.5f);
            innerRect.anchoredPosition = Vector2.zero;
            innerRect.sizeDelta = new Vector2(794f, 78f);
            inner.raycastTarget = false;

            GameObject clipObject = NewUiObject("Progress Fill Clip", innerRect,
                typeof(RectMask2D));
            fillClip = clipObject.GetComponent<RectTransform>();
            fillClip.anchorMin = fillClip.anchorMax = new Vector2(0f, 0.5f);
            fillClip.pivot = new Vector2(0f, 0.5f);
            fillClip.anchoredPosition = new Vector2(10f, 0f);
            fillClip.sizeDelta = new Vector2(0f, TrackHeight);

            RectTransform liquidStrip = NewUiObject("Liquid Colour Strip", fillClip)
                .GetComponent<RectTransform>();
            liquidStrip.anchorMin = liquidStrip.anchorMax = new Vector2(0f, 0.5f);
            liquidStrip.pivot = new Vector2(0f, 0.5f);
            liquidStrip.anchoredPosition = Vector2.zero;
            liquidStrip.sizeDelta = new Vector2(TrackWidth, TrackHeight);

            Color[] colours = { Turquoise, Yellow, Coral, Pink };
            float segmentWidth = TrackWidth / colours.Length;
            for (int i = 0; i < colours.Length; i++)
            {
                Image segment = CreateImage("Liquid Segment " + i, liquidStrip,
                    null, colours[i]);
                RectTransform segmentRect = segment.rectTransform;
                segmentRect.anchorMin = segmentRect.anchorMax = new Vector2(0f, 0.5f);
                segmentRect.pivot = new Vector2(0f, 0.5f);
                segmentRect.anchoredPosition = new Vector2(i * segmentWidth, 0f);
                segmentRect.sizeDelta = new Vector2(segmentWidth + 1f, TrackHeight);
                segment.raycastTarget = false;
            }

            Image topLight = CreateImage("Liquid Top Light", liquidStrip, null,
                new Color(1f, 1f, 1f, 0.18f));
            RectTransform topLightRect = topLight.rectTransform;
            topLightRect.anchorMin = new Vector2(0f, 0.62f);
            topLightRect.anchorMax = new Vector2(1f, 0.88f);
            topLightRect.offsetMin = Vector2.zero;
            topLightRect.offsetMax = Vector2.zero;
            topLight.raycastTarget = false;

            Image leftCap = CreateImage("Liquid Start Cap", liquidStrip,
                roundedSprite, Turquoise);
            leftCap.type = Image.Type.Simple;
            RectTransform leftCapRect = leftCap.rectTransform;
            leftCapRect.anchorMin = leftCapRect.anchorMax = new Vector2(0f, 0.5f);
            leftCapRect.pivot = new Vector2(0.5f, 0.5f);
            leftCapRect.anchoredPosition = new Vector2(TrackHeight * 0.5f, 0f);
            leftCapRect.sizeDelta = new Vector2(TrackHeight, TrackHeight);
            leftCap.raycastTarget = false;

            frontCapImage = CreateImage("Liquid Leading Cap", innerRect,
                roundedSprite, Turquoise);
            frontCapImage.type = Image.Type.Simple;
            frontCap = frontCapImage.rectTransform;
            frontCap.anchorMin = frontCap.anchorMax = new Vector2(0f, 0.5f);
            frontCap.pivot = new Vector2(0.5f, 0.5f);
            frontCap.sizeDelta = new Vector2(TrackHeight, TrackHeight);
            frontCapImage.raycastTarget = false;

            Image sheenImage = CreateImage("Liquid Sheen", fillClip, null,
                new Color(1f, 1f, 1f, 0.20f));
            sheen = sheenImage.rectTransform;
            sheen.anchorMin = sheen.anchorMax = new Vector2(0f, 0.5f);
            sheen.pivot = new Vector2(0.5f, 0.5f);
            sheen.sizeDelta = new Vector2(72f, TrackHeight * 1.8f);
            sheen.localRotation = Quaternion.Euler(0f, 0f, -18f);
            sheenImage.raycastTarget = false;

            percentLabel = CreateText("Loading Percent", barFrame, font, 31,
                FontStyle.Bold, WarmCream);
            Stretch(percentLabel.rectTransform);
            percentLabel.raycastTarget = false;
            AddOutline(percentLabel, RoyalPurpleDark, 1.8f);
        }

        private void SetProgressImmediate(float value)
        {
            displayedProgress = Mathf.Clamp01(value);
            if (fillClip != null)
                fillClip.sizeDelta = new Vector2(TrackWidth * displayedProgress,
                    TrackHeight);

            if (frontCap != null)
            {
                float width = TrackWidth * displayedProgress;
                frontCap.gameObject.SetActive(displayedProgress > 0.008f);
                frontCap.anchoredPosition = new Vector2(
                    10f + Mathf.Max(TrackHeight * 0.5f,
                        width - TrackHeight * 0.5f), 0f);
            }
            if (frontCapImage != null)
                frontCapImage.color = ProgressColour(displayedProgress);
            if (percentLabel != null)
                percentLabel.text = Mathf.RoundToInt(displayedProgress * 100f) + "%";
        }

        private void StartShimmer()
        {
            if (sheen == null) return;
            sheen.anchoredPosition = new Vector2(-90f, 0f);
            shimmerTween = sheen.DOAnchorPosX(TrackWidth + 90f, 1.05f)
                .SetDelay(0.18f).SetLoops(-1, LoopType.Restart)
                .SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(this);
        }

        private void FadeOut(float duration)
        {
            KillTween(ref fadeTween);
            Tween tween = canvasGroup.DOFade(0f, duration)
                .SetEase(Ease.InQuad).SetUpdate(true).SetTarget(this);
            fadeTween = tween;
            tween.OnComplete(() =>
            {
                if (ReferenceEquals(fadeTween, tween)) fadeTween = null;
                HideImmediate();
            });
        }

        private void KillOwnedTweens()
        {
            KillTween(ref fadeTween);
            KillTween(ref progressTween);
            KillTween(ref shimmerTween);
            KillTween(ref pulseTween);
        }

        private static void KillTween(ref Tween tween)
        {
            Tween current = tween;
            tween = null;
            if (current != null && current.IsActive()) current.Kill(false);
        }

        private static Color ProgressColour(float progress)
        {
            float scaled = Mathf.Clamp01(progress) * 4f;
            if (scaled <= 1f) return Turquoise;
            if (scaled <= 2f) return Color.Lerp(Turquoise, Yellow, scaled - 1f);
            if (scaled <= 3f) return Color.Lerp(Yellow, Coral, scaled - 2f);
            return Color.Lerp(Coral, Pink, scaled - 3f);
        }

        private static Sprite CreateRoundedSprite(out Texture2D texture)
        {
            const int size = 64;
            const float radius = 15f;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Royal Loading Rounded UI (Runtime)",
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
            sprite.name = "Royal Loading Rounded UI (Runtime)";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static RawImage CreateRawImage(string name, Transform parent,
                                               Texture texture, Color colour)
        {
            GameObject go = NewUiObject(name, parent, typeof(RawImage));
            RawImage image = go.GetComponent<RawImage>();
            image.texture = texture;
            image.color = colour;
            return image;
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
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
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

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetBottomRect(RectTransform rect, float x, float y,
                                          float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static int UiLayer()
        {
            int layer = LayerMask.NameToLayer("UI");
            return layer >= 0 ? layer : 0;
        }
    }
}
