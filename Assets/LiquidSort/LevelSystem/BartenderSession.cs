using System;
using System.Collections;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Tur FSM'inin ve terminal navigation kapısının sahibi.
    ///
    /// FSM controller'ı aynalar; terminal kararını, progress yazımını ve board'u hâlâ
    /// controller sahiplenir. Session'ın yazabildiği tek şey kullanıcıdan gelen açık
    /// "devam / yeniden dene / ana menü" niyetidir. Bu niyet terminal frame'inde çalıştırılmaz:
    /// sebep olan dökme/teslim sunumu bitip token hâlâ güncel kaldıktan sonra controller'ın
    /// state-guarded navigation komutuna aktarılır. Böylece iki ayrı level otoritesi oluşmaz.
    ///
    ///   level yükleme   : Start() loadOnStart ile kendi kendine yüklüyor
    ///   terminal karar  : EvaluateTerminalState Won/Failed'ı kendisi yazıyor
    ///   kalıcılık       : PlayerPrefs yazımı kazanma dalının İÇİNDE
    ///   pause           : BartenderLevelController.Pause/Resume
    ///
    /// Aynalama yine de bedava değil, asıl kazancı veriyor: <see cref="CurrentToken"/>.
    /// Teslim animasyonu ~0.87 sn sürüyor; level o sırada yenilenirse uçuştan dönen
    /// gecikmeli callback artık bayat token'ından tanınabiliyor. Sunum köprüsü bu token'ı
    /// tüketir; bayat callback yalnız kendi kilidini bırakır, yeni turu yenileyemez.
    ///
    /// Diğer yetkiler devredilmez: controller tek load/terminal/progress/pause sahibidir.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderSession : MonoBehaviour
    {
        private enum TerminalIntent
        {
            None,
            ContinueAfterWin,
            RetryAfterFailure,
            PaidRetryAfterFailure,
            ReturnToMainMenu,
        }

        [Header("Level source")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Terminal presentation bariyeri. Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [Tooltip("Seat/portal/deferral bariyeri. Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Tooltip("Kabul edilen her akış geçişini konsola yazar. Yalnız teşhis içindir.")]
        [SerializeField] private bool logTransitions = false;

        private readonly BsRoundStateMachine machine =
            new BsRoundStateMachine(BsFlowState.Menu);

        private BartenderLevelController subscribedController;
        private BartenderLevelState mirroredState = BartenderLevelState.Unloaded;
        private TerminalIntent pendingTerminalIntent;
        private int pendingTerminalCoinCost;
        private BsRoundOutcome? terminalOutcome;
        private BsRoundToken terminalToken;
        private int terminalEnteredFrame = -1;
        private bool terminalReady;
        private bool terminalCommandInProgress;
        private BartenderLoadingOverlayPresenter loadingOverlay;
        private Coroutine terminalLoadingRoutine;
        private BartenderLevelController terminalLoadingBarrierController;

        public BartenderLevelController Controller => controller;

        public BsFlowState State => machine.State;

        /// <summary>
        /// Bu gameplay neslinin kimliği. Uzun süren bir sunum başlarken kopyalanır,
        /// bittiğinde <see cref="IsGameplayTokenValid"/> ile sınanır: eşleşmiyorsa
        /// arada level değişmiş demektir ve callback hiçbir şeye dokunmamalıdır.
        /// </summary>
        public BsRoundToken CurrentToken => machine.CurrentToken;

        public bool AcceptsInput => BsFlowRules.AcceptsInput(machine.State);
        public bool TimersRun => BsFlowRules.TimersRun(machine.State);
        public bool IsLevelOver => BsFlowRules.IsLevelOver(machine.State);
        public bool CanPause => BsFlowRules.CanPause(machine.State);
        public bool CanContinueAfterWin => CanRequestTerminal(BsRoundOutcome.Won);
        public bool CanRetryAfterFailure => CanRequestTerminal(BsRoundOutcome.Failed);
        public bool CanReturnToMainMenuFromTerminal =>
            terminalOutcome.HasValue && CanRequestTerminal(terminalOutcome.Value);

        public bool IsGameplayTokenValid(BsRoundToken token) =>
            machine.IsGameplayTokenValid(token);

        public bool IsTokenCurrent(BsRoundToken token) => machine.IsTokenCurrent(token);

        public bool TryGetPlayingToken(out BsRoundToken token) =>
            machine.TryGetPlayingToken(out token);

        /// <summary>
        /// Kabul edilen her geçişte, geçişin KENDİ içinde yayımlanır. Kaynak projede
        /// mutasyon ile yayın ayrıydı ve doğruluk on iki ayrı çağrı yerinin disiplinine
        /// bağlıydı; burada ayırmıyoruz, çünkü derleyici o disiplini kontrol etmiyor.
        /// </summary>
        public event Action<BsTransitionResult> FlowChanged;

        /// <summary>
        /// Terminal state'in sebep sunumu tamamen bittiğinde bir kez yayımlanır. Gerçek
        /// sonuç paneli ileride butonlarını bu fact'e göre açabilir.
        /// </summary>
        public event Action<BsRoundOutcome> TerminalReady;

        /// <summary>Queued terminal navigation tamamlandığında sonucu yayımlar.</summary>
        public event Action<BartenderTerminalCommandResult> TerminalCommandCompleted;

        private void Awake()
        {
            ResolveDependencies();
            BsAudio.Ensure()?.StartBgm();
            if (GetComponent<BartenderAudioBridge>() == null)
                gameObject.AddComponent<BartenderAudioBridge>();
            loadingOverlay = BartenderLoadingOverlayPresenter.Attach(gameObject);
            loadingOverlay?.Prewarm();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            // Bileşen sonradan eklenmiş olabilir: controller çoktan bir level yüklemiş
            // olsa bile akış onun bulunduğu yere getirilir.
            SyncFromController();
        }

        private void OnDisable()
        {
            CancelTerminalLoadingTransition();
            Unsubscribe();
            ResetTerminalNavigation();
            mirroredState = BartenderLevelState.Unloaded;
            if (machine.State != BsFlowState.Menu)
                Dispatch(BsFlowTrigger.ReturnToMenu);
        }

        private void LateUpdate()
        {
            // Readiness'in açıldığı frame'de komut çalıştırılmaz. Bu, TerminalReady
            // dinleyicisinden gelen reentrant navigation'ı da bir sonraki frame'e iter.
            if (TryOpenTerminalGate()) return;
            PumpTerminalIntent();
        }

        private void ResolveDependencies()
        {
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourInteraction == null)
                pourInteraction = GetComponent<BartenderPourInteraction>();
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
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

        /// <summary>
        /// Akışın Playing'e girdiği tek nokta BURASI, StateChanged değil. Controller
        /// bildirimleri komut sırasında birleştiriyor ve yalnız SON durumu yayıyor:
        /// yüklenir yüklenmez kazanılan bir level'da dinleyici Playing'i hiç görmez.
        /// LevelLoaded ise StateChanged'den önce ve her yüklemede tam bir kez çıkıyor.
        /// </summary>
        private void HandleLevelLoaded(BsLevel level)
        {
            ResetTerminalNavigation();
            // Menu -> Loading -> Playing. Controller yüklemeyi zaten bitirdiği için iki
            // geçiş arka arkaya gider; Loading burada bir bekleme değil, sadece FSM'in
            // yeni tur kimliğini (RoundId) ürettiği kapı.
            Dispatch(BsFlowTrigger.LoadRequested);
            Dispatch(BsFlowTrigger.LevelLoaded);
            mirroredState = BartenderLevelState.Playing;
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == mirroredState) return;
            BartenderLevelState previous = mirroredState;
            mirroredState = state;

            switch (state)
            {
                case BartenderLevelState.Playing:
                    // Yalnızca pause'dan dönüş. İlk giriş LevelLoaded'ın işi.
                    if (previous == BartenderLevelState.Paused)
                        Dispatch(BsFlowTrigger.ResumeRequested);
                    break;

                case BartenderLevelState.Paused:
                    Dispatch(BsFlowTrigger.PauseRequested);
                    break;

                case BartenderLevelState.Won:
                    Finish(BsRoundOutcome.Won);
                    ArmTerminalNavigation(BsRoundOutcome.Won);
                    break;

                case BartenderLevelState.Failed:
                    Finish(BsRoundOutcome.Failed);
                    ArmTerminalNavigation(BsRoundOutcome.Failed);
                    break;

                case BartenderLevelState.Unloaded:
                case BartenderLevelState.CampaignComplete:
                    // CampaignComplete'in FSM karşılığı yok; ikisi de menüye düşer.
                    // Çeviri kayıplı, bilerek: kampanya bitişi akışın değil üst
                    // katmanın bilgisi.
                    ResetTerminalNavigation();
                    Dispatch(BsFlowTrigger.ReturnToMenu);
                    break;
            }
        }

        /// <summary>
        /// Terminal geçiş tabloda değil, atomik TryFinish yolunda yaşar: önce durum
        /// kilitlenir, sonra epoch artar. Sıra bu yüzden önemli — epoch arttığı anda
        /// uçuştaki bütün gameplay callback'leri bayatlar.
        /// </summary>
        private void Finish(BsRoundOutcome outcome)
        {
            if (machine.TryFinish(machine.CurrentToken, outcome,
                    out BsTransitionResult transition))
            {
                Publish(transition);
                return;
            }

            // Playing'de değilken terminal geldi. Aynalayıcı için bu bir hata değil:
            // controller yüklenir yüklenmez kazanılmış bir level'ı böyle bildirebilir.
            if (logTransitions)
                Debug.Log($"Akış: terminal '{outcome}' reddedildi ({transition.RejectReason}), "
                        + $"durum {machine.State}.", this);
        }

        private void Dispatch(BsFlowTrigger trigger)
        {
            BsTransitionResult transition = machine.Dispatch(trigger);
            if (transition.Accepted) Publish(transition);
            else if (logTransitions)
                Debug.Log($"Akış: '{trigger}' reddedildi ({transition.RejectReason}), "
                        + $"durum {machine.State}.", this);
        }

        private void Publish(BsTransitionResult transition)
        {
            if (logTransitions)
                Debug.Log($"Akış: {transition.From} -> {transition.To} "
                        + $"({transition.Trigger}), {transition.Token}.", this);
            InvokeSafely(FlowChanged, transition);
        }

        /// <summary>
        /// Sonuç paneli veya code-only terminal input için explicit win niyeti. Token'lı
        /// overload, eski bir panel callback'inin yeni round'u ilerletmesini engeller.
        /// </summary>
        public bool RequestContinueAfterWin() =>
            RequestContinueAfterWin(machine.CurrentToken);

        public bool RequestContinueAfterWin(BsRoundToken expectedTerminalToken)
        {
            if (expectedTerminalToken != terminalToken
                || !CanRequestTerminal(BsRoundOutcome.Won)) return false;
            pendingTerminalIntent = TerminalIntent.ContinueAfterWin;
            return true;
        }

        /// <summary>Explicit failure retry intent; stale terminal token'ları reddeder.</summary>
        public bool RequestRetryAfterFailure() =>
            RequestRetryAfterFailure(machine.CurrentToken);

        public bool RequestRetryAfterFailure(BsRoundToken expectedTerminalToken)
        {
            if (expectedTerminalToken != terminalToken
                || !CanRequestTerminal(BsRoundOutcome.Failed)) return false;
            pendingTerminalIntent = TerminalIntent.RetryAfterFailure;
            return true;
        }

        /// <summary>
        /// Jetonla bir canı geri alıp aynı bölümü yeniden başlatma niyeti. Harcama bu
        /// çağrıda değil, token ve terminal state hâlâ geçerliyken controller komutunda
        /// atomik olarak yapılır.
        /// </summary>
        public bool RequestPaidRetryAfterFailure(
            BsRoundToken expectedTerminalToken, int coinCost)
        {
            if (expectedTerminalToken != terminalToken || coinCost <= 0
                || BartenderProgressService.Lives >= BartenderProgressService.MaxLives
                || !BartenderProgressService.CanAfford(coinCost)
                || !CanRequestTerminal(BsRoundOutcome.Failed)) return false;
            pendingTerminalCoinCost = coinCost;
            pendingTerminalIntent = TerminalIntent.PaidRetryAfterFailure;
            return true;
        }

        /// <summary>
        /// Won veya Failed sonuç kartını kapatıp ana menüye döner. Token'lı overload,
        /// eski bir kartın yeni turu kapatmasını; intent kapısı da çift tıklamayı önler.
        /// Terminal settlement controller tarafında daha önce tamamlandığı için bu komut
        /// başarı/başarısızlık makbuzunu veya can eksiltmeyi tekrarlamaz.
        /// </summary>
        public bool RequestReturnToMainMenuFromTerminal() =>
            RequestReturnToMainMenuFromTerminal(machine.CurrentToken);

        public bool RequestReturnToMainMenuFromTerminal(
            BsRoundToken expectedTerminalToken)
        {
            if (expectedTerminalToken != terminalToken || !terminalOutcome.HasValue
                || !CanRequestTerminal(terminalOutcome.Value)) return false;
            pendingTerminalIntent = TerminalIntent.ReturnToMainMenu;
            return true;
        }

        private void ArmTerminalNavigation(BsRoundOutcome outcome)
        {
            BsFlowState expected = outcome == BsRoundOutcome.Won
                ? BsFlowState.Won
                : BsFlowState.Failed;
            if (machine.State != expected) return;

            pendingTerminalIntent = TerminalIntent.None;
            pendingTerminalCoinCost = 0;
            terminalOutcome = outcome;
            terminalToken = machine.CurrentToken;
            terminalEnteredFrame = Time.frameCount;
            terminalReady = false;
            terminalCommandInProgress = false;
        }

        private bool TryOpenTerminalGate()
        {
            if (terminalReady || !terminalOutcome.HasValue
                || terminalCommandInProgress || controller == null)
                return false;
            if (Time.frameCount <= terminalEnteredFrame
                || !machine.IsTokenCurrent(terminalToken)
                || !PresentationBarrierClear())
                return false;

            BsRoundOutcome outcome = terminalOutcome.Value;
            if (!TerminalStatesMatch(outcome)) return false;

            terminalReady = true;
            InvokeTerminalReadySafely(outcome);
            return true;
        }

        private bool CanRequestTerminal(BsRoundOutcome outcome)
        {
            return terminalReady && terminalOutcome == outcome
                && pendingTerminalIntent == TerminalIntent.None
                && !terminalCommandInProgress
                && Time.frameCount > terminalEnteredFrame
                && machine.IsTokenCurrent(terminalToken)
                && PresentationBarrierClear()
                && TerminalStatesMatch(outcome);
        }

        private bool TerminalStatesMatch(BsRoundOutcome outcome)
        {
            if (controller == null) return false;
            return outcome == BsRoundOutcome.Won
                ? machine.State == BsFlowState.Won
                    && controller.State == BartenderLevelState.Won
                : machine.State == BsFlowState.Failed
                    && controller.State == BartenderLevelState.Failed;
        }

        private void PumpTerminalIntent()
        {
            TerminalIntent intent = pendingTerminalIntent;
            if (intent == TerminalIntent.None || terminalCommandInProgress
                || controller == null || !terminalOutcome.HasValue)
                return;

            BsRoundOutcome outcome = intent == TerminalIntent.ContinueAfterWin
                ? BsRoundOutcome.Won
                : intent == TerminalIntent.RetryAfterFailure
                    || intent == TerminalIntent.PaidRetryAfterFailure
                    ? BsRoundOutcome.Failed
                    : terminalOutcome.Value;
            if (!CanExecuteQueuedIntent(outcome))
            {
                pendingTerminalIntent = TerminalIntent.None;
                pendingTerminalCoinCost = 0;
                return;
            }

            BsRoundToken requestedToken = terminalToken;
            int requestedCoinCost = pendingTerminalCoinCost;
            pendingTerminalIntent = TerminalIntent.None;
            pendingTerminalCoinCost = 0;
            terminalReady = false;
            terminalCommandInProgress = true;

            if (intent != TerminalIntent.ReturnToMainMenu)
            {
                terminalLoadingRoutine = StartCoroutine(RunTerminalLoadIntent(
                    intent, outcome, requestedToken, requestedCoinCost));
                return;
            }

            BartenderTerminalCommandResult result;
            switch (intent)
            {
                case TerminalIntent.ContinueAfterWin:
                    result = controller.TryContinueAfterWin();
                    break;
                case TerminalIntent.RetryAfterFailure:
                    result = controller.TryRetryAfterFailure();
                    break;
                case TerminalIntent.PaidRetryAfterFailure:
                    result = controller.TryPaidRetryAfterFailure(requestedCoinCost);
                    break;
                case TerminalIntent.ReturnToMainMenu:
                    result = controller.TryReturnToMainMenuFromTerminal();
                    break;
                default:
                    result = BartenderTerminalCommandResult.Rejected;
                    break;
            }

            terminalCommandInProgress = false;
            if (result == BartenderTerminalCommandResult.Rejected
                && machine.IsTokenCurrent(requestedToken)
                && TerminalStatesMatch(outcome))
                terminalReady = true;

            InvokeTerminalCommandCompletedSafely(result);
        }

        private IEnumerator RunTerminalLoadIntent(
            TerminalIntent intent, BsRoundOutcome outcome,
            BsRoundToken requestedToken, int requestedCoinCost)
        {
            if (loadingOverlay == null)
                loadingOverlay = BartenderLoadingOverlayPresenter.Attach(gameObject);
            bool overlayVisible = loadingOverlay != null && loadingOverlay.Begin();
            if (overlayVisible)
            {
                loadingOverlay.AdvanceTo(0.32f, 0.24f);
                yield return null;
                yield return new WaitForSecondsRealtime(0.16f);
                loadingOverlay.AdvanceTo(0.56f, 0.14f);
                yield return new WaitForSecondsRealtime(0.10f);
            }
            else
            {
                yield return null;
            }

            BartenderTerminalCommandResult result =
                BartenderTerminalCommandResult.Rejected;
            if (CanExecuteQueuedIntent(outcome))
            {
                switch (intent)
                {
                    case TerminalIntent.ContinueAfterWin:
                        result = controller.TryContinueAfterWin();
                        break;
                    case TerminalIntent.RetryAfterFailure:
                        result = controller.TryRetryAfterFailure();
                        break;
                    case TerminalIntent.PaidRetryAfterFailure:
                        result = controller.TryPaidRetryAfterFailure(requestedCoinCost);
                        break;
                }
            }

            bool levelLoaded = result == BartenderTerminalCommandResult.NextLevelLoaded
                            || result == BartenderTerminalCommandResult.CurrentLevelReloaded;
            if (levelLoaded && controller != null
                && controller.AcquirePresentationBarrier(this))
                terminalLoadingBarrierController = controller;

            if (overlayVisible)
            {
                if (result == BartenderTerminalCommandResult.Rejected)
                    yield return loadingOverlay.CancelAndHide();
                else
                {
                    loadingOverlay.AdvanceTo(0.84f, 0.10f);
                    yield return new WaitForSecondsRealtime(0.06f);
                    yield return loadingOverlay.CompleteAndHide();
                }
            }

            ReleaseTerminalLoadingBarrier();
            terminalLoadingRoutine = null;
            terminalCommandInProgress = false;
            if (result == BartenderTerminalCommandResult.Rejected
                && machine.IsTokenCurrent(requestedToken)
                && TerminalStatesMatch(outcome))
                terminalReady = true;

            InvokeTerminalCommandCompletedSafely(result);
        }

        private void CancelTerminalLoadingTransition()
        {
            bool ownedTransition = terminalLoadingRoutine != null
                                || terminalLoadingBarrierController != null;
            if (terminalLoadingRoutine != null)
            {
                StopCoroutine(terminalLoadingRoutine);
                terminalLoadingRoutine = null;
            }
            ReleaseTerminalLoadingBarrier();
            if (ownedTransition && loadingOverlay != null)
                loadingOverlay.HideImmediate();
        }

        private void ReleaseTerminalLoadingBarrier()
        {
            if (terminalLoadingBarrierController != null)
                terminalLoadingBarrierController.ReleasePresentationBarrier(this);
            terminalLoadingBarrierController = null;
        }

        private bool CanExecuteQueuedIntent(BsRoundOutcome outcome)
        {
            return terminalOutcome == outcome
                && Time.frameCount > terminalEnteredFrame
                && machine.IsTokenCurrent(terminalToken)
                && PresentationBarrierClear()
                && TerminalStatesMatch(outcome);
        }

        private bool PresentationBarrierClear()
        {
            if (controller == null || controller.PresentationLocked) return false;
            if (pourInteraction != null && pourInteraction.Busy) return false;
            BartenderTutorialDirector tutorial = pourInteraction != null
                ? pourInteraction.GetComponent<BartenderTutorialDirector>()
                : null;
            if (tutorial == null) tutorial = GetComponent<BartenderTutorialDirector>();
            if (tutorial != null && tutorial.BlocksTerminalPresentation) return false;
            return shelfView == null
                || (!shelfView.SeatAnimationPlaying
                    && !shelfView.DeliveryPlaying
                    && !shelfView.SynchronizationDeferred);
        }

        private void ResetTerminalNavigation()
        {
            pendingTerminalIntent = TerminalIntent.None;
            pendingTerminalCoinCost = 0;
            terminalOutcome = null;
            terminalToken = default;
            terminalEnteredFrame = -1;
            terminalReady = false;
            terminalCommandInProgress = false;
        }

        private void InvokeTerminalReadySafely(BsRoundOutcome outcome)
        {
            InvokeSafely(TerminalReady, outcome);
        }

        private void InvokeTerminalCommandCompletedSafely(
            BartenderTerminalCommandResult result)
        {
            InvokeSafely(TerminalCommandCompleted, result);
        }

        private void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try { ((Action<T>)invocationList[i])(value); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        /// <summary>
        /// Controller'ın bulunduğu yere hizalanır. Yalnız abonelik başlarken çağrılır;
        /// her karede yoklama yapmaz, çünkü aynalamanın kaynağı event'lerdir.
        /// </summary>
        private void SyncFromController()
        {
            if (controller == null) return;
            BartenderLevelState state = controller.State;
            mirroredState = state;

            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
            {
                ResetTerminalNavigation();
                if (machine.State != BsFlowState.Menu)
                    Dispatch(BsFlowTrigger.ReturnToMenu);
                return;
            }

            if (machine.State == BsFlowState.Menu && controller.CurrentLevel != null)
            {
                Dispatch(BsFlowTrigger.LoadRequested);
                Dispatch(BsFlowTrigger.LevelLoaded);
            }

            switch (state)
            {
                case BartenderLevelState.Paused: Dispatch(BsFlowTrigger.PauseRequested); break;
                case BartenderLevelState.Won:
                    Finish(BsRoundOutcome.Won);
                    ArmTerminalNavigation(BsRoundOutcome.Won);
                    break;
                case BartenderLevelState.Failed:
                    Finish(BsRoundOutcome.Failed);
                    ArmTerminalNavigation(BsRoundOutcome.Failed);
                    break;
            }
        }
    }
}
