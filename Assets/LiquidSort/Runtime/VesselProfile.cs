using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Everything one glass shape is, baked.
    ///
    /// A vessel used to configure itself the first time it was needed: trace the drawing,
    /// paint its own mask and shell textures, then bisect its interior polygon a few
    /// thousand times a frame to find where the waterlines go. All of that is a function
    /// of the drawing alone, so none of it belongs in Play Mode. It is computed once by
    /// <c>Tools &gt; LiquidSort &gt; Bake Vessel Profile</c> and stored here.
    ///
    /// Adding a glass is therefore a new asset and a bake, never new code: a profile
    /// carries its own art, its own interior, its own capacity, its own look, and its own
    /// tables. Nothing about a particular glass is written in C#.
    ///
    /// The tables are the part worth explaining. Two questions get asked every frame
    /// while a glass moves, and both used to be answered by search:
    ///
    ///   where is the waterline that leaves <c>fraction</c> of the interior below it,
    ///   when the glass is tilted by <c>angle</c>?              -> <see cref="tilted"/>
    ///
    ///   what fraction of the interior is below <c>level</c>,
    ///   with the glass upright?                                -> <see cref="upright"/>
    ///
    /// Sampling both onto a grid and interpolating turns a bisection over a polygon into
    /// four array reads, and turns a per-frame allocation into none.
    /// </summary>
    [CreateAssetMenu(menuName = "LiquidSort/Vessel Profile", fileName = "VesselProfile")]
    public sealed class VesselProfile : ScriptableObject
    {
        [Header("Art")]
        [Tooltip("The drawing the interior is traced from, and the glass drawn in front of the liquid.")]
        public Sprite front;
        [Tooltip("Optional immutable source used only to trace/bake vessel geometry. Leave empty to trace Front. This lets a visually repaired front sprite change optical transmission without moving the liquid mask, mouth, or volume polygon.")]
        public Sprite traceSource;
        [Tooltip("Optional body behind the liquid. Empty leaves the interior showing the backdrop.")]
        public Sprite back;
        [Tooltip("Optional overlay above everything: rim, highlights, anything painted rather than lit.")]
        public Sprite frame;
        [Tooltip("Shared liquid material for every vessel using this profile. Per bottle values ride in a MaterialPropertyBlock, so one asset serves the whole set and nothing is cloned.")]
        public Material liquidMaterial;
        [Tooltip("Shared contour material. Recolours the glass drawing from the theme at draw time, so no sprite is repainted on the CPU and the source texture needs no Read/Write.")]
        public Material contourMaterial;
        [Tooltip("Optional thin additive edge/lens material. It reuses front/frame alpha, so profiled vessels get side light and a bottom bounce without generating or reading a texture at runtime.")]
        public Material thinGlassFxMaterial;
        [Header("Glass-part reflections")]
        [Tooltip("Artist-authored highlight strength on glass outside the liquid cavity horizontally, such as a mug handle.")]
        [Range(0f, 1f)] public float handleGlassLight;
        [Tooltip("Artist-authored highlight strength below the liquid cavity, such as a cocktail stem and foot.")]
        [Range(0f, 1f)] public float stemFootGlassLight;
        [Tooltip("Replaces smooth photorealistic shading below the bowl with a few clean toy-like colour bands. Intended for stemmed glasses; zero preserves the authored drawing.")]
        [Range(0f, 1f)] public float stemFootToonStrength;
        [Tooltip("Softness of the cavity-to-accessory boundary as a share of interior width.")]
        [Range(0.005f, 0.10f)] public float accessoryGlassLightFeather = 0.025f;
        [Tooltip("Cool highlight on the outermost lower silhouette. Zero keeps the authored bottom untouched.")]
        [Range(0f, 1f)] public float bottomRimGlassLight;
        [Tooltip("Per-vessel share of the scene theme's liquid-colour reflection. Keep stemmed glass low so the liquid hue does not travel down the stem or around the foot.")]
        [Range(0f, 1f)] public float liquidBounceScale = 1f;
        [Header("Baked art")]
        [Tooltip("Interior coverage, baked. The liquid shader clips against this.")]
        public Texture2D interiorMask;

        [Header("Interior correction")]
        [Tooltip("Clips the baked liquid cavity to a sloped right wall. Enable for a right-handled drawing whose transparent handle hole joins the body cavity during tracing.")]
        public bool clipRightInterior;
        [Tooltip("Right interior-wall x at local y=0.")]
        public float rightInteriorXAtY0;
        [Tooltip("How much the right interior-wall limit moves in x for one local unit of y.")]
        public float rightInteriorSlope;

        [Header("Pour pose")]
        [Tooltip("Only pose belongs to the vessel. Timing stays shared so every transfer keeps the same rhythm.")]
        public PourPose pourPose = PourPose.Default;

        [Header("Contents")]
        [Range(1, 8)] public int capacity = 2;

        [Header("Baked geometry, in vessel local units")]
        public Vector2[] interiorPolygon;
        public Rect interiorBounds;
        public float polygonArea;
        [Tooltip("Where liquid leaves the vessel. x is 0 for a narrow neck.")]
        public Vector2 mouthLocal;
        [Tooltip("Half width of an open rim. Zero means a single centred mouth.")]
        public float mouthHalfWidth;
        [Tooltip("Height under which the drawing hides the liquid behind its own outline.")]
        public float visibleBottomLocal;
        [Tooltip("True after the baker produced the automatic optical-height table for this artwork.")]
        public bool hasVisibleLiquidFloor;
        [Tooltip("Diagnostic start of the baked optical-height map. Runtime uses the full table below, not this single value.")]
        public float visibleLiquidFloor;

        [Header("Look")]
        [Tooltip("Top face ellipse half depth as a fraction of the liquid's current span.")]
        [Range(0.02f, 0.20f)] public float surfaceBulge = 0.135f;
        [Range(0.01f, 0.30f)] public float maxCapDepth = 0.075f;
        [Tooltip("Share of the interior height left empty above a full vessel. Measured from the top of the interior polygon, which on a wide mouthed glass is the back of the rim ellipse; the front of that ellipse is much lower, so this has to be generous or the liquid draws over the mouth.")]
        [Range(0f, 0.50f)] public float brimHeadroom = 0.34f;
        [Tooltip("Gap between the liquid surface and the brim, measured in top-face depths. This is the one that keeps a set of different vessels consistent.")]
        [Range(0f, 8f)] public float brimGapCaps = 3.2f;
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("1 gives every unit the same height. 0 gives every unit the same volume, which makes the top slice of a cone a sliver.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [HideInInspector, Tooltip("Legacy value retained for existing profile serialization. Fixed cumulative unit crests supersede this state-dependent lift.")]
        [Range(0f, 0.25f)] public float singleUnitSurfaceLift;
        [Tooltip("Curvature of shared colour boundaries. The measured Magic Sort look uses 1 with a shallow 0.098 depth; 0 is available only for deliberately straight bands.")]
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;
        [HideInInspector, Tooltip("Legacy value retained for existing profile serialization. Fixed cumulative unit crests are always stable now.")]
        public bool lockJunctionLayoutToLogicalVolume;

        [Header("Baked tables")]
        public TiltTable tilted;
        public UprightTable upright;

        /// <summary>
        /// The rect the liquid quad and the interior mask share. Both the bake and the
        /// runtime derive it from the same place, because a mask rasterised into one rect
        /// and sampled through another is off by exactly the padding.
        /// </summary>
        public Rect QuadRect
        {
            get
            {
                float pad = 0.02f + interiorBounds.height * maxCapDepth
                            * Mathf.Clamp01(surfaceAllowance) * 1.15f;
                return new Rect(interiorBounds.xMin - pad, interiorBounds.yMin - pad,
                    interiorBounds.width + pad * 2f, interiorBounds.height + pad * 2f);
            }
        }

        public bool IsBaked => interiorPolygon != null && interiorPolygon.Length >= 3
                               && tilted != null && tilted.IsValid
                               && upright != null && upright.IsValid;

        /// <summary>
        /// Small art-specific corrections for a pour. A coupe should not be lifted or
        /// tilted like a tall tumbler, but both still use the same 0.3 / 0.3 / 0.3 beat.
        /// Lengths are authored in the same local units as the baked interior and are
        /// multiplied by the instance scale at runtime.
        /// </summary>
        [System.Serializable]
        public struct PourPose
        {
            [Tooltip("Use these values instead of PourAnimator's neutral fallback.")]
            public bool enabled;
            [Tooltip("Vertical gap between this vessel's mouth and an incoming source mouth.")]
            [Range(0.05f, 0.60f)] public float receiveClearance;
            [Tooltip("Height of the carry arc above a direct trip to the target.")]
            [Range(0.02f, 0.50f)] public float carryArc;
            [Tooltip("Degrees beyond the first geometrically valid spill angle.")]
            [Range(0f, 30f)] public float extraTilt;
            [Tooltip("Visual cap for the last part of the pour.")]
            [Range(70f, 120f)] public float maximumTilt;
            [Tooltip("Main falling column width.")]
            [Range(0.02f, 0.20f)] public float streamWidth;
            [Tooltip("Width at the leading edge and final drop.")]
            [Range(0.01f, 0.16f)] public float streamTipWidth;

            public static PourPose Default => new PourPose
            {
                enabled = false,
                receiveClearance = 0.26f,
                carryArc = 0.20f,
                extraTilt = 8f,
                maximumTilt = 96f,
                streamWidth = 0.085f,
                streamTipWidth = 0.055f
            };
        }

        /// <summary>
        /// Waterline, chord centre and half chord for every (tilt, fill) pair, on a grid.
        ///
        /// Three values per cell rather than one, because the shader needs the chord as
        /// well as the height, and finding the chord meant a second walk of the polygon.
        /// </summary>
        [System.Serializable]
        public sealed class TiltTable
        {
            public int angleSteps;
            public int fillSteps;
            public float maxAngle;
            public float[] level;
            public float[] centreX;
            public float[] halfChord;
            [Tooltip("Most the vessel may hold at each tilt before its top face would cross the brim. Stored as an area fraction rather than a height, so clamping it leaves the waterline and its chord agreeing with each other.")]
            public float[] ceilingFill;

            public bool IsValid =>
                angleSteps > 1 && fillSteps > 1 && level != null &&
                level.Length == angleSteps * fillSteps &&
                centreX != null && centreX.Length == level.Length &&
                halfChord != null && halfChord.Length == level.Length &&
                ceilingFill != null && ceilingFill.Length == angleSteps;

            /// <summary>Bilinear read. Angle in degrees, fill as a fraction of the interior area.</summary>
            public void Sample(float angle, float fill, out float outLevel, out float outCentre, out float outHalf)
            {
                float a = Mathf.InverseLerp(-maxAngle, maxAngle, Mathf.Clamp(angle, -maxAngle, maxAngle))
                          * (angleSteps - 1);
                float f = Mathf.Clamp01(fill) * (fillSteps - 1);

                int a0 = Mathf.Clamp((int)a, 0, angleSteps - 1);
                int a1 = Mathf.Min(a0 + 1, angleSteps - 1);
                int f0 = Mathf.Clamp((int)f, 0, fillSteps - 1);
                int f1 = Mathf.Min(f0 + 1, fillSteps - 1);
                float ta = a - a0;
                float tf = f - f0;

                int i00 = a0 * fillSteps + f0, i01 = a0 * fillSteps + f1;
                int i10 = a1 * fillSteps + f0, i11 = a1 * fillSteps + f1;

                outLevel = Blend(level, i00, i01, i10, i11, ta, tf);
                outCentre = Blend(centreX, i00, i01, i10, i11, ta, tf);
                outHalf = Blend(halfChord, i00, i01, i10, i11, ta, tf);
            }

            public float CeilingFillAt(float angle)
            {
                float a = Mathf.InverseLerp(-maxAngle, maxAngle, Mathf.Clamp(angle, -maxAngle, maxAngle))
                          * (angleSteps - 1);
                int a0 = Mathf.Clamp((int)a, 0, angleSteps - 1);
                int a1 = Mathf.Min(a0 + 1, angleSteps - 1);
                return Mathf.Lerp(ceilingFill[a0], ceilingFill[a1], a - a0);
            }

            private static float Blend(float[] table, int i00, int i01, int i10, int i11, float ta, float tf) =>
                Mathf.Lerp(Mathf.Lerp(table[i00], table[i01], tf),
                           Mathf.Lerp(table[i10], table[i11], tf), ta);
        }

        /// <summary>
        /// The upright vessel sampled by height: how much of it is below a given level,
        /// and how deep the top face ellipse is there. Everything that decides where the
        /// bands go is a combination of these two and a couple of baked scalars.
        /// </summary>
        [System.Serializable]
        public sealed class UprightTable
        {
            public int steps;
            public float minY;
            public float maxY;
            [Tooltip("Lowest level the liquid still reads at. Below this the vessel's own outline covers it.")]
            public float floorY;
            [Tooltip("Highest waterline a full vessel is allowed, with the brim headroom already applied.")]
            public float ceilingY;
            public float[] areaFraction;
            public float[] capHalfDepth;
            [Tooltip("Tilt at which a vessel of this fill first reaches its mouth, per fill step.")]
            public float[] spillAngle;
            [Tooltip("Cumulative visible liquid height at every upright level. Baked from the authored front art, so an opaque or translucent base does not crush the lowest colour band.")]
            public float[] visibleHeight;
            [Tooltip("Inverse of visibleHeight, sampled from zero to totalVisibleHeight. This turns a desired on-screen height into a vessel-local waterline without reading the source texture at runtime.")]
            public float[] levelAtVisibleFraction;
            public float totalVisibleHeight;

            public bool IsValid =>
                steps > 1 && areaFraction != null && areaFraction.Length == steps
                && capHalfDepth != null && capHalfDepth.Length == steps
                && spillAngle != null && spillAngle.Length == steps;

            public bool HasVisibleHeightMap =>
                steps > 1 && totalVisibleHeight > 1e-5f
                && visibleHeight != null && visibleHeight.Length == steps
                && levelAtVisibleFraction != null && levelAtVisibleFraction.Length == steps;

            public float AreaFractionAt(float level) => Read(areaFraction, level);

            public float CapHalfDepthAt(float level) => Read(capHalfDepth, level);

            /// <summary>
            /// Amount of liquid height the player can actually see below this level.
            /// Unlike geometric height, authored glass laid over the liquid contributes
            /// only by its transmission, so a thick base contributes almost nothing.
            /// </summary>
            public float VisibleHeightAt(float level)
            {
                if (!HasVisibleHeightMap)
                    return Mathf.Clamp(level - minY, 0f, Mathf.Max(0f, maxY - minY));
                return Read(visibleHeight, level);
            }

            /// <summary>
            /// Inverse of <see cref="VisibleHeightAt"/>. The inverse is baked too, so a
            /// frame only performs two array reads and never searches or allocates.
            /// </summary>
            public float LevelAtVisibleHeight(float height)
            {
                if (!HasVisibleHeightMap)
                    return Mathf.Clamp(minY + height, minY, maxY);

                float t = Mathf.Clamp01(height / totalVisibleHeight) * (steps - 1);
                int i0 = Mathf.Clamp((int)t, 0, steps - 1);
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(levelAtVisibleFraction[i0],
                    levelAtVisibleFraction[i1], t - i0);
            }

            /// <summary>Spill tilt for a fill fraction of the interior area.</summary>
            public float SpillAngleFor(float fill)
            {
                float t = Mathf.Clamp01(fill) * (steps - 1);
                int i0 = Mathf.Clamp((int)t, 0, steps - 1);
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(spillAngle[i0], spillAngle[i1], t - i0);
            }

            private float Read(float[] table, float level)
            {
                float t = Mathf.InverseLerp(minY, maxY, level) * (steps - 1);
                t = Mathf.Clamp(t, 0f, steps - 1);
                int i0 = (int)t;
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(table[i0], table[i1], t - i0);
            }
        }
    }
}
