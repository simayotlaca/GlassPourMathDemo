using System;
using BartenderSort.Core;
using UnityEngine;

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
        [Tooltip("Round-token owner. Empty resolves the BartenderSession on this rig.")]
        [SerializeField] private BartenderSession session;
        [Tooltip("Optional. Empty resolves the OrderStripPresenter on this rig.")]
        [SerializeField] private OrderStripPresenter orderStrip;

        [Header("Host scene")]
        [Tooltip("Optional. A portable prefab resolves Camera.main when this is empty.")]
        [SerializeField] private Camera inputCamera;

        [Header("Pointer feel")]
        [SerializeField, Min(0f)] private float pickPadding = 0.22f;
        [SerializeField, Min(0f)] private float selectionLift = 0.16f;
        [SerializeField, Min(0.01f)] private float selectionSpeed = 14f;

        [Header("Rejected move feel")]
        [SerializeField, Min(0.08f)] private float rejectionDuration = 0.28f;
        [SerializeField, Range(0f, 12f)] private float sourceRejectionWobble = 3.5f;
        [SerializeField, Range(0f, 12f)] private float targetRejectionWobble = 6f;
        [SerializeField, Range(0f, 1f)] private float rejectionHighlightAlpha = 0.58f;
        [SerializeField] private Color rejectedSourceColor =
            new Color(1f, 0.63f, 0.16f, 1f);
        [SerializeField] private Color rejectedTargetColor =
            new Color(1f, 0.20f, 0.18f, 1f);

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private PourAnimator subscribedAnimator;
        private IBartenderInputPolicy inputPolicy;

        private LiquidBottle selectedBottle;
        private Transform selectedMotionRoot;
        private int selectedGlassId = -1;
        private Vector3 selectedHomePosition;
        private Quaternion selectedHomeRotation = Quaternion.identity;
        private Vector3 selectedHomeScale = Vector3.one;
        private float selectedRoyalRelativeScale = 1f;

        private int activeOperationId;
        private bool deliveryPresentationActive;
        private BartenderDeliveryReceipt activeDeliveryReceipt;
        private int lockedRevision = -1;
        private int activeTransactionToken;
        private int nextTransactionToken;
        private BartenderLevelController transactionController;
        private BartenderShelfLevelView transactionView;
        private PourAnimator transactionAnimator;
        private BartenderSession transactionSession;
        private BsRoundToken transactionRoundToken;
        private bool hasTransactionRoundToken;

        public int SelectedGlassId => selectedGlassId;
        public bool Busy => activeOperationId != 0 || deliveryPresentationActive
                         || (pourAnimator != null && pourAnimator.Busy)
                         || (orderStrip != null && orderStrip.TransitionPlaying);
        public string LastRejection { get; private set; }
        public BartenderLevelController Controller => controller;
        public BartenderShelfLevelView ShelfView => shelfView;
        public PourAnimator Animator => pourAnimator;
        public BartenderSession Session => session;
        public Camera InputCamera => ResolveCamera();
        public IBartenderInputPolicy InputPolicy
        {
            get
            {
                DropDestroyedInputPolicy();
                return inputPolicy;
            }
        }

        /// <summary>Raised only when the selected domain glass id actually changes.</summary>
        public event Action<int> SelectionChanged;

        /// <summary>
        /// Acquires the optional modal world-input gate without stealing it from another
        /// flow. Tutorial, accessibility and scripted-demo layers all share this lease.
        /// </summary>
        public bool TrySetInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null) return false;
            DropDestroyedInputPolicy();
            if (inputPolicy != null && !ReferenceEquals(inputPolicy, policy)) return false;
            inputPolicy = policy;
            return true;
        }

        public bool ClearInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null || !ReferenceEquals(inputPolicy, policy)) return false;
            inputPolicy = null;
            return true;
        }

        /// <summary>
        /// Lets a modal flow begin from a deterministic neutral pointer state without
        /// mutating the board. The normal selection-change receipt is still published.
        /// </summary>
        public void ClearSelectionForModal() => ClearSelection(true);

        public void Configure(BartenderLevelController levelController,
                              BartenderShelfLevelView view,
                              PourAnimator animator,
                              Camera sceneCamera = null,
                              BartenderSession roundSession = null)
        {
            CancelRejectionFeedbacks(true);
            CancelAndFinishPresentation();
            Unsubscribe();
            ClearSelection(true);
            inputPolicy = null;
            controller = levelController;
            shelfView = view;
            pourAnimator = animator;
            session = roundSession;
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
            CancelRejectionFeedbacks(true);
            CancelAndFinishPresentation();
            ClearSelection(true);
            inputPolicy = null;
        }

        private void OnValidate()
        {
            pickPadding = Mathf.Max(0f, pickPadding);
            selectionLift = Mathf.Max(0f, selectionLift);
            selectionSpeed = Mathf.Max(0.01f, selectionSpeed);
            rejectionDuration = Mathf.Max(0.08f, rejectionDuration);
            sourceRejectionWobble = Mathf.Clamp(sourceRejectionWobble, 0f, 12f);
            targetRejectionWobble = Mathf.Clamp(targetRejectionWobble, 0f, 12f);
            rejectionHighlightAlpha = Mathf.Clamp01(rejectionHighlightAlpha);
        }

        private void Update()
        {
            AnimateSelection();
            if (TryHandleTerminalPointer()) return;
            if (!CanReadPointer()
                || !TryReadPointerDown(out Vector2 screenPoint, out int pointerId)) return;
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId)) return;
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

            if (!CheckInputPolicy(
                    BartenderInputRequest.Pour(sourceGlassId, targetGlassId),
                    out rejectionReason))
                return false;

            if (controller == null || shelfView == null || pourAnimator == null
                || session == null)
                return Reject("Gameplay rig controller/view/animator/session bağlantısı eksik.",
                              out rejectionReason);
            if (!ReferenceEquals(session.Controller, controller))
                return Reject("Tur FSM'i farklı bir level controller'a bağlı.",
                              out rejectionReason);
            if (!session.AcceptsInput)
                return Reject("Tur FSM'i şu anda gameplay komutu kabul etmiyor.",
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
            if (!rule.Success)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                return Reject(rule.Reason, out rejectionReason);
            }

            // A new legal operation owns these roots next. Stop only our feedback
            // sequences (never transform.DOKill), restore their authored rotations, then
            // hand the same transforms to PourAnimator.
            CancelRejectionFeedback(source, true);
            CancelRejectionFeedback(target, true);

            if (!shelfView.TryGetSeatPose(sourceGlassId,
                    out BartenderGlassSeatPose home))
                return Reject("Kaynak bardağın raf oturma pozu bulunamadı.",
                              out rejectionReason);

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
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                return Reject(domainRejection, out rejectionReason);
            }

            CaptureTransactionRoundToken(transactionToken);

            // TryPour synchronously notifies every listener. One of them may disable or
            // reconfigure this bridge; in that case its lifecycle cleanup already reconciled
            // the committed move and continuing here would orphan a new lock.
            if (!CanContinueTransaction(transactionToken, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction = activeTransactionToken == transactionToken;
                if (stillOwnsTransaction)
                {
                    FinishPresentationTransaction(IsTransactionRoundCurrent());
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
                    source, target, receipt.Amount, home.MotionRoot,
                    home.Position, home.Rotation, home.LocalScale, false);
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

            if (!CheckInputPolicy(BartenderInputRequest.Delivery(glassId),
                                  out rejectionReason))
                return false;

            if (controller == null || shelfView == null || session == null)
                return Reject("Gameplay rig controller/view/session bağlantısı eksik.",
                              out rejectionReason);
            if (!ReferenceEquals(session.Controller, controller))
                return Reject("Tur FSM'i farklı bir level controller'a bağlı.",
                              out rejectionReason);
            if (!session.AcceptsInput)
                return Reject("Tur FSM'i şu anda gameplay komutu kabul etmiyor.",
                              out rejectionReason);
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.DeliveryPlaying || Busy || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.",
                              out rejectionReason);
            if (!shelfView.TryGetBottle(glassId, out LiquidBottle deliveryBottle))
                return Reject("Bardağın aktif sahne bağlantısı bulunamadı.",
                              out rejectionReason);
            if (controller.MatchedOrderSlot(glassId) < 0)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Reject("Bardak açık bir siparişi karşılamıyor.",
                              out rejectionReason);
            }

            CancelRejectionFeedback(deliveryBottle, true);

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
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Reject(domainRejection, out rejectionReason);
            }

            CaptureTransactionRoundToken(transactionToken);

            // Delivered/BoardChanged are synchronous. If a listener reconfigured this rig,
            // its lifecycle cleanup already reconciled the committed board and there is no
            // safe object left on which to start a portal presentation.
            if (!CanContinueTransaction(transactionToken, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction = activeTransactionToken == transactionToken;
                if (stillOwnsTransaction)
                    FinishPresentationTransaction(IsTransactionRoundCurrent());
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
            activeDeliveryReceipt = receipt;

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
                && session != null
                && ReferenceEquals(session.Controller, controller)
                && controller.State == BartenderLevelState.Playing
                && session.AcceptsInput
                && !controller.PresentationLocked
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.DeliveryPlaying
                && !Busy;
        }

        /// <summary>
        /// Bu prototype sahnesinde henüz authored win/fail paneli yok. Kaynak oyundaki
        /// explicit Devam / Tekrar Dene butonlarının code-only karşılığı olarak, terminal
        /// sunumu bittikten sonraki ilk YENİ dokunuş session'a niyet yollar. Session'ın
        /// frame/token kapısı teslimi başlatan aynı dokunuşun burada tekrar kullanılmasını
        /// ve çift dokunuşla iki level atlanmasını engeller.
        /// </summary>
        private bool TryHandleTerminalPointer()
        {
            if (controller == null || shelfView == null || session == null
                || !ReferenceEquals(session.Controller, controller))
                return false;

            bool continueAfterWin = session.CanContinueAfterWin;
            bool retryAfterFailure = session.CanRetryAfterFailure;
            if (!continueAfterWin && !retryAfterFailure) return false;
            if (Busy || controller.PresentationLocked || !shelfView.Ready
                || shelfView.SeatAnimationPlaying || shelfView.DeliveryPlaying
                || shelfView.SynchronizationDeferred)
                return false;
            if (!TryReadPointerDown(out Vector2 screenPoint, out int pointerId))
                return false;

            // Gerçek bir sonuç paneli eklendiğinde UI click'i kendi butonuna bırakılır;
            // world tap fallback aynı click'i tüketmez.
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId)) return true;
            if (!CheckInputPolicy(
                    BartenderInputRequest.Background(selectedGlassId), out _))
                return true;

            bool accepted = continueAfterWin
                ? session.RequestContinueAfterWin()
                : session.RequestRetryAfterFailure();
            if (!accepted)
                LastRejection = "Terminal geçiş niyeti artık güncel değil.";
            return true;
        }

        private static bool TryReadPointerDown(out Vector2 screenPoint,
                                               out int pointerId)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Began)
                {
                    screenPoint = touch.position;
                    pointerId = touch.fingerId;
                    return true;
                }
            }

            if (Input.touchCount > 0)
            {
                screenPoint = default;
                pointerId = -1;
                return false;
            }

            if (Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                pointerId = -1;
                return true;
            }

            screenPoint = default;
            pointerId = -1;
            return false;
        }

        private void HandlePointerDown(Vector2 screenPoint)
        {
            Camera camera = ResolveCamera();
            if (camera == null
                || !shelfView.TryPickBottle(camera, screenPoint, pickPadding,
                                            out LiquidBottle hit, out int hitId))
            {
                if (!CheckInputPolicy(
                        BartenderInputRequest.Background(selectedGlassId), out _))
                    return;
                ClearSelectionWithSound(true);
                return;
            }

            if (!CheckInputPolicy(
                    BartenderInputRequest.Bottle(hitId, selectedGlassId), out _))
                return;

            if (controller != null && controller.MatchedOrderSlot(hitId) >= 0)
            {
                ClearSelectionWithSound(true);
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
                ClearSelectionWithSound(true);
                return;
            }

            int sourceId = selectedGlassId;
            if (TryCommitAndAnimatePour(sourceId, hitId, out _)) return;
            // Kaynak tap akışında geçersiz hedef eski seçimi korur. Böylece Invalid
            // ile yeni bir GlassPickup aynı karede üst üste çalmaz.
            return;
        }

        private void SelectIfUsable(LiquidBottle bottle, int glassId)
        {
            if (bottle == null || controller == null
                || !controller.CanSelectAsPourSource(glassId))
            {
                ClearSelection(true);
                return;
            }

            ClearSelection(true);
            if (!shelfView.TryGetSeatPose(glassId,
                    out BartenderGlassSeatPose home))
                return;
            selectedBottle = bottle;
            selectedMotionRoot = home.MotionRoot != null
                ? home.MotionRoot
                : bottle.transform;
            selectedGlassId = glassId;
            selectedHomePosition = home.Position;
            selectedHomeRotation = home.Rotation;
            selectedHomeScale = home.LocalScale;
            selectedRoyalRelativeScale = VesselPresentationMath.RelativeToRoyalReference(
                bottle.transform, bottle.profile);
            BsAudio.Instance?.Play(BsSfx.GlassPickup);
            NotifySelectionChanged(glassId);
        }

        private void AnimateSelection()
        {
            if (selectedBottle == null || Busy) return;

            // Safe-area fitting runs after ordinary gameplay LateUpdates and may move or
            // scale the complete composition after a glass was selected. Always ask the
            // shelf for its current layout-derived world pose instead of animating towards
            // a stale snapshot from the selection frame.
            if (shelfView != null && selectedGlassId >= 0
                && shelfView.TryGetSeatPose(selectedGlassId,
                    out BartenderGlassSeatPose liveHome))
            {
                selectedHomePosition = liveHome.Position;
                selectedHomeRotation = liveHome.Rotation;
                selectedHomeScale = liveHome.LocalScale;
                selectedMotionRoot = liveHome.MotionRoot != null
                    ? liveHome.MotionRoot
                    : selectedBottle.transform;
                selectedRoyalRelativeScale = VesselPresentationMath.RelativeToRoyalReference(
                    selectedBottle.transform, selectedBottle.profile);
            }

            Transform motionRoot = selectedMotionRoot != null
                ? selectedMotionRoot
                : selectedBottle.transform;

            float follow = 1f - Mathf.Exp(-selectionSpeed * Time.unscaledDeltaTime);
            float scaledLift = VesselPresentationMath.ReferenceDistance(
                selectionLift, selectedRoyalRelativeScale);
            Vector3 liftDirection = shelfView != null
                ? shelfView.LayoutUpWorld
                : Vector3.up;
            Vector3 wanted = selectedHomePosition + liftDirection * scaledLift;
            motionRoot.position = Vector3.Lerp(
                motionRoot.position, wanted, follow);
            BartenderInvalidMoveFeedback rejection =
                selectedBottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (rejection == null || !rejection.Playing)
            {
                motionRoot.rotation = Quaternion.Slerp(
                    motionRoot.rotation, selectedHomeRotation, follow);
            }
            motionRoot.localScale = Vector3.Lerp(
                motionRoot.localScale, selectedHomeScale, follow);

            BottleShell shell = selectedBottle.GetComponent<BottleShell>();
            if (shell != null)
                shell.highlight = Mathf.Lerp(shell.highlight, 1f, follow);
        }

        private void ClearSelection(bool restorePose)
        {
            int previousGlassId = selectedGlassId;
            LiquidBottle bottle = selectedBottle;
            if (bottle != null)
            {
                if (restorePose && (pourAnimator == null || !pourAnimator.Busy))
                {
                    if (shelfView != null && previousGlassId >= 0
                        && shelfView.TryGetSeatPose(previousGlassId,
                            out BartenderGlassSeatPose liveHome))
                    {
                        selectedHomePosition = liveHome.Position;
                        selectedHomeRotation = liveHome.Rotation;
                        selectedHomeScale = liveHome.LocalScale;
                        selectedMotionRoot = liveHome.MotionRoot != null
                            ? liveHome.MotionRoot
                            : bottle.transform;
                    }
                    Transform motionRoot = selectedMotionRoot != null
                        ? selectedMotionRoot
                        : bottle.transform;
                    motionRoot.SetPositionAndRotation(
                        selectedHomePosition, selectedHomeRotation);
                    motionRoot.localScale = selectedHomeScale;
                }
                BottleShell shell = bottle.GetComponent<BottleShell>();
                if (shell != null) shell.highlight = 0f;
            }
            selectedBottle = null;
            selectedMotionRoot = null;
            selectedGlassId = -1;
            selectedHomePosition = default;
            selectedHomeRotation = Quaternion.identity;
            selectedHomeScale = Vector3.one;
            selectedRoyalRelativeScale = 1f;
            if (previousGlassId >= 0) NotifySelectionChanged(-1);
        }

        private void ClearSelectionWithSound(bool restorePose)
        {
            bool hadSelection = selectedBottle != null;
            ClearSelection(restorePose);
            if (hadSelection) BsAudio.Instance?.Play(BsSfx.GlassSet);
        }

        private void HandlePourFinished(int operationId, PourOutcome outcome)
        {
            if (operationId != activeOperationId) return;
            FinishPresentationTransaction(IsTransactionRoundCurrent());
        }

        private void FinishPresentationTransaction(bool refresh)
        {
            activeOperationId = 0;
            deliveryPresentationActive = false;
            activeDeliveryReceipt = null;
            int revision = lockedRevision;
            lockedRevision = -1;
            BartenderShelfLevelView finishingView = transactionView;
            BartenderLevelController finishingController = transactionController;
            transactionView = null;
            transactionController = null;
            transactionAnimator = null;
            transactionSession = null;
            transactionRoundToken = default;
            hasTransactionRoundToken = false;
            activeTransactionToken = 0;

            try
            {
                if (finishingView != null
                    && finishingView.IsSynchronizationDeferredBy(this))
                {
                    if (refresh)
                        finishingView.EndSynchronizationDeferralAndRefresh(this, true);
                    else
                        finishingView.DropSynchronizationDeferral(this);
                }
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
            transactionSession = session;
            transactionRoundToken = default;
            hasTransactionRoundToken = false;
            lockedRevision = -1;
            deliveryPresentationActive = false;
            activeDeliveryReceipt = null;
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
            && ReferenceEquals(transactionSession, session)
            && ReferenceEquals(controller, ownerController)
            && ReferenceEquals(shelfView, ownerView)
            && ReferenceEquals(pourAnimator, ownerAnimator)
            && IsTransactionRoundCurrent()
            && ownerView != null && ownerView.IsSynchronizationDeferredBy(this);

        private void CaptureTransactionRoundToken(int token)
        {
            if (token == 0 || activeTransactionToken != token
                || transactionSession == null) return;
            transactionRoundToken = transactionSession.CurrentToken;
            hasTransactionRoundToken = true;
        }

        private bool IsTransactionRoundCurrent()
        {
            return !hasTransactionRoundToken
                || (transactionSession != null
                    && ReferenceEquals(session, transactionSession)
                    && transactionSession.IsTokenCurrent(transactionRoundToken));
        }

        private void CancelAndFinishPresentation()
        {
            PourAnimator activeAnimator = transactionAnimator;
            if (activeAnimator != null) activeAnimator.CancelActivePour();
            PortalDeliveryAnimator activePortal = deliveryPresentationActive
                && transactionView != null
                ? transactionView.DeliveryPortal
                : null;
            if (activePortal != null) activePortal.CancelAll();
            FinishPresentationTransaction(IsTransactionRoundCurrent());
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            CancelRejectionFeedbacks(true);
            ClearSelection(true);
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing && activeOperationId == 0)
            {
                CancelRejectionFeedbacks(true);
                ClearSelectionWithSound(true);
            }
        }

        private void HandlePresentationChanged()
        {
            if (activeOperationId != 0) return;

            // The shelf has already applied its new authoritative poses. Killing without
            // restoring an older cached rotation prevents a stale feedback tween from
            // writing over that freshly laid-out board.
            CancelRejectionFeedbacks(false);
            ClearSelection(false);
        }

        private void PlayRejectedPourFeedback(LiquidBottle source, LiquidBottle target)
        {
            float direction = source != null && target != null
                && source.transform.position.x > target.transform.position.x
                ? -1f
                : 1f;
            PlayRejectionFeedback(source, rejectedSourceColor,
                -direction * sourceRejectionWobble);
            PlayRejectionFeedback(target, rejectedTargetColor,
                direction * targetRejectionWobble);
        }

        private void PlayRejectionFeedback(LiquidBottle bottle, Color color,
                                           float wobbleDegrees)
        {
            if (bottle == null || !bottle.gameObject.activeInHierarchy) return;
            BartenderInvalidMoveFeedback feedback =
                bottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (feedback == null)
                feedback = bottle.gameObject.AddComponent<BartenderInvalidMoveFeedback>();
            Transform motionRoot = bottle.transform;
            if (shelfView != null
                && shelfView.TryGetMotionRoot(bottle, out Transform resolvedRoot))
                motionRoot = resolvedRoot;
            feedback.Play(color, rejectionHighlightAlpha,
                wobbleDegrees, rejectionDuration, motionRoot);
        }

        private static void CancelRejectionFeedback(LiquidBottle bottle,
                                                     bool restoreRotation)
        {
            if (bottle == null) return;
            BartenderInvalidMoveFeedback feedback =
                bottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (feedback != null) feedback.Cancel(restoreRotation);
        }

        private void CancelRejectionFeedbacks(bool restoreRotation)
        {
            if (shelfView == null) return;
            BartenderInvalidMoveFeedback[] feedbacks =
                shelfView.GetComponentsInChildren<BartenderInvalidMoveFeedback>(true);
            for (int i = 0; i < feedbacks.Length; i++)
            {
                BartenderInvalidMoveFeedback feedback = feedbacks[i];
                if (feedback != null) feedback.Cancel(restoreRotation);
            }
        }

        private void HandleDeliveryPresentationFinished(BartenderDeliveryReceipt receipt)
        {
            if (!deliveryPresentationActive
                || !ReferenceEquals(activeDeliveryReceipt, receipt)) return;
            // A stale callback may only clean up its own lease; it must never refresh a
            // replacement round. Delivery already refreshed before the portal started.
            FinishPresentationTransaction(false);
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
            if (session == null) session = GetComponent<BartenderSession>();
            if (orderStrip == null) orderStrip = GetComponent<OrderStripPresenter>();
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

        private bool CheckInputPolicy(BartenderInputRequest request,
                                      out string rejectionReason)
        {
            rejectionReason = null;
            DropDestroyedInputPolicy();
            IBartenderInputPolicy policy = inputPolicy;
            if (policy == null) return true;

            bool allowed;
            try
            {
                allowed = policy.Allows(request, out rejectionReason);
            }
            catch (Exception exception)
            {
                // A presentation-only policy must never wedge the authoritative gameplay
                // bridge. Drop a broken lease and fail open.
                Debug.LogException(exception, this);
                if (ReferenceEquals(inputPolicy, policy)) inputPolicy = null;
                rejectionReason = null;
                return true;
            }

            if (allowed) return true;
            if (string.IsNullOrEmpty(rejectionReason))
                rejectionReason = "Bu adımda parlayan hedefe dokun.";
            LastRejection = rejectionReason;
            try
            {
                policy.HandleRejected(request, rejectionReason);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                if (ReferenceEquals(inputPolicy, policy)) inputPolicy = null;
            }
            return false;
        }

        private void DropDestroyedInputPolicy()
        {
            if (inputPolicy is UnityEngine.Object unityOwner && unityOwner == null)
                inputPolicy = null;
        }

        private void NotifySelectionChanged(int glassId)
        {
            Action<int> handlers = SelectionChanged;
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<int>)invocationList[i]).Invoke(glassId);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private bool Reject(string reason, out string rejectionReason)
        {
            LastRejection = string.IsNullOrEmpty(reason) ? "Dökme reddedildi." : reason;
            rejectionReason = LastRejection;
            return false;
        }
    }
}
