using System;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Üst şeritteki "SEVİYE n" rozeti.
    ///
    /// Sayı level asset'inin <see cref="BsLevel.Index"/>'inden gelir, kampanya
    /// slotundan DEĞİL. İkisi bugün aynı sırada ama aynı şey değil: slot destedeki yer,
    /// Index ise level'ın kendi kimliği. Bir level araya eklenirse kayan slot olur,
    /// oyuncunun gördüğü numara olmaz.
    ///
    /// Hem eski <see cref="Text"/> hem <see cref="TMP_Text"/> desteklenir; hangisi
    /// bağlıysa o sürülür. Proje TMP Essential Resources'ı henüz almadığı için sahne
    /// bugün eskisini kullanıyor, ama art geldiğinde bileşen değişmeyecek.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LevelBadgePresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Yazı")]
        [SerializeField] private Text legacyLabel = null;
        [SerializeField] private TMP_Text richLabel = null;
        [Tooltip("Level 1..30 için atlas tarafından paketlenen bitmap yazı katmanı.")]
        [SerializeField] private Image spriteLabel = null;
        [Tooltip("Dizi sırası level numarasıdır: slot 0 = Level 1.")]
        [SerializeField] private Sprite[] levelSprites = Array.Empty<Sprite>();
        [Tooltip("{0} level numarasıyla değiştirilir.")]
        [SerializeField] private string format = "SEVİYE {0}";
        [Tooltip("Level yokken yazılan metin.")]
        [SerializeField] private string idleText = "BARTENDER";
        [Tooltip("Kampanya bittiğinde yazılan metin.")]
        [SerializeField] private string completedText = "TAMAMLANDI";

        [Header("Görünürlük")]
        [Tooltip("Rozetin tamamı. Level yokken kapatılsın mı?")]
        [SerializeField] private GameObject badgeRoot = null;
        [SerializeField] private bool hideWhenUnloaded = false;

        private BartenderLevelController subscribedController;

        public string CurrentText { get; private set; } = "";

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            Refresh();
        }

        private void OnDisable() => Unsubscribe();

        /// <summary>Authoring API for an editor builder.</summary>
        public void ConfigureSceneBindings(BartenderLevelController levelController,
                                           Text label, GameObject root)
        {
            Unsubscribe();
            controller = levelController;
            legacyLabel = label;
            badgeRoot = root;
            if (!isActiveAndEnabled) return;
            Subscribe();
            Refresh();
        }

        /// <summary>
        /// Bitmap yazı atlası kullanan sahneler için authoring API. Atlas runtime'da
        /// isimle aranmaz; level N doğrudan dizinin N-1 slotuna gider.
        /// </summary>
        public void ConfigureSceneBindings(BartenderLevelController levelController,
                                           Image bitmapLabel, Sprite[] sprites,
                                           Text fallbackLabel, GameObject root)
        {
            Unsubscribe();
            controller = levelController;
            spriteLabel = bitmapLabel;
            levelSprites = sprites ?? Array.Empty<Sprite>();
            legacyLabel = fallbackLabel;
            richLabel = null;
            badgeRoot = root;
            if (!isActiveAndEnabled) return;
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
            bool hasBitmapPath = spriteLabel != null && levelSprites != null
                                 && levelSprites.Length >= 30;
            bool hasTextPath = legacyLabel != null || richLabel != null;
            if (!hasBitmapPath && !hasTextPath)
            {
                reason = "Rozet için ne 1-30 sprite dizisi ne de fallback yazı bağlanmış.";
                return false;
            }
            if (spriteLabel != null)
            {
                if (levelSprites == null || levelSprites.Length < 30)
                {
                    reason = "Rozet sprite dizisi Level 1-30 için tam 30 eleman içermeli.";
                    return false;
                }
                for (int i = 0; i < 30; i++)
                {
                    if (levelSprites[i] != null) continue;
                    reason = $"Rozet sprite dizisinin {i + 1}. level elemanı eksik.";
                    return false;
                }
            }
            if (legacyLabel != null && legacyLabel.font == null)
            {
                reason = "Rozet yazısının font referansı eksik.";
                return false;
            }
            reason = null;
            return true;
        }

        public void Refresh()
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            bool complete = controller != null
                            && controller.State == BartenderLevelState.CampaignComplete;

            CurrentText = level != null
                ? string.Format(format, level.Index)
                : (complete ? completedText : idleText);

            Sprite selected = level != null ? SpriteForLevel(level.Index) : null;
            bool useBitmap = spriteLabel != null && selected != null;
            if (spriteLabel != null)
            {
                spriteLabel.sprite = selected;
                spriteLabel.enabled = useBitmap;
            }
            if (legacyLabel != null)
            {
                legacyLabel.text = CurrentText;
                legacyLabel.enabled = !useBitmap;
            }
            if (richLabel != null)
            {
                richLabel.text = CurrentText;
                richLabel.enabled = !useBitmap;
            }
            if (badgeRoot != null && hideWhenUnloaded)
                badgeRoot.SetActive(level != null || complete);
        }

        public Sprite SpriteForLevel(int levelIndex)
        {
            int slot = levelIndex - 1;
            return levelSprites != null && slot >= 0 && slot < levelSprites.Length
                ? levelSprites[slot]
                : null;
        }

        private void ResolveDependencies()
        {
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.StateChanged += HandleStateChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level) => Refresh();
        private void HandleStateChanged(BartenderLevelState state) => Refresh();
    }
}
