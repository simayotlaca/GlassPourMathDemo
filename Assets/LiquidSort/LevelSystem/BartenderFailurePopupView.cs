using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Authored failure-popup hierarchy used by <see cref="BartenderRoundFeedbackPresenter"/>.
    /// Round, life and retry decisions stay in the presenter/session; this component
    /// only exposes the visual roots and actions.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderFailurePopupView : MonoBehaviour
    {
        [Header("Overlay")]
        [SerializeField] private CanvasGroup overlayGroup = null;
        [SerializeField] private Image dimmer = null;
        [SerializeField] private Image flash = null;

        [Header("Card")]
        [SerializeField] private RectTransform cardPivot = null;

        [Header("Actions")]
        [SerializeField] private Button paidContinueButton = null;
        [SerializeField] private Text paidContinueCostLabel = null;
        [SerializeField] private Button retryButton = null;
        [SerializeField] private Button closeButton = null;

        public CanvasGroup OverlayGroup => overlayGroup;
        public Image Dimmer => dimmer;
        public Image Flash => flash;
        public RectTransform CardPivot => cardPivot;
        public Button PaidContinueButton => paidContinueButton;
        public Button RetryButton => retryButton;
        public Button CloseButton => closeButton;

        public bool IsReady => overlayGroup != null && dimmer != null && flash != null
                               && cardPivot != null && paidContinueButton != null
                               && paidContinueCostLabel != null && retryButton != null
                               && closeButton != null;

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
            if (overlayGroup == null) return;
            overlayGroup.alpha = visible ? 1f : 0f;
            overlayGroup.interactable = visible;
            overlayGroup.blocksRaycasts = visible;
        }

        public void SetPurchaseCost(int coinCost)
        {
            if (paidContinueCostLabel == null) return;
            paidContinueCostLabel.text = Mathf.Max(0, coinCost)
                .ToString(CultureInfo.InvariantCulture);
        }

        public void SetButtonsInteractable(bool canRetry, bool canPaidContinue,
                                           bool canClose)
        {
            if (retryButton != null) retryButton.interactable = canRetry;
            if (paidContinueButton != null)
                paidContinueButton.interactable = canPaidContinue;
            if (closeButton != null) closeButton.interactable = canClose;
        }
    }
}
