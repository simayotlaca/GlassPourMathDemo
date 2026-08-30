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
        [Tooltip("Optional body behind the liquid. Empty leaves the interior showing the backdrop.")]
        public Sprite back;
        [Tooltip("Optional overlay above everything: rim, highlights, anything painted rather than lit.")]
        public Sprite frame;
        [Tooltip("Interior coverage, baked. The liquid shader clips against this.")]
        public Texture2D interiorMask;

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

        [Header("Look")]
        [Tooltip("Top face ellipse half depth as a fraction of the liquid's current span.")]
        [Range(0.02f, 0.20f)] public float surfaceBulge = 0.135f;
        [Range(0.01f, 0.30f)] public float maxCapDepth = 0.075f;
        [Tooltip("Share of the interior height left empty above a full vessel. Measured from the top of the interior polygon, which on a wide mouthed glass is the back of the rim ellipse; the front of that ellipse is much lower, so this has to be generous or the liquid draws over the mouth.")]
        [Range(0f, 0.50f)] public float brimHeadroom = 0.34f;
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("1 gives every unit the same height. 0 gives every unit the same volume, which makes the top slice of a cone a sliver.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;

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

            public bool IsValid =>
                steps > 1 && areaFraction != null && areaFraction.Length == steps
                && capHalfDepth != null && capHalfDepth.Length == steps
                && spillAngle != null && spillAngle.Length == steps;

            public float AreaFractionAt(float level) => Read(areaFraction, level);

            public float CapHalfDepthAt(float level) => Read(capHalfDepth, level);

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
