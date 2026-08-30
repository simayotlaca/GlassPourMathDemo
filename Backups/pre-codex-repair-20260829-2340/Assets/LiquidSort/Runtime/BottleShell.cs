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
    ///   GlassLight (order 7)   additive highlight over all of it
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
        [Tooltip("Optional authored artwork in front of the liquid. Empty uses procedural rim art.")]
        public Sprite frontOverride;
        [Tooltip("Repaint the drawing's stroke on Awake instead of using it as drawn. Every number below is live; push them around until it looks right.")]
        public bool restyleLine;
        public GlassLineStyler.Style lineStyle = GlassLineStyler.Style.Default;
        public int backOrder = 0;
        public int frontOrder = 5;
        public int neckOrder = 6;

        [Header("Glass light")]
        [Tooltip("Additive highlight over the whole bottle. This is the layer that makes it read as glass.")]
        // Off by default: an additive pass over the whole silhouette washes the rim
        // out to grey, and the reference art has no such bloom. Turn it on per bottle if
        // you want it.
        public bool drawGlassLight = true;
        public int lightOrder = 7;
        [Tooltip("Serialized additive material. Empty falls back to Shader.Find, which only resolves shaders a build already includes.")]
        public Material glassLightMaterial;
        public GlassLightProfile lightProfile = GlassLightProfile.Reference;
        [Tooltip("Resting strength of the light layer. The rest is headroom for the selection pulse.")]
        [Range(0f, 1f)] public float lightIntensity = 0.85f;
        [Range(0f, 1f)] public float selectionBoost = 0.15f;

        [Header("Contact shadow")]
        public bool drawShadow;   // reference bottles cast none
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
        private SpriteRenderer glassLight;
        private SpriteRenderer shadow;
        private Sprite generatedBack;
        private Sprite generatedFront;
        private Sprite generatedNeck;
        private Sprite generatedLight;
        private Sprite generatedShadow;
        private bool built;
        private Sprite styledLine;
        private int builtSettingsHash;
        private int builtGeometryHash;
        private int appliedSortingHash;

        private void OnEnable()
        {
            built = false;
            builtSettingsHash = 0;
            builtGeometryHash = 0;
            appliedSortingHash = 0;
        }

        private void OnDisable() => ReleaseGeneratedArt();

        private void OnDestroy() => ReleaseGeneratedArt();

        private void OnValidate()
        {
            pixelsPerUnit = Mathf.Max(1f, pixelsPerUnit);
            wallThickness = Mathf.Max(0.001f, wallThickness);
            neckWidth = Mathf.Max(0.02f, neckWidth);
            shadowHeight = Mathf.Max(0.02f, shadowHeight);
            shadowWidth = Mathf.Max(0.05f, shadowWidth);
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
            int wanted = unchecked((((backOrder * 397 + frontOrder) * 397 + neckOrder) * 397
                                    + lightOrder) * 397 + shadowOrder);
            if (wanted == appliedSortingHash) return;
            appliedSortingHash = wanted;

            if (back != null) back.sortingOrder = backOrder;
            if (front != null) front.sortingOrder = frontOrder;
            if (neck != null) neck.sortingOrder = neckOrder;
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
            if (glassLight == null || !glassLight.enabled) return;
            float amount = Mathf.Clamp01(lightIntensity + Mathf.Clamp01(highlight) * selectionBoost);
            Color wanted = new Color(1f, 1f, 1f, amount);
            if (glassLight.color != wanted) glassLight.color = wanted;
        }

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
            float wall = wallThickness <= 1f ? wallThickness * bounds.width : wallThickness;
            Rect padded = Expand(bounds, wall * 2f + 0.02f);

            if (drawShadow && shadowStrength > 0.001f)
            {
                // Sit the shadow under whatever the bottle actually stands on: authored
                // artwork usually reaches below the interior polygon, a stem especially.
                float baseY = frontOverride != null ? frontOverride.bounds.min.y : bounds.yMin;
                float halfSpan = bounds.width * 0.5f * Mathf.Max(0.05f, shadowWidth);
                var shadowRect = new Rect(
                    bounds.center.x - halfSpan,
                    baseY + shadowOffsetY - shadowHeight * 0.5f,
                    halfSpan * 2f,
                    shadowHeight);

                shadow = Child("Shadow", shadowOrder, shadow);
                Sprite nextShadow = BottleArtFactory.Shadow(shadowRect, pixelsPerUnit,
                    new Color(0.008f, 0.012f, 0.035f, Mathf.Clamp01(shadowStrength)));
                AssignSprite(shadow, nextShadow, ref generatedShadow, true);
            }
            else
            {
                DisableLayer("Shadow", ref shadow, ref generatedShadow);
            }

            back = Child("BackGlass", backOrder, back);
            Sprite nextBack = backOverride != null
                ? backOverride
                : BottleArtFactory.BackGlass(interior, bounds, pixelsPerUnit);
            AssignSprite(back, nextBack, ref generatedBack, backOverride == null);

            // Restyle once, then reuse. The styler walks every pixel of the source, so
            // this must not run per frame.
            if (restyleLine && frontOverride != null && styledLine == null)
                styledLine = GlassLineStyler.Create(frontOverride, lineStyle);

            front = Child("FrontGlass", frontOrder, front);
            Sprite nextFront = styledLine != null
                ? styledLine
                : frontOverride != null
                ? frontOverride
                : BottleArtFactory.FrontGlass(interior, padded, pixelsPerUnit, wall);
            AssignSprite(front, nextFront, ref generatedFront, frontOverride == null);

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
                if (frontOverride == null)
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
            if (current == null || back == null || front == null) return true;
            if (drawNeck && neck == null) return true;
            if (!back.enabled || !front.enabled) return true;
            if (backOverride == null && generatedBack == null) return true;
            if (frontOverride == null && generatedFront == null) return true;
            if (drawNeck && generatedNeck == null) return true;
            if (back.sortingOrder != backOrder || front.sortingOrder != frontOrder) return true;
            if (back.sortingLayerName != current.sortingLayer
                || front.sortingLayerName != current.sortingLayer)
                return true;
            if (drawNeck && (neck.sortingOrder != neckOrder
                             || neck.sortingLayerName != current.sortingLayer
                             || !neck.enabled))
                return true;
            if (!drawNeck && neck != null && neck.enabled) return true;

            bool wantsLight = drawGlassLight && GlassLightMaterial() != null;
            if (wantsLight && (glassLight == null || generatedLight == null
                               || !glassLight.enabled
                               || glassLight.sprite != generatedLight
                               || glassLight.sortingOrder != lightOrder
                               || glassLight.sortingLayerName != current.sortingLayer))
                return true;
            if (!wantsLight && glassLight != null && glassLight.enabled) return true;

            bool wantsShadow = drawShadow && shadowStrength > 0.001f;
            if (wantsShadow && (shadow == null || generatedShadow == null
                                || !shadow.enabled
                                || shadow.sprite != generatedShadow
                                || shadow.sortingOrder != shadowOrder
                                || shadow.sortingLayerName != current.sortingLayer))
                return true;
            if (!wantsShadow && shadow != null && shadow.enabled) return true;

            Sprite wantedBack = backOverride != null ? backOverride : generatedBack;
            Sprite wantedFront = frontOverride != null ? frontOverride : generatedFront;
            if (back.sprite != wantedBack || front.sprite != wantedFront) return true;
            return drawNeck && neck.sprite != generatedNeck;
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
                hash = hash * 31 + ObjectId(frontOverride);
                // Sorting orders are deliberately absent. They decide the order things
                // are drawn in and nothing about what is drawn, so folding them into the
                // rebuild hash meant that nudging a vessel in front of its neighbours
                // repainted every texture it owns.
                hash = hash * 31 + drawGlassLight.GetHashCode();
                hash = hash * 31 + ObjectId(glassLightMaterial);
                hash = hash * 31 + lightProfile.Hash();
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
