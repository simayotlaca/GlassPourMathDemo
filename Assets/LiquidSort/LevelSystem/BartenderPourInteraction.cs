using System;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Portable input/presentation bridge for the Bartender shelf.
    ///
    /// The controller remains the only rule authority. A legal move is committed there
    /// first while the view defers its BoardChanged refresh; PourAnimator then animates the
    /// still-pre-command scene bottles. Completion and cancellation both reconcile from the
    /// controller snapshot, so presentation can never roll domain state back.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderPourInteraction : MonoBehaviour
    {
        [Header("Required rig references")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private PourAnimator pourAnimator;

        [Header("Host scene")]
        [Tooltip("Optional. A portable prefab resolves Camera.main when this is empty.")]
        [SerializeField] private Camera inputCamera;

        [Header("Pointer feel")]
        [SerializeField, Min(0f)] private float pickPadding = 0.22f;
        [SerializeField, Min(0f)] private float selectionLift = 0.16f;
        [SerializeField, Min(0.01f)] private float selectionSpeed = 14f;

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private PourAnimator subscribedAnimator;

        private LiquidBottle selectedBottle;
        private int selectedGlassId = -1;
        private Vector3 selectedHomePosition;

        private int activeOperationId;
        private bool deliveryPresentationActive;
        private int lockedRevision = -1;
        private int activeTransactionToken;
        private int nextTransactionToken;
        private BartenderLevelController transactionController;
        private BartenderShelfLevelView transactionView;
        private PourAnimator transactionAnimator;

        public int SelectedGlassId => selectedGlassId;
        public bool Busy => activeOperationId != 0 || deliveryPresentationActive
                         || (pourAnimator != null && pourAnimator.Busy);
        public string LastRejection { get; private set; }
        public BartenderLevelController Controller => controller;
        public BartenderShelfLevelView ShelfView => shelfView;
        public PourAnimator Animator => pourAnimator;

        public void Configure(BartenderLevelController levelController,
                              BartenderShelfLevelView view,
                              PourAnimator animator,
                              Camera sceneCamera = null)
        {
            CancelAndFinishPresentation();
            Unsubscribe();
            ClearSelection(true);
            controller = levelController;
            shelfView = view;
            pourAnimator = animator;
            inputCamera = sceneCamera;
            ResolveDependencies();
            if (isActiveAndEnabled) Subscribe();
        }

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            CancelAndFinishPresentation();
            ClearSelection(true);
        }

        private void OnValidate()
        {
            pickPadding = Mathf.Max(0f, pickPadding);
            selectionLift = Mathf.Max(0f, selectionLift);
            selectionSpeed = Mathf.Max(0.01f, selectionSpeed);
        }

        private void Update()
        {
            AdoptExternalDeliveryPresentation();
            AnimateSelection();
            if (!CanReadPointer() || !TryReadPointerDown(out Vector2 screenPoint)) return;
            if (IsPointerOverUi()) return;
            HandlePointerDown(screenPoint);
        }

        /// <summary>
        /// Programmatic entry point for a future drag/touch UI. A true result means the
        /// domain move committed; if the visual animation cannot start, the view safely
        /// snaps to that committed result and the method still returns true.
        /// </summary>
        public bool TryCommitAndAnimatePour(int sourceGlassId, int targetGlassId,
                                            out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();

            if (controller == null || shelfView == null || pourAnimator == null)
                return Reject("Gameplay rig controller/view/animator bağlantısı eksik.",
                              out rejectionReason);
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.DeliveryPlaying || Busy || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.",
                              out rejectionReason);
            if (!shelfView.TryGetBottle(sourceGlassId, out LiquidBottle source)
                || !shelfView.TryGetBottle(targetGlassId, out LiquidBottle target))
                return Reject("Bardakların aktif sahne bağlantısı bulunamadı.",
                              out rejectionReason);

            PourResult rule = controller.CanPour(sourceGlassId, targetGlassId);
            if (!rule.Success) return Reject(rule.Reason, out rejectionReason);

            Vector3 home = source == selectedBottle
                ? selectedHomePosition
                : source.transform.position;

            if (!shelfView.TryBeginSynchronizationDeferral(this))
                return Reject("Bardak görünümü başka bir senkronizasyonu bekliyor.",
                              out rejectionReason);

            BartenderLevelController committedController = controller;
            BartenderShelfLevelView deferredView = shelfView;
            PourAnimator selectedAnimator = pourAnimator;
            int transactionToken = BeginPresentationTransaction(
                committedController, deferredView, selectedAnimator);

            BartenderPourReceipt receipt;
            string domainRejection;
            bool committed;
            try
            {
                committed = committedController.TryPour(
                    sourceGlassId, targetGlassId, out receipt, out domainRejection);
            }
            catch
            {
                FinishPresentationTransaction(true);
                throw;
            }
            if (!committed)
            {
                FinishPresentationTransaction(false);
                return Reject(domainRejection, out rejectionReason);
            }

            // TryPour synchronously notifies every listener. One of them may disable or
            // reconfigure this bridge; in that case its lifecycle cleanup already reconciled
            // the committed move and continuing here would orphan a new lock.
            if (!CanContinueTransaction(transactionToken, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction = activeTransactionToken == transactionToken;
                if (stillOwnsTransaction)
                {
                    FinishPresentationTransaction(true);
                    ClearSelection(true);
                }
                LastRejection = "Dökme kaydedildi; sahne değiştiği için sonuç anında gösterildi.";
                rejectionReason = LastRejection;
                return true;
            }

            if (!committedController.TryAcquirePresentationLock(this, receipt.Revision))
            {
                FinishPresentationTransaction(true);
                LastRejection = "Dökme kaydedildi; sunum kilidi alınamadığı için anında gösterildi.";
                rejectionReason = LastRejection;
                ClearSelection(true);
                return true;
            }

            lockedRevision = receipt.Revision;
            // Bartender rules intentionally allow unlike colours to stack. Passing false is
            // therefore not a sandbox shortcut; it is required to match BsBoard.CanPour.
            bool animationStarted;
            try
            {
                animationStarted = selectedAnimator.TryStartPour(
                    source, target, receipt.Amount, home.y, false);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                animationStarted = false;
            }
            if (!animationStarted)
            {
                FinishPresentationTransaction(true);
                LastRejection = "Dökme kaydedildi; animasyon başlayamadığı için sonuç gösterildi.";
                rejectionReason = LastRejection;
                ClearSelection(true);
                return true;
            }

            activeOperationId = selectedAnimator.ActiveOperationId;
            ClearSelection(false);
            return true;
        }

        /// <summary>
        /// Commits a matched glass delivery and holds the controller's presentation lock
        /// until the shelf reports that the portal's complete swallow/bounce beat finished.
        /// The shelf refresh is deferred just long enough to acquire that lock before the
        /// portal starts, mirroring the pour transaction without delaying domain authority.
        /// </summary>
        public bool TryCommitAndAnimateDelivery(int glassId, out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();

            if (controller == null || shelfView == null)
                return Reject("Gameplay rig controller/view bağlantısı eksik.",
                              out rejectionReason);
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.DeliveryPlaying || Busy || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.",
                              out rejectionReason);
            if (!shelfView.TryGetBottle(glassId, out _))
                return Reject("Bardağın aktif sahne bağlantısı bulunamadı.",
                              out rejectionReason);
            if (controller.MatchedOrderSlot(glassId) < 0)
                return Reject("Bardak açık bir siparişi karşılamıyor.",
                              out rejectionReason);

            ClearSelection(true);
            if (!shelfView.TryBeginSynchronizationDeferral(this))
                return Reject("Bardak görünümü başka bir senkronizasyonu bekliyor.",
                              out rejectionReason);

            BartenderLevelController committedController = controller;
            BartenderShelfLevelView deferredView = shelfView;
            PourAnimator selectedAnimator = pourAnimator;
            int transactionToken = BeginPresentationTransaction(
                committedController, deferredView, selectedAnimator);

            BartenderDeliveryReceipt receipt;
            string domainRejection;
            bool committed;
            try
            {
                committed = committedController.TryDeliver(
                    glassId, out receipt, out domainRejection);
            }
            catch
            {
                FinishPresentationTransaction(true);
                throw;
            }
            if (!committed)
            {
                FinishPresentationTransaction(false);
                return Reject(domainRejection, out rejectionReason);
            }

            // Delivered/BoardChanged are synchronous. If a listener reconfigured this rig,
            // its lifecycle cleanup already reconciled the committed board and there is no
            // safe object left on which to start a portal presentation.
            if (!CanContinueTransaction(transactionToken, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction = activeTransactionToken == transactionToken;
                if (stillOwnsTransaction) FinishPresentationTransaction(true);
                LastRejection = "Teslim kaydedildi; sahne değiştiği için sonuç anında gösterildi.";
                rejectionReason = LastRejection;
                return true;
            }

            if (!committedController.TryAcquirePresentationLock(this, receipt.Revision))
            {
                FinishPresentationTransaction(true);
                LastRejection =
                    "Teslim kaydedildi; sunum kilidi alınamadığı için sonuç anında gösterildi.";
                rejectionReason = LastRejection;
                return true;
            }

            lockedRevision = receipt.Revision;
            deliveryPresentationActive = true;

            bool synchronized;
            try
            {
                synchronized = deferredView.EndSynchronizationDeferralAndRefresh(this, true);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, deferredView);
                synchronized = false;
            }

            // Refresh/cancellation callbacks are allowed to finish the transaction
            // synchronously. Never release a newer transaction from this older call frame.
            if (activeTransactionToken != transactionToken
                || !deliveryPresentationActive)
                return true;

            if (!synchronized || !deferredView.DeliveryPlaying)
            {
                FinishPresentationTransaction(false);
                LastRejection = synchronized
                    ? "Teslim kaydedildi; portal animasyonu kullanılamadığı için sonuç gösterildi."
                    : "Teslim kaydedildi; görünüm yenilenemedi.";
                rejectionReason = LastRejection;
            }
            return true;
        }

        private bool CanReadPointer()
        {
            return controller != null && shelfView != null && pourAnimator != null
                && controller.State == BartenderLevelState.Playing
                && !controller.PresentationLocked
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.DeliveryPlaying
                && !Busy;
        }

        private static bool TryReadPointerDown(out Vector2 screenPoint)
        {
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began)
                {
                    screenPoint = touch.position;
                    return true;
                }
            }

            if (Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                return true;
            }

            screenPoint = default;
            return false;
        }

        private static bool IsPointerOverUi()
        {
            EventSystem events = EventSystem.current;
            if (events == null) return false;
            if (Input.touchCount > 0)
                return events.IsPointerOverGameObject(Input.GetTouch(0).fingerId);
            return events.IsPointerOverGameObject();
        }

        private void HandlePointerDown(Vector2 screenPoint)
        {
            Camera camera = ResolveCamera();
            if (camera == null
                || !shelfView.TryPickBottle(camera, screenPoint, pickPadding,
                                            out LiquidBottle hit, out int hitId))
            {
                ClearSelection(true);
                return;
            }

            if (controller != null && controller.MatchedOrderSlot(hitId) >= 0)
            {
                ClearSelection(true);
                TryCommitAndAnimateDelivery(hitId, out _);
                return;
            }

            if (selectedBottle == null)
            {
                SelectIfUsable(hit, hitId);
                return;
            }

            if (hit == selectedBottle)
            {
                ClearSelection(true);
                return;
            }

            int sourceId = selectedGlassId;
            if (TryCommitAndAnimatePour(sourceId, hitId, out _)) return;

            ClearSelection(true);
            SelectIfUsable(hit, hitId);
        }

        private void SelectIfUsable(LiquidBottle bottle, int glassId)
        {
            RtGlass glass = controller != null ? controller.GlassById(glassId) : null;
            if (bottle == null || glass == null || glass.IsEmpty)
            {
                ClearSelection(true);
                return;
            }

            ClearSelection(true);
            selectedBottle = bottle;
            selectedGlassId = glassId;
            selectedHomePosition = bottle.transform.position;
        }

        private void AnimateSelection()
        {
            if (selectedBottle == null || Busy) return;

            float follow = 1f - Mathf.Exp(-selectionSpeed * Time.unscaledDeltaTime);
            Vector3 wanted = selectedHomePosition + Vector3.up * selectionLift;
            selectedBottle.transform.position = Vector3.Lerp(
                selectedBottle.transform.position, wanted, follow);

            BottleShell shell = selectedBottle.GetComponent<BottleShell>();
            if (shell != null)
                shell.highlight = Mathf.Lerp(shell.highlight, 1f, follow);
        }

        private void ClearSelection(bool restorePose)
        {
            LiquidBottle bottle = selectedBottle;
            if (bottle != null)
            {
                if (restorePose && (pourAnimator == null || !pourAnimator.Busy))
                    bottle.transform.position = selectedHomePosition;
                BottleShell shell = bottle.GetComponent<BottleShell>();
                if (shell != null) shell.highlight = 0f;
            }
            selectedBottle = null;
            selectedGlassId = -1;
            selectedHomePosition = default;
        }

        private void HandlePourFinished(int operationId, PourOutcome outcome)
        {
            if (operationId != activeOperationId) return;
            FinishPresentationTransaction(true);
        }

        private void FinishPresentationTransaction(bool refresh)
        {
            activeOperationId = 0;
            deliveryPresentationActive = false;
            int revision = lockedRevision;
            lockedRevision = -1;
            BartenderShelfLevelView finishingView = transactionView;
            BartenderLevelController finishingController = transactionController;
            transactionView = null;
            transactionController = null;
            transactionAnimator = null;
            activeTransactionToken = 0;

            try
            {
                if (finishingView != null
                    && finishingView.IsSynchronizationDeferredBy(this))
                    finishingView.EndSynchronizationDeferralAndRefresh(this, refresh);
            }
            catch (Exception exception)
            {
                // A third-party PresentationChanged listener must never wedge the rule
                // controller. The view already dropped deferral ownership before notifying.
                Debug.LogException(exception, finishingView);
            }
            finally
            {
                if (finishingController != null && revision >= 0)
                    finishingController.ReleasePresentationLock(this, revision);
            }
        }

        private int BeginPresentationTransaction(BartenderLevelController ownerController,
                                                 BartenderShelfLevelView ownerView,
                                                 PourAnimator ownerAnimator)
        {
            unchecked
            {
                nextTransactionToken++;
                if (nextTransactionToken == 0) nextTransactionToken = 1;
            }
            activeTransactionToken = nextTransactionToken;
            transactionController = ownerController;
            transactionView = ownerView;
            transactionAnimator = ownerAnimator;
            lockedRevision = -1;
            deliveryPresentationActive = false;
            return activeTransactionToken;
        }

        private bool CanContinueTransaction(int token,
                                            BartenderLevelController ownerController,
                                            BartenderShelfLevelView ownerView,
                                            PourAnimator ownerAnimator) =>
            token != 0 && activeTransactionToken == token && isActiveAndEnabled
            && ReferenceEquals(transactionController, ownerController)
            && ReferenceEquals(transactionView, ownerView)
            && ReferenceEquals(transactionAnimator, ownerAnimator)
            && ReferenceEquals(controller, ownerController)
            && ReferenceEquals(shelfView, ownerView)
            && ReferenceEquals(pourAnimator, ownerAnimator)
            && ownerView != null && ownerView.IsSynchronizationDeferredBy(this);

        private void CancelAndFinishPresentation()
        {
            PourAnimator activeAnimator = transactionAnimator;
            if (activeAnimator != null) activeAnimator.CancelActivePour();
            PortalDeliveryAnimator activePortal = deliveryPresentationActive
                && transactionView != null
                ? transactionView.DeliveryPortal
                : null;
            if (activePortal != null) activePortal.CancelAll();
            FinishPresentationTransaction(true);
        }

        private void HandleLevelLoaded(BsLevel _) => ClearSelection(true);

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing && activeOperationId == 0)
                ClearSelection(true);
        }

        private void HandlePresentationChanged()
        {
            if (activeOperationId == 0) ClearSelection(false);
        }

        private void HandleDeliveryPresentationFinished()
        {
            if (!deliveryPresentationActive) return;
            FinishPresentationTransaction(false);
        }

        /// <summary>
        /// A badge or future UI may commit through the controller directly. Its BoardChanged
        /// notification starts the same shelf portal before this component's Update runs. Take
        /// ownership here so those entry points receive the same bounce-length presentation
        /// lock as a body tap, without coupling them back to this input component.
        /// </summary>
        private void AdoptExternalDeliveryPresentation()
        {
            if (controller == null || shelfView == null || !shelfView.DeliveryPlaying
                || deliveryPresentationActive || activeOperationId != 0
                || activeTransactionToken != 0 || controller.PresentationLocked)
                return;

            int revision = controller.BoardRevision;
            BeginPresentationTransaction(controller, shelfView, pourAnimator);
            if (!controller.TryAcquirePresentationLock(this, revision))
            {
                FinishPresentationTransaction(false);
                return;
            }

            lockedRevision = revision;
            deliveryPresentationActive = true;
            ClearSelection(true);
        }

        private Camera ResolveCamera()
        {
            if (inputCamera == null) inputCamera = Camera.main;
            return inputCamera;
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourAnimator == null) pourAnimator = GetComponent<PourAnimator>();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                }
            }

            if (subscribedView != shelfView)
            {
                if (subscribedView != null)
                {
                    subscribedView.PresentationChanged -= HandlePresentationChanged;
                    subscribedView.DeliveryPresentationFinished -=
                        HandleDeliveryPresentationFinished;
                }
                subscribedView = shelfView;
                if (subscribedView != null)
                {
                    subscribedView.PresentationChanged += HandlePresentationChanged;
                    subscribedView.DeliveryPresentationFinished +=
                        HandleDeliveryPresentationFinished;
                }
            }

            if (subscribedAnimator != pourAnimator)
            {
                if (subscribedAnimator != null)
                    subscribedAnimator.PourFinished -= HandlePourFinished;
                subscribedAnimator = pourAnimator;
                if (subscribedAnimator != null)
                    subscribedAnimator.PourFinished += HandlePourFinished;
            }
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            if (subscribedView != null)
            {
                subscribedView.PresentationChanged -= HandlePresentationChanged;
                subscribedView.DeliveryPresentationFinished -=
                    HandleDeliveryPresentationFinished;
            }
            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            subscribedController = null;
            subscribedView = null;
            subscribedAnimator = null;
        }

        private bool Reject(string reason, out string rejectionReason)
        {
            LastRejection = string.IsNullOrEmpty(reason) ? "Dökme reddedildi." : reason;
            rejectionReason = LastRejection;
            return false;
        }
    }
}
