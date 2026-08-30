using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Alt şeritteki üç booster düğmesi: geri al, +bardak, karıştır.
    ///
    /// OPTİMİSTİK UI YOK — <see cref="BartenderPausePresenter"/> ile aynı disiplin.
    /// Düğme yalnızca komutu yollar; sayaç, kural motoru komutu kabul edip
    /// <see cref="BartenderLevelController.BoostersChanged"/> geri geldiğinde düşer.
    /// Reddedilen bir dokunuşta ekranda hiçbir şey kıpırdamaz.
    ///
    /// BARDAK TİPİ SEÇİMİ BURADA. Kural motoru "hangi bardağı ekleyeyim" sorusunu
    /// cevaplayamaz, çünkü sahnedeki havuzun sonlu olduğunu bilmez: her tipin elle
    /// yerleştirilmiş, sayısı sabit scene objeleri var. Boş slotu olan bir tip
    /// seçmezsek view bütün sunumu reddeder. O yüzden tip, açık siparişlere ve havuz
    /// doluluğuna bakılarak burada kararlaştırılır.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoosterBarPresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Havuz doluluğu ve süren animasyon buradan okunur.")]
        [SerializeField] private BartenderShelfLevelView shelfView;
        [Tooltip("Opsiyonel. Dökme sürerken booster kabul edilmesin diye okunur.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;

        [Header("Düğmeler")]
        [SerializeField] private Button undoButton = null;
        [SerializeField] private Button extraGlassButton = null;
        [SerializeField] private Button shuffleButton = null;

        [Header("Kalan sayaçları (opsiyonel)")]
        [SerializeField] private Text undoCountLabel = null;
        [SerializeField] private Text extraGlassCountLabel = null;
        [SerializeField] private Text shuffleCountLabel = null;
        [SerializeField] private TMP_Text undoCountRichLabel = null;
        [SerializeField] private TMP_Text extraGlassCountRichLabel = null;
        [SerializeField] private TMP_Text shuffleCountRichLabel = null;
        [Tooltip("Stok bu değerin üstündeyken sayaç yazılmaz. MVP'de stoklar 99, "
               + "yani her düğmenin altında '99' yazması bilgi değil gürültü olurdu.")]
        [SerializeField, Min(0)] private int hideCountAbove = 20;

        private BartenderLevelController subscribedController;

        public string LastRejection { get; private set; }

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
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
                                           Button undo, Button extraGlass, Button shuffle)
        {
            Unsubscribe();
            UnhookButtons();
            controller = levelController;
            shelfView = view;
            pourInteraction = pour;
            undoButton = undo;
            extraGlassButton = extraGlass;
            shuffleButton = shuffle;
            if (!isActiveAndEnabled) return;
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
                reason = "BartenderShelfLevelView Inspector referansı eksik; +bardak "
                       + "havuzda boş slot olup olmadığını soramaz.";
                return false;
            }
            if (undoButton == null || extraGlassButton == null || shuffleButton == null)
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

        public void RequestExtraGlass()
        {
            if (!CanCommand()) return;
            if (!TryChooseExtraGlassType(out GlassType type))
            {
                LastRejection = "Havuzda boş bardak slotu kalmadı";
                return;
            }
            if (!controller.TryAddExtraGlass(type, out _, out string reason))
                LastRejection = reason;
        }

        public void RequestShuffle()
        {
            if (!CanCommand()) return;
            if (!controller.TryShuffle(out string reason)) LastRejection = reason;
        }

        /// <summary>
        /// Hangi bardak eklenmeli? Önce SOLDAKİ açık siparişin bardağı denenir — kural
        /// 3'te teslim önceliği de soldan başlar, yani oyuncunun bir sonraki hedefi
        /// oradadır. O tipin havuzu doluysa boş slotu olan ilk tip alınır.
        /// </summary>
        public bool TryChooseExtraGlassType(out GlassType type)
        {
            type = GlassType.Tumbler;
            if (controller == null || shelfView == null) return false;

            BsLevel level = controller.CurrentLevel;
            int slots = level != null ? Mathf.Max(1, level.OrderSlots) : 0;
            for (int i = 0; i < slots; i++)
            {
                OrderDef order = controller.OrderAtSlot(i);
                if (order == null || !shelfView.HasFreePoolSlot(order.Glass)) continue;
                type = order.Glass;
                return true;
            }

            foreach (GlassType candidate in BsRules.AllGlassTypes)
            {
                if (!shelfView.HasFreePoolSlot(candidate)) continue;
                type = candidate;
                return true;
            }
            return false;
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
            bool gate = CanCommand();
            bool hasController = controller != null;

            SetInteractable(undoButton,
                gate && hasController && controller.UndoRemaining > 0
                && controller.HasUndoableMove);
            SetInteractable(extraGlassButton,
                gate && hasController && controller.ExtraGlassRemaining > 0
                && controller.ActiveGlassCount < controller.MaxActiveGlasses
                && TryChooseExtraGlassType(out _));
            SetInteractable(shuffleButton,
                gate && hasController && controller.ShuffleRemaining > 0);

            if (!hasController) return;
            SetCount(undoCountLabel, undoCountRichLabel, controller.UndoRemaining);
            SetCount(extraGlassCountLabel, extraGlassCountRichLabel,
                controller.ExtraGlassRemaining);
            SetCount(shuffleCountLabel, shuffleCountRichLabel, controller.ShuffleRemaining);
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

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button != null && button.interactable != interactable)
                button.interactable = interactable;
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
            if (extraGlassButton != null)
                extraGlassButton.onClick.AddListener(RequestExtraGlass);
            if (shuffleButton != null) shuffleButton.onClick.AddListener(RequestShuffle);
        }

        private void UnhookButtons()
        {
            if (undoButton != null) undoButton.onClick.RemoveListener(RequestUndo);
            if (extraGlassButton != null)
                extraGlassButton.onClick.RemoveListener(RequestExtraGlass);
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
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.BoostersChanged -= Refresh;
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level) => Refresh();
        private void HandleStateChanged(BartenderLevelState state) => Refresh();
    }
}
