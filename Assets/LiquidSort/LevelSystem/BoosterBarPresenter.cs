using BartenderSort.Core;
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

        private BartenderLevelController subscribedController;

        public string LastRejection { get; private set; }
        private float EffectiveAddTimeSeconds =>
            addTimeSeconds > 0f && !float.IsNaN(addTimeSeconds)
            && !float.IsInfinity(addTimeSeconds) ? addTimeSeconds : 15f;
        private int EffectiveAddTimeCoinCost => addTimeCoinCost > 0 ? addTimeCoinCost : 900;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            EnsureEconomyLabels();
            HookButtons();
            Subscribe();
            Refresh();
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnhookButtons();
        }

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
            if (!controller.TryPurchaseTimeBoost(
                    EffectiveAddTimeSeconds, EffectiveAddTimeCoinCost, out string reason))
                LastRejection = reason;
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
            SetBalance(coinBalanceLabel, coinBalanceRichLabel,
                BartenderEconomy.Coins, showBalance);
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

        private static void SetBalance(Text legacy, TMP_Text rich, int balance,
                                       bool visible)
        {
            string value = $"ALTIN {Mathf.Max(0, balance)}";
            if (legacy != null)
            {
                legacy.text = value;
                if (legacy.gameObject.activeSelf != visible)
                    legacy.gameObject.SetActive(visible);
            }
            if (rich == null) return;
            rich.text = value;
            if (rich.gameObject.activeSelf != visible)
                rich.gameObject.SetActive(visible);
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
        /// boş olabilir. Runtime fallback yalnız eksik iki sunum objesini üretir; authored
        /// label bağlandığında hiçbir şey yaratmaz.
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

            if (coinBalanceLabel != null || coinBalanceRichLabel != null
                || addTimeButton == null)
                return;

            Canvas canvas = addTimeButton.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Transform topBar = canvas.transform.Find("Safe Area/01 Top Bar");
            Transform parent = topBar != null ? topBar : canvas.transform;
            coinBalanceLabel = CreateLegacyLabel(parent,
                "Coin Balance - Runtime", TextAnchor.MiddleLeft,
                new Vector2(0f, topBar != null ? 0.5f : 1f),
                topBar != null ? new Vector2(22f, 0f) : new Vector2(22f, -34f),
                new Vector2(180f, 46f), 24);
            // OnEnable içindeki ilk Refresh'e kadar tek karelik bir HUD parlaması olmasın.
            if (coinBalanceLabel != null)
                coinBalanceLabel.gameObject.SetActive(false);
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

        private void HandleLevelLoaded(BsLevel level) => Refresh();
        private void HandleStateChanged(BartenderLevelState state) => Refresh();
        private void HandleCoinsChanged(int balance) => Refresh();
    }
}
