using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Where the light on a bottle comes from and how hard it hits.
    ///
    /// Everything is in bottle space and normalised, so one profile suits a bottle,
    /// a coupe and a martini glass without retuning.
    /// </summary>
    [System.Serializable]
    public struct GlassLightProfile
    {
        [Tooltip("Key light direction in bottle space. Up and to the left in the reference art.")]
        public Vector2 keyDirection;
        [Tooltip("White specular along the part of the wall that faces the key light.")]
        [Range(0f, 2f)] public float rimStrength;
        [Tooltip("Cool rim on the wall facing away from it.")]
        [Range(0f, 2f)] public float fillStrength;
        [Tooltip("Centre of the wide gloss column. -1 is the left wall, +1 the right.")]
        [Range(-1f, 1f)] public float glossX;
        [Range(0.02f, 1f)] public float glossWidth;
        [Range(0f, 1f)] public float glossStrength;
        [Tooltip("The narrow hard line inside the column. This is what reads as glass.")]
        [Range(0f, 1f)] public float streakStrength;
        [Tooltip("Centre of the smaller reflection on the opposite side of the glass.")]
        [Range(-1f, 1f)] public float secondaryGlossX;
        [Range(0.02f, 1f)] public float secondaryGlossWidth;
        [Range(0f, 1f)] public float secondaryGlossStrength;
        [Range(0f, 1f)] public float shoulderStrength;
        [Tooltip("Height of the shoulder glint, 0 at the base and 1 at the brim.")]
        [Range(0f, 1f)] public float shoulderHeight;

        /// <summary>Measured off the reference art.</summary>
        public static GlassLightProfile Reference => new GlassLightProfile
        {
            keyDirection = new Vector2(-0.50f, 0.87f),
            // Off. A specular painted along the traced contour fights whatever rim the
            // drawing already has, and on a hand drawn outline it bands into grey
            // segments that read as scuffed metal. The glassiness that is worth keeping
            // is the column inside the wall, not the line on it.
            rimStrength = 0f,
            fillStrength = 0f,
            glossX = -0.46f,
            glossWidth = 0.26f,
            glossStrength = 0.55f,
            streakStrength = 0.42f,
            secondaryGlossX = 0.54f,
            secondaryGlossWidth = 0.12f,
            secondaryGlossStrength = 0.42f,
            shoulderStrength = 0.42f,
            shoulderHeight = 0.84f
        };

        public int Hash()
        {
            unchecked
            {
                int hash = keyDirection.GetHashCode();
                hash = hash * 31 + rimStrength.GetHashCode();
                hash = hash * 31 + fillStrength.GetHashCode();
                hash = hash * 31 + glossX.GetHashCode();
                hash = hash * 31 + glossWidth.GetHashCode();
                hash = hash * 31 + glossStrength.GetHashCode();
                hash = hash * 31 + streakStrength.GetHashCode();
                hash = hash * 31 + secondaryGlossX.GetHashCode();
                hash = hash * 31 + secondaryGlossWidth.GetHashCode();
                hash = hash * 31 + secondaryGlossStrength.GetHashCode();
                hash = hash * 31 + shoulderStrength.GetHashCode();
                hash = hash * 31 + shoulderHeight.GetHashCode();
                return hash;
            }
        }
    }

    /// <summary>
    /// Procedural stand in art so the system runs with zero imported assets.
    /// Replace every sprite here with your own PNGs once you have them: the only
    /// hard requirement is that the interior mask matches the interior polygon.
    /// </summary>
    public static class BottleArtFactory
    {
        private static Material unlitVertexColor;
        private static Material glassLight;

        /// <summary>Alpha only mask of the interior polygon, covering exactly <paramref name="rect"/>.</summary>
        public static Texture2D MaskTexture(Vector2[] polygon, Rect rect, float pixelsPerUnit)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 2048);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "LiquidInteriorMask",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };

            var pixels = new Color32[w * h];
            float texel = rect.width / w;
            for (int y = 0; y < h; y++)
            {
                float wy = rect.yMin + (y + 0.5f) * rect.height / h;
                for (int x = 0; x < w; x++)
                {
                    float wx = rect.xMin + (x + 0.5f) * texel;
                    float d = SignedDistance(polygon, new Vector2(wx, wy));
                    byte a = (byte)(Mathf.Clamp01(0.5f - d / texel) * 255f);
                    pixels[y * w + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Dark empty interior that sits behind the liquid.</summary>
        /// <summary>
        /// Soft rounded panel for the playfield. A scene with a busy table needs the
        /// glasses to sit on something calmer, but darkening the whole frame throws the
        /// table away; this only quiets the strip the puzzle occupies.
        /// </summary>
        public static Sprite Panel(Rect rect, float pixelsPerUnit, Color tint,
            float cornerRadius, float softness = 0.22f)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 2048);
            var tex = NewTexture(w, h, "LiquidPlayfieldPanel");
            var pixels = new Color32[w * h];

            float halfW = rect.width * 0.5f;
            float halfH = rect.height * 0.5f;
            float radius = Mathf.Clamp(cornerRadius, 0f, Mathf.Min(halfW, halfH));
            float fade = Mathf.Max(0.001f, softness);

            for (int y = 0; y < h; y++)
            {
                float py = (y + 0.5f) / h * rect.height - halfH;
                for (int x = 0; x < w; x++)
                {
                    float px = (x + 0.5f) / w * rect.width - halfW;

                    // Rounded box distance: positive outside, negative in.
                    float dx = Mathf.Abs(px) - (halfW - radius);
                    float dy = Mathf.Abs(py) - (halfH - radius);
                    float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f)
                                             + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                    float d = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;

                    float cover = Mathf.Clamp01(-d / fade);
                    cover = cover * cover * (3f - 2f * cover);
                    Color c = tint;
                    c.a = tint.a * cover;
                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        public static Sprite BackGlass(Vector2[] polygon, Rect rect, float pixelsPerUnit,
            GlassVisualTheme.Settings theme)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 2048);
            var tex = NewTexture(w, h, "LiquidBackGlass");
            var pixels = new Color32[w * h];

            // Empty glass is mostly the background, not a colour of its own. This used
            // to be 95% opaque navy, which meant an unfilled vessel punched a dark hole
            // in whatever scene it stood in.
            Color body = theme.backTint;
            body.a = Mathf.Clamp01(theme.backAlpha);
            float texel = rect.width / w;

            for (int y = 0; y < h; y++)
            {
                float wy = rect.yMin + (y + 0.5f) * rect.height / h;
                for (int x = 0; x < w; x++)
                {
                    float wx = rect.xMin + (x + 0.5f) * texel;
                    float d = SignedDistance(polygon, new Vector2(wx, wy));
                    float inside = Mathf.Clamp01(0.5f - d / texel);
                    if (inside <= 0f) { pixels[y * w + x] = new Color32(0, 0, 0, 0); continue; }

                    // Two short, asymmetric shoulder reflections. They live on the rear
                    // glass sprite, below the opaque liquid draw, so they remain visible
                    // in the empty upper chamber and disappear naturally behind colour.
                    // A full-height centre streak would cross every band and expose the
                    // trick; these deliberately stay away from the middle of the vessel.
                    float u = (wx - rect.xMin) / Mathf.Max(rect.width, 1e-4f);
                    float v = (wy - rect.yMin) / Mathf.Max(rect.height, 1e-4f);

                    // A narrow core inside a softer lobe reads as a reflection. One
                    // broad Gaussian reads as fog or translucent plastic, especially
                    // on a bright scene. The two lobes intentionally differ in height,
                    // angle and strength so the glass does not look mechanically lit.
                    float leftCore = RotatedGauss(u, v, 0.22f, 0.77f,
                        0.16f, 0.040f, -15f);
                    float leftHalo = RotatedGauss(u, v, 0.22f, 0.77f,
                        0.205f, 0.072f, -15f);
                    float rightCore = RotatedGauss(u, v, 0.76f, 0.69f,
                        0.105f, 0.030f, 10f);
                    float rightHalo = RotatedGauss(u, v, 0.76f, 0.69f,
                        0.145f, 0.055f, 10f);
                    float left = Mathf.Clamp01(leftCore * 0.82f + leftHalo * 0.28f);
                    float right = 0.60f * Mathf.Clamp01(
                        rightCore * 0.82f + rightHalo * 0.28f);
                    float shoulder = Mathf.Clamp01(left + right);
                    float leftShare = left / Mathf.Max(left + right, 1e-4f);
                    Color reflection = Color.Lerp(theme.glassFillLight,
                        theme.glassKeyLight, leftShare);
                    Color c = Color.Lerp(body, reflection, shoulder * 0.82f);
                    c.a = Mathf.Clamp01(body.a + shoulder * theme.shoulderStrength);
                    c.a *= inside;
                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            // This back layer is generated once and sampled only by the GPU. Dropping
            // the CPU copy after upload avoids retaining another full RGBA texture per
            // vessel for the entire level.
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        /// <summary>
        /// Crisp glass wall, drawn outward from the interior boundary.
        ///
        /// The profile is measured off the reference art. On a bottle whose interior
        /// chord is 143px the wall is 9px, so 6.3% of the interior width. Across that
        /// wall, from the inner side outward: three parts dark navy, then a bright blue
        /// band that peaks one pixel short of the outer edge, then a one pixel cut to
        /// nothing. That last hard cut is what makes it read sharp; a line whose
        /// highlight sits in its middle and fades out slowly reads soft no matter how
        /// many pixels you give it.
        /// </summary>
        public static Sprite FrontGlass(Vector2[] polygon, Rect rect, float pixelsPerUnit,
            float wallThickness = 0.055f, float contourWidth = 0.35f,
            float keyLight = 0.85f, float fillLight = 0.45f)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 4096);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 4096);
            var tex = NewTexture(w, h, "LiquidFrontGlass");
            var pixels = new Color32[w * h];

            Color deep = new Color(0.047f, 0.165f, 0.439f, 1f);    // rgb 12,42,112
            Color bright = new Color(0.278f, 0.525f, 0.792f, 1f);  // rgb 71,134,202
            Color contourColor = new Color(0.024f, 0.031f, 0.094f, 1f);
            Color warm = new Color(0.95f, 0.98f, 1f, 1f);
            Color cool = new Color(0.30f, 0.62f, 1f, 1f);

            float texel = rect.width / w;
            float wall = Mathf.Max(texel * 2f, wallThickness);
            float contour = wall * Mathf.Clamp01(contourWidth);

            // One signed distance per pixel, kept, so the wall can be lit from the
            // gradient of that same field instead of paying for four more polygon walks.
            float[] field = DistanceField(polygon, rect, w, h);
            Vector2 key = new Vector2(-0.50f, 0.87f).normalized;
            Vector2 fill = new Vector2(0.80f, -0.60f).normalized;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = y * w + x;
                    float d = field[index];

                    // t: 0 at the interior boundary, 1 at the outer edge of the wall.
                    float t = d / wall;
                    if (t <= -0.02f || d >= wall + contour + texel)
                    {
                        pixels[index] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    Color c;
                    if (d >= wall)
                    {
                        // A dark hairline outside the wall. Without it the bright stroke
                        // sits straight on the backdrop and the silhouette goes soft.
                        c = contourColor;
                        c.a = Mathf.Clamp01((wall + contour - d) / texel);
                    }
                    else
                    {
                        // Highlight sits at 0.82 of the way out and falls off fast inward.
                        float highlight = Mathf.Clamp01(1f - Mathf.Abs(t - 0.82f) / 0.38f);
                        c = Color.Lerp(deep, bright, highlight * highlight);

                        // The gradient of the distance field is the outward surface normal
                        // of the silhouette, which is all a flat outline needs to be lit
                        // like a real wall: white where it faces the key, cool where it
                        // turns away. A stroke of one constant colour never reads as glass.
                        Vector2 n = FieldNormal(field, w, h, x, y);
                        float spec = Mathf.Pow(Mathf.Clamp01(Vector2.Dot(n, key)), 6f);
                        float rim = Mathf.Pow(Mathf.Clamp01(Vector2.Dot(n, fill)), 3f);
                        c = Color.Lerp(c, warm, spec * keyLight);
                        c = Color.Lerp(c, cool, rim * fillLight);

                        // One texel of coverage on the inner side, nothing softer.
                        c.a = Mathf.Clamp01((d + texel * 0.5f) / texel);
                    }

                    pixels[index] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        /// <summary>
        /// Additive light pass drawn over the whole bottle: the specular along the glass
        /// wall, the wide gloss column with its hard streak, the smaller opposite-side
        /// reflection, and the shoulder glint.
        ///
        /// This is a separate layer rather than part of the liquid shader on purpose.
        /// The highlight is painted on the glass, so it has to cross the empty part of
        /// the vessel too; a highlight that stopped dead at the waterline would give the
        /// whole trick away. It also means authored front artwork gets lit, which a
        /// change inside <see cref="FrontGlass"/> alone cannot do.
        ///
        /// RGB carries the hue of the light and alpha carries how much of it lands, so
        /// the renderer's own colour scales the entire highlight in one multiply.
        /// </summary>
        public static Sprite GlassLight(Vector2[] polygon, Rect rect, float pixelsPerUnit,
            float wallThickness, GlassLightProfile profile)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 4096);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 4096);
            var tex = NewTexture(w, h, "LiquidGlassLight");
            var pixels = new Color32[w * h];

            float texel = rect.width / w;
            float wall = Mathf.Max(texel * 2f, wallThickness);
            float[] field = DistanceField(polygon, rect, w, h);

            Vector2 key = profile.keyDirection.sqrMagnitude > 1e-6f
                ? profile.keyDirection.normalized
                : Vector2.up;
            Vector2 fill = -key;

            // The reference brim catches a cold, almost cyan white. Warm white was the
            // obvious choice and it is the wrong one: added over a blue glass line at
            // partial strength it desaturates to grey, and the brim reads as scuffed
            // metal. Only the thin inner streak stays warm.
            var rimTint = new Vector3(0.78f, 0.93f, 1f);
            var warm = new Vector3(1f, 0.97f, 0.92f);
            var cool = new Vector3(0.36f, 0.64f, 1f);
            var sky = new Vector3(0.62f, 0.78f, 1f);

            float halfWidth = Mathf.Max(rect.width * 0.5f, 1e-4f);
            float centreX = rect.center.x;
            float streakX = profile.glossX + profile.glossWidth * 0.55f;

            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                // The column runs the height of the body and stops short of both ends.
                float lengthwise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.20f, v))
                                 * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.70f, 0.99f, v)));
                float shoulder = Gauss(v - profile.shoulderHeight, 0.055f);

                for (int x = 0; x < w; x++)
                {
                    int index = y * w + x;
                    float d = field[index];
                    if (d > wall)
                    {
                        pixels[index] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float u = (rect.xMin + (x + 0.5f) * texel - centreX) / halfWidth;
                    Vector3 lit = Vector3.zero;

                    if (d > -wall * 0.35f)
                    {
                        Vector2 n = FieldNormal(field, w, h, x, y);
                        float across = (d / wall - 0.55f) / 0.45f;
                        float band = Mathf.Exp(-across * across);
                        lit += rimTint * (band * profile.rimStrength
                                       * Mathf.Pow(Mathf.Clamp01(Vector2.Dot(n, key)), 5f));
                        lit += cool * (band * profile.fillStrength
                                       * Mathf.Pow(Mathf.Clamp01(Vector2.Dot(n, fill)), 3f));
                    }

                    if (d < 0f)
                    {
                        // Fade the painted highlights in from the wall so they never
                        // collide with the specular sitting on it.
                        float inside = Mathf.Clamp01(-d / (wall * 1.5f));
                        lit += sky * (Gauss(u - profile.glossX, profile.glossWidth)
                                      * lengthwise * inside * profile.glossStrength);
                        lit += warm * (Gauss(u - streakX, profile.glossWidth * 0.14f)
                                       * lengthwise * inside * profile.streakStrength);
                        lit += sky * (Gauss(u - profile.secondaryGlossX,
                                           Mathf.Max(0.02f, profile.secondaryGlossWidth))
                                      * lengthwise * inside
                                      * profile.secondaryGlossStrength);
                        lit += warm * (Gauss(u - profile.glossX * 0.75f, 0.20f)
                                       * shoulder * inside * profile.shoulderStrength);
                    }

                    float peak = Mathf.Max(lit.x, Mathf.Max(lit.y, lit.z));
                    if (peak <= 1f / 255f)
                    {
                        pixels[index] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    lit /= peak;
                    pixels[index] = new Color(Mathf.Clamp01(lit.x), Mathf.Clamp01(lit.y),
                        Mathf.Clamp01(lit.z), Mathf.Clamp01(peak));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        /// <summary>
        /// Painterly ground contact composited into one sprite: a very soft warm pool,
        /// a broad plum cast shadow, then a tight dark AO at the foot. Keeping the three
        /// lobes in one texture preserves the existing one-renderer/one-draw-call setup.
        /// </summary>
        public static Sprite Shadow(Rect rect, float pixelsPerUnit, Color tint,
            Color wideTint, Color groundGlowTint, float softness = 1.7f)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 1024);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 1024);
            var tex = NewTexture(w, h, "LiquidContactShadow");
            var pixels = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                float py = (y + 0.5f) / h * 2f - 1f;
                for (int x = 0; x < w; x++)
                {
                    float px = (x + 0.5f) / w * 2f - 1f;
                    bool composite = wideTint.a > 0.001f || groundGlowTint.a > 0.001f;
                    if (!composite)
                    {
                        float r = Mathf.Sqrt(px * px + py * py);
                        float a = Mathf.Pow(Mathf.Clamp01(1f - r),
                            Mathf.Max(softness, 0.1f));
                        pixels[y * w + x] = new Color(
                            tint.r, tint.g, tint.b, tint.a * a);
                        continue;
                    }

                    // Coordinates are in the enlarged composite rect. The contact point
                    // sits high in it (py ~ .68); the soft pool and cast shadow have room
                    // to spread below without being clipped by the texture bounds.
                    float warm = EllipseLobe(px, py, 0f, 0.02f, 0.96f, 0.78f, 1.35f);
                    float broad = EllipseLobe(px, py, 0.08f, 0.36f, 0.80f, 0.40f, 1.65f);
                    float contact = EllipseLobe(px, py, 0f, 0.74f, 0.56f, 0.21f, 1.10f);
                    // Gaussian lobes are mathematically non-zero forever. Fade the last
                    // part of the enlarged bake rect explicitly or a stronger warm pool
                    // reveals the otherwise transparent sprite's rectangular boundary.
                    float edgeFadeX = 1f - Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(0.82f, 1f, Mathf.Abs(px)));
                    float edgeFadeY = 1f - Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(0.88f, 1f, Mathf.Abs(py)));
                    float edgeFade = edgeFadeX * edgeFadeY;
                    warm *= edgeFade;
                    broad *= edgeFade;
                    contact *= edgeFade;

                    Color result = Color.clear;
                    result = AlphaOver(result, groundGlowTint, warm);
                    result = AlphaOver(result, wideTint, broad);
                    result = AlphaOver(result, tint, contact);
                    pixels[y * w + x] = result;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        private static float EllipseLobe(float x, float y, float centreX, float centreY,
            float radiusX, float radiusY, float falloff)
        {
            float nx = (x - centreX) / Mathf.Max(radiusX, 1e-4f);
            float ny = (y - centreY) / Mathf.Max(radiusY, 1e-4f);
            return Mathf.Exp(-(nx * nx + ny * ny) * Mathf.Max(falloff, 0.1f));
        }

        /// <summary>Straight-alpha Porter-Duff over, used while baking the ground sprite.</summary>
        private static Color AlphaOver(Color destination, Color source, float coverage)
        {
            float sourceAlpha = Mathf.Clamp01(source.a * coverage);
            float destinationAlpha = Mathf.Clamp01(destination.a);
            float alpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
            if (alpha <= 1e-5f) return Color.clear;

            Vector3 premultiplied = new Vector3(source.r, source.g, source.b) * sourceAlpha
                                  + new Vector3(destination.r, destination.g, destination.b)
                                  * destinationAlpha * (1f - sourceAlpha);
            return new Color(premultiplied.x / alpha, premultiplied.y / alpha,
                premultiplied.z / alpha, alpha);
        }

        private static float Gauss(float x, float sigma)
        {
            float t = x / Mathf.Max(sigma, 1e-4f);
            return Mathf.Exp(-t * t);
        }

        private static float RotatedGauss(float x, float y, float centerX, float centerY,
            float radiusX, float radiusY, float angleDegrees)
        {
            float radians = angleDegrees * Mathf.Deg2Rad;
            float cosine = Mathf.Cos(radians);
            float sine = Mathf.Sin(radians);
            float dx = x - centerX;
            float dy = y - centerY;
            float along = cosine * dx + sine * dy;
            float across = -sine * dx + cosine * dy;
            float nx = along / Mathf.Max(radiusX, 1e-4f);
            float ny = across / Mathf.Max(radiusY, 1e-4f);
            return Mathf.Exp(-(nx * nx + ny * ny));
        }

        /// <summary>Signed distance to the polygon at every pixel centre of <paramref name="rect"/>.</summary>
        private static float[] DistanceField(Vector2[] polygon, Rect rect, int w, int h)
        {
            var field = new float[w * h];
            float dx = rect.width / w;
            float dy = rect.height / h;
            for (int y = 0; y < h; y++)
            {
                float wy = rect.yMin + (y + 0.5f) * dy;
                int row = y * w;
                for (int x = 0; x < w; x++)
                    field[row + x] = SignedDistance(polygon, new Vector2(rect.xMin + (x + 0.5f) * dx, wy));
            }
            return field;
        }

        /// <summary>Outward surface normal, from the gradient of the distance field.</summary>
        /// <summary>
        /// Outward normal of the distance field.
        ///
        /// A one texel central difference is what you reach for first and it is wrong
        /// here: the field is traced off a hand drawn outline, so at one texel the
        /// gradient is mostly the wobble of the drawing. The rim light then raises that
        /// to the fifth power and the wobble becomes the visible banding along the rim.
        /// A Sobel over a radius that grows with the texture averages the wobble out
        /// while still following the real curve.
        /// </summary>
        private static Vector2 FieldNormal(float[] field, int w, int h, int x, int y)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(w / 220f));

            int xm = Mathf.Max(x - r, 0);
            int xp = Mathf.Min(x + r, w - 1);
            int ym = Mathf.Max(y - r, 0);
            int yp = Mathf.Min(y + r, h - 1);

            float gx = (field[ym * w + xp] + 2f * field[y * w + xp] + field[yp * w + xp])
                     - (field[ym * w + xm] + 2f * field[y * w + xm] + field[yp * w + xm]);
            float gy = (field[yp * w + xm] + 2f * field[yp * w + x] + field[yp * w + xp])
                     - (field[ym * w + xm] + 2f * field[ym * w + x] + field[ym * w + xp]);

            var g = new Vector2(gx, gy);
            float m = g.magnitude;
            return m > 1e-6f ? g / m : Vector2.up;
        }

        /// <summary>Flat fill with a rim, used for necks, caps and any solid glass piece.</summary>
        public static Sprite Solid(Vector2[] polygon, Rect rect, float pixelsPerUnit,
            Color fill, Color rim, float rimWidth)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 2048);
            var tex = NewTexture(w, h, "LiquidSolid");
            var pixels = new Color32[w * h];
            float texel = rect.width / w;

            for (int y = 0; y < h; y++)
            {
                float wy = rect.yMin + (y + 0.5f) * rect.height / h;
                for (int x = 0; x < w; x++)
                {
                    float wx = rect.xMin + (x + 0.5f) * texel;
                    float d = SignedDistance(polygon, new Vector2(wx, wy));
                    float inside = Mathf.Clamp01(0.5f - d / texel);
                    if (inside <= 0f) { pixels[y * w + x] = new Color32(0, 0, 0, 0); continue; }

                    float edge = Mathf.Clamp01(1f + d / Mathf.Max(0.001f, rimWidth));
                    Color c = Color.Lerp(fill, rim, edge * edge);
                    c.a *= inside;
                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return MakeSprite(tex, rect, pixelsPerUnit);
        }

        public static Material UnlitVertexColor()
        {
            if (unlitVertexColor != null) return unlitVertexColor;
            Shader shader = Shader.Find("LiquidSort/UnlitVertexColor");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            unlitVertexColor = new Material(shader)
            {
                name = "LiquidSortUnlit",
                hideFlags = HideFlags.DontSave
            };
            unlitVertexColor.mainTexture = Texture2D.whiteTexture;
            return unlitVertexColor;
        }

        /// <summary>
        /// Additive material for the glass light layer. Returns null when the shader is
        /// missing, and <see cref="BottleShell"/> then simply skips the layer rather than
        /// drawing the light pass with an alpha blended material, which would flood the
        /// bottle with white. Serialize a material asset in a build; Shader.Find only
        /// resolves shaders the build already includes.
        /// </summary>
        public static Material GlassLightMaterial()
        {
            if (glassLight != null) return glassLight;
            Shader shader = Shader.Find("LiquidSort/GlassLight");
            if (shader == null) return null;
            glassLight = new Material(shader)
            {
                name = "LiquidSortGlassLight",
                hideFlags = HideFlags.DontSave
            };
            return glassLight;
        }

        /// <summary>
        /// Releases a sprite and its texture produced by this factory. Generated shell
        /// art is one-sprite-per-texture, so ownership is unambiguous. Authored sprite
        /// assets must never be passed here.
        /// </summary>
        internal static void ReleaseGeneratedSprite(Sprite sprite)
        {
            if (sprite == null) return;

            Texture2D texture = sprite.texture;
            DestroyRuntimeObject(sprite);
            if (texture != null && texture != Texture2D.whiteTexture)
                DestroyRuntimeObject(texture);
        }

        private static void DestroyRuntimeObject(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }

        private static Texture2D NewTexture(int w, int h, string name)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
        }

        private static Sprite MakeSprite(Texture2D tex, Rect rect, float pixelsPerUnit)
        {
            var pivot = new Vector2(-rect.xMin / rect.width, -rect.yMin / rect.height);
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), pivot, pixelsPerUnit,
                0, SpriteMeshType.FullRect);
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        /// <summary>Negative inside the polygon, positive outside, in world units.</summary>
        public static float SignedDistance(Vector2[] polygon, Vector2 point)
        {
            float best = float.MaxValue;
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[j];
                Vector2 b = polygon[i];
                best = Mathf.Min(best, DistanceToSegment(point, a, b));
                if ((b.y > point.y) != (a.y > point.y) &&
                    point.x < (a.x - b.x) * (point.y - b.y) / (a.y - b.y) + b.x)
                {
                    inside = !inside;
                }
            }
            return inside ? -best : best;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared < 1e-8f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSquared);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
