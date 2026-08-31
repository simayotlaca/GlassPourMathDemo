using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Adds the two rule-reveal beats that are easy to miss in the static shelf view:
    /// a hidden top layer becoming known after a pour, and a delivery counter opening a
    /// glass/segment lock. Domain receipts decide <em>what</em> changed; the shelf's
    /// PresentationChanged event decides when the refreshed bottle is safe to decorate.
    ///
    /// Feedback lives on a disposable child SpriteRenderer. The layout-owned bottle root
    /// is never moved, rotated or scaled, so this presenter cannot fight PourAnimator,
    /// selection lift, portal travel or shelf reseating.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class MechanicRevealPresenter : MonoBehaviour
    {
        private enum BeatKind
        {
            HiddenReveal,
            LockOpened
        }

        private struct PendingBeat
        {
            public int Revision;
            public int GlassId;
            public BeatKind Kind;
            public Color Tint;
        }

        private sealed class ActiveFeedback
        {
            public LiquidBottle Bottle;
            public SpriteRenderer Renderer;
            public Coroutine Routine;
            public Vector3 BaseLocalPosition;
            public Quaternion BaseLocalRotation;
            public Vector3 BaseLocalScale;
        }

        private const string FeedbackChildName = "MechanicRevealFeedback";

        [Header("Runtime binding")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Header("Hidden colour reveal")]
        [SerializeField, Min(0.05f)] private float revealDuration = 0.56f;
        [SerializeField, Range(0f, 0.35f)] private float revealHop = 0.13f;
        [SerializeField, Range(0f, 0.3f)] private float revealScale = 0.09f;
        [SerializeField, Range(0f, 1f)] private float revealAlpha = 0.68f;
        [SerializeField] private Color revealFallback = new Color(0.25f, 0.86f, 1f, 1f);

        [Header("Chain / layer lock opened")]
        [SerializeField, Min(0.05f)] private float unlockDuration = 0.72f;
        [SerializeField, Range(0f, 0.35f)] private float unlockHop = 0.10f;
        [SerializeField, Range(0f, 0.3f)] private float unlockScale = 0.12f;
        [SerializeField, Range(0f, 15f)] private float unlockWobbleDegrees = 6f;
        [SerializeField] private Color unlockGold = new Color(1f, 0.72f, 0.16f, 0.76f);

        [Header("Overlay")]
        [SerializeField, Min(1)] private int sortingBoost = 14;

        private readonly List<PendingBeat> pending = new List<PendingBeat>(8);
        private readonly Dictionary<LiquidBottle, ActiveFeedback> active =
            new Dictionary<LiquidBottle, ActiveFeedback>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;

        /// <summary>Optional explicit wiring for a hand-authored host.</summary>
        public void Configure(BartenderLevelController levelController,
                              BartenderShelfLevelView levelView)
        {
            Unsubscribe();
            StopAllFeedback();
            pending.Clear();
            controller = levelController;
            shelfView = levelView;
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
            StopAllFeedback();
            pending.Clear();
        }

        private void OnValidate()
        {
            revealDuration = Mathf.Max(0.05f, revealDuration);
            unlockDuration = Mathf.Max(0.05f, unlockDuration);
            revealHop = Mathf.Clamp(revealHop, 0f, 0.35f);
            unlockHop = Mathf.Clamp(unlockHop, 0f, 0.35f);
            revealScale = Mathf.Clamp(revealScale, 0f, 0.3f);
            unlockScale = Mathf.Clamp(unlockScale, 0f, 0.3f);
            unlockWobbleDegrees = Mathf.Clamp(unlockWobbleDegrees, 0f, 15f);
            revealAlpha = Mathf.Clamp01(revealAlpha);
            sortingBoost = Mathf.Max(1, sortingBoost);
        }

        private void LateUpdate()
        {
            RebindIfNeeded();
            TryFlushPending();
        }

        private void HandlePoured(BartenderPourReceipt receipt)
        {
            if (receipt == null) return;

            StopFeedbackForGlass(receipt.SourceBefore != null
                ? receipt.SourceBefore.Id
                : -1);
            StopFeedbackForGlass(receipt.TargetBefore != null
                ? receipt.TargetBefore.Id
                : -1);

            if (!TryGetRevealedTop(receipt, out Layer revealed)) return;

            Color tint = controller != null && controller.Palette != null
                ? controller.Palette.ColorAt(revealed.Color)
                : revealFallback;
            // Dark drink colours still need to read as a light beat over dark glass art.
            tint = Color.Lerp(tint, Color.white, 0.22f);
            tint.a = revealAlpha;
            Queue(receipt.Revision, receipt.SourceAfter.Id,
                  BeatKind.HiddenReveal, tint);
        }

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            if (receipt == null || controller == null) return;
            if (receipt.DeliveredGlass != null)
                StopFeedbackForGlass(receipt.DeliveredGlass.Id);

            BsBoard snapshot = controller.Board;
            if (snapshot == null) return;
            int afterDelivered = snapshot.Delivered;
            int beforeDelivered = Mathf.Max(0, afterDelivered - 1);

            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null
                    || !CrossedUnlockThreshold(glass, beforeDelivered, afterDelivered))
                    continue;

                Queue(receipt.Revision, glass.Id, BeatKind.LockOpened, unlockGold);
            }
        }

        private static bool TryGetRevealedTop(BartenderPourReceipt receipt,
                                               out Layer revealed)
        {
            revealed = default;
            RtGlass before = receipt.SourceBefore;
            RtGlass after = receipt.SourceAfter;
            if (before == null || after == null || before.Id != after.Id
                || after.Layers.Count == 0)
                return false;

            int top = after.Layers.Count - 1;
            if (top >= before.Layers.Count) return false;
            Layer hidden = before.Layers[top];
            Layer shown = after.Layers[top];
            if (!hidden.Hidden || shown.Hidden || hidden.Color != shown.Color) return false;

            revealed = shown;
            return true;
        }

        private static bool CrossedUnlockThreshold(RtGlass glass, int beforeDelivered,
                                                   int afterDelivered)
        {
            if (glass.IsChained(beforeDelivered) && !glass.IsChained(afterDelivered))
                return true;

            for (int i = 0; i < glass.Layers.Count; i++)
            {
                Layer layer = glass.Layers[i];
                if (layer.IsLocked(beforeDelivered) && !layer.IsLocked(afterDelivered))
                    return true;
            }
            return false;
        }

        private void Queue(int revision, int glassId, BeatKind kind, Color tint)
        {
            if (revision < 0 || glassId < 0) return;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingBeat existing = pending[i];
                if (existing.Revision == revision && existing.GlassId == glassId
                    && existing.Kind == kind)
                    return;
            }

            pending.Add(new PendingBeat
            {
                Revision = revision,
                GlassId = glassId,
                Kind = kind,
                Tint = tint
            });
        }

        private void HandlePresentationChanged() => TryFlushPending();

        private void TryFlushPending()
        {
            if (pending.Count == 0 || controller == null || shelfView == null
                || !shelfView.Ready || shelfView.SynchronizationDeferred)
                return;

            int revision = controller.BoardRevision;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingBeat beat = pending[i];
                if (beat.Revision > revision) continue;

                // Counter locks are a reward for the completed delivery beat. Waiting for
                // both portal travel and shelf reseating also keeps their gold wobble from
                // visually competing with the departing glass.
                if (beat.Revision == revision && beat.Kind == BeatKind.LockOpened
                    && (shelfView.DeliveryPlaying || shelfView.SeatAnimationPlaying))
                    continue;

                pending.RemoveAt(i);
                // An older beat belongs to a presentation that was superseded before it
                // became visible. Never flash it on a recycled pool bottle.
                if (beat.Revision != revision) continue;
                if (!shelfView.TryGetBottle(beat.GlassId, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                    continue;

                Play(bottle, beat.Kind, beat.Tint);
            }
        }

        private void Play(LiquidBottle bottle, BeatKind kind, Color tint)
        {
            StopFeedback(bottle);
            SpriteRenderer source = FindVisualSource(bottle);
            if (source == null || source.sprite == null) return;

            SpriteRenderer overlay = GetOrCreateOverlay(bottle);
            ConfigureOverlay(overlay, source, bottle, tint);

            var feedback = new ActiveFeedback
            {
                Bottle = bottle,
                Renderer = overlay,
                BaseLocalPosition = overlay.transform.localPosition,
                BaseLocalRotation = overlay.transform.localRotation,
                BaseLocalScale = overlay.transform.localScale
            };
            active[bottle] = feedback;
            feedback.Routine = StartCoroutine(Animate(feedback, kind, tint));
        }

        private IEnumerator Animate(ActiveFeedback feedback, BeatKind kind, Color tint)
        {
            float duration = kind == BeatKind.HiddenReveal
                ? revealDuration
                : unlockDuration;
            float hop = kind == BeatKind.HiddenReveal ? revealHop : unlockHop;
            float scaleAmount = kind == BeatKind.HiddenReveal
                ? revealScale
                : unlockScale;
            float wobble = kind == BeatKind.LockOpened ? unlockWobbleDegrees : 1.5f;
            float cycles = kind == BeatKind.LockOpened ? 4f : 1.25f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (feedback.Bottle == null || feedback.Renderer == null
                    || !feedback.Bottle.gameObject.activeInHierarchy)
                    break;

                float t = Mathf.Clamp01(elapsed / duration);
                float fadeIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.16f));
                float fadeOut = 1f - Mathf.SmoothStep(
                    0f, 1f, Mathf.InverseLerp(0.52f, 1f, t));
                float pulse = Mathf.Sin(Mathf.PI * t);
                float angle = Mathf.Sin(t * Mathf.PI * 2f * cycles)
                            * wobble * (1f - t);

                Transform tr = feedback.Renderer.transform;
                tr.localPosition = feedback.BaseLocalPosition
                                 + Vector3.up * (hop * pulse);
                tr.localRotation = feedback.BaseLocalRotation
                                 * Quaternion.AngleAxis(angle, Vector3.forward);
                tr.localScale = feedback.BaseLocalScale * (1f + scaleAmount * pulse);
                feedback.Renderer.color = new Color(
                    tint.r, tint.g, tint.b, tint.a * fadeIn * fadeOut);

                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            FinishFeedback(feedback);
        }

        private SpriteRenderer GetOrCreateOverlay(LiquidBottle bottle)
        {
            Transform found = bottle.transform.Find(FeedbackChildName);
            if (found == null)
            {
                var child = new GameObject(FeedbackChildName);
                child.transform.SetParent(bottle.transform, false);
                found = child.transform;
            }

            SpriteRenderer renderer = found.GetComponent<SpriteRenderer>();
            if (renderer == null) renderer = found.gameObject.AddComponent<SpriteRenderer>();
            bottle.InvalidateRenderers();
            return renderer;
        }

        private void ConfigureOverlay(SpriteRenderer overlay, SpriteRenderer source,
                                      LiquidBottle bottle, Color tint)
        {
            Transform tr = overlay.transform;
            tr.SetParent(bottle.transform, false);
            Transform sourceTransform = source.transform;
            if (sourceTransform.parent == bottle.transform)
            {
                tr.localPosition = sourceTransform.localPosition;
                tr.localRotation = sourceTransform.localRotation;
                tr.localScale = sourceTransform.localScale;
            }
            else
            {
                tr.SetPositionAndRotation(sourceTransform.position, sourceTransform.rotation);
                Vector3 parentScale = bottle.transform.lossyScale;
                Vector3 sourceScale = sourceTransform.lossyScale;
                tr.localScale = new Vector3(
                    SafeRatio(sourceScale.x, parentScale.x),
                    SafeRatio(sourceScale.y, parentScale.y),
                    SafeRatio(sourceScale.z, parentScale.z));
            }
            overlay.gameObject.layer = bottle.gameObject.layer;
            overlay.sprite = source.sprite;
            overlay.drawMode = source.drawMode;
            if (source.drawMode != SpriteDrawMode.Simple) overlay.size = source.size;
            overlay.flipX = source.flipX;
            overlay.flipY = source.flipY;
            overlay.spriteSortPoint = source.spriteSortPoint;
            overlay.maskInteraction = source.maskInteraction;
            overlay.sortingLayerID = source.sortingLayerID;
            overlay.sortingOrder = source.sortingOrder + sortingBoost;
            overlay.SetPropertyBlock(null);

            BottleShell shell = bottle.GetComponent<BottleShell>();
            Material glowMaterial = shell != null ? shell.glassLightMaterial : null;
            if (glowMaterial == null)
            {
                Transform light = bottle.transform.Find("GlassLight");
                SpriteRenderer lightRenderer = light != null
                    ? light.GetComponent<SpriteRenderer>()
                    : null;
                if (lightRenderer != null) glowMaterial = lightRenderer.sharedMaterial;
            }
            overlay.sharedMaterial = glowMaterial != null
                ? glowMaterial
                : source.sharedMaterial;
            overlay.color = new Color(tint.r, tint.g, tint.b, 0f);
            overlay.enabled = true;
        }

        private static SpriteRenderer FindVisualSource(LiquidBottle bottle)
        {
            Transform front = bottle.transform.Find("FrontGlass");
            SpriteRenderer exact = front != null ? front.GetComponent<SpriteRenderer>() : null;
            if (exact != null && exact.sprite != null) return exact;

            SpriteRenderer[] renderers = bottle.GetComponentsInChildren<SpriteRenderer>(true);
            SpriteRenderer best = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer candidate = renderers[i];
                if (candidate == null || candidate.sprite == null
                    || candidate.name == FeedbackChildName)
                    continue;
                if (best == null || candidate.sortingOrder > best.sortingOrder)
                    best = candidate;
            }
            return best;
        }

        private static float SafeRatio(float numerator, float denominator) =>
            Mathf.Abs(denominator) > 0.0001f ? numerator / denominator : 1f;

        private void StopFeedbackForGlass(int glassId)
        {
            if (glassId < 0 || shelfView == null
                || !shelfView.TryGetBottle(glassId, out LiquidBottle bottle))
                return;
            StopFeedback(bottle);
        }

        private void StopFeedback(LiquidBottle bottle)
        {
            if (bottle == null || !active.TryGetValue(bottle, out ActiveFeedback feedback))
                return;
            if (feedback.Routine != null) StopCoroutine(feedback.Routine);
            ResetOverlay(feedback.Renderer);
            active.Remove(bottle);
        }

        private void FinishFeedback(ActiveFeedback feedback)
        {
            if (feedback == null) return;
            if (feedback.Bottle != null
                && active.TryGetValue(feedback.Bottle, out ActiveFeedback current)
                && ReferenceEquals(current, feedback))
                active.Remove(feedback.Bottle);
            ResetOverlay(feedback.Renderer);
        }

        private void StopAllFeedback()
        {
            foreach (ActiveFeedback feedback in active.Values)
            {
                if (feedback.Routine != null) StopCoroutine(feedback.Routine);
                ResetOverlay(feedback.Renderer);
            }
            active.Clear();
        }

        private static void ResetOverlay(SpriteRenderer overlay)
        {
            if (overlay == null) return;
            overlay.enabled = false;
            overlay.color = Color.clear;
            overlay.sprite = null;
            overlay.SetPropertyBlock(null);
            Transform tr = overlay.transform;
            tr.localPosition = Vector3.zero;
            tr.localRotation = Quaternion.identity;
            tr.localScale = Vector3.one;
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            pending.Clear();
            StopAllFeedback();
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Unloaded
                && state != BartenderLevelState.CampaignComplete)
                return;
            pending.Clear();
            StopAllFeedback();
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void RebindIfNeeded()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            BartenderLevelController wanted = shelfView != null
                ? shelfView.Controller
                : controller;
            if (ReferenceEquals(wanted, controller)
                && ReferenceEquals(subscribedView, shelfView))
                return;

            Unsubscribe();
            pending.Clear();
            StopAllFeedback();
            controller = wanted;
            Subscribe();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.Poured -= HandlePoured;
                    subscribedController.Delivered -= HandleDelivered;
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.Poured += HandlePoured;
                    subscribedController.Delivered += HandleDelivered;
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                }
            }

            if (subscribedView == shelfView) return;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.PresentationChanged += HandlePresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.Poured -= HandlePoured;
                subscribedController.Delivered -= HandleDelivered;
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedController = null;
            subscribedView = null;
        }
    }

    /// <summary>
    /// Keeps the effect additive to existing scenes and prefabs. Installation happens once
    /// per loaded scene and only attaches the presenter beside an authored shelf view; it
    /// never creates or rebuilds gameplay content.
    /// </summary>
    internal static class MechanicRevealPresenterInstaller
    {
        private static bool sceneHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            sceneHooked = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForLoadedScenes()
        {
            if (!sceneHooked)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
                sceneHooked = true;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
                InstallInScene(SceneManager.GetSceneAt(i));
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode _) =>
            InstallInScene(scene);

        private static void InstallInScene(Scene scene)
        {
            if (!Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                BartenderShelfLevelView[] views =
                    roots[i].GetComponentsInChildren<BartenderShelfLevelView>(true);
                for (int j = 0; j < views.Length; j++)
                {
                    BartenderShelfLevelView view = views[j];
                    if (view != null && view.GetComponent<MechanicRevealPresenter>() == null)
                        view.gameObject.AddComponent<MechanicRevealPresenter>();
                }
            }
        }
    }
}
