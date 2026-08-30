using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Repaints a glass drawing's stroke, in memory, at the size it will be shown.
    ///
    /// Two things make a traced outline look traced. Its edge dissolves when a big texture
    /// is minified, because bilinear sampling reconstructs a shrinking stroke from a
    /// fraction of the texels it covers. And its colour is the same the whole way round,
    /// where real drawn glass catches light on the edges facing the light.
    ///
    /// Both are fixed here. The silhouette is rebuilt from a signed distance field at the
    /// target resolution, so the edge is exactly one texel of coverage no matter the
    /// scale; and the gradient of that same field is the outward normal, which costs
    /// nothing and gives the stroke somewhere for the light to come from.
    ///
    /// Everything is a setting. There is no menu item and no generated file: the sprite
    /// is produced on Awake from whatever the fields say, so you can push the numbers
    /// around in the inspector and watch the glass change.
    /// </summary>
    public static class GlassLineStyler
    {
        [System.Serializable]
        public struct Style
        {
            [Tooltip("Alpha above which a pixel counts as glass line.")]
            [Range(0.02f, 0.9f)] public float alphaThreshold;
            [Tooltip("Width of the rebuilt sprite. Match it to how big the glass actually appears.")]
            public int targetWidth;

            [Header("Stroke profile (0 = outer edge, 1 = inner edge)")]
            [Range(0f, 0.5f)] public float highlightAt;
            [Tooltip("Bright out to here, then falls away by shoulderEnd.")]
            [Range(0f, 1f)] public float plateauEnd;
            [Range(0f, 1f)] public float shoulderEnd;
            [Tooltip("Blend width, in stroke pixels, between treating a line as thin and as a wall.")]
            public float thinLineWidth;

            [Header("Colour")]
            public Color deep;
            public Color bright;

            [Header("Light")]
            [Tooltip("Degrees, 0 = from the right, 90 = from above.")]
            [Range(0f, 360f)] public float lightAngle;
            [Range(0f, 1.5f)] public float shadeLow;
            [Range(0.5f, 2f)] public float shadeHigh;

            public static Style Default => new Style
            {
                alphaThreshold = 0.35f,
                targetWidth = 1024,
                highlightAt = 0.12f,
                plateauEnd = 0.44f,
                shoulderEnd = 0.64f,
                thinLineWidth = 7f,
                deep = new Color(0.047f, 0.165f, 0.439f),
                bright = new Color(0.278f, 0.525f, 0.792f),
                lightAngle = 53f,
                shadeLow = 0.85f,
                shadeHigh = 1.15f
            };
        }

        /// <summary>Builds a restyled copy of <paramref name="source"/>, same world size.</summary>
        public static Sprite Create(Sprite source, Style style)
        {
            if (source == null || source.texture == null) return null;

            Color32[] pixels;
            try
            {
                pixels = source.texture.GetPixels32();
            }
            catch (UnityException)
            {
                Debug.LogError($"LiquidSort: '{source.texture.name}' is not readable. " +
                               "Tick Read/Write on its import settings to restyle its line.");
                return null;
            }

            int w = source.texture.width;
            int h = source.texture.height;

            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(style.alphaThreshold * 255f), 1, 255);
            var ink = new bool[w * h];
            for (int i = 0; i < ink.Length; i++) ink[i] = pixels[i].a >= cut;

            bool[] outside = FloodOutside(ink, w, h);
            var pockets = new bool[w * h];
            for (int i = 0; i < pockets.Length; i++) pockets[i] = !ink[i] && !outside[i];

            int[] fromOutside = DistanceThroughInk(outside, ink, w, h);
            int[] fromPockets = DistanceThroughInk(pockets, ink, w, h);

            // Position across the wall, and how thick the wall is there. Both are smooth
            // fields, so they survive resampling; the hard edged colour they produce
            // would not.
            var across = new float[w * h];
            var thickness = new float[w * h];
            for (int i = 0; i < ink.Length; i++)
            {
                if (!ink[i]) { across[i] = 0f; thickness[i] = 0f; continue; }
                int a = fromOutside[i];
                int b = fromPockets[i];
                if (a == int.MaxValue && b == int.MaxValue) { across[i] = style.highlightAt; thickness[i] = 99f; }
                else if (a == int.MaxValue) { across[i] = 1f; thickness[i] = b * 2f; }
                else if (b == int.MaxValue) { across[i] = 0f; thickness[i] = a * 2f; }
                else { across[i] = a / (float)(a + b); thickness[i] = a + b; }
            }
            Diffuse(across, ink, w, h);
            Diffuse(thickness, ink, w, h);

            float[] signed = SignedDistanceField(ink, w, h);

            int outW = style.targetWidth > 0 ? Mathf.Clamp(style.targetWidth, 64, w) : w;
            int outH = Mathf.Max(1, Mathf.RoundToInt(outW * (h / (float)w)));
            float scale = w / (float)outW;

            float rad = style.lightAngle * Mathf.Deg2Rad;
            var light = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            float thinBlend = Mathf.Max(0.5f, style.thinLineWidth);

            var output = new Color32[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                float sy = (y + 0.5f) * scale - 0.5f;
                for (int x = 0; x < outW; x++)
                {
                    float sx = (x + 0.5f) * scale - 0.5f;

                    // One texel of coverage at the target resolution, whatever the scale.
                    float d = SampleBilinear(signed, w, h, sx, sy) / scale;
                    float alpha = Mathf.Clamp01(0.5f - d);
                    if (alpha <= 0.002f) { output[y * outW + x] = new Color32(0, 0, 0, 0); continue; }

                    float acrossHere = SampleBilinear(across, w, h, sx, sy);
                    float thickHere = SampleBilinear(thickness, w, h, sx, sy) / scale;

                    // A thin line has no room for a gradient across it, and the highlight
                    // sits near the outer edge where a two pixel stroke can never reach.
                    float shaped = Mathf.Lerp(style.highlightAt, acrossHere,
                        Mathf.Clamp01((thickHere - 2f) / thinBlend));

                    float core = 1f - Mathf.SmoothStep(style.plateauEnd, style.shoulderEnd, shaped);
                    float lip = Mathf.SmoothStep(0f, 0.10f, shaped);
                    float highlight = core * Mathf.Lerp(0.86f, 1f, lip);

                    // The gradient of the distance field is the outward normal, free.
                    float gx = SampleBilinear(signed, w, h, sx + 1f, sy)
                               - SampleBilinear(signed, w, h, sx - 1f, sy);
                    float gy = SampleBilinear(signed, w, h, sx, sy + 1f)
                               - SampleBilinear(signed, w, h, sx, sy - 1f);
                    var normal = new Vector2(gx, gy);
                    float lambert = normal.sqrMagnitude > 1e-6f
                        ? Vector2.Dot(normal.normalized, light)
                        : 0f;
                    float shade = Mathf.Lerp(style.shadeLow, style.shadeHigh, lambert * 0.5f + 0.5f);

                    Color c = Color.Lerp(style.deep, style.bright, Mathf.Clamp01(highlight * shade));
                    output[y * outW + x] = new Color32(
                        (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f),
                        (byte)(alpha * 255f));
                }
            }

            var tex = new Texture2D(outW, outH, TextureFormat.RGBA32, false)
            {
                name = source.name + "_Styled",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            tex.SetPixels32(output);
            tex.Apply(false, false);

            // Same world size as the source, whatever resolution we rebuilt it at.
            float worldWidth = Mathf.Max(0.0001f, source.bounds.size.x);
            var pivot = new Vector2(
                (source.pivot.x - source.rect.x) / Mathf.Max(1f, source.rect.width),
                (source.pivot.y - source.rect.y) / Mathf.Max(1f, source.rect.height));

            var sprite = Sprite.Create(tex, new Rect(0f, 0f, outW, outH), pivot,
                outW / worldWidth, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        private static bool[] FloodOutside(bool[] ink, int w, int h)
        {
            var outside = new bool[w * h];
            var stack = new System.Collections.Generic.Stack<int>(w * h / 8);

            void Seed(int index)
            {
                if (ink[index] || outside[index]) return;
                outside[index] = true;
                stack.Push(index);
            }

            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int x = index % w;
                int y = index / w;
                if (x > 0) Seed(index - 1);
                if (x < w - 1) Seed(index + 1);
                if (y > 0) Seed(index - w);
                if (y < h - 1) Seed(index + w);
            }
            return outside;
        }

        private static int[] DistanceThroughInk(bool[] seeds, bool[] ink, int w, int h)
        {
            var distance = new int[seeds.Length];
            for (int i = 0; i < distance.Length; i++) distance[i] = int.MaxValue;

            var queue = new System.Collections.Generic.Queue<int>();
            for (int index = 0; index < seeds.Length; index++)
            {
                if (!seeds[index]) continue;
                int x = index % w;
                int y = index / w;

                void Seed(int n)
                {
                    if (!ink[n] || distance[n] != int.MaxValue) return;
                    distance[n] = 1;
                    queue.Enqueue(n);
                }

                if (x > 0) Seed(index - 1);
                if (x < w - 1) Seed(index + 1);
                if (y > 0) Seed(index - w);
                if (y < h - 1) Seed(index + w);
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int next = distance[index] + 1;
                int x = index % w;
                int y = index / w;

                void Step(int n)
                {
                    if (!ink[n] || distance[n] <= next) return;
                    distance[n] = next;
                    queue.Enqueue(n);
                }

                if (x > 0) Step(index - 1);
                if (x < w - 1) Step(index + 1);
                if (y > 0) Step(index - w);
                if (y < h - 1) Step(index + w);
            }
            return distance;
        }

        private static void Diffuse(float[] field, bool[] ink, int w, int h)
        {
            var copy = (float[])field.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (ink[i]) continue;

                float sum = 0f;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (!ink[j]) continue;
                    sum += copy[j];
                    n++;
                }
                if (n > 0) field[i] = sum / n;
            }
        }

        private static float[] SignedDistanceField(bool[] ink, int w, int h)
        {
            float[] outer = Chamfer(ink, w, h, true);
            float[] inner = Chamfer(ink, w, h, false);
            var signed = new float[ink.Length];
            for (int i = 0; i < signed.Length; i++)
                signed[i] = ink[i] ? -inner[i] : outer[i];
            return signed;
        }

        private static float[] Chamfer(bool[] ink, int w, int h, bool distanceToInk)
        {
            const float Big = 1e9f;
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++)
                d[i] = (ink[i] == distanceToInk) ? 0f : Big;

            void Relax(int i, int j, float cost)
            {
                float v = d[j] + cost;
                if (v < d[i]) d[i] = v;
            }

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (x > 0) Relax(i, i - 1, 3f);
                if (y > 0) Relax(i, i - w, 3f);
                if (x > 0 && y > 0) Relax(i, i - w - 1, 4f);
                if (x < w - 1 && y > 0) Relax(i, i - w + 1, 4f);
            }
            for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (x < w - 1) Relax(i, i + 1, 3f);
                if (y < h - 1) Relax(i, i + w, 3f);
                if (x < w - 1 && y < h - 1) Relax(i, i + w + 1, 4f);
                if (x > 0 && y < h - 1) Relax(i, i + w - 1, 4f);
            }

            for (int i = 0; i < d.Length; i++) d[i] = Mathf.Min(d[i], Big) / 3f;
            return d;
        }

        private static float SampleBilinear(float[] field, int w, int h, float x, float y)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = Mathf.Clamp01(x - x0);
            float fy = Mathf.Clamp01(y - y0);

            float top = Mathf.Lerp(field[y0 * w + x0], field[y0 * w + x1], fx);
            float bottom = Mathf.Lerp(field[y1 * w + x0], field[y1 * w + x1], fx);
            return Mathf.Lerp(top, bottom, fy);
        }

    }
}
