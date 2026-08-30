using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Stand in glass around a <see cref="LiquidBottle"/>: dark interior behind the
    /// liquid, glass wall and neck in front of it. Swap the three SpriteRenderers for
    /// your own artwork; only the sorting orders matter.
    ///
    ///   Shadow     (order -1)  contact shadow on the backdrop
    ///   BackGlass  (order 0)   empty interior
    ///   Liquid     (order 1)   the shader quad owned by LiquidBottle
    ///   FrontGlass (order 5)   wall, rim light
    ///   Neck       (order 6)   neck and cap
    ///   ThinGlassFX(order 7)   authored-alpha side light and bottom lens
    ///   GlassLight (order 7)   optional legacy procedural highlight
    ///   Frame      (order 8)   immutable artist-painted reflection/overlay
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(LiquidBottle))]
    [DisallowMultipleComponent]
    public sealed class BottleShell : MonoBehaviour
    {
        public float pixelsPerUnit = 384f;
        [Tooltip("Glass wall thickness. The reference art uses 6.3% of the interior width.")]
        public float wallThickness = 0.063f;
        public float neckWidth = 0.34f;
        [Tooltip("Draw the procedural neck/cap. Disable this when custom glass artwork already includes it.")]
        public bool drawNeck = true;
        [Tooltip("Optional authored artwork behind the liquid. Empty uses procedural stand-in art.")]
        public Sprite backOverride;
        [Tooltip("Legacy CPU repaint of the stroke. Leave off: the contour material recolours it at draw time instead, with no allocation and no Read/Write requirement.")]
        public bool restyleLine;
        public GlassLineStyler.Style lineStyle = GlassLineStyler.Style.Default;
        [Tooltip("Scene level glass colours. Usually assigned by a GlassThemeBinder on the board rather than per bottle. Empty falls back to neutral defaults.")]
        public GlassVisualTheme theme;
        [Tooltip("Fallback contour material when the profile has none.")]
        public Material contourMaterial;
        public int backOrder = 0;
        public int frontOrder = 5;
        public int neckOrder = 6;
        [Tooltip("Draw order for VesselProfile.Frame, the immutable artist-painted overlay above the liquid and glass body.")]
        public int frameOrder = 8;

        [Header("Thin glass FX")]
        [Tooltip("Fallback shared thin-FX material for loose, unprofiled scene bottles. Baked profiles take this from VesselProfile instead.")]
        public Material thinGlassFxMaterial;
        [Tooltip("Optional fallback FX sprite for loose bottles. Empty reuses the bottle's resolved glass art, so no duplicate art is required.")]
        public Sprite thinGlassFxSprite;
        [Tooltip("Resting strength of the profile's authored-alpha side light.")]
        [Range(0f, 1f)] public float thinFxIntensity = 0.24f;
        [Tooltip("Extra thin-FX strength while selected. The liquid centre is never part of this layer.")]
        [Range(0f, 1f)] public float thinFxSelectionBoost = 0.08f;
        [Tooltip("Per-vessel multiplier for the narrow visible-floor seam light. Zero is a diagnostic off switch; one uses the scene theme value.")]
        [Range(0f, 2f)] public float floorSeamLightScale = 1f;
        [Tooltip("Per-vessel multiplier for authored reflections on glass-only parts such as handles, stems, feet and the outer lip.")]
        [Range(0f, 2f)] public float accessoryLightScale = 1f;
        [Tooltip("Per-vessel multiplier for the profile's thin lower silhouette highlight.")]
        [Range(0f, 2f)] public float bottomRimLightScale = 1f;
        public int thinFxOrder = 7;

        [Header("Glass light")]
        [Tooltip("Additive highlight over the whole bottle. This is the layer that makes it read as glass.")]
        // A low-strength authored light is part of the neutral master glass. Its intensity
        // stays deliberately below the selection pulse, and the thin wall pass carries
        // most of the edge definition, so this adds volume without bleaching the contour.
        // Keep the legacy full-silhouette wash disabled. Authored contours plus ThinFX
        // preserve crisp liquid colours and place light only on real glass pixels.
        public bool drawGlassLight = false;
        public int lightOrder = 7;
        [Tooltip("Serialized additive material. Empty falls back to Shader.Find, which only resolves shaders a build already includes.")]
        public Material glassLightMaterial;
        public GlassLightProfile lightProfile = GlassLightProfile.Reference;
        [Tooltip("Resting strength of the light layer. The rest is headroom for the selection pulse.")]
        [Range(0f, 1f)] public float lightIntensity = 0.18f;
        [Range(0f, 1f)] public float selectionBoost = 0.12f;

        [Header("Contact shadow")]
        // On: a glass standing on a table needs something under it, or it floats. The
        // reference bottles sat in ice and cast none, which is why this used to be off.
        public bool drawShadow = true;
        public int shadowOrder = -1;
        [Range(0f, 1f)] public float shadowStrength = 0.40f;
        public float shadowHeight = 0.26f;
        [Tooltip("Shadow width as a share of the bottle's own width.")]
        public float shadowWidth = 1.15f;
        public float shadowOffsetY = -0.02f;

        /// <summary>
        /// 0 at rest, 1 while the bottle is picked up. Drives the glass light only, so
        /// nothing has to be regenerated to pulse it.
        /// </summary>
        [System.NonSerialized] public float highlight;

        private LiquidBottle bottle;
        private SpriteRenderer back;
        private SpriteRenderer front;
        private SpriteRenderer neck;
        private SpriteRenderer frame;
        private SpriteRenderer thinGlassFx;
        private SpriteRenderer glassLight;
        private SpriteRenderer shadow;
        private Sprite generatedBack;
        private Sprite generatedFront;
        private Sprite generatedNeck;
        private Sprite generatedLight;
        private Sprite generatedShadow;
        private bool built;
        private Sprite styledLine;
        private MaterialPropertyBlock contourBlock;
        private MaterialPropertyBlock thinFxBlock;
        private static readonly int ContourDarkId = Shader.PropertyToID("_ContourDark");
        private static readonly int ContourLightId = Shader.PropertyToID("_ContourLight");
        private static readonly int LightAngleId = Shader.PropertyToID("_LightAngle");
        private static readonly int ContourSpecularId = Shader.PropertyToID("_SpecularColor");
        private static readonly int InteriorRectId = Shader.PropertyToID("_InteriorRect");
        private static readonly int FxKeyColorId = Shader.PropertyToID("_FxColor");
        private static readonly int FxFillColorId = Shader.PropertyToID("_FxColor2");
        private static readonly int FxSideStrengthId = Shader.PropertyToID("_SideStrength");
        private static readonly int FxBottomStrengthId = Shader.PropertyToID("_BottomStrength");
        private static readonly int ContourAccessoryFxId = Shader.PropertyToID("_AccessoryFx");
        private static readonly int ContourContactStrengthId = Shader.PropertyToID("_ContactStrength");
        private static readonly int ContourBottomRimStrengthId = Shader.PropertyToID("_BottomRimStrength");
        private static readonly int ContourRimHotspotStrengthId = Shader.PropertyToID("_RimHotspotStrength");
        private static readonly int ContourLiquidBounceColorId = Shader.PropertyToID("_LiquidBounceColor");
        private static readonly int ContourLiquidBounceStrengthId = Shader.PropertyToID("_LiquidBounceStrength");
        private static readonly int ContourPaintedToyStrengthId = Shader.PropertyToID("_PaintedToyStrength");
        private static readonly int ContourToyMidColorId = Shader.PropertyToID("_ToyMidColor");
        private static readonly int ContourToyFillColorId = Shader.PropertyToID("_ToyFillColor");
        private static readonly int FxVisibleFloorYId = Shader.PropertyToID("_VisibleFloorY");
        private static readonly int FxVisibleBottomYId = Shader.PropertyToID("_VisibleBottomY");
        private static readonly int FxMaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int FxMaskRectId = Shader.PropertyToID("_MaskRect");
        private static readonly int FxMaskReachId = Shader.PropertyToID("_MaskReach");
        private static readonly int FxUseMaskId = Shader.PropertyToID("_UseMask");
        private int builtLineHash;
        private bool bounceApplied;
        private Color appliedBounceColor;
        private float appliedBounceStrength = -1f;

        /// <summary>Colours for this glass. Neutral defaults when no theme is assigned.</summary>
        private GlassVisualTheme.Settings Theme =>
            theme != null ? theme.settings : GlassVisualTheme.Settings.Default;
        private int builtSettingsHash;
        private int builtGeometryHash;
        private int appliedSortingHash;

        private void OnEnable()
        {
            built = false;
            builtSettingsHash = 0;
            builtGeometryHash = 0;
            appliedSortingHash = 0;
            bounceApplied = false;
            appliedBounceStrength = -1f;
        }

        private void OnDisable()
        {
            // This renderer owns no generated art, but it is still a child renderer and
            // would otherwise survive while only BottleShell is disabled. Clearing it
            // also guarantees that OnEnable -> Build restores the material, sprite and
            // object-space rect from one authoritative path.
            DisableThinFx();
            ReleaseGeneratedArt();
        }

        private void OnDestroy() => ReleaseGeneratedArt();

        private void OnValidate()
        {
            pixelsPerUnit = Mathf.Max(1f, pixelsPerUnit);
            wallThickness = Mathf.Max(0.001f, wallThickness);
            neckWidth = Mathf.Max(0.02f, neckWidth);
            shadowHeight = Mathf.Max(0.02f, shadowHeight);
            shadowWidth = Mathf.Max(0.05f, shadowWidth);
            floorSeamLightScale = Mathf.Clamp(floorSeamLightScale, 0f, 2f);
            accessoryLightScale = Mathf.Clamp(accessoryLightScale, 0f, 2f);
            bottomRimLightScale = Mathf.Clamp(bottomRimLightScale, 0f, 2f);
            built = false;
        }

        private void LateUpdate()
        {
            LiquidBottle current = GetComponent<LiquidBottle>();
            int wantedHash = SettingsHash(current);
            if (!built || wantedHash != builtSettingsHash || RenderersNeedRefresh(current))
                Build();

            ApplySorting();
            ApplyHighlight();
        }

        /// <summary>
        /// Pushes the authored draw orders onto the renderers. Cheap enough to run every
        /// frame and, unlike a rebuild, it allocates nothing and paints nothing.
        /// </summary>
        private void ApplySorting()
        {
            // Only when the authored orders actually change. Writing them unconditionally
            // every frame would fight LiquidBottle.SetSortingOffset, which lifts the whole
            // vessel in front of its neighbours for the length of a pour.
            int wanted = unchecked((((((backOrder * 397 + frontOrder) * 397 + neckOrder) * 397
                                    + frameOrder) * 397 + thinFxOrder) * 397 + lightOrder) * 397
                                    + shadowOrder);
            if (wanted == appliedSortingHash) return;
            appliedSortingHash = wanted;

            if (back != null) back.sortingOrder = backOrder;
            if (front != null) front.sortingOrder = frontOrder;
            if (neck != null) neck.sortingOrder = neckOrder;
            if (frame != null) frame.sortingOrder = frameOrder;
            if (thinGlassFx != null) thinGlassFx.sortingOrder = thinFxOrder;
            if (glassLight != null) glassLight.sortingOrder = lightOrder;
            if (shadow != null) shadow.sortingOrder = shadowOrder;

            // The offsets LiquidBottle applies are relative to whatever it cached, so it
            // has to look again after the orders underneath it move.
            GetComponent<LiquidBottle>()?.InvalidateRenderers();
        }

        /// <summary>
        /// Scales the additive light layer. Sprite vertex colours are 8 bit, so the
        /// resting look is deliberately below full strength and the selection pulse
        /// spends the headroom that leaves.
        /// </summary>
        private void ApplyHighlight()
        {
            ApplyLiquidBounce();

            if (thinGlassFx != null && thinGlassFx.enabled)
            {
                float thinAmount = Mathf.Clamp01(thinFxIntensity
                    + Mathf.Clamp01(highlight) * thinFxSelectionBoost);
                Color thinWanted = new Color(1f, 1f, 1f, thinAmount);
                if (thinGlassFx.color != thinWanted) thinGlassFx.color = thinWanted;
            }

            if (glassLight == null || !glassLight.enabled) return;
            float amount = Mathf.Clamp01(lightIntensity + Mathf.Clamp01(highlight) * selectionBoost);
            Color wanted = new Color(1f, 1f, 1f, amount);
            if (glassLight.color != wanted) glassLight.color = wanted;
        }

        /// <summary>
        /// Sends the bottom liquid hue to the contour without rebuilding any art. The
        /// value changes while a pour drains/fills, so this belongs beside the cheap
        /// per-frame highlight update rather than in Build().
        /// </summary>
        private void ApplyLiquidBounce()
        {
            if (front == null || bottle == null || front.sharedMaterial == null) return;
            if (UsesAuthoredFront(ResolveFront(bottle))) return;

            GlassVisualTheme.Settings settings = Theme;
            Color source = bottle.VisualBottomColor;
            float profileScale = bottle.Profiled
                ? bottle.profile.liquidBounceScale
                : 1f;
            float strength = source.a > 0.001f
                ? settings.liquidBounceStrength * profileScale
                    * bottle.VisualBottomPresence
                : 0f;
            Color bounce = strength > 0.001f
                ? LiquidPalette.CapFor(source)
                : Color.clear;
            bounce.a = 1f;

            if (bounceApplied && Nearly(appliedBounceColor, bounce)
                              && Mathf.Abs(appliedBounceStrength - strength) < 0.001f)
                return;

            contourBlock ??= new MaterialPropertyBlock();
            front.GetPropertyBlock(contourBlock);
            contourBlock.SetColor(ContourLiquidBounceColorId, bounce);
            contourBlock.SetFloat(ContourLiquidBounceStrengthId, strength);
            front.SetPropertyBlock(contourBlock);

            appliedBounceColor = bounce;
            appliedBounceStrength = strength;
            bounceApplied = true;
        }

        private static bool Nearly(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f
            && Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;

        public void Build()
        {
            bottle = GetComponent<LiquidBottle>();
            if (bottle == null)
            {
                ReleaseGeneratedArt();
                return;
            }

            int geometryHash = GeometryHash(bottle);
            if (geometryHash != builtGeometryHash)
            {
                // LiquidBottle caches its polygon, quad and generated mask. Runtime
                // field changes do not invoke its OnValidate, so invalidate those
                // caches before requesting the replacement shell geometry.
                bottle.Invalidate();
            }

            Vector2[] interior = bottle.InteriorPolygon;
            Rect bounds = bottle.InteriorBounds;
            Sprite authoredFront = ResolveFront(bottle);
            Sprite authoredBack = ResolveBack(bottle);
            Sprite authoredFrame = ResolveFrame(bottle);
            ResolveFloorRange(bottle, bounds, out float opticalFloor, out float visibleBottom);
            float wall = wallThickness <= 1f ? wallThickness * bounds.width : wallThickness;
            Rect padded = Expand(bounds, wall * 2f + 0.02f);

            if (drawShadow && shadowStrength > 0.001f)
            {
                // Sit the shadow under whatever the bottle actually stands on: authored
                // artwork usually reaches below the interior polygon, a stem especially.
                float baseY = authoredFront != null ? authoredFront.bounds.min.y : bounds.yMin;
                float footprintWidth = bounds.width * Mathf.Max(0.05f, shadowWidth);
                bool compositeGround = Theme.wideShadowStrength > 0.001f
                                    || Theme.groundGlowStrength > 0.001f;
                float shadowRectWidth = footprintWidth * (compositeGround ? 1.65f : 1f);
                float shadowRectHeight = shadowHeight * (compositeGround ? 2.2f : 1f);
                float shadowTop = baseY + shadowOffsetY
                                + (compositeGround ? shadowHeight * 0.35f : 0f);
                var shadowRect = new Rect(
                    bounds.center.x - shadowRectWidth * 0.5f,
                    shadowTop - shadowRectHeight,
                    shadowRectWidth,
                    shadowRectHeight);

                shadow = Child("Shadow", shadowOrder, shadow);
                Color shadowTint = Theme.shadowColor;
                shadowTint.a = Mathf.Clamp01(shadowStrength * Theme.shadowStrength);
                Color wideShadowTint = Theme.wideShadowColor;
                wideShadowTint.a = Mathf.Clamp01(
                    shadowStrength * Theme.wideShadowStrength);
                Color groundGlowTint = Theme.groundGlowColor;
                groundGlowTint.a = Mathf.Clamp01(
                    shadowStrength * Theme.groundGlowStrength);
                Sprite nextShadow = BottleArtFactory.Shadow(shadowRect, pixelsPerUnit,
                    shadowTint, wideShadowTint, groundGlowTint);
                AssignSprite(shadow, nextShadow, ref generatedShadow, true);
            }
            else
            {
                DisableLayer("Shadow", ref shadow, ref generatedShadow);
            }

            // No back artwork means the scene shows through, not that a back has to be
            // invented. Treating the empty case as "generate one" is what put a nearly
            // opaque navy disc inside every unfilled glass.
            GlassVisualTheme.Settings themeNow = Theme;
            if (authoredBack != null)
            {
                back = Child("BackGlass", backOrder, back);
                AssignSprite(back, authoredBack, ref generatedBack, false);
            }
            else if (themeNow.backAlpha > 0.001f || themeNow.shoulderStrength > 0.001f)
            {
                back = Child("BackGlass", backOrder, back);
                Sprite nextBack = BottleArtFactory.BackGlass(
                    interior, bounds, pixelsPerUnit, themeNow);
                AssignSprite(back, nextBack, ref generatedBack, true);
            }
            else
            {
                DisableLayer("BackGlass", ref back, ref generatedBack);
            }

            // Restyle once, then reuse. The styler walks every pixel of the source, so
            // this must not run per frame.
            // Shape comes from the drawing, colour from the scene. Same glass, same
            // polygon, different table.
            GlassLineStyler.Style themedLine = lineStyle;
            themedLine.deep = themeNow.contourDark;
            themedLine.bright = themeNow.contourLight;
            themedLine.lightAngle = themeNow.lightDirection;

            int lineHash = themeNow.GlassHash();
            if (lineHash != builtLineHash && styledLine != null)
            {
                // The styler walks every pixel, so its result is cached. A theme change is
                // the one thing that has to throw that cache away.
                ReleaseGeneratedSprite(ref styledLine, front);
            }
            builtLineHash = lineHash;

            // Turning the restyle off has to actually drop what it produced, or the
            // renderer keeps drawing the styled sprite and the setting appears stuck.
            if (!restyleLine && styledLine != null)
                ReleaseGeneratedSprite(ref styledLine, front);

            if (restyleLine && authoredFront != null && styledLine == null)
                styledLine = GlassLineStyler.Create(authoredFront, themedLine);

            front = Child("FrontGlass", frontOrder, front);
            Sprite nextFront = styledLine != null
                ? styledLine
                : authoredFront != null
                ? authoredFront
                : BottleArtFactory.FrontGlass(interior, padded, pixelsPerUnit, wall);
            AssignSprite(front, nextFront, ref generatedFront, authoredFront == null);

            // The supplied glass PNG already contains its approved modelling and light.
            // In this mode it is a literal foreground plate: white vertex colour, no
            // inherited property block, and Unity's ordinary sprite material. Liquid,
            // sorting and pour transforms remain independent underneath it.
            bool authoredFrontPassThrough = UsesAuthoredFront(authoredFront);
            if (authoredFrontPassThrough)
            {
                front.sharedMaterial = themeNow.authoredFrontMaterial;
                front.color = Color.white;
                front.SetPropertyBlock(null);
                bounceApplied = false;
                appliedBounceStrength = -1f;
            }

            // Frame is a literal authored plate: no CPU repaint, no theme tint and no
            // generated texture. It is ideal for neutral glass reflections that must
            // remain above both the dynamic liquid and the coloured front material.
            if (authoredFrame != null)
            {
                frame = Child("Frame", frameOrder, frame);
                frame.sprite = authoredFrame;
                frame.color = Color.white;
                frame.SetPropertyBlock(null);
            }
            else
            {
                DisableFrame();
            }

            // Colour the stroke on the GPU. The material is shared by every vessel and the
            // theme rides in a property block, so switching a scene's theme costs nothing
            // and clones nothing.
            Material contour = bottle.Profiled && bottle.profile.contourMaterial != null
                ? bottle.profile.contourMaterial
                : contourMaterial;
            if (!authoredFrontPassThrough && contour != null && styledLine == null)
            {
                front.sharedMaterial = contour;
                contourBlock ??= new MaterialPropertyBlock();
                front.GetPropertyBlock(contourBlock);
                contourBlock.SetColor(ContourDarkId, themeNow.contourDark);
                contourBlock.SetColor(ContourLightId, themeNow.contourLight);
                contourBlock.SetFloat(LightAngleId, themeNow.lightDirection);
                contourBlock.SetColor(ContourSpecularId, themeNow.glassKeyLight);
                contourBlock.SetVector(InteriorRectId, new Vector4(
                    bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax));
                Vector4 accessoryFx = Vector4.zero;
                if (bottle.Profiled)
                {
                    accessoryFx = new Vector4(
                        bottle.profile.handleGlassLight * accessoryLightScale,
                        bottle.profile.stemFootGlassLight * accessoryLightScale,
                        Mathf.Max(0.005f, bottle.profile.accessoryGlassLightFeather),
                        bottle.profile.stemFootToonStrength);
                }
                contourBlock.SetVector(ContourAccessoryFxId, accessoryFx);
                contourBlock.SetFloat(FxVisibleFloorYId, opticalFloor);
                contourBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                contourBlock.SetFloat(ContourContactStrengthId,
                    themeNow.bottomLensStrength * floorSeamLightScale);
                contourBlock.SetFloat(ContourRimHotspotStrengthId,
                    themeNow.rimHotspotStrength);
                // These are always written, including zero, because the shared material
                // also serves non-toy vessels and an MPB can survive an editor rebuild.
                contourBlock.SetFloat(ContourPaintedToyStrengthId,
                    themeNow.paintedToyStrength);
                contourBlock.SetColor(ContourToyMidColorId, themeNow.toyMidColor);
                contourBlock.SetColor(ContourToyFillColorId, themeNow.toyFillColor);
                contourBlock.SetFloat(ContourBottomRimStrengthId,
                    bottle.Profiled
                        ? bottle.profile.bottomRimGlassLight * bottomRimLightScale
                        : 0f);
                front.SetPropertyBlock(contourBlock);
            }

            // Baked vessels do not need a generated full-silhouette light texture. The
            // thin pass reuses the authored front/frame alpha and its shader admits only
            // side-wall pixels plus a narrow band at the interior floor. Centre liquid
            // pixels therefore never enter the pass, and no source texture is read back.
            if (TryGetThinFx(bottle, out Sprite thinSprite, out Material thinMaterial,
                    out Rect thinInteriorBounds))
            {
                thinGlassFx = Child("ThinGlassFX", thinFxOrder, thinGlassFx);
                thinGlassFx.sprite = thinSprite;
                thinGlassFx.sharedMaterial = thinMaterial;

                thinFxBlock ??= new MaterialPropertyBlock();
                thinGlassFx.GetPropertyBlock(thinFxBlock);
                thinFxBlock.SetVector(InteriorRectId, new Vector4(
                    thinInteriorBounds.xMin, thinInteriorBounds.yMin,
                    thinInteriorBounds.xMax, thinInteriorBounds.yMax));
                thinFxBlock.SetColor(FxKeyColorId, themeNow.glassKeyLight);
                thinFxBlock.SetColor(FxFillColorId, themeNow.glassFillLight);
                thinFxBlock.SetFloat(FxSideStrengthId, themeNow.sideFxStrength);
                // The seam correction is now a bounded recolour in GlassContour. An
                // additive light here made the lower edge white while leaving the upper
                // authored navy row black, producing the two-tone strip it was meant to
                // hide. Keep ThinFX for side reflections only.
                thinFxBlock.SetFloat(FxBottomStrengthId, 0f);
                thinFxBlock.SetFloat(FxVisibleFloorYId, opticalFloor);
                thinFxBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                if (bottle.Profiled && bottle.profile.interiorMask != null)
                {
                    Rect maskRect = bottle.profile.QuadRect;
                    Texture2D maskTexture = bottle.profile.interiorMask;
                    float maskTexel = maskRect.width / Mathf.Max(1, maskTexture.width);
                    float reachLocal = Mathf.Max(wall * 1.15f, maskTexel * 2.5f);
                    thinFxBlock.SetTexture(FxMaskTexId, maskTexture);
                    thinFxBlock.SetVector(FxMaskRectId, new Vector4(
                        maskRect.xMin, maskRect.yMin, maskRect.width, maskRect.height));
                    thinFxBlock.SetVector(FxMaskReachId, new Vector4(
                        reachLocal / Mathf.Max(maskRect.width, 1e-4f),
                        reachLocal / Mathf.Max(maskRect.height, 1e-4f), 0f, 0f));
                    thinFxBlock.SetFloat(FxUseMaskId, 1f);
                }
                else
                {
                    thinFxBlock.SetFloat(FxUseMaskId, 0f);
                }
                thinGlassFx.SetPropertyBlock(thinFxBlock);
                ApplyHighlight();
            }
            else
            {
                DisableThinFx();
            }

            if (drawNeck)
            {
                // Neck runs from just inside the body up to the pour lip.
                float top = bounds.yMax - 0.10f;
                float mouthY = Mathf.Max(top + 0.20f, bottle.mouthLocal.y);
                Vector2[] neckPolygon = VesselFillMath.BottleInterior(
                    neckWidth, mouthY - top, top, 0.04f, neckWidth * 0.45f, 8);
                Rect neckRect = Expand(PolygonBounds(neckPolygon), 0.03f);

                neck = Child("Neck", neckOrder, neck);
                Sprite nextNeck = BottleArtFactory.Solid(neckPolygon, neckRect, pixelsPerUnit,
                    new Color(0.07f, 0.10f, 0.20f, 1f), new Color(0.35f, 0.60f, 0.95f, 1f), 0.05f);
                AssignSprite(neck, nextNeck, ref generatedNeck, true);
            }
            else
            {
                DisableNeck();
            }

            Material lightMaterial = GlassLightMaterial();
            if (drawGlassLight && lightMaterial != null)
            {
                // The procedural front glass is already lit from the same key light, so
                // the layer only has to top it up. Authored artwork is flat and needs
                // the whole thing.
                GlassLightProfile profile = lightProfile;
                if (authoredFront == null)
                {
                    profile.rimStrength *= 0.5f;
                    profile.fillStrength *= 0.5f;
                }

                glassLight = Child("GlassLight", lightOrder, glassLight);
                glassLight.sharedMaterial = lightMaterial;
                Sprite nextLight = BottleArtFactory.GlassLight(
                    interior, padded, pixelsPerUnit, wall, profile);
                AssignSprite(glassLight, nextLight, ref generatedLight, true);
                ApplyHighlight();
            }
            else
            {
                DisableLayer("GlassLight", ref glassLight, ref generatedLight);
            }

            // New child renderers appeared, so the sorting offset cache the bottle keeps
            // for the selection lift no longer covers all of them.
            bottle.InvalidateRenderers();

            builtGeometryHash = geometryHash;
            builtSettingsHash = SettingsHash(bottle);
            built = true;
        }

        private SpriteRenderer Child(string childName, int order, SpriteRenderer cached)
        {
            SpriteRenderer renderer = cached;
            if (renderer == null)
            {
                Transform found = transform.Find(childName);
                if (found == null)
                {
                    var go = new GameObject(childName);
                    go.transform.SetParent(transform, false);
                    found = go.transform;
                }

                renderer = found.GetComponent<SpriteRenderer>();
                if (renderer == null) renderer = found.gameObject.AddComponent<SpriteRenderer>();
            }

            Transform rendererTransform = renderer.transform;
            rendererTransform.SetParent(transform, false);
            rendererTransform.localPosition = Vector3.zero;
            rendererTransform.localRotation = Quaternion.identity;
            rendererTransform.localScale = Vector3.one;
            renderer.sortingOrder = order;
            renderer.sortingLayerName = bottle.sortingLayer;
            renderer.enabled = true;
            return renderer;
        }

        private static void AssignSprite(SpriteRenderer renderer, Sprite sprite,
            ref Sprite generated, bool ownsSprite)
        {
            Sprite previous = generated;
            renderer.sprite = sprite;
            generated = ownsSprite ? sprite : null;

            if (previous != null && previous != sprite)
                BottleArtFactory.ReleaseGeneratedSprite(previous);
        }

        private void DisableNeck()
        {
            if (neck == null)
            {
                Transform found = transform.Find("Neck");
                if (found != null) neck = found.GetComponent<SpriteRenderer>();
            }

            ReleaseGeneratedSprite(ref generatedNeck, neck);
            if (neck != null) neck.enabled = false;
        }

        private void DisableThinFx()
        {
            if (thinGlassFx == null)
            {
                Transform found = transform.Find("ThinGlassFX");
                if (found != null) thinGlassFx = found.GetComponent<SpriteRenderer>();
            }

            if (thinGlassFx == null) return;
            thinGlassFx.enabled = false;
            thinGlassFx.sprite = null;
            thinGlassFx.SetPropertyBlock(null);
        }

        private void DisableFrame()
        {
            if (frame == null)
            {
                Transform found = transform.Find("Frame");
                if (found != null) frame = found.GetComponent<SpriteRenderer>();
            }

            if (frame == null) return;
            frame.enabled = false;
            frame.sprite = null;
            frame.SetPropertyBlock(null);
        }

        /// <summary>Turns an optional layer off and gives its generated sprite back.</summary>
        private void DisableLayer(string childName, ref SpriteRenderer cached, ref Sprite generated)
        {
            if (cached == null)
            {
                Transform found = transform.Find(childName);
                if (found != null) cached = found.GetComponent<SpriteRenderer>();
            }

            ReleaseGeneratedSprite(ref generated, cached);
            if (cached != null) cached.enabled = false;
        }

        private void ReleaseGeneratedArt()
        {
            ReleaseGeneratedSprite(ref generatedBack, back);
            ReleaseGeneratedSprite(ref generatedFront, front);
            ReleaseGeneratedSprite(ref generatedNeck, neck);
            ReleaseGeneratedSprite(ref generatedLight, glassLight);
            ReleaseGeneratedSprite(ref generatedShadow, shadow);
            ReleaseGeneratedSprite(ref styledLine, front);
            built = false;
        }

        private static void ReleaseGeneratedSprite(ref Sprite generated, SpriteRenderer renderer)
        {
            if (generated == null) return;
            Sprite releasing = generated;
            generated = null;
            if (renderer != null && renderer.sprite == releasing) renderer.sprite = null;
            BottleArtFactory.ReleaseGeneratedSprite(releasing);
        }

        private bool RenderersNeedRefresh(LiquidBottle current)
        {
            // sortingOrder is intentionally absent from this health check. PourAnimator
            // raises the complete vessel temporarily; draw order is state, not damaged art,
            // and must never trigger texture/sprite regeneration.
            if (current == null || front == null) return true;

            Sprite authoredBack = ResolveBack(current);
            Sprite authoredFront = ResolveFront(current);
            Sprite authoredFrame = ResolveFrame(current);
            bool wantsAuthoredFront = UsesAuthoredFront(authoredFront);
            // Rear shoulders are independent of the broad empty-glass tint. A theme is
            // allowed to keep the cavity perfectly transparent (backAlpha = 0) while
            // retaining the two local reflections that sell the fake glass. Keeping
            // this predicate identical to Build() also prevents a rebuild every frame.
            bool wantsBack = authoredBack != null || Theme.backAlpha > 0.001f
                                             || Theme.shoulderStrength > 0.001f;

            // Compare ids, not names. Renderer.sortingLayerName builds a fresh string on
            // every get, and this method reads it five times per vessel per frame.
            int layer = SortingLayer.NameToID(current.sortingLayer);
            if (drawNeck && neck == null) return true;
            if (!front.enabled) return true;
            if (wantsAuthoredFront
                && (front.sharedMaterial != Theme.authoredFrontMaterial
                    || front.color != Color.white))
                return true;
            if (wantsBack && (back == null || !back.enabled)) return true;
            if (!wantsBack && back != null && back.enabled) return true;
            if (wantsBack && authoredBack == null && generatedBack == null) return true;
            if (authoredFront == null && generatedFront == null) return true;
            if (drawNeck && generatedNeck == null) return true;
            if (front.sortingLayerID != layer) return true;
            if (wantsBack && back.sortingLayerID != layer) return true;
            if (authoredFrame != null
                && (frame == null || !frame.enabled
                    || frame.sprite != authoredFrame
                    || frame.color != Color.white
                    || frame.sortingLayerID != layer))
                return true;
            if (authoredFrame == null && frame != null && frame.enabled) return true;
            if (drawNeck && (neck.sortingLayerID != layer || !neck.enabled)) return true;
            if (!drawNeck && neck != null && neck.enabled) return true;

            bool wantsThinFx = TryGetThinFx(current, out Sprite wantedThinSprite,
                out Material wantedThinMaterial, out _);
            if (wantsThinFx && (thinGlassFx == null || !thinGlassFx.enabled
                                || thinGlassFx.sprite != wantedThinSprite
                                || thinGlassFx.sharedMaterial != wantedThinMaterial
                                || thinGlassFx.sortingLayerID != layer))
                return true;
            if (!wantsThinFx && thinGlassFx != null && thinGlassFx.enabled) return true;

            bool wantsLight = drawGlassLight && GlassLightMaterial() != null;
            if (wantsLight && (glassLight == null || generatedLight == null
                               || !glassLight.enabled
                               || glassLight.sprite != generatedLight
                               || glassLight.sortingLayerID != layer))
                return true;
            if (!wantsLight && glassLight != null && glassLight.enabled) return true;

            bool wantsShadow = drawShadow && shadowStrength > 0.001f;
            if (wantsShadow && (shadow == null || generatedShadow == null
                                || !shadow.enabled
                                || shadow.sprite != generatedShadow
                                || shadow.sortingLayerID != layer))
                return true;
            if (!wantsShadow && shadow != null && shadow.enabled) return true;

            Sprite wantedBack = authoredBack != null ? authoredBack : generatedBack;

            // styledLine has to be part of this or the check can never pass while the line
            // is being restyled: Build() hands the renderer the styled sprite and this
            // asked for the untouched one, so they never matched, RenderersNeedRefresh
            // returned true every frame, and every vessel repainted its back glass, shadow
            // and neck textures once per frame. Megabytes of garbage a frame for nothing.
            Sprite wantedFront = styledLine != null
                ? styledLine
                : authoredFront != null ? authoredFront : generatedFront;
            if (wantsBack && back.sprite != wantedBack) return true;
            if (front.sprite != wantedFront) return true;
            return drawNeck && neck.sprite != generatedNeck;
        }

        private Sprite ResolveFront(LiquidBottle source)
        {
            if (source == null) return null;
            return source.Profiled ? source.profile.front : source.glassArt;
        }

        private bool UsesAuthoredFront(Sprite authoredFront)
        {
            GlassVisualTheme.Settings settings = Theme;
            return authoredFront != null
                && settings.preserveAuthoredFront
                && settings.authoredFrontMaterial != null;
        }

        private Sprite ResolveBack(LiquidBottle source)
        {
            if (backOverride != null) return backOverride;
            return source != null && source.Profiled ? source.profile.back : null;
        }

        private static Sprite ResolveFrame(LiquidBottle source) =>
            source != null && source.Profiled ? source.profile.frame : null;

        private static void ResolveFloorRange(LiquidBottle source, Rect bounds,
            out float opticalFloor, out float visibleBottom)
        {
            opticalFloor = bounds.yMin;
            visibleBottom = opticalFloor;
            if (source == null) return;

            if (source.Profiled)
            {
                if (source.profile.hasVisibleLiquidFloor)
                    opticalFloor = source.profile.visibleLiquidFloor;
                if (source.profile.visibleBottomLocal > LiquidBottle.Unmeasured + 1f)
                    visibleBottom = source.profile.visibleBottomLocal;
            }
            else if (source.visibleBottomLocal > LiquidBottle.Unmeasured + 1f)
            {
                visibleBottom = source.visibleBottomLocal;
            }
        }

        private Material GlassLightMaterial()
        {
            return glassLightMaterial != null
                ? glassLightMaterial
                : BottleArtFactory.GlassLightMaterial();
        }

        private int SettingsHash(LiquidBottle source)
        {
            unchecked
            {
                int hash = GeometryHash(source);
                hash = hash * 31 + pixelsPerUnit.GetHashCode();
                hash = hash * 31 + wallThickness.GetHashCode();
                hash = hash * 31 + neckWidth.GetHashCode();
                hash = hash * 31 + drawNeck.GetHashCode();
                hash = hash * 31 + ObjectId(backOverride);
                hash = hash * 31 + ObjectId(thinGlassFxMaterial);
                hash = hash * 31 + ObjectId(thinGlassFxSprite);
                hash = hash * 31 + floorSeamLightScale.GetHashCode();
                hash = hash * 31 + accessoryLightScale.GetHashCode();
                hash = hash * 31 + bottomRimLightScale.GetHashCode();
                hash = hash * 31 + ObjectId(source != null ? source.profile : null);
                if (source != null && source.Profiled)
                {
                    hash = hash * 31 + ObjectId(source.profile.frame);
                    hash = hash * 31 + ObjectId(source.profile.front);
                    hash = hash * 31 + ObjectId(source.profile.contourMaterial);
                    hash = hash * 31 + ObjectId(source.profile.thinGlassFxMaterial);
                    hash = hash * 31 + source.profile.handleGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.stemFootGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.stemFootToonStrength.GetHashCode();
                    hash = hash * 31 + source.profile.accessoryGlassLightFeather.GetHashCode();
                    hash = hash * 31 + source.profile.bottomRimGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.liquidBounceScale.GetHashCode();
                }
                // Sorting orders are deliberately absent. They decide the order things
                // are drawn in and nothing about what is drawn, so folding them into the
                // rebuild hash meant that nudging a vessel in front of its neighbours
                // repainted every texture it owns.
                hash = hash * 31 + drawGlassLight.GetHashCode();
                hash = hash * 31 + ObjectId(glassLightMaterial);
                hash = hash * 31 + lightProfile.Hash();
                // Panel-only theme changes belong to GlassThemeBinder. They must not
                // regenerate every vessel's back glass and contact shadow.
                hash = hash * 31 + Theme.GlassHash();
                hash = hash * 31 + restyleLine.GetHashCode();
                hash = hash * 31 + drawShadow.GetHashCode();
                hash = hash * 31 + shadowStrength.GetHashCode();
                hash = hash * 31 + shadowHeight.GetHashCode();
                hash = hash * 31 + shadowWidth.GetHashCode();
                hash = hash * 31 + shadowOffsetY.GetHashCode();
                hash = hash * 31 + (source != null && source.sortingLayer != null
                    ? source.sortingLayer.GetHashCode()
                    : 0);
                return hash;
            }
        }

        private static int GeometryHash(LiquidBottle source)
        {
            if (source == null) return 0;
            unchecked
            {
                int hash = source.GetInstanceID();
                hash = hash * 31 + source.interiorWidth.GetHashCode();
                hash = hash * 31 + source.interiorHeight.GetHashCode();
                hash = hash * 31 + source.interiorBottom.GetHashCode();
                hash = hash * 31 + source.bottomCornerRadius.GetHashCode();
                hash = hash * 31 + source.topCornerRadius.GetHashCode();
                hash = hash * 31 + source.mouthLocal.GetHashCode();
                hash = hash * 31 + source.maskPixelsPerUnit.GetHashCode();
                hash = hash * 31 + ObjectId(source.maskSprite);
                Vector2[] custom = source.customInteriorPolygon;
                int customCount = custom != null ? custom.Length : 0;
                hash = hash * 31 + customCount;
                for (int i = 0; i < customCount; i++)
                    hash = hash * 31 + custom[i].GetHashCode();
                return hash;
            }
        }

        private static int ObjectId(Object value) => value != null ? value.GetInstanceID() : 0;

        private bool TryGetThinFx(LiquidBottle source, out Sprite sprite,
            out Material material, out Rect interiorBounds)
        {
            sprite = null;
            material = null;
            interiorBounds = default;
            if (source == null) return false;
            // The contact correction lives in the contour pass. ThinFX now exists only
            // for authored-alpha side reflections, so do not keep an invisible renderer
            // and draw call alive when those reflections are disabled.
            GlassVisualTheme.Settings settings = Theme;
            if (settings.sideFxStrength <= 0.001f)
                return false;

            if (source.Profiled)
            {
                VesselProfile profile = source.profile;
                material = profile.thinGlassFxMaterial;
                sprite = profile.frame != null ? profile.frame : profile.front;
                interiorBounds = profile.interiorBounds;
                return sprite != null && material != null;
            }

            // Loose build-scene vessels already carry their exact local polygon. Reuse
            // that geometry rather than the padded liquid quad rect, otherwise the FX
            // mask drifts and the bottom lens can reach into art below the actual bowl.
            material = thinGlassFxMaterial;
            sprite = thinGlassFxSprite != null
                ? thinGlassFxSprite
                : ResolveFront(source) != null
                ? ResolveFront(source)
                : front != null
                ? front.sprite
                : generatedFront;
            Vector2[] polygon = source.InteriorPolygon;
            if (polygon != null && polygon.Length >= 3)
                interiorBounds = PolygonBounds(polygon);
            return sprite != null && material != null;
        }

        private static Rect Expand(Rect r, float amount)
        {
            return new Rect(r.xMin - amount, r.yMin - amount,
                r.width + amount * 2f, r.height + amount * 2f);
        }

        private static Rect PolygonBounds(Vector2[] polygon)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 p = polygon[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
