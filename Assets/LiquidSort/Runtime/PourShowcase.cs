using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// A small set of vessels that pour into each other, so the whole system can be watched
    /// in one place: the level holding while a glass tilts, the surface staying flat, the
    /// stream, the receiving glass filling, the slosh settling afterwards.
    ///
    /// Drop this on an empty GameObject, give it the glass drawing, press Play. It builds
    /// its own glasses, so there is nothing to wire up and nothing to click.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PourShowcase : MonoBehaviour
    {
        [Header("Art")]
        [Tooltip("Baked vessel. With one assigned the glasses take their shape, art, capacity and look from it, and nothing is traced or generated at runtime. Bake it with Tools > LiquidSort.")]
        public VesselProfile profile;
        [Tooltip("Optional third vessel. Its own profile supplies its shape, capacity and pour pose; leave empty to keep the original two-glass showcase.")]
        public VesselProfile thirdProfile;
        [Tooltip("Fallback drawing, used only when no profile is assigned.")]
        public Sprite glassArt;
        [Tooltip("Repaint the drawing's stroke. The numbers live on each glass's BottleShell.")]
        public bool restyleLine = true;
        [Tooltip("Scene palette for contour and fake-glass reflections. Empty uses the neutral GlassVisualTheme defaults.")]
        public GlassVisualTheme glassTheme;

        [Header("Layout")]
        public float separation = 3.2f;
        public float glassY = -0.6f;
        public int capacity = 2;

        [Header("Contents, bottom to top")]
        public Color lower = new Color(0.020f, 0.478f, 0.392f);   // teal 057A64
        public Color upper = new Color(0.988f, 0.435f, 0.847f);   // pink FC6FD8

        [Header("Input")]
        [Tooltip("Tap a glass to pick it up, tap another to pour into it. The transfer only happens if the target has room and the colours match, which is the actual puzzle rule.")]
        public bool interactive = true;

        [Header("Loop")]
        [Tooltip("Pour back and forth on a timer instead of waiting to be tapped. Leave this off when interactive is on, or the two fight over the glasses.")]
        public bool autoPour;
        public float pauseBetweenPours = 1.1f;
        public bool loopForever = true;

        private LiquidBottle left;
        private LiquidBottle right;
        private LiquidBottle third;
        private PourAnimator animator;

        private IEnumerator Start()
        {
            if (profile == null && glassArt == null)
            {
                Debug.LogError("LiquidSort: PourShowcase needs a vessel profile or a glass drawing.", this);
                yield break;
            }
            if (profile != null && !profile.IsBaked)
            {
                Debug.LogError($"LiquidSort: '{profile.name}' has not been baked. " +
                               "Select it and run Tools > LiquidSort > Bake Selected Vessel Profiles.", this);
                yield break;
            }
            if (thirdProfile != null && !thirdProfile.IsBaked)
            {
                Debug.LogError($"LiquidSort: '{thirdProfile.name}' has not been baked. " +
                               "Select it and run Tools > LiquidSort > Bake Selected Vessel Profiles.", this);
                yield break;
            }
            if (profile != null) capacity = profile.capacity;

            bool hasThird = thirdProfile != null;
            float leftX = hasThird ? -separation : -separation * 0.5f;
            float rightX = hasThird ? 0f : separation * 0.5f;

            left = BuildGlass("LeftGlass", leftX, profile, new List<Color> { lower, upper });
            right = BuildGlass("MiddleGlass", rightX, profile,
                hasThird ? new List<Color> { lower } : new List<Color>());
            if (hasThird)
                third = BuildGlass("Mug", separation, thirdProfile, new List<Color>());

            animator = gameObject.AddComponent<PourAnimator>();

            if (interactive)
            {
                // The board owns picking and the puzzle rule; the showcase only owns the
                // two glasses. Generation is off because they are already filled above.
                var board = gameObject.AddComponent<WaterSortBoard>();
                board.generateOnStart = false;
                board.capacity = capacity;
                board.pourAnimator = animator;
                // This is an animation workbench, not a puzzle level. With only two
                // vessels, enforcing colour matching deadlocks after the first band.
                // The real WaterSortBoard default remains strict.
                board.requireMatchingColors = false;
                board.bottles.Add(left);
                board.bottles.Add(right);
                if (third != null) board.bottles.Add(third);
            }

            // One frame for every bottle to trace its art and build its shell.
            yield return null;

            while (autoPour)
            {
                yield return new WaitForSeconds(pauseBetweenPours);

                LiquidBottle from = left.IsEmpty ? right : left;
                LiquidBottle into = from == left ? right : left;
                int amount = WaterSortBoard.TransferAmount(from, into, false);
                if (amount <= 0) yield break;

                if (!animator.TryStartPour(from, into, amount, float.NaN, false))
                    yield break;
                int operationId = animator.ActiveOperationId;
                while (animator.Busy && animator.ActiveOperationId == operationId)
                    yield return null;
                if (animator.LastOutcome != PourOutcome.Completed) yield break;

                if (!loopForever) yield break;
            }
        }

        private LiquidBottle BuildGlass(string glassName, float x, VesselProfile vesselProfile,
            List<Color> contents)
        {
            var go = new GameObject(glassName);
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(x, glassY, 0f);

            var bottle = go.AddComponent<LiquidBottle>();
            bottle.capacity = vesselProfile != null ? vesselProfile.capacity : capacity;
            bottle.profile = vesselProfile;
            if (vesselProfile == null) bottle.glassArt = glassArt;
            bottle.sortingOrder = 1;

            var shell = go.AddComponent<BottleShell>();
            shell.backOverride = vesselProfile != null ? vesselProfile.back : null;
            shell.drawNeck = false;
            shell.restyleLine = restyleLine;
            shell.theme = glassTheme;

            bottle.SetUnits(contents);
            return bottle;
        }
    }
}
