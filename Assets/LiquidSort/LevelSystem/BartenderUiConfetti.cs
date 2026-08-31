using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Lightweight, pooled confetti drawn inside a Screen Space Overlay canvas.
    /// UI graphics are used deliberately: a world ParticleSystem cannot render in
    /// front of an overlay canvas, regardless of its renderer sorting order.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderUiConfetti : MonoBehaviour
    {
        private const int PieceCount = 64;
        private const int OpeningBurstCount = 20;
        private const float ReferenceWidth = 720f;
        private const float ReferenceHeight = 1280f;

        private static readonly Color[] Palette =
        {
            new Color32(0xFF, 0xD3, 0x4D, 0xFF),
            new Color32(0xFF, 0xA9, 0x1F, 0xFF),
            new Color32(0x8B, 0x48, 0xE8, 0xFF),
            new Color32(0x4F, 0xD7, 0xFF, 0xFF),
            new Color32(0xFF, 0x66, 0xA8, 0xFF),
            new Color32(0xFF, 0x69, 0x58, 0xFF),
            new Color32(0xFF, 0xF4, 0xCB, 0xFF),
        };

        private struct PieceMotion
        {
            public Vector2 Position;
            public Vector2 Velocity;
            public float Gravity;
            public float Delay;
            public float Elapsed;
            public float Rotation;
            public float AngularVelocity;
            public float SwayAmplitude;
            public float SwayFrequency;
            public float SwayPhase;
            public float FlipFrequency;
            public Color Colour;
        }

        private RectTransform root;
        private RectTransform[] pieceRects;
        private Image[] pieceImages;
        private PieceMotion[] motions;
        private System.Random random;
        private Rect canvasBounds;
        private bool playing;
        private int playIndex;

        public static BartenderUiConfetti AttachTo(RectTransform canvasRoot)
        {
            if (canvasRoot == null) return null;

            BartenderUiConfetti existing =
                canvasRoot.GetComponentInChildren<BartenderUiConfetti>(true);
            if (existing != null) return existing;

            var layerObject = new GameObject(
                "Victory Confetti (Runtime)", typeof(RectTransform));
            layerObject.hideFlags = HideFlags.DontSave;
            layerObject.transform.SetParent(canvasRoot, false);

            RectTransform layer = layerObject.GetComponent<RectTransform>();
            layer.anchorMin = Vector2.zero;
            layer.anchorMax = Vector2.one;
            layer.offsetMin = Vector2.zero;
            layer.offsetMax = Vector2.zero;

            BartenderUiConfetti confetti =
                layerObject.AddComponent<BartenderUiConfetti>();
            confetti.root = layer;
            confetti.EnsurePool();
            confetti.StopAndClear();
            return confetti;
        }

        public void Play()
        {
            EnsurePool();
            if (root == null || pieceRects == null) return;

            gameObject.SetActive(true);
            root.SetAsLastSibling();
            canvasBounds = ResolveCanvasBounds();
            int seed = unchecked(0x51F15EED + playIndex++ * 7919);
            random = new System.Random(seed);

            for (int i = 0; i < pieceRects.Length; i++)
            {
                if (i < OpeningBurstCount)
                    ConfigureOpeningBurst(i);
                else
                    ConfigureRain(i, true);
            }

            playing = true;
        }

        public void StopAndClear()
        {
            playing = false;
            HideAllPieces();
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        private void Awake()
        {
            if (root == null) root = transform as RectTransform;
        }

        private void OnDisable()
        {
            playing = false;
            HideAllPieces();
        }

        private void OnRectTransformDimensionsChange()
        {
            if (!playing || root == null) return;
            Rect updated = ResolveCanvasBounds();
            if (updated.width <= 1f || updated.height <= 1f) return;
            canvasBounds = updated;
        }

        private void Update()
        {
            if (!playing || motions == null || random == null) return;

            float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (deltaTime <= 0f) return;

            for (int i = 0; i < motions.Length; i++)
            {
                PieceMotion motion = motions[i];
                motion.Elapsed += deltaTime;
                if (motion.Elapsed < motion.Delay)
                {
                    pieceImages[i].enabled = false;
                    motions[i] = motion;
                    continue;
                }

                float activeTime = motion.Elapsed - motion.Delay;
                motion.Velocity.y -= motion.Gravity * deltaTime;
                motion.Position += motion.Velocity * deltaTime;
                motion.Rotation += motion.AngularVelocity * deltaTime;

                if (motion.Position.y < canvasBounds.yMin - 110f)
                {
                    ConfigureRain(i, false);
                    continue;
                }

                Image image = pieceImages[i];
                RectTransform rect = pieceRects[i];
                image.enabled = true;

                float sway = Mathf.Sin(
                    activeTime * motion.SwayFrequency + motion.SwayPhase)
                    * motion.SwayAmplitude;
                rect.anchoredPosition = motion.Position + Vector2.right * sway;
                rect.localRotation = Quaternion.Euler(0f, 0f, motion.Rotation);

                float flip = Mathf.Lerp(0.20f, 1f, Mathf.Abs(Mathf.Cos(
                    activeTime * motion.FlipFrequency + motion.SwayPhase)));
                rect.localScale = new Vector3(flip, 1f, 1f);

                float fadeIn = Mathf.Clamp01(activeTime / 0.08f);
                Color colour = motion.Colour;
                colour.a *= fadeIn;
                image.color = colour;
                motions[i] = motion;
            }
        }

        private void EnsurePool()
        {
            if (pieceRects != null || !Application.isPlaying) return;
            if (root == null) root = transform as RectTransform;
            if (root == null) return;

            pieceRects = new RectTransform[PieceCount];
            pieceImages = new Image[PieceCount];
            motions = new PieceMotion[PieceCount];

            for (int i = 0; i < PieceCount; i++)
            {
                var pieceObject = new GameObject(
                    $"Confetti {i + 1:00}", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image));
                pieceObject.hideFlags = HideFlags.DontSave;
                pieceObject.transform.SetParent(root, false);

                RectTransform rect = pieceObject.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.localScale = Vector3.one;

                Image image = pieceObject.GetComponent<Image>();
                image.raycastTarget = false;
                image.maskable = false;
                image.enabled = false;

                pieceRects[i] = rect;
                pieceImages[i] = image;
            }
        }

        private void ConfigureOpeningBurst(int index)
        {
            bool fromLeft = index % 2 == 0;
            float inset = Mathf.Max(16f, canvasBounds.width * 0.035f);
            float startX = fromLeft
                ? canvasBounds.xMin + inset
                : canvasBounds.xMax - inset;
            float startY = canvasBounds.yMin + canvasBounds.height * Next(0.08f, 0.22f);

            PieceMotion motion = NewMotion(index);
            motion.Position = new Vector2(startX, startY);
            motion.Velocity = new Vector2(
                (fromLeft ? 1f : -1f) * Next(150f, 330f),
                Next(590f, 860f));
            motion.Gravity = Next(610f, 820f);
            motion.Delay = Next(0f, 0.28f);
            motion.SwayAmplitude = Next(5f, 18f);
            motion.SwayFrequency = Next(3.5f, 6.2f);
            motions[index] = motion;
            PreparePiece(index, motion);
        }

        private void ConfigureRain(int index, bool openingWave)
        {
            PieceMotion motion = NewMotion(index);
            motion.Position = new Vector2(
                Next(canvasBounds.xMin + 12f, canvasBounds.xMax - 12f),
                canvasBounds.yMax + Next(16f, openingWave ? 220f : 90f));
            motion.Velocity = new Vector2(Next(-42f, 42f), Next(-510f, -345f));
            motion.Gravity = Next(45f, 115f);
            motion.Delay = openingWave ? Next(0.04f, 0.85f) : Next(0.05f, 0.55f);
            motion.SwayAmplitude = Next(10f, 34f);
            motion.SwayFrequency = Next(2.4f, 5.4f);
            motions[index] = motion;
            PreparePiece(index, motion);
        }

        private PieceMotion NewMotion(int index)
        {
            Color colour = Palette[random.Next(Palette.Length)];
            float scale = Mathf.Clamp(
                Mathf.Min(canvasBounds.width / ReferenceWidth,
                    canvasBounds.height / ReferenceHeight), 0.78f, 1.32f);
            bool square = random.NextDouble() < 0.18d;
            float width = square ? Next(10f, 16f) : Next(7f, 12f);
            float height = square ? width : Next(17f, 29f);
            pieceRects[index].sizeDelta = new Vector2(width, height) * scale;

            return new PieceMotion
            {
                Rotation = Next(-180f, 180f),
                AngularVelocity = Next(-620f, 620f),
                SwayPhase = Next(0f, Mathf.PI * 2f),
                FlipFrequency = Next(5.5f, 10.5f),
                Colour = colour,
            };
        }

        private void PreparePiece(int index, PieceMotion motion)
        {
            RectTransform rect = pieceRects[index];
            rect.anchoredPosition = motion.Position;
            rect.localRotation = Quaternion.Euler(0f, 0f, motion.Rotation);
            rect.localScale = Vector3.one;
            pieceImages[index].color = motion.Colour;
            pieceImages[index].enabled = false;
        }

        private Rect ResolveCanvasBounds()
        {
            Rect found = root != null ? root.rect : default;
            if (found.width > 1f && found.height > 1f) return found;

            RectTransform parent = root != null ? root.parent as RectTransform : null;
            if (parent != null && parent.rect.width > 1f && parent.rect.height > 1f)
                return parent.rect;
            return new Rect(
                -ReferenceWidth * 0.5f, -ReferenceHeight * 0.5f,
                ReferenceWidth, ReferenceHeight);
        }

        private float Next(float minimum, float maximum)
        {
            return minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        private void HideAllPieces()
        {
            if (pieceImages == null) return;
            for (int i = 0; i < pieceImages.Length; i++)
            {
                if (pieceImages[i] != null) pieceImages[i].enabled = false;
                if (pieceRects[i] != null) pieceRects[i].localScale = Vector3.one;
            }
        }
    }
}
