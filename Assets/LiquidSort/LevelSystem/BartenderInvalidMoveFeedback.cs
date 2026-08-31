using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Presentation-only rejection beat for one shelf glass. The bottle root gets a short
    /// local no-no wobble while a cloned silhouette supplies the coloured pulse. Keeping
    /// the pulse on its own renderer avoids mutating BottleShell's authored materials or
    /// fighting the selection highlight that BottleShell writes every frame.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidBottle))]
    public sealed class BartenderInvalidMoveFeedback : MonoBehaviour
    {
        private const string OverlayName = "InvalidMoveHighlight";

        private LiquidBottle bottle;
        private SpriteRenderer overlay;
        private MaterialPropertyBlock overlayBlock;
        private Sequence activeSequence;
        private Quaternion restLocalRotation = Quaternion.identity;
        private bool ownsRotation;

        public bool Playing => activeSequence != null;

        /// <summary>
        /// Restarts the rejection beat. It owns only this component's sequence; callers
        /// never need to kill all tweens on the bottle transform.
        /// </summary>
        public void Play(Color tint, float highlightAlpha, float wobbleDegrees,
                         float duration)
        {
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy) return;

            Cancel(true);
            if (!TryPrepareOverlay(tint, highlightAlpha)) return;

            float total = Mathf.Max(0.08f, duration);
            float firstDuration = total * 0.18f;
            float secondDuration = total * 0.26f;
            float thirdDuration = total * 0.22f;
            float finalDuration = total - firstDuration - secondDuration - thirdDuration;

            restLocalRotation = transform.localRotation;
            ownsRotation = true;
            Quaternion first = restLocalRotation
                * Quaternion.AngleAxis(wobbleDegrees, Vector3.forward);
            Quaternion second = restLocalRotation
                * Quaternion.AngleAxis(-wobbleDegrees * 0.82f, Vector3.forward);
            Quaternion third = restLocalRotation
                * Quaternion.AngleAxis(wobbleDegrees * 0.38f, Vector3.forward);

            Color clear = tint;
            clear.a = 0f;
            Color peak = tint;
            peak.a = Mathf.Clamp01(highlightAlpha * tint.a);
            overlay.color = clear;
            overlay.enabled = true;

            Sequence sequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true).SetRecyclable(true);
            sequence.Append(transform.DOLocalRotateQuaternion(first, firstDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Append(transform.DOLocalRotateQuaternion(second, secondDuration)
                .SetEase(Ease.InOutSine).SetRecyclable(true));
            sequence.Append(transform.DOLocalRotateQuaternion(third, thirdDuration)
                .SetEase(Ease.InOutSine).SetRecyclable(true));
            sequence.Append(transform.DOLocalRotateQuaternion(
                    restLocalRotation, finalDuration)
                .SetEase(Ease.OutSine).SetRecyclable(true));

            float riseDuration = total * 0.22f;
            sequence.Insert(0f, overlay.DOColor(peak, riseDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(riseDuration, overlay.DOColor(clear, total - riseDuration)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.OnComplete(HandleCompleted);
            sequence.OnKill(HandleKilled);
            activeSequence = sequence;
        }

        /// <summary>
        /// Stops only the rejection sequence. Pass false after an authoritative shelf
        /// layout has already written a newer rotation, so the cached old pose cannot win.
        /// </summary>
        public void Cancel(bool restoreRotation)
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);

            if (restoreRotation && ownsRotation)
                transform.localRotation = restLocalRotation;
            ownsRotation = false;
            HideOverlay();
        }

        private void OnDisable() => Cancel(true);

        private void OnDestroy()
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
        }

        private void HandleCompleted()
        {
            activeSequence = null;
            if (ownsRotation) transform.localRotation = restLocalRotation;
            ownsRotation = false;
            HideOverlay();
        }

        private void HandleKilled()
        {
            // Cancel clears the reference before Kill, so this path is reserved for a
            // Safe Mode/external kill and still leaves no tilted or glowing residue.
            if (activeSequence == null) return;
            activeSequence = null;
            if (ownsRotation) transform.localRotation = restLocalRotation;
            ownsRotation = false;
            HideOverlay();
        }

        private bool TryPrepareOverlay(Color tint, float highlightAlpha)
        {
            bottle ??= GetComponent<LiquidBottle>();
            SpriteRenderer source = FindSilhouetteRenderer();
            if (bottle == null || source == null || source.sprite == null) return false;

            EnsureOverlay();
            if (overlay == null) return false;

            Transform overlayTransform = overlay.transform;
            Transform sourceTransform = source.transform;
            if (sourceTransform.parent == transform)
            {
                overlayTransform.localPosition = sourceTransform.localPosition;
                overlayTransform.localRotation = sourceTransform.localRotation;
                overlayTransform.localScale = sourceTransform.localScale;
            }
            else
            {
                overlayTransform.SetPositionAndRotation(
                    sourceTransform.position, sourceTransform.rotation);
                Vector3 parentScale = transform.lossyScale;
                Vector3 sourceScale = sourceTransform.lossyScale;
                overlayTransform.localScale = new Vector3(
                    SafeRatio(sourceScale.x, parentScale.x),
                    SafeRatio(sourceScale.y, parentScale.y),
                    SafeRatio(sourceScale.z, parentScale.z));
            }

            overlay.sprite = source.sprite;
            overlay.sharedMaterial = source.sharedMaterial;
            overlay.drawMode = source.drawMode;
            overlay.size = source.size;
            overlay.flipX = source.flipX;
            overlay.flipY = source.flipY;
            overlay.maskInteraction = source.maskInteraction;
            overlay.sortingLayerID = source.sortingLayerID;
            overlay.sortingOrder = HighestSortingOrder() + 2;

            overlayBlock ??= new MaterialPropertyBlock();
            source.GetPropertyBlock(overlayBlock);
            overlay.SetPropertyBlock(overlayBlock);

            Color clear = tint;
            clear.a = 0f;
            overlay.color = clear;
            overlay.enabled = highlightAlpha > 0.001f;
            return true;
        }

        private void EnsureOverlay()
        {
            if (overlay != null) return;

            Transform existing = transform.Find(OverlayName);
            if (existing != null) overlay = existing.GetComponent<SpriteRenderer>();
            if (overlay == null)
            {
                var overlayObject = new GameObject(OverlayName);
                overlayObject.layer = gameObject.layer;
                overlayObject.transform.SetParent(transform, false);
                overlay = overlayObject.AddComponent<SpriteRenderer>();
                bottle?.InvalidateRenderers();
            }
        }

        private SpriteRenderer FindSilhouetteRenderer()
        {
            SpriteRenderer fallback = null;
            SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer candidate = renderers[i];
                if (candidate == null || candidate == overlay
                    || candidate.name == OverlayName || !candidate.enabled
                    || candidate.sprite == null)
                    continue;
                if (candidate.name == "FrontGlass") return candidate;
                if (fallback == null && candidate.name != "Shadow")
                    fallback = candidate;
            }
            return fallback;
        }

        private int HighestSortingOrder()
        {
            int highest = 0;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer candidate = renderers[i];
                if (candidate == null || candidate == overlay) continue;
                highest = Mathf.Max(highest, candidate.sortingOrder);
            }
            return Mathf.Min(32765, highest);
        }

        private void HideOverlay()
        {
            if (overlay == null) return;
            overlay.enabled = false;
            Color clear = overlay.color;
            clear.a = 0f;
            overlay.color = clear;
        }

        private static float SafeRatio(float numerator, float denominator) =>
            Mathf.Abs(denominator) > 0.0001f ? numerator / denominator : 1f;
    }
}
