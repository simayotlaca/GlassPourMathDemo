using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Kaynak oyundaki ses çağrılarını hedef projenin event tabanlı tur/presentation
    /// mimarisine bağlar. Sahne ve portable prefab aynı runtime köprüsünü kullanır.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BartenderSession))]
    public sealed class BartenderAudioBridge : MonoBehaviour
    {
        private BartenderSession session;
        private BartenderLevelController controller;
        private BartenderPourInteraction interaction;
        private BartenderPausePresenter pausePresenter;
        private PourAnimator subscribedAnimator;
        private BsAudio audio;
        private BsAudio.LoopLease pourLoopLease;
        private int flowOperationId;

        private void Awake()
        {
            ResolveDependencies();
            audio = BsAudio.Ensure();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            audio = BsAudio.Ensure();
            Subscribe();
            audio?.StartBgm();
        }

        private void OnDisable()
        {
            Unsubscribe();
            EndPourFlow(false);
        }

        private void Update()
        {
            RefreshAnimatorSubscription();
            TrackPourPhase();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (interaction == null) interaction = GetComponent<BartenderPourInteraction>();
            if (pausePresenter == null) pausePresenter = GetComponent<BartenderPausePresenter>();
        }

        private void Subscribe()
        {
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.LevelLoaded += HandleLevelLoaded;
                controller.Poured -= HandlePoured;
                controller.Poured += HandlePoured;
                controller.Delivered -= HandleDelivered;
                controller.Delivered += HandleDelivered;
            }

            if (session != null)
            {
                session.FlowChanged -= HandleFlowChanged;
                session.FlowChanged += HandleFlowChanged;
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalReady += HandleTerminalReady;
            }

            if (pausePresenter != null)
            {
                pausePresenter.SettingsChanged -= HandleSettingsChanged;
                pausePresenter.SettingsChanged += HandleSettingsChanged;
            }

            RefreshAnimatorSubscription();
        }

        private void Unsubscribe()
        {
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.Poured -= HandlePoured;
                controller.Delivered -= HandleDelivered;
            }

            if (session != null)
            {
                session.FlowChanged -= HandleFlowChanged;
                session.TerminalReady -= HandleTerminalReady;
            }

            if (pausePresenter != null)
                pausePresenter.SettingsChanged -= HandleSettingsChanged;

            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            subscribedAnimator = null;
        }

        private void RefreshAnimatorSubscription()
        {
            PourAnimator wanted = interaction != null ? interaction.Animator : null;
            if (wanted == null) wanted = GetComponent<PourAnimator>();
            if (subscribedAnimator == wanted) return;

            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            EndPourFlow(false);
            subscribedAnimator = wanted;
            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished += HandlePourFinished;
        }

        private void TrackPourPhase()
        {
            if (subscribedAnimator == null)
            {
                EndPourFlow(false);
                return;
            }

            int operationId = subscribedAnimator.ActiveOperationId;
            PourPhase phase = subscribedAnimator.Phase;
            if (phase == PourPhase.Flow && operationId != 0)
            {
                if (flowOperationId != operationId) BeginPourFlow(operationId);
                return;
            }

            if (flowOperationId == 0) return;
            bool completedFlow = operationId == flowOperationId
                                 && (phase == PourPhase.Return
                                     || phase == PourPhase.WaitingForTail);
            EndPourFlow(completedFlow);
        }

        private void BeginPourFlow(int operationId)
        {
            EndPourFlow(false);
            flowOperationId = operationId;
            audio?.Play(BsSfx.PourStart);
            pourLoopLease = audio?.AcquireLoop(BsSfx.PourLoop);
        }

        private void EndPourFlow(bool playEnd)
        {
            pourLoopLease?.Dispose();
            pourLoopLease = null;
            if (playEnd) audio?.Play(BsSfx.PourEnd);
            flowOperationId = 0;
        }

        private void HandlePourFinished(int operationId, PourOutcome outcome)
        {
            if (flowOperationId == operationId)
                EndPourFlow(outcome == PourOutcome.Completed);
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            EndPourFlow(false);
            audio?.InvalidateLoops();
            audio?.StartBgm();
        }

        private void HandlePoured(BartenderPourReceipt receipt)
        {
            if (receipt == null || controller == null) return;
            int matchedBefore = MatchCount(controller, receipt.SourceBefore,
                receipt.TargetBefore);
            int matchedAfter = MatchCount(controller, receipt.SourceAfter,
                receipt.TargetAfter);
            if (matchedAfter > matchedBefore) audio?.Play(BsSfx.Check);
        }

        private static int MatchCount(BartenderLevelController owner,
                                      RtGlass first, RtGlass second)
        {
            int count = owner.MatchedOrderSlot(first) >= 0 ? 1 : 0;
            if (owner.MatchedOrderSlot(second) >= 0) count++;
            return count;
        }

        private void HandleDelivered(BartenderDeliveryReceipt _) =>
            audio?.Play(BsSfx.DeliverSlide);

        private void HandleTerminalReady(BsRoundOutcome outcome) =>
            audio?.Play(outcome == BsRoundOutcome.Won ? BsSfx.Win : BsSfx.Fail);

        private void HandleFlowChanged(BsTransitionResult transition)
        {
            if (transition.To == BsFlowState.Paused)
                audio?.PauseLoops();
            else if (transition.From == BsFlowState.Paused
                     && transition.To == BsFlowState.Playing)
                audio?.ResumeLoops();
        }

        private void HandleSettingsChanged()
        {
            if (audio == null || pausePresenter == null) return;
            audio.ApplyPreferences(pausePresenter.SoundOn, pausePresenter.MusicOn);
        }
    }
}
