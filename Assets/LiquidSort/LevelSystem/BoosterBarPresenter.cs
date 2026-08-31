using BartenderSort.Core;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Alt şeritteki üç booster düğmesi: geri al, +süre, karıştır.
    ///
    /// OPTİMİSTİK UI YOK — <see cref="BartenderPausePresenter"/> ile aynı disiplin.
    /// Düğme yalnızca komutu yollar; sayaç, kural motoru komutu kabul edip
    /// <see cref="BartenderLevelController.BoostersChanged"/> geri geldiğinde düşer.
    /// Reddedilen bir dokunuşta ekranda hiçbir şey kıpırdamaz.
    ///
    /// Orta düğme ücretli bir tekliftir. Controller önce terminal/timer/stock
    /// invariantlarını doğrular, sonra tek ekonomi otoritesinden altını düşer ve yalnız
    /// o anda açık olan süreli siparişlere bonusu atomik olarak uygular.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoosterBarPresenter : MonoBehaviour
    {
        private const string MainMenuThemeResourcePath =
            "Ui/MainMenu/BartenderMainMenuTheme";
        private const string RuntimeCoinHudName = "Coin Balance - Runtime";

        private static readonly Color RoyalBrown =
            new Color32(0x52, 0x30, 0x22, 0xFF);
        private static readonly Color RoyalGold =
            new Color32(0xFF, 0xC8, 0x3D, 0xFF);
        private static readonly Color SpendRed =
            new Color32(0xE7, 0x4E, 0x54, 0xFF);

        [Header("Rig references")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Seat/portal/senkronizasyon sunum bariyeri buradan okunur.")]
        [SerializeField] private BartenderShelfLevelView shelfView;
        [Tooltip("Opsiyonel. Dökme sürerken booster kabul edilmesin diye okunur.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;

        [Header("Düğmeler")]
        [SerializeField] private Button undoButton = null;
        [FormerlySerializedAs("extraGlassButton")]
        [SerializeField] private Button addTimeButton = null;
        [SerializeField] private Button shuffleButton = null;

        [Header("+Süre teklifi")]
        [SerializeField, Min(1f)] private float addTimeSeconds = 15f;
        [SerializeField, Min(1)] private int addTimeCoinCost = 900;

        [Header("Sayaçlar / fiyatlar (opsiyonel)")]
        [SerializeField] private Text undoCountLabel = null;
        [FormerlySerializedAs("extraGlassCountLabel")]
        [SerializeField] private Text addTimePriceLabel = null;
        [SerializeField] private Text shuffleCountLabel = null;
        [SerializeField] private TMP_Text undoCountRichLabel = null;
        [FormerlySerializedAs("extraGlassCountRichLabel")]
        [SerializeField] private TMP_Text addTimePriceRichLabel = null;
        [SerializeField] private TMP_Text shuffleCountRichLabel = null;
        [SerializeField] private Text coinBalanceLabel = null;
        [SerializeField] private TMP_Text coinBalanceRichLabel = null;
        [Tooltip("Stok bu değerin üstündeyken sayaç yazılmaz. MVP'de stoklar 99, "
               + "yani her düğmenin altında '99' yazması bilgi değil gürültü olurdu.")]
        [SerializeField, Min(0)] private int hideCountAbove = 20;

        [Header("Altın harcama feedback'i")]
        [Tooltip("+Süre satın alındığında bakiyenin eski değerden yeni değere iniş süresi.")]
        [SerializeField, Range(0.4f, 0.6f)] private float coinCountDownDuration = 0.5f;

        private BartenderLevelController subscribedController;

        private RectTransform coinHudRoot;
        private Image coinHudFlash;
        private Text coinSpendFeedbackLabel;
        private CanvasGroup coinSpendFeedbackGroup;
        private RectTransform coinSpendFeedbackRect;
        private Vector3 coinHudRestScale = Vector3.one;
        private Vector2 coinSpendFeedbackRestPosition;

        private Sequence coinFeedbackSequence;
        private int coinFeedbackRevision;
        private bool coinBalanceInitialized;
        private int trackedCoinBalance;
        private int displayedCoinBalance;

        // CoinsChanged kaynak bilgisi taşımadığı için yalnız bu senkron çağrı aralığında
        // gelen, beklenen tutardaki azalış +Süre harcaması olarak sunulur. Win ödülü veya
        // ücretli retry gibi başka ekonomi olayları yanlışlıkla "-altın" üretmez.
        private bool timePurchaseInFlight;
        private int expectedTimePurchaseOldBalance;
        private int expectedTimePurchaseCost;

        public string LastRejection { get; private set; }
        private float EffectiveAddTimeSeconds =>
            addTimeSeconds > 0f && !float.IsNaN(addTimeSeconds)
            && !float.IsInfinity(addTimeSeconds) ? addTimeSeconds : 15f;
        private int EffectiveAddTimeCoinCost => addTimeCoinCost > 0 ? addTimeCoinCost : 900;
        private float EffectiveCoinCountDownDuration =>
            coinCountDownDuration >= 0.4f && coinCountDownDuration <= 0.6f
                ? coinCountDownDuration
                : 0.5f;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            EnsureEconomyLabels();
            InitializeCoinBalanceProjection();
            HookButtons();
            Subscribe();
            Refresh();
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnhookButtons();
            CancelCoinFeedback(true);
            coinBalanceInitialized = false;
            timePurchaseInFlight = false;
            expectedTimePurchaseOldBalance = 0;
            expectedTimePurchaseCost = 0;
        }

        private void OnDestroy() => CancelCoinFeedback(false);

        /// <summary>Authoring API for an editor builder.</summary>
        public void ConfigureSceneBindings(BartenderLevelController levelController,
                                           BartenderShelfLevelView view,
                                           BartenderPourInteraction pour,
                                           Button undo, Button addTime, Button shuffle)
        {
            Unsubscribe();
            UnhookButtons();
            controller = levelController;
            shelfView = view;
            pourInteraction = pour;
            undoButton = undo;
            addTimeButton = addTime;
            shuffleButton = shuffle;
            if (!isActiveAndEnabled) return;
            EnsureEconomyLabels();
            InitializeCoinBalanceProjection();
            HookButtons();
            Subscribe();
            Refresh();
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "BartenderShelfLevelView Inspector referansı eksik; booster "
                       + "sunum bariyerini okuyamaz.";
                return false;
            }
            if (undoButton == null || addTimeButton == null || shuffleButton == null)
            {
                reason = "Üç booster düğmesinin üçü de bağlı olmalı.";
                return false;
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Booster Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log("Booster şeridi: bağlantılar geçerli.", this);
            else
                Debug.LogError("Booster bar binding error: " + reason, this);
        }

        // ---- Komutlar ---------------------------------------------------------------

        public void RequestUndo()
        {
            if (!CanCommand()) return;
            if (!controller.TryUndo(out string reason)) LastRejection = reason;
        }

        public void RequestAddTime()
        {
            if (!CanCommand()) return;

            int coinCost = EffectiveAddTimeCoinCost;
            int oldBalance = BartenderEconomy.Coins;
            SynchronizeCoinProjectionBeforePurchase(oldBalance);
            timePurchaseInFlight = true;
            expectedTimePurchaseOldBalance = oldBalance;
            expectedTimePurchaseCost = coinCost;
            try
            {
                if (!controller.TryPurchaseTimeBoost(
                        EffectiveAddTimeSeconds, coinCost, out string reason))
                    LastRejection = reason;
            }
            finally
            {
                timePurchaseInFlight = false;
                expectedTimePurchaseOldBalance = 0;
                expectedTimePurchaseCost = 0;

                // Presenter devre dışıyken doğrudan API ile çağrılmışsa event aboneliği
                // yoktur. Yeniden açıldığında doğru bakiyeden başlamak için yalnız modeli
                // eşitler; aktif bir feedback'i başarı yolunda asla erkenden snap etmez.
                int currentBalance = BartenderEconomy.Coins;
                if (coinBalanceInitialized && trackedCoinBalance != currentBalance)
                    ProjectCoinBalanceImmediately(currentBalance);
            }
        }

        public void RequestShuffle()
        {
            if (!CanCommand()) return;
            if (!controller.TryShuffle(out string reason)) LastRejection = reason;
        }

        private bool CanCommand()
        {
            LastRejection = null;
            if (controller == null) return false;
            if (controller.State != BartenderLevelState.Playing) return false;
            if (controller.PresentationLocked) return false;
            if (pourInteraction != null && pourInteraction.Busy) return false;
            if (shelfView == null) return true;
            return shelfView.Ready && !shelfView.SeatAnimationPlaying
                   && !shelfView.DeliveryPlaying && !shelfView.SynchronizationDeferred;
        }

        // ---- Projeksiyon ------------------------------------------------------------

        private void Update() => Refresh();

        /// <summary>
        /// Düğmelerin tıklanabilirliği ve sayaçları. Her karede koşar: durumun büyük
        /// kısmı (süren animasyon, sunum kilidi) olay yaymaz, yalnız okunabilir.
        /// </summary>
        public void Refresh()
        {
            // Availability and appearance are deliberately separate. A booster that is
            // out of stock or unaffordable must still keep the authored, lively artwork;
            // Button.interactable remains the authority that prevents its onClick.
            KeepDisabledVisualVivid(undoButton);
            KeepDisabledVisualVivid(addTimeButton);
            KeepDisabledVisualVivid(shuffleButton);

            bool gate = CanCommand();
            bool hasController = controller != null;

            SetInteractable(undoButton,
                gate && hasController && controller.UndoRemaining > 0
                && controller.HasUndoableMove);
            SetInteractable(addTimeButton,
                gate && hasController
                && controller.CanPurchaseTimeBoost(
                    EffectiveAddTimeSeconds, EffectiveAddTimeCoinCost, out _));
            SetInteractable(shuffleButton,
                gate && hasController && controller.ShuffleRemaining > 0);

            if (!hasController) return;
            SetCount(undoCountLabel, undoCountRichLabel, controller.UndoRemaining);
            SetOffer(addTimePriceLabel, addTimePriceRichLabel,
                EffectiveAddTimeCoinCost);
            SetCount(shuffleCountLabel, shuffleCountRichLabel, controller.ShuffleRemaining);
            // Altın bu şeritte yalnız ücretli +süre gerçekten kullanılabildiğinde anlamlı.
            // Tutorial/erken level kontrolü yerine level kuralını okumak, özelliğin açıldığı
            // ilk seviyeyi tek bir otoriteden takip eder.
            bool showBalance = controller.CurrentLevel != null
                               && controller.CurrentLevel.AllowTimedOrders;
            EnsureCoinBalanceProjectionInitialized();
            int projectedBalance = Mathf.Max(0, displayedCoinBalance);
            SetBalance(projectedBalance, showBalance);
        }

        private void SetCount(Text legacy, TMP_Text rich, int remaining)
        {
            bool show = remaining <= hideCountAbove;
            string text = show ? remaining.ToString() : "";
            if (legacy != null)
            {
                legacy.text = text;
                if (legacy.gameObject.activeSelf != show) legacy.gameObject.SetActive(show);
            }
            if (rich == null) return;
            rich.text = text;
            if (rich.gameObject.activeSelf != show) rich.gameObject.SetActive(show);
        }

        private static void SetOffer(Text legacy, TMP_Text rich, int cost)
        {
            string value = Mathf.Max(0, cost).ToString();
            if (legacy != null) legacy.text = value;
            if (rich != null) rich.text = value;
        }

        private void SetBalance(int balance, bool visible)
        {
            string value = $"ALTIN {Mathf.Max(0, balance)}";
            if (coinBalanceLabel != null)
            {
                coinBalanceLabel.text = value;
                bool labelOwnsVisibility = coinHudRoot == null
                    || object.ReferenceEquals(coinHudRoot.gameObject,
                        coinBalanceLabel.gameObject);
                if (labelOwnsVisibility
                    && coinBalanceLabel.gameObject.activeSelf != visible)
                    coinBalanceLabel.gameObject.SetActive(visible);
            }
            if (coinBalanceRichLabel != null)
            {
                coinBalanceRichLabel.text = value;
                bool labelOwnsVisibility = coinHudRoot == null
                    || object.ReferenceEquals(coinHudRoot.gameObject,
                        coinBalanceRichLabel.gameObject);
                if (labelOwnsVisibility
                    && coinBalanceRichLabel.gameObject.activeSelf != visible)
                    coinBalanceRichLabel.gameObject.SetActive(visible);
            }
            if (coinHudRoot != null && coinHudRoot.gameObject.activeSelf != visible)
                coinHudRoot.gameObject.SetActive(visible);
        }

        private void InitializeCoinBalanceProjection()
        {
            CancelCoinFeedback(false);
            trackedCoinBalance = Mathf.Max(0, BartenderEconomy.Coins);
            displayedCoinBalance = trackedCoinBalance;
            coinBalanceInitialized = true;
            ResetCoinFeedbackVisuals();
            WriteDisplayedCoinBalance();
        }

        private void EnsureCoinBalanceProjectionInitialized()
        {
            if (!coinBalanceInitialized) InitializeCoinBalanceProjection();
        }

        private void SynchronizeCoinProjectionBeforePurchase(int currentBalance)
        {
            EnsureCoinBalanceProjectionInitialized();
            currentBalance = Mathf.Max(0, currentBalance);
            if (trackedCoinBalance == currentBalance) return;
            ProjectCoinBalanceImmediately(currentBalance);
        }

        private void ProjectCoinBalanceImmediately(int balance)
        {
            trackedCoinBalance = Mathf.Max(0, balance);
            displayedCoinBalance = trackedCoinBalance;
            coinBalanceInitialized = true;
            CancelCoinFeedback(false);
            ResetCoinFeedbackVisuals();
            WriteDisplayedCoinBalance();
        }

        private void WriteDisplayedCoinBalance()
        {
            if (coinBalanceLabel != null)
                coinBalanceLabel.text = $"ALTIN {Mathf.Max(0, displayedCoinBalance)}";
            if (coinBalanceRichLabel != null)
                coinBalanceRichLabel.text = $"ALTIN {Mathf.Max(0, displayedCoinBalance)}";
        }

        private void PlayTimePurchaseFeedback(int oldBalance, int newBalance, int coinCost)
        {
            CancelCoinFeedback(false);
            displayedCoinBalance = Mathf.Max(0, oldBalance);
            WriteDisplayedCoinBalance();
            ResetCoinFeedbackVisuals();

            int revision = ++coinFeedbackRevision;
            float duration = EffectiveCoinCountDownDuration;
            Sequence sequence = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            coinFeedbackSequence = sequence;

            Tween countDown = DOVirtual.Int(
                    displayedCoinBalance, Mathf.Max(0, newBalance), duration,
                    value =>
                    {
                        if (revision != coinFeedbackRevision) return;
                        displayedCoinBalance = value;
                        WriteDisplayedCoinBalance();
                    })
                .SetEase(Ease.OutCubic)
                .SetRecyclable(true);
            sequence.Join(countDown);

            if (coinHudRoot != null)
            {
                coinHudRoot.localScale = coinHudRestScale;
                sequence.Insert(0f, coinHudRoot
                    .DOPunchScale(Vector3.one * 0.095f, 0.42f, 7, 0.55f)
                    .SetRecyclable(true));
            }

            if (coinHudFlash != null)
            {
                SetGraphicAlpha(coinHudFlash, 0f);
                sequence.Insert(0f, coinHudFlash.DOFade(0.72f, 0.10f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true));
                sequence.Insert(0.10f, coinHudFlash.DOFade(0f, 0.32f)
                    .SetEase(Ease.InSine).SetRecyclable(true));
            }

            if (coinSpendFeedbackLabel != null && coinSpendFeedbackRect != null
                && coinSpendFeedbackGroup != null)
            {
                coinSpendFeedbackLabel.text = "-" + Mathf.Max(0, coinCost);
                coinSpendFeedbackRect.anchoredPosition =
                    coinSpendFeedbackRestPosition;
                coinSpendFeedbackRect.localScale = Vector3.one * 0.88f;
                coinSpendFeedbackGroup.alpha = 1f;
                coinSpendFeedbackLabel.gameObject.SetActive(true);
                sequence.Insert(0f, coinSpendFeedbackRect
                    .DOAnchorPosY(coinSpendFeedbackRestPosition.y + 42f, duration)
                    .SetEase(Ease.OutCubic).SetRecyclable(true));
                sequence.Insert(0f, coinSpendFeedbackRect.DOScale(1.04f, 0.18f)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
                sequence.Insert(0.08f, coinSpendFeedbackGroup
                    .DOFade(0f, Mathf.Max(0.01f, duration - 0.08f))
                    .SetEase(Ease.InQuad).SetRecyclable(true));
            }

            sequence.OnComplete(() =>
            {
                if (revision != coinFeedbackRevision
                    || !object.ReferenceEquals(coinFeedbackSequence, sequence)) return;
                coinFeedbackSequence = null;
                displayedCoinBalance = trackedCoinBalance;
                ResetCoinFeedbackVisuals();
                WriteDisplayedCoinBalance();
            });
            sequence.OnKill(() =>
            {
                if (revision == coinFeedbackRevision
                    && object.ReferenceEquals(coinFeedbackSequence, sequence))
                    coinFeedbackSequence = null;
            });
        }

        private void CancelCoinFeedback(bool snapToTrackedBalance)
        {
            coinFeedbackRevision++;
            Sequence sequence = coinFeedbackSequence;
            coinFeedbackSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
            ResetCoinFeedbackVisuals();
            if (!snapToTrackedBalance || !coinBalanceInitialized) return;
            displayedCoinBalance = trackedCoinBalance;
            WriteDisplayedCoinBalance();
        }

        private void ResetCoinFeedbackVisuals()
        {
            if (coinHudRoot != null) coinHudRoot.localScale = coinHudRestScale;
            if (coinHudFlash != null) SetGraphicAlpha(coinHudFlash, 0f);
            if (coinSpendFeedbackRect != null)
            {
                coinSpendFeedbackRect.anchoredPosition =
                    coinSpendFeedbackRestPosition;
                coinSpendFeedbackRect.localScale = Vector3.one;
            }
            if (coinSpendFeedbackGroup != null) coinSpendFeedbackGroup.alpha = 0f;
            if (coinSpendFeedbackLabel != null)
                coinSpendFeedbackLabel.gameObject.SetActive(false);
        }

        private static void SetGraphicAlpha(Graphic graphic, float alpha)
        {
            if (graphic == null) return;
            Color color = graphic.color;
            color.a = Mathf.Clamp01(alpha);
            graphic.color = color;
        }

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button != null && button.interactable != interactable)
                button.interactable = interactable;
        }

        private static void KeepDisabledVisualVivid(Button button)
        {
            if (button == null) return;
            ColorBlock colors = button.colors;
            if (colors.disabledColor == colors.normalColor) return;
            colors.disabledColor = colors.normalColor;
            button.colors = colors;
        }

        /// <summary>
        /// Mevcut scene/prefab eski MVP'den geldiği için fiyat ve bakiye label ref'leri
        /// boş olabilir. Fiyat için güvenli metin fallback'i; bakiye için ise mevcut Royal
        /// main-menu theme'indeki frame, font ve coin ikonunu kullanan küçük bir HUD üretir.
        /// Authored label bağlandığında hiçbir sunum objesi yaratılmaz.
        /// </summary>
        private void EnsureEconomyLabels()
        {
            if (addTimeButton != null
                && addTimePriceLabel == null && addTimePriceRichLabel == null)
            {
                addTimePriceLabel = CreateLegacyLabel(addTimeButton.transform,
                    "Add Time Price - Runtime", TextAnchor.MiddleCenter,
                    new Vector2(0.5f, 0.5f), new Vector2(0f, -43f),
                    new Vector2(94f, 28f), 20);
            }

            if (coinBalanceLabel != null || coinBalanceRichLabel != null)
            {
                ResolveCoinHudRootFromAuthoredLabel();
                return;
            }
            if (addTimeButton == null)
                return;

            Canvas canvas = addTimeButton.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Transform topBar = canvas.transform.Find("Safe Area/01 Top Bar");
            Transform parent = topBar != null ? topBar : canvas.transform;
            coinBalanceLabel = CreateRoyalCoinHud(parent, topBar != null);
            // OnEnable içindeki ilk Refresh'e kadar tek karelik bir HUD parlaması olmasın.
            if (coinHudRoot != null) coinHudRoot.gameObject.SetActive(false);
        }

        private Text CreateRoyalCoinHud(Transform parent, bool usesTopBar)
        {
            Transform existing = parent != null ? parent.Find(RuntimeCoinHudName) : null;
            if (TryBindRuntimeCoinHud(existing, out Text existingLabel))
                return existingLabel;

            BartenderMainMenuTheme theme =
                Resources.Load<BartenderMainMenuTheme>(MainMenuThemeResourcePath);
            Font font = theme != null && theme.UiFont != null
                ? theme.UiFont
                : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                  ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            var rootObject = new GameObject(RuntimeCoinHudName,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rootObject.hideFlags = HideFlags.DontSave;
            rootObject.layer = parent != null ? parent.gameObject.layer : 0;
            RectTransform root = (RectTransform)rootObject.transform;
            root.SetParent(parent, false);
            root.anchorMin = root.anchorMax = usesTopBar
                ? new Vector2(0f, 0.5f)
                : new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 0.5f);
            root.anchoredPosition = usesTopBar
                ? new Vector2(14f, 0f)
                : new Vector2(14f, -34f);
            root.sizeDelta = new Vector2(220f, 58f);

            Image frame = rootObject.GetComponent<Image>();
            frame.raycastTarget = false;
            frame.sprite = theme != null ? theme.ResourceFrame : null;
            frame.type = HasSpriteBorder(frame.sprite)
                ? Image.Type.Sliced
                : Image.Type.Simple;
            frame.color = frame.sprite != null
                ? Color.white
                : new Color32(0xFF, 0xEF, 0xC8, 0xF5);

            Image flash = CreateRuntimeImage("Spend Flash", root,
                theme != null ? theme.ResourceFrame : null, RoyalGold);
            Stretch(flash.rectTransform);
            flash.type = HasSpriteBorder(flash.sprite)
                ? Image.Type.Sliced
                : Image.Type.Simple;
            SetGraphicAlpha(flash, 0f);

            Image coinIcon = CreateRuntimeImage("Cocktail Coin", root,
                theme != null ? theme.CoinCocktail : null, Color.white);
            RectTransform iconRect = coinIcon.rectTransform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(30f, 0f);
            iconRect.sizeDelta = new Vector2(62f, 62f);
            coinIcon.preserveAspect = true;

            Text balance = CreateRuntimeText("Balance", root, font, 25,
                FontStyle.Bold, RoyalBrown, TextAnchor.MiddleCenter);
            RectTransform balanceRect = balance.rectTransform;
            balanceRect.anchorMin = new Vector2(0.25f, 0f);
            balanceRect.anchorMax = new Vector2(0.96f, 1f);
            balanceRect.offsetMin = balanceRect.offsetMax = Vector2.zero;
            balance.resizeTextForBestFit = true;
            balance.resizeTextMinSize = 18;
            balance.resizeTextMaxSize = 25;

            Text spend = CreateRuntimeText("Spend Feedback", root, font, 25,
                FontStyle.Bold, SpendRed, TextAnchor.MiddleCenter);
            var spendGroup = spend.gameObject.AddComponent<CanvasGroup>();
            RectTransform spendRect = spend.rectTransform;
            spendRect.anchorMin = spendRect.anchorMax = new Vector2(0.5f, 1f);
            spendRect.pivot = new Vector2(0.5f, 0.5f);
            spendRect.anchoredPosition = new Vector2(12f, 8f);
            spendRect.sizeDelta = new Vector2(140f, 38f);
            var spendOutline = spend.gameObject.AddComponent<Outline>();
            spendOutline.effectColor = new Color32(0xFF, 0xEE, 0xC5, 0xF0);
            spendOutline.effectDistance = new Vector2(1.4f, -1.4f);

            coinHudRoot = root;
            coinHudFlash = flash;
            coinSpendFeedbackLabel = spend;
            coinSpendFeedbackGroup = spendGroup;
            coinSpendFeedbackRect = spendRect;
            CaptureCoinFeedbackRestPose();
            ResetCoinFeedbackVisuals();
            return balance;
        }

        private bool TryBindRuntimeCoinHud(Transform candidate, out Text balance)
        {
            balance = null;
            if (candidate == null) return false;

            // Eski runtime fallback tek başına Text idi. Domain reload sırasında hâlâ
            // yaşıyorsa onu güvenle kullan; yeni sahne açılışlarında Royal pill kurulur.
            if (candidate.TryGetComponent(out Text legacyBalance))
            {
                balance = legacyBalance;
                coinHudRoot = candidate as RectTransform;
                CaptureCoinFeedbackRestPose();
                return true;
            }

            Transform balanceTransform = candidate.Find("Balance");
            if (balanceTransform == null
                || !balanceTransform.TryGetComponent(out balance)) return false;
            coinHudRoot = candidate as RectTransform;
            coinHudFlash = candidate.Find("Spend Flash")?.GetComponent<Image>();
            coinSpendFeedbackLabel =
                candidate.Find("Spend Feedback")?.GetComponent<Text>();
            coinSpendFeedbackRect =
                coinSpendFeedbackLabel != null
                    ? coinSpendFeedbackLabel.rectTransform
                    : null;
            coinSpendFeedbackGroup =
                coinSpendFeedbackLabel != null
                    ? coinSpendFeedbackLabel.GetComponent<CanvasGroup>()
                    : null;
            CaptureCoinFeedbackRestPose();
            ResetCoinFeedbackVisuals();
            return true;
        }

        private void ResolveCoinHudRootFromAuthoredLabel()
        {
            RectTransform labelRect = coinBalanceLabel != null
                ? coinBalanceLabel.rectTransform
                : coinBalanceRichLabel != null
                    ? coinBalanceRichLabel.rectTransform
                    : null;
            Transform runtimeCandidate = labelRect != null ? labelRect.parent : null;
            if (runtimeCandidate != null
                && runtimeCandidate.name == RuntimeCoinHudName
                && TryBindRuntimeCoinHud(runtimeCandidate, out _)) return;

            coinHudRoot = labelRect;
            coinHudFlash = null;
            coinSpendFeedbackLabel = null;
            coinSpendFeedbackGroup = null;
            coinSpendFeedbackRect = null;
            CaptureCoinFeedbackRestPose();
        }

        private void CaptureCoinFeedbackRestPose()
        {
            coinHudRestScale = coinHudRoot != null
                ? coinHudRoot.localScale
                : Vector3.one;
            coinSpendFeedbackRestPosition = coinSpendFeedbackRect != null
                ? coinSpendFeedbackRect.anchoredPosition
                : Vector2.zero;
        }

        private static Image CreateRuntimeImage(string name, Transform parent,
                                                Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            go.hideFlags = HideFlags.DontSave;
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Text CreateRuntimeText(string name, Transform parent, Font font,
                                              int fontSize, FontStyle style,
                                              Color color, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text));
            go.hideFlags = HideFlags.DontSave;
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            Text label = go.GetComponent<Text>();
            label.font = font;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private static bool HasSpriteBorder(Sprite sprite) =>
            sprite != null && sprite.border.sqrMagnitude > 0.0001f;

        private static void Stretch(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static Text CreateLegacyLabel(Transform parent, string name,
                                              TextAnchor alignment, Vector2 anchor,
                                              Vector2 position, Vector2 size,
                                              int fontSize)
        {
            Transform existing = parent != null ? parent.Find(name) : null;
            if (existing != null && existing.TryGetComponent(out Text existingText))
                return existingText;

            var go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text));
            go.layer = parent != null ? parent.gameObject.layer : 0;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(anchor.x <= 0f ? 0f : 0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Text label = go.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                         ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = fontSize;
            label.fontStyle = FontStyle.Bold;
            label.alignment = alignment;
            label.color = new Color(1f, 0.82f, 0.18f, 1f);
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.20f, 0.08f, 0.28f, 0.90f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            return label;
        }

        // ---- Wiring -----------------------------------------------------------------

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourInteraction == null)
                pourInteraction = GetComponent<BartenderPourInteraction>();
        }

        private void HookButtons()
        {
            if (undoButton != null) undoButton.onClick.AddListener(RequestUndo);
            if (addTimeButton != null)
                addTimeButton.onClick.AddListener(RequestAddTime);
            if (shuffleButton != null) shuffleButton.onClick.AddListener(RequestShuffle);
        }

        private void UnhookButtons()
        {
            if (undoButton != null) undoButton.onClick.RemoveListener(RequestUndo);
            if (addTimeButton != null)
                addTimeButton.onClick.RemoveListener(RequestAddTime);
            if (shuffleButton != null) shuffleButton.onClick.RemoveListener(RequestShuffle);
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.BoostersChanged += Refresh;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.StateChanged += HandleStateChanged;
            BartenderEconomy.CoinsChanged += HandleCoinsChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.BoostersChanged -= Refresh;
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            BartenderEconomy.CoinsChanged -= HandleCoinsChanged;
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            ProjectCoinBalanceImmediately(BartenderEconomy.Coins);
            Refresh();
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing) CancelCoinFeedback(true);
            Refresh();
        }

        private void HandleCoinsChanged(int balance)
        {
            balance = Mathf.Max(0, balance);
            EnsureCoinBalanceProjectionInitialized();
            int previousBalance = trackedCoinBalance;
            bool acceptedTimePurchase = timePurchaseInFlight
                && expectedTimePurchaseCost > 0
                && previousBalance == expectedTimePurchaseOldBalance
                && balance == expectedTimePurchaseOldBalance
                              - expectedTimePurchaseCost;

            trackedCoinBalance = balance;
            if (acceptedTimePurchase)
                PlayTimePurchaseFeedback(previousBalance, balance,
                    expectedTimePurchaseCost);
            else
                ProjectCoinBalanceImmediately(balance);
            Refresh();
        }
    }
}
