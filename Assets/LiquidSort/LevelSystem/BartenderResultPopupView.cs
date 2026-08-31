using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Authored success-popup hierarchy used by <see cref="BartenderRoundFeedbackPresenter"/>.
    /// This component deliberately owns no round/economy logic; it only exposes the
    /// visual roots and buttons that the presenter drives.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderResultPopupView : MonoBehaviour
    {
        [Header("Overlay")]
        [SerializeField] private CanvasGroup overlayGroup = null;
        [SerializeField] private Image dimmer = null;
        [SerializeField] private Image flash = null;

        [Header("Card")]
        [SerializeField] private RectTransform cardPivot = null;

        [Header("Actions")]
        [SerializeField] private Button rewardButton = null;
        [SerializeField] private Button continueButton = null;
        [SerializeField] private Button closeButton = null;

        public CanvasGroup OverlayGroup => overlayGroup;
        public Image Dimmer => dimmer;
        public Image Flash => flash;
        public RectTransform CardPivot => cardPivot;
        public Button RewardButton => rewardButton;
        public Button ContinueButton => continueButton;
        public Button CloseButton => closeButton;

        public bool IsReady => overlayGroup != null && dimmer != null && flash != null
                               && cardPivot != null && rewardButton != null
                               && continueButton != null && closeButton != null;

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
            if (overlayGroup == null) return;
            overlayGroup.alpha = visible ? 1f : 0f;
            overlayGroup.interactable = visible;
            overlayGroup.blocksRaycasts = visible;
        }

        public void SetButtonsInteractable(bool canContinue, bool canClose, bool canReward)
        {
            if (continueButton != null) continueButton.interactable = canContinue;
            if (closeButton != null) closeButton.interactable = canClose;
            if (rewardButton != null) rewardButton.interactable = canReward;
        }
    }
}
