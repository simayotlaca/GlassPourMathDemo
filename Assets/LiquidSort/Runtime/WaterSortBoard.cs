using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// The puzzle itself: pick a bottle, pick a target, move the whole run of matching
    /// units on top. All the "liquid" here is integer state; the visuals follow.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WaterSortBoard : MonoBehaviour
    {
        [Header("Layout")]
        public List<LiquidBottle> bottles = new List<LiquidBottle>();
        public PourAnimator pourAnimator;
        public Camera boardCamera;

        [Header("Level")]
        public bool generateOnStart = true;
        public int seed = 0;
        public int colorCount = 4;
        public int capacity = 4;
        // Body colour of each liquid, sampled straight out of the reference footage.
        // These are the body, not the top face: the shader lifts the cap off them.
        public Color[] palette =
        {
            Hex(0xFC6FD8), // pink
            Hex(0x5E1D8D), // purple
            Hex(0x6A0051), // wine
            Hex(0x6FA400), // lime
            Hex(0xADAB82), // sand
            Hex(0x057A64), // teal
            Hex(0xE35800), // orange
            Hex(0x0098D7)  // blue
        };

        /// <summary>Palette entries are written as the hex the colours were sampled as.</summary>
        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        [Header("Interaction")]
        public float selectionLift = 0.18f;
        public float selectionSpeed = 14f;
        public float pickPadding = 0.16f;
        [Tooltip("Game boards keep this on. Animation sandboxes may turn it off so every colour can be poured repeatedly with only two vessels.")]
        public bool requireMatchingColors = true;

        private LiquidBottle selected;
        private PourAnimator subscribedAnimator;
        private int activeBoardOperationId;
        private readonly Dictionary<LiquidBottle, float> baseHeights = new Dictionary<LiquidBottle, float>();
        private readonly Dictionary<LiquidBottle, BottleShell> shells = new Dictionary<LiquidBottle, BottleShell>();

        public bool Solved { get; private set; }

        private void Start()
        {
            if (boardCamera == null) boardCamera = Camera.main;
            if (pourAnimator == null) pourAnimator = GetComponent<PourAnimator>();
            if (pourAnimator == null) pourAnimator = gameObject.AddComponent<PourAnimator>();
            SubscribeToAnimator();

            CacheHeights();
            if (generateOnStart) Generate();
        }

        private void OnEnable() => SubscribeToAnimator();

        private void OnDisable()
        {
            UnsubscribeFromAnimator();
            if (pourAnimator != null) pourAnimator.CancelActivePour();
            activeBoardOperationId = 0;
            SnapSelectionToRest();
        }

        private void SubscribeToAnimator()
        {
            if (subscribedAnimator == pourAnimator) return;
            UnsubscribeFromAnimator();
            subscribedAnimator = pourAnimator;
            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished += HandlePourFinished;
        }

        private void UnsubscribeFromAnimator()
        {
            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            subscribedAnimator = null;
        }

        private void CacheHeights()
        {
            baseHeights.Clear();
            shells.Clear();
            for (int i = 0; i < bottles.Count; i++)
            {
                if (bottles[i] == null) continue;
                baseHeights[bottles[i]] = bottles[i].transform.position.y;
                shells[bottles[i]] = bottles[i].GetComponent<BottleShell>();
            }
        }

        /// <summary>Deals a shuffled bag of units. Two spare bottles keep it solvable in practice.</summary>
        public void Generate()
        {
            // Invalidate the old visual operation before touching any logical stack. Its
            // owned coroutine is stopped synchronously and cannot mutate the new deal later.
            if (pourAnimator != null) pourAnimator.CancelActivePour();
            SnapSelectionToRest();
            if (bottles.Count == 0) return;

            var random = seed == 0 ? new System.Random() : new System.Random(seed);
            int colors = Mathf.Clamp(colorCount, 1, Mathf.Min(palette.Length, bottles.Count));

            var bag = new List<Color>(colors * capacity);
            for (int c = 0; c < colors; c++)
                for (int u = 0; u < capacity; u++)
                    bag.Add(palette[c]);

            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }

            int index = 0;
            for (int b = 0; b < bottles.Count; b++)
            {
                var bottle = bottles[b];
                if (bottle == null) continue;
                bottle.capacity = capacity;

                var contents = new List<Color>();
                if (b < colors)
                {
                    for (int u = 0; u < capacity && index < bag.Count; u++, index++)
                        contents.Add(bag[index]);
                }
                bottle.SetUnits(contents);
            }

            Solved = false;
            selected = null;
            CacheHeights();
        }

        private void Update()
        {
            AnimateSelection();

            if (pourAnimator != null && pourAnimator.Busy) return;
            if (!Input.GetMouseButtonDown(0)) return;

            LiquidBottle hit = Pick(Input.mousePosition);
            if (hit == null)
            {
                selected = null;
                return;
            }

            if (selected == null)
            {
                if (!hit.IsEmpty) selected = hit;
                return;
            }

            if (hit == selected)
            {
                selected = null;
                return;
            }

            int amount = TransferAmount(selected, hit, requireMatchingColors);
            if (amount <= 0)
            {
                selected = hit.IsEmpty ? null : hit;
                return;
            }

            LiquidBottle source = selected;
            float homeY = baseHeights.TryGetValue(source, out float cachedHome)
                ? cachedHome
                : source.transform.position.y;
            if (pourAnimator != null
                && pourAnimator.TryStartPour(source, hit, amount, homeY,
                    requireMatchingColors))
            {
                activeBoardOperationId = pourAnimator.ActiveOperationId;
                selected = null;
            }
        }

        private void HandlePourFinished(int operationId, PourOutcome outcome)
        {
            if (operationId != activeBoardOperationId) return;
            activeBoardOperationId = 0;
            if (outcome == PourOutcome.Completed && isActiveAndEnabled) CheckSolved();
        }

        /// <summary>How many units may legally move from source to target.</summary>
        public static int TransferAmount(LiquidBottle source, LiquidBottle target)
            => TransferAmount(source, target, true);

        /// <summary>Sandbox overload. Production boards should require matching colours.</summary>
        public static int TransferAmount(LiquidBottle source, LiquidBottle target,
            bool requireMatchingColors)
        {
            if (source == null || target == null || source == target) return 0;
            if (source.IsEmpty || target.IsFull) return 0;
            if (requireMatchingColors && !target.CanReceive(source.TopColor)) return 0;
            return Mathf.Min(source.TopRunLength, target.FreeSpace);
        }

        private void CheckSolved()
        {
            for (int i = 0; i < bottles.Count; i++)
            {
                var b = bottles[i];
                if (b == null) continue;
                if (!b.IsEmpty && !b.IsComplete) return;
            }
            Solved = true;
            Debug.Log("LiquidSort: solved.");
        }

        private void AnimateSelection()
        {
            for (int i = 0; i < bottles.Count; i++)
            {
                var bottle = bottles[i];
                if (bottle == null || !baseHeights.TryGetValue(bottle, out float home)) continue;
                if (pourAnimator != null && pourAnimator.Busy) continue;

                bool picked = bottle == selected;
                float wanted = picked ? home + selectionLift : home;
                Vector3 position = bottle.transform.position;
                float follow = 1f - Mathf.Exp(-selectionSpeed * Time.deltaTime);
                position.y = Mathf.Lerp(position.y, wanted, follow);
                bottle.transform.position = position;

                // Lift and glow on the same curve, so the pick reads as one movement.
                if (shells.TryGetValue(bottle, out BottleShell shell) && shell != null)
                    shell.highlight = Mathf.Lerp(shell.highlight, picked ? 1f : 0f, follow);
            }
        }

        private void SnapSelectionToRest()
        {
            foreach (KeyValuePair<LiquidBottle, float> pair in baseHeights)
            {
                LiquidBottle bottle = pair.Key;
                if (bottle == null) continue;
                Vector3 position = bottle.transform.position;
                position.y = pair.Value;
                bottle.transform.position = position;
                if (shells.TryGetValue(bottle, out BottleShell shell) && shell != null)
                    shell.highlight = 0f;
            }
            selected = null;
        }

        private LiquidBottle Pick(Vector3 screenPoint)
        {
            if (boardCamera == null) return null;
            Vector3 world = boardCamera.ScreenToWorldPoint(screenPoint);

            LiquidBottle best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < bottles.Count; i++)
            {
                var bottle = bottles[i];
                if (bottle == null) continue;

                Vector3 local = bottle.transform.InverseTransformPoint(new Vector3(world.x, world.y, 0f));
                Rect r = bottle.InteriorBounds;
                if (local.x < r.xMin - pickPadding || local.x > r.xMax + pickPadding) continue;
                if (local.y < r.yMin - pickPadding || local.y > r.yMax + pickPadding) continue;

                float distance = Mathf.Abs(local.x - r.center.x);
                if (distance < bestDistance) { bestDistance = distance; best = bottle; }
            }
            return best;
        }
    }
}
