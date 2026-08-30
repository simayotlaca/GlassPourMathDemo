using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort
{
    public enum PourPhase
    {
        Idle,
        Carry,
        Flow,
        Return,
        WaitingForTail
    }

    public enum PourOutcome
    {
        Completed,
        Cancelled
    }

    /// <summary>
    /// Lift, carry, tilt, pour, return.
    ///
    /// The tilt is not a hand tuned table: it is the angle at which the current fill
    /// actually reaches the mouth, from <see cref="VesselFillMath.SpillAngle"/>, so a
    /// nearly full vessel barely turns and an almost empty one goes past horizontal.
    ///
    /// The reference has one compact 0.3 / 0.3 / 0.3 beat: move, transfer, return. The
    /// source starts losing liquid when the stream is emitted; only the receiver waits
    /// for the short stream travel time.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PourAnimator : MonoBehaviour
    {
        [Header("Timing")]
        public float moveTime = 0.30f;
        [Tooltip("Seconds per unit of liquid. The reference pours a unit in about a third of a second.")]
        public float unitTime = 0.30f;
        [Tooltip("Whole return beat, including the tiny landing settle.")]
        public float returnTime = 0.30f;
        [Tooltip("Last part of returnTime reserved for the tiny landing settle.")]
        public float settleTime = 0.13f;

        [Header("Easing")]
        public Ease carryEase = Ease.InOutSine;
        public Ease tiltEase = Ease.InOutSine;
        public Ease returnEase = Ease.OutCubic;

        // Kept deliberately small. A wide rimmed glass tips out over its own edge long
        // before it is anywhere near horizontal, so the big swing a narrow necked bottle
        // needs just reads as flailing here.
        [Header("Pose")]
        [Tooltip("Neutral receiver clearance when no baked vessel pose is present.")]
        public float pourHeight = 0.26f;
        [Tooltip("Extra degrees past the angle at which the liquid first reaches the mouth.")]
        public float overTilt = 8f;
        public float maxTilt = 96f;
        [Tooltip("Height of the arc the vessel travels along on its way to the target, over the straight line.")]
        public float carryArc = 0.20f;
        [Tooltip("How much bigger the vessel gets while it is off the shelf. A little goes a long way.")]
        [Range(1f, 1.3f)] public float carryScale = 1.015f;
        public int frontSortingOffset = 60;

        [Header("Feel")]
        [Tooltip("How fast the tilt chases the angle the current fill wants while pouring.")]
        public float tiltFollow = 9f;
        public float landingWave = 0.014f;   // a settle, not a squiggle
        [Tooltip("Degrees the vessel rocks through after it lands, before it stands still.")]
        [Range(0.5f, 0.8f)] public float settleRock = 0.8f;

        public PourStream stream;

        public PourPhase Phase { get; private set; } = PourPhase.Idle;
        public bool Busy => Phase != PourPhase.Idle;
        public int ActiveOperationId { get; private set; }
        public PourOutcome LastOutcome { get; private set; } = PourOutcome.Cancelled;
        public bool Committed { get; private set; }
        private readonly List<Action<int, PourOutcome>> pourFinishedListeners =
            new List<Action<int, PourOutcome>>(4);
        private readonly List<Action<int, PourOutcome>> notificationSnapshot =
            new List<Action<int, PourOutcome>>(4);

        public event Action<int, PourOutcome> PourFinished
        {
            add
            {
                if (value == null || pourFinishedListeners.Contains(value)) return;
                pourFinishedListeners.Add(value);
                if (notificationSnapshot.Capacity < pourFinishedListeners.Count)
                    notificationSnapshot.Capacity = pourFinishedListeners.Count;
            }
            remove
            {
                if (value != null) pourFinishedListeners.Remove(value);
            }
        }

        // A cached typed callback lets one pooled scalar tween drive the whole carry.
        // Keeping its pose in fields avoids per-pour closures and lets the lip remain
        // pinned while the vessel performs the delayed final tilt.
        private TweenCallback<float> carryProgressCallback;
        private Transform carryDriven;
        private Vector3 carryStartPosition;
        private Vector3 carryPreTiltPosition;
        private Vector3 carryControl;
        private Vector3 carryStartScale;
        private Vector3 carryEndScale;
        private Vector3 carryAnchor;
        private Vector3 carryMouthOffset;
        private Quaternion carryStartRotation;
        private Quaternion carryEndRotation;
        private TweenCallback tweenCompletedCallback;
        private TweenCallback tweenKilledCallback;
        private Tween activeTween;
        private bool activeTweenFinished;
        private bool activeTweenCancelled;
        private bool cancellationRequested;
        private bool completingOperation;

        // One animator owns one coroutine and one transaction. External boards only submit
        // requests; they never own the iterator that mutates this state.
        private Coroutine activeRoutine;
        private int nextOperationId;
        private LiquidBottle activeSource;
        private LiquidBottle activeTarget;
        private Transform activeSourceTransform;
        private Renderer[] activeSourceRenderers;
        private int[] activeSourceBaseOrders;
        private int activeAmount;
        private Color activeColor;
        private bool requireMatchingColors;
        private bool targetPrepared;
        private int activeSourceModelVersion;
        private int activeTargetModelVersion;
        private Vector3 activeHome;
        private Quaternion activeHomeRotation;
        private Vector3 activeHomeScale;

        private static bool tweenPoolPrepared;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrepareTweenPool()
        {
            if (tweenPoolPrepared) return;
            DOTween.Init();
            DOTween.SetTweensCapacity(200, 50);
            tweenPoolPrepared = true;
        }

        private void Awake()
        {
            carryProgressCallback = ApplyCarryProgress;
            tweenCompletedCallback = MarkTweenCompleted;
            tweenKilledCallback = MarkTweenKilled;
            AwakeInternal();
        }

        private void AwakeInternal()
        {
            if (stream == null)
            {
                var go = new GameObject("PourStream");
                go.transform.SetParent(transform, false);
                stream = go.AddComponent<PourStream>();
            }
        }

        /// <summary>
        /// Starts one owned transfer operation. The animator, not its caller, owns the
        /// coroutine so disabling a board or this component cannot leave a zombie iterator
        /// mutating the bottles on later frames.
        /// </summary>
        public bool TryStartPour(LiquidBottle source, LiquidBottle target, int amount,
            float homeY = float.NaN, bool requireColorMatch = true)
        {
            if (stream == null && isActiveAndEnabled) AwakeInternal();
            if (Busy || completingOperation || !isActiveAndEnabled
                || source == null || target == null
                || source == target || amount <= 0 || source.IsEmpty || target.IsFull)
                return false;

            if (!source.isActiveAndEnabled || !target.isActiveAndEnabled
                || stream == null || !stream.isActiveAndEnabled)
                return false;

            if (requireColorMatch && !target.CanReceive(source.TopColor)) return false;

            unchecked
            {
                nextOperationId++;
                if (nextOperationId == 0) nextOperationId = 1;
            }

            int operationId = nextOperationId;
            amount = Mathf.Min(amount, source.TopRunLength, target.FreeSpace);
            if (amount <= 0 || !source.TryReserveTransfer(this, operationId)) return false;
            if (!target.TryReserveTransfer(this, operationId))
            {
                source.ReleaseTransferReservation(this, operationId);
                return false;
            }

            cancellationRequested = false;
            Committed = false;
            targetPrepared = false;
            activeSource = source;
            activeTarget = target;
            activeSourceTransform = source.transform;
            activeAmount = amount;
            activeColor = source.TopColor;
            requireMatchingColors = requireColorMatch;
            activeSourceModelVersion = source.ModelVersion;
            activeTargetModelVersion = target.ModelVersion;
            activeHome = source.transform.position;
            if (!float.IsNaN(homeY)) activeHome.y = homeY;
            activeHomeRotation = source.transform.rotation;
            activeHomeScale = source.transform.localScale;

            ActiveOperationId = operationId;
            Phase = PourPhase.Carry;

            try
            {
                source.GetSortingSnapshot(out activeSourceRenderers, out activeSourceBaseOrders);
                source.SetSortingOffset(frontSortingOffset);
                Coroutine started = StartCoroutine(RunPourOperation(operationId));
                if (started == null || ActiveOperationId != operationId)
                {
                    if (started != null) StopCoroutine(started);
                    CompleteActiveOperation(operationId, PourOutcome.Cancelled);
                    return false;
                }
                activeRoutine = started;
                return true;
            }
            catch
            {
                CompleteActiveOperation(operationId, PourOutcome.Cancelled);
                throw;
            }
        }

        /// <summary>
        /// Compatibility waiter for old demo callers. The real operation is still owned by
        /// this animator; stopping the caller only stops waiting for it.
        /// </summary>
        [Obsolete("Use TryStartPour. The animator now owns the transfer operation.")]
        public IEnumerator Pour(LiquidBottle source, LiquidBottle target, int amount,
            float homeY = float.NaN)
        {
            if (!TryStartPour(source, target, amount, homeY, false)) yield break;
            int operationId = ActiveOperationId;
            while (Busy && ActiveOperationId == operationId) yield return null;
        }

        private IEnumerator RunPourOperation(int operationId)
        {
            bool completed = false;
            try
            {
                LiquidBottle source = activeSource;
                LiquidBottle target = activeTarget;
                int amount = activeAmount;
                Color color = activeColor;
                Vector3 start = source.transform.position;
                Vector3 home = activeHome;
                Quaternion homeRotation = activeHomeRotation;
                Vector3 homeScale = activeHomeScale;

                Pose sourcePose = ResolvePose(source);
                Pose targetPose = ResolvePose(target);
                float sourceScale = UniformScale(source.transform);
                float targetScaleY = Mathf.Abs(target.transform.lossyScale.y);

                // Tilt towards the target: mouth to the left means a positive Z rotation.
                float sign = target.transform.position.x < source.transform.position.x ? 1f : -1f;
                Vector2 selectedMouth = source.PourMouthLocal(target.transform.position.x);
                Vector3 mouthLocal = new Vector3(selectedMouth.x, selectedMouth.y, 0f);
                Vector3 mouthOffset = ScaledLocalOffset(source.transform, mouthLocal) * carryScale;

                float tilt = sign * Mathf.Min(sourcePose.maximumTilt,
                    source.SpillAngle() + sourcePose.extraTilt);
                Vector3 anchor = target.MouthWorld
                                 + Vector3.up * (targetPose.receiveClearance * targetScaleY);
                Vector3 destination = anchor - Quaternion.Euler(0f, 0f, tilt) * mouthOffset;

                yield return LiftAndCarry(operationId, source, start, homeScale, anchor,
                    mouthOffset, destination, tilt, sourcePose.carryArc * sourceScale);
                if (!OperationCanContinue(operationId)) yield break;

                // Only rendering is staged. Source and receiver logical stacks stay at
                // their original, internally consistent state until the whole beat succeeds.
                if (!target.BeginReceivePreview(this, operationId, color, amount)) yield break;
                targetPrepared = true;
                Phase = PourPhase.Flow;

                stream.Begin(source, target, color,
                    sourcePose.streamWidth * sourceScale,
                    sourcePose.streamTipWidth * sourceScale);

                float targetFrom = target.DisplayVolume;
                float transferDuration = Mathf.Max(0.05f, unitTime * amount);
                yield return Drain(operationId, source, target, amount, anchor, mouthOffset,
                    sign, tilt, sourcePose.extraTilt, sourcePose.maximumTilt, targetFrom,
                    transferDuration);
                if (!OperationCanContinue(operationId)) yield break;

                // The tail is frozen in world space by StopEmitting, so the vessel may
                // return immediately while the last drop and delayed receiver fill finish.
                if (stream.Active) stream.StopEmitting();
                Phase = PourPhase.Return;
                yield return Return(operationId, source, target, home, homeRotation, homeScale,
                    sign, targetFrom, amount, transferDuration);
                if (!OperationCanContinue(operationId)) yield break;

                Phase = PourPhase.WaitingForTail;
                while (stream != null && stream.isActiveAndEnabled && stream.Active)
                {
                    if (!OperationCanContinue(operationId)) yield break;
                    yield return null;
                }

                if (!OperationCanContinue(operationId)) yield break;
                if (!source.TryCommitTransferTo(target, this, operationId,
                        activeSourceModelVersion, activeTargetModelVersion, color, amount,
                        requireMatchingColors))
                    yield break;

                Committed = true;
                completed = true;
            }
            finally
            {
                if (ActiveOperationId == operationId)
                    CompleteActiveOperation(operationId,
                        completed ? PourOutcome.Completed : PourOutcome.Cancelled);
            }
        }

        /// <summary>
        /// The selected vessel is already lifted by the board. Translation gets the first
        /// sixty percent of this beat; the last forty percent pivots around the lip at the
        /// receiving anchor. This reads as approach-then-pour without adding another hop.
        /// </summary>
        private IEnumerator LiftAndCarry(int operationId, LiquidBottle source, Vector3 start,
            Vector3 homeScale, Vector3 anchor, Vector3 mouthOffset, Vector3 destination,
            float tilt, float arc)
        {
            carryDriven = source.transform;
            carryStartPosition = start;
            carryStartRotation = source.transform.rotation;
            carryEndRotation = Quaternion.Euler(0f, 0f, tilt);
            carryStartScale = homeScale;
            carryEndScale = homeScale * carryScale;
            carryAnchor = anchor;
            carryMouthOffset = mouthOffset;

            // Scale is already at its carry value when the pivot begins, which makes this
            // pre-tilt endpoint exactly continuous with anchor - rotation * mouthOffset.
            carryPreTiltPosition = anchor - carryStartRotation * mouthOffset;
            float arcHeight = arc
                              + Mathf.Abs(carryPreTiltPosition.x - start.x) * 0.025f;
            carryControl = Vector3.Lerp(start, carryPreTiltPosition, 0.5f)
                           + Vector3.up * (arcHeight * 2f);

            Tween carry = DOVirtual.Float(0f, 1f, Mathf.Max(0.001f, moveTime),
                    carryProgressCallback)
                .SetEase(Ease.Linear).SetRecyclable(true).SetTarget(source.transform);
            // Keep this in the current state machine. Yielding a second IEnumerator here
            // would create one more managed iterator object for every transfer.
            ArmTween(carry);
            while (!activeTweenFinished)
            {
                if (!OperationCanContinue(operationId)) yield break;
                yield return null;
            }

            if (activeTweenCancelled)
            {
                carryDriven = null;
                cancellationRequested = true;
                yield break;
            }

            if (!OperationCanContinue(operationId)) yield break;

            carryDriven = null;
            source.transform.SetPositionAndRotation(destination, Quaternion.Euler(0f, 0f, tilt));
            source.transform.localScale = carryEndScale;
        }

        private void ApplyCarryProgress(float progress)
        {
            if (carryDriven == null) return;

            const float translationShare = 0.60f;
            if (progress < translationShare)
            {
                float t = Mathf.Clamp01(progress / translationShare);
                float eased = DOVirtual.EasedValue(0f, 1f, t, carryEase);
                float inverse = 1f - eased;
                Vector3 position = inverse * inverse * carryStartPosition
                                   + 2f * inverse * eased * carryControl
                                   + eased * eased * carryPreTiltPosition;
                float scaleProgress = DOVirtual.EasedValue(0f, 1f, t, Ease.OutSine);
                carryDriven.SetPositionAndRotation(position, carryStartRotation);
                carryDriven.localScale = Vector3.LerpUnclamped(
                    carryStartScale, carryEndScale, scaleProgress);
                return;
            }

            float tiltProgress = Mathf.Clamp01(
                (progress - translationShare) / (1f - translationShare));
            float tiltEased = DOVirtual.EasedValue(0f, 1f, tiltProgress, tiltEase);
            Quaternion rotation = Quaternion.SlerpUnclamped(
                carryStartRotation, carryEndRotation, tiltEased);
            carryDriven.SetPositionAndRotation(
                carryAnchor - rotation * carryMouthOffset, rotation);
            carryDriven.localScale = carryEndScale;
        }

        /// <summary>
        /// Moves the rendered volume across. The target colour comes from a visual preview;
        /// neither logical stack changes until the complete operation commits.
        /// The tilt keeps chasing the angle the shrinking fill wants, the same way a real
        /// bottle has to be turned further as it empties.
        /// </summary>
        private IEnumerator Drain(int operationId, LiquidBottle source, LiquidBottle target,
            int amount, Vector3 anchor, Vector3 mouthOffset, float sign, float startTilt,
            float extraTilt, float maximumTilt, float targetFrom, float duration)
        {
            float sourceFrom = source.DisplayVolume;
            float currentTilt = startTilt;
            float elapsed = 0f;
            bool impacted = false;
            bool emissionClosed = false;

            // Source flow starts at emission. Receiver flow has its own clock, latched
            // to the exact frame the visible stream head reaches the surface.
            while (elapsed < duration)
            {
                if (!OperationCanContinue(operationId)) yield break;
                elapsed += Time.deltaTime;
                float sourceProgress = Mathf.Clamp01(elapsed / duration);
                float targetProgress = stream.HasLanded
                    ? Mathf.Clamp01(stream.LandedAge / duration)
                    : 0f;
                source.DisplayVolume = sourceFrom - amount * sourceProgress;
                target.DisplayVolume = targetFrom + amount * targetProgress;

                if (!impacted && stream.HasLanded)
                {
                    float impactX = target.transform.InverseTransformPoint(
                        new Vector3(stream.FallX, target.SurfaceWorldY, 0f)).x;
                    target.Splash(impactX, 0.72f);
                    target.Kick(landingWave);
                    impacted = true;
                }

                if (!emissionClosed && sourceProgress >= 0.999f)
                {
                    stream.StopEmitting();
                    emissionClosed = true;
                }

                float wanted = sign * Mathf.Min(maximumTilt, source.SpillAngle() + extraTilt);
                currentTilt = Mathf.Lerp(currentTilt, wanted, 1f - Mathf.Exp(-tiltFollow * Time.deltaTime));
                Quaternion rotation = Quaternion.Euler(0f, 0f, currentTilt);
                source.transform.SetPositionAndRotation(anchor - rotation * mouthOffset, rotation);
                yield return null;
            }

            if (!OperationCanContinue(operationId)) yield break;
            source.DisplayVolume = sourceFrom - amount;
            if (!stream.HasLanded)
            {
                // An authored travel time longer than the whole pour is invalid for this
                // compact animation. Abort transactionally instead of filling through air.
                cancellationRequested = true;
                stream.Cancel();
                yield break;
            }

            target.DisplayVolume = targetFrom
                                   + amount * Mathf.Clamp01(stream.LandedAge / duration);
            if (!impacted)
            {
                float impactX = target.transform.InverseTransformPoint(
                    new Vector3(stream.FallX, target.SurfaceWorldY, 0f)).x;
                target.Splash(impactX, 0.72f);
                target.Kick(landingWave);
            }
            if (!emissionClosed) stream.StopEmitting();
        }

        /// <summary>
        /// Home, then a short rock on the spot. A vessel that stops dead the instant it
        /// touches down reads as a sprite being repositioned; one that rocks once reads as
        /// having been put down.
        /// </summary>
        private IEnumerator Return(int operationId, LiquidBottle source, LiquidBottle target,
            Vector3 home, Quaternion homeRotation, Vector3 homeScale, float pourSign,
            float targetFrom, int amount, float transferDuration)
        {
            float total = Mathf.Max(0.03f, returnTime);
            float settle = Mathf.Min(Mathf.Clamp(settleTime, 0.11f, 0.15f),
                Mathf.Max(0f, total - 0.03f));
            float travel = total - settle;
            float drop = SettleDropWorld(source);
            float rockDegrees = Mathf.Clamp(settleRock, 0.5f, 0.8f);
            Quaternion rockRotation = Quaternion.AngleAxis(
                -pourSign * rockDegrees, Vector3.forward) * homeRotation;
            Sequence sequence = DOTween.Sequence().SetRecyclable(true).SetTarget(source.transform);
            sequence.Append(source.transform.DOMove(home, travel).SetEase(returnEase).SetRecyclable(true));
            sequence.Join(source.transform.DORotateQuaternion(homeRotation, travel)
                .SetEase(returnEase).SetRecyclable(true));
            sequence.Join(source.transform.DOScale(homeScale, travel)
                .SetEase(returnEase).SetRecyclable(true));

            if (settle > 0.001f)
            {
                sequence.Append(source.transform.DOMoveY(home.y - drop, settle * 0.38f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
                sequence.Join(source.transform.DORotateQuaternion(rockRotation, settle * 0.38f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
                sequence.Append(source.transform.DOMoveY(home.y, settle * 0.62f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
                sequence.Join(source.transform.DORotateQuaternion(homeRotation, settle * 0.62f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
            }

            ArmTween(sequence);
            while (!activeTweenFinished)
            {
                if (!OperationCanContinue(operationId)) yield break;
                if (stream == null || !stream.isActiveAndEnabled)
                    target.DisplayVolume = targetFrom + amount;
                else
                {
                    float targetProgress = Mathf.Clamp01(stream.LandedAge / transferDuration);
                    target.DisplayVolume = targetFrom + amount * targetProgress;
                }
                yield return null;
            }

            if (activeTweenCancelled)
            {
                cancellationRequested = true;
                yield break;
            }

            // Robust when somebody authors a return shorter than the actual stream delay.
            while (stream != null && stream.isActiveAndEnabled
                   && stream.LandedAge < transferDuration)
            {
                if (!OperationCanContinue(operationId)) yield break;
                float targetProgress = Mathf.Clamp01(stream.LandedAge / transferDuration);
                target.DisplayVolume = targetFrom + amount * targetProgress;
                yield return null;
            }
            if (!OperationCanContinue(operationId)) yield break;
            target.DisplayVolume = targetFrom + amount;
        }

        private static float SettleDropWorld(LiquidBottle bottle)
        {
            const float settlePixels = 1.5f;
            Sprite art = bottle.Profiled && bottle.profile.front != null
                ? bottle.profile.front
                : bottle.glassArt != null ? bottle.glassArt : bottle.maskSprite;
            float pixelsPerUnit = art != null
                ? art.pixelsPerUnit
                : Mathf.Max(1f, bottle.maskPixelsPerUnit);
            return settlePixels * UniformScale(bottle.transform) / Mathf.Max(1f, pixelsPerUnit);
        }

        private void ArmTween(Tween tween)
        {
            activeTween = tween;
            activeTweenFinished = tween == null;
            activeTweenCancelled = false;
            if (tween == null) return;
            // Recyclable tween references must never be polled after completion: DOTween
            // may already have lent the object to another animation. A cached callback
            // gives this animator its own stable completion flag with no per-pour delegate.
            tween.OnComplete(tweenCompletedCallback);
            tween.OnKill(tweenKilledCallback);
        }

        private void MarkTweenCompleted()
        {
            activeTweenFinished = true;
            activeTween = null;
        }

        private void MarkTweenKilled()
        {
            // AutoKill follows OnComplete during a normal finish. Only a kill that
            // arrives before completion is cancellation.
            activeTween = null;
            if (!activeTweenFinished)
            {
                activeTweenCancelled = true;
                activeTweenFinished = true;
            }
        }

        private bool OperationCanContinue(int operationId)
        {
            return ActiveOperationId == operationId
                   && Phase != PourPhase.Idle
                   && !cancellationRequested
                   && isActiveAndEnabled
                   && activeSource != null && activeSource.isActiveAndEnabled
                   && activeTarget != null && activeTarget.isActiveAndEnabled
                   && stream != null && stream.isActiveAndEnabled;
        }

        private void KillOwnedTween()
        {
            Tween owned = activeTween;
            activeTween = null;
            if (owned != null && owned.IsActive()) owned.Kill(false);
            activeTweenFinished = true;
        }

        /// <summary>
        /// Cancels the exact owned operation and restores the last committed model state
        /// immediately. Safe to call repeatedly from reset, disable and scene teardown.
        /// </summary>
        public bool CancelActivePour()
        {
            if (!Busy) return false;

            int operationId = ActiveOperationId;
            cancellationRequested = true;
            Coroutine ownedRoutine = activeRoutine;
            activeRoutine = null;
            KillOwnedTween();
            if (ownedRoutine != null) StopCoroutine(ownedRoutine);

            // Unity does not promise that stopping a coroutine executes its finally block.
            // This explicit path is therefore authoritative; the runner's finally is only
            // an idempotent safety net.
            if (ActiveOperationId == operationId)
                CompleteActiveOperation(operationId, PourOutcome.Cancelled);
            return true;
        }

        private void CompleteActiveOperation(int operationId, PourOutcome outcome)
        {
            if (operationId == 0 || ActiveOperationId != operationId) return;

            activeRoutine = null;
            KillOwnedTween();
            if (stream != null) stream.Cancel();

            // Clean the two endpoints independently. One may already have been destroyed.
            if (activeTarget != null)
            {
                if (targetPrepared)
                    activeTarget.ClearReceivePreview(this, operationId);
                activeTarget.DisplayVolume = activeTarget.UnitCount;
                if (outcome == PourOutcome.Cancelled)
                    activeTarget.ClearTransientMotion();
                activeTarget.ReleaseTransferReservation(this, operationId);
            }

            if (activeSource != null)
            {
                activeSource.DisplayVolume = activeSource.UnitCount;
                activeSource.ReleaseTransferReservation(this, operationId);
            }

            if (activeSourceTransform != null)
            {
                activeSourceTransform.SetPositionAndRotation(activeHome, activeHomeRotation);
                activeSourceTransform.localScale = activeHomeScale;
            }
            if (activeSource != null && outcome == PourOutcome.Cancelled)
                activeSource.ClearTransientMotion();
            RestoreSourceSorting();

            carryDriven = null;
            LastOutcome = outcome;
            if (outcome != PourOutcome.Completed) Committed = false;

            activeSource = null;
            activeTarget = null;
            activeSourceTransform = null;
            activeSourceRenderers = null;
            activeSourceBaseOrders = null;
            activeAmount = 0;
            activeColor = Color.clear;
            requireMatchingColors = true;
            targetPrepared = false;
            activeSourceModelVersion = 0;
            activeTargetModelVersion = 0;
            cancellationRequested = false;
            ActiveOperationId = 0;
            Phase = PourPhase.Idle;

            completingOperation = true;
            try
            {
                NotifyPourFinished(operationId, outcome);
            }
            finally
            {
                completingOperation = false;
            }
        }

        private void NotifyPourFinished(int operationId, PourOutcome outcome)
        {
            notificationSnapshot.Clear();
            for (int i = 0; i < pourFinishedListeners.Count; i++)
                notificationSnapshot.Add(pourFinishedListeners[i]);

            for (int i = 0; i < notificationSnapshot.Count; i++)
            {
                try
                {
                    notificationSnapshot[i](operationId, outcome);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
            notificationSnapshot.Clear();
        }

        private void RestoreSourceSorting()
        {
            if (activeSourceRenderers == null || activeSourceBaseOrders == null) return;
            int count = Mathf.Min(activeSourceRenderers.Length, activeSourceBaseOrders.Length);
            for (int i = 0; i < count; i++)
            {
                if (activeSourceRenderers[i] != null)
                    activeSourceRenderers[i].sortingOrder = activeSourceBaseOrders[i];
            }
        }

        private Pose ResolvePose(LiquidBottle bottle)
        {
            if (bottle != null && bottle.Profiled && bottle.profile.pourPose.enabled)
            {
                VesselProfile.PourPose p = bottle.profile.pourPose;
                return new Pose(p.receiveClearance, p.carryArc, p.extraTilt,
                    p.maximumTilt, p.streamWidth, p.streamTipWidth);
            }

            return new Pose(pourHeight, carryArc, overTilt, maxTilt,
                stream != null ? stream.width : 0.085f,
                stream != null ? stream.tipWidth : 0.055f);
        }

        private static float UniformScale(Transform value)
        {
            Vector3 scale = value.lossyScale;
            return Mathf.Max(0.001f, (Mathf.Abs(scale.x) + Mathf.Abs(scale.y)) * 0.5f);
        }

        private static Vector3 ScaledLocalOffset(Transform owner, Vector3 local)
        {
            Vector3 scale = owner.lossyScale;
            return new Vector3(local.x * Mathf.Abs(scale.x),
                local.y * Mathf.Abs(scale.y), local.z * Mathf.Abs(scale.z));
        }

        private readonly struct Pose
        {
            public readonly float receiveClearance;
            public readonly float carryArc;
            public readonly float extraTilt;
            public readonly float maximumTilt;
            public readonly float streamWidth;
            public readonly float streamTipWidth;

            public Pose(float receiveClearance, float carryArc, float extraTilt,
                float maximumTilt, float streamWidth, float streamTipWidth)
            {
                this.receiveClearance = receiveClearance;
                this.carryArc = carryArc;
                this.extraTilt = extraTilt;
                this.maximumTilt = maximumTilt;
                this.streamWidth = streamWidth;
                this.streamTipWidth = streamTipWidth;
            }
        }

        private void OnDisable()
        {
            CancelActivePour();
        }
    }
}
