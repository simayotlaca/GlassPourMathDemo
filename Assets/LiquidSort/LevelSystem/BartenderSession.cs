using System;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Tur akışının sahibi — ama bu ilk sürümde bilerek YETKİSİZ.
    ///
    /// FSM'i tutar ve <see cref="BartenderLevelController"/>'ı AYNALAR: level yüklemez,
    /// terminal karar vermez, ilerleme kaydetmez, pause etmez. Controller ne yaparsa
    /// akış onu izler. Sebebi şu: bu projede o dört işin sahibi zaten controller ve
    /// ikisine birden yetki vermek dört ayrı "çift otorite" dikişi açardı —
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
    /// Yetki devri sonraki adım ve tek tek yapılmalı; her biri controller tarafında bir
    /// şeyin kapatılmasını gerektiriyor, hiçbiri bu dosyadan tek başına çözülemez.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderSession : MonoBehaviour
    {
        [Header("Level source")]
        [Tooltip("Boşsa aynı GameObject üzerinde aranır.")]
        [SerializeField] private BartenderLevelController controller;

        [Tooltip("Kabul edilen her akış geçişini konsola yazar. Yalnız teşhis içindir.")]
        [SerializeField] private bool logTransitions = false;

        private readonly BsRoundStateMachine machine =
            new BsRoundStateMachine(BsFlowState.Menu);

        private BartenderLevelController subscribedController;
        private BartenderLevelState mirroredState = BartenderLevelState.Unloaded;

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

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            // Bileşen sonradan eklenmiş olabilir: controller çoktan bir level yüklemiş
            // olsa bile akış onun bulunduğu yere getirilir.
            SyncFromController();
        }

        private void OnDisable() => Unsubscribe();

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

        /// <summary>
        /// Akışın Playing'e girdiği tek nokta BURASI, StateChanged değil. Controller
        /// bildirimleri komut sırasında birleştiriyor ve yalnız SON durumu yayıyor:
        /// yüklenir yüklenmez kazanılan bir level'da dinleyici Playing'i hiç görmez.
        /// LevelLoaded ise StateChanged'den önce ve her yüklemede tam bir kez çıkıyor.
        /// </summary>
        private void HandleLevelLoaded(BsLevel level)
        {
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
                    break;

                case BartenderLevelState.Failed:
                    Finish(BsRoundOutcome.Failed);
                    break;

                case BartenderLevelState.Unloaded:
                case BartenderLevelState.CampaignComplete:
                    // CampaignComplete'in FSM karşılığı yok; ikisi de menüye düşer.
                    // Çeviri kayıplı, bilerek: kampanya bitişi akışın değil üst
                    // katmanın bilgisi.
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
            FlowChanged?.Invoke(transition);
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
                return;

            if (machine.State == BsFlowState.Menu && controller.CurrentLevel != null)
            {
                Dispatch(BsFlowTrigger.LoadRequested);
                Dispatch(BsFlowTrigger.LevelLoaded);
            }

            switch (state)
            {
                case BartenderLevelState.Paused: Dispatch(BsFlowTrigger.PauseRequested); break;
                case BartenderLevelState.Won: Finish(BsRoundOutcome.Won); break;
                case BartenderLevelState.Failed: Finish(BsRoundOutcome.Failed); break;
            }
        }
    }
}
