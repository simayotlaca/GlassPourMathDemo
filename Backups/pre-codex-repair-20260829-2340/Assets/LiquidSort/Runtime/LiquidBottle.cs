using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>
    /// One bottle: the puzzle state (a stack of coloured units) plus the single
    /// quad that draws the liquid through <c>LiquidSort/BottleLiquid</c>.
    ///
    /// The liquid is never simulated. Every frame we ask <see cref="VesselFillMath"/>
    /// for the waterlines that keep the volume constant at the current tilt, and hand
    /// those heights to the shader. That is the whole trick.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class LiquidBottle : MonoBehaviour
    {
        public const int MaxBands = 8;

        // Resolved once. Setting a property by name costs a string hash and a dictionary
        // lookup every call, and a moving bottle sets fifteen of them every frame.
        private static readonly int BandColorId = Shader.PropertyToID("_BandColor");
        private static readonly int BandCapId = Shader.PropertyToID("_BandCap");
        private static readonly int BandInfoId = Shader.PropertyToID("_BandInfo");
        private static readonly int BandCountId = Shader.PropertyToID("_BandCount");
        private static readonly int AngleId = Shader.PropertyToID("_Angle");
        private static readonly int BulgeId = Shader.PropertyToID("_Bulge");
        private static readonly int InnerCurveId = Shader.PropertyToID("_InnerCurve");
        private static readonly int InnerBulgeId = Shader.PropertyToID("_InnerBulge");
        private static readonly int InnerMaxId = Shader.PropertyToID("_InnerMax");
        private static readonly int BulgeMaxId = Shader.PropertyToID("_BulgeMax");
        private static readonly int WaveId = Shader.PropertyToID("_Wave");
        private static readonly int CapFlashId = Shader.PropertyToID("_CapFlash");
        private static readonly int MaskUvId = Shader.PropertyToID("_MaskUV");
        private static readonly int QuadSizeId = Shader.PropertyToID("_QuadSize");
        private static readonly int InteriorId = Shader.PropertyToID("_Interior");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        public const float Unmeasured = -9999f;

        [Header("Interior shape (bottle local space, pivot at the bottle base)")]
        public float interiorWidth = 0.76f;
        public float interiorHeight = 2.00f;
        public float interiorBottom = 0.16f;
        public float bottomCornerRadius = 0.34f;
        public float topCornerRadius = 0.24f;
        public Vector2 mouthLocal = new Vector2(0f, 2.62f);
        [Tooltip("Optional exact interior silhouette. Three or more points replace the rounded bottle shape.")]
        public Vector2[] customInteriorPolygon;
        [Tooltip("Half width of an open rim. Zero keeps a single centred bottle mouth.")]
        public float mouthHalfWidth;

        [Header("Vessel")]
        [Tooltip("Baked shape, art and tables for this glass. With one assigned nothing is traced, no texture is generated and no waterline is searched for at runtime; every answer is read out of the asset. Leave empty to fall back to the loose fields below.")]
        public VesselProfile profile;

        [Header("Glass artwork")]
        [Tooltip("The drawing this bottle lives inside. Its texture needs Read/Write enabled. Leave empty to use the shape fields above instead.")]
        public Sprite glassArt;
        [Tooltip("Trace the interior off glassArt when the scene starts. Every glass shape configures itself this way, so there is nothing to set up per bottle.")]
        public bool fitToArt = true;

        [Header("Contents")]
        public int capacity = 4;
        [SerializeField] private List<Color> units = new List<Color>();

        [Header("Rendering")]
        public Sprite maskSprite;
        public Material liquidMaterial;
        public int sortingOrder = 1;
        public string sortingLayer = "Default";
        public float maskPixelsPerUnit = 160f;
        [Tooltip("Top face ellipse half-depth as a fraction of the current horizontal liquid span.")]
        [Range(0.02f, 0.20f)] public float surfaceBulge = 0.135f;   // measured off the reference art
        [Tooltip("Cap depth ceiling as a fraction of interior height. Stops a wide bowl from getting a top face as deep as itself.")]
        [Range(0.01f, 0.30f)] public float maxCapDepth = 0.075f;
        [Tooltip("Chord fraction below which the vessel's bottom is treated as hidden behind its own outline. Bands are shared out above this, so the lowest colour is not crushed into a tip nobody can see.")]
        [Range(0f, 0.8f)] public float visibleBottomChord = 0.40f;
        [Tooltip("Local height under which the drawing itself hides the liquid, measured off the artwork by the fitter. -9999 means unmeasured, in which case the chord estimate above is used.")]
        public float visibleBottomLocal = Unmeasured;
        [Tooltip("How much of the interior height stays empty above the waterline when the vessel is full. Measured from the top of the interior, which on a wide mouthed glass is the BACK of the rim ellipse. The front of that ellipse sits a long way lower, so a headroom that looks generous against the polygon still lets the liquid draw over the mouth. On the cocktail glass the surface only clears the front of the rim at 0.34.")]
        [Range(0f, 0.50f)] public float brimHeadroom = 0.34f;
        [Tooltip("A full vessel stops short of the brim so its top face never gets clipped away by the rim.")]
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Tooltip("How much of the top face may rise above the interior outline, into the open mouth. 1 lets the whole back rim sit there.")]
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("1 gives every unit the same height, like the reference bottles. 0 gives every unit the same volume, which makes the top slice of a cone thin.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [Tooltip("Curvature of shared colour boundaries; 1 matches the top slice silhouette.")]
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Tooltip("How deep the arc between two colours sags, as a fraction of the chord. Measured off the reference: 14px on a 143px chord.")]
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;

        [Header("Slosh")]
        public float sloshGain = 0.075f;
        public float sloshMaxAngle = 11f;
        public float sloshStiffness = 190f;
        public float sloshDamping = 13f;

        // Rendered volume in units. Animated independently from the logical stack so a
        // pour can drain the source and fill the target continuously.
        [SerializeField, HideInInspector] private float displayVolume = -1f;

        private Transform liquidRoot;
        private MeshRenderer liquidRenderer;
        private MeshFilter liquidFilter;
        private MaterialPropertyBlock block;
        private Mesh quad;
        private Texture2D generatedMask;

        private Vector2[] interiorPolygon;
        private readonly List<Vector2> rotatedPolygon = new List<Vector2>();
        private float polygonArea;
        private Rect quadRect;
        private bool quadRectValid;
        private bool fittedToArt;
        private Vector4 maskUv = new Vector4(0f, 0f, 1f, 1f);

        private readonly Vector4[] bandColors = new Vector4[MaxBands];
        private readonly Vector4[] bandCaps = new Vector4[MaxBands];
        private readonly Vector4[] bandInfo = new Vector4[MaxBands];
        private readonly List<Color> groupColors = new List<Color>();
        private readonly List<int> groupTops = new List<int>();

        private Renderer[] cachedRenderers;
        private int[] cachedBaseOrders;

        private float sloshAngle;
        private float sloshVelocity;
        private float previousAngle;
        private float lastBuiltAngle = float.NaN;
        private float lastBuiltVolume = float.NaN;
        private int contentVersion;
        private int builtContentVersion = -1;
        private float surfaceLocalY;
        private float waveAmplitude;
        private float capFlash;

        /// <summary>True once a baked profile is driving this vessel.</summary>
        public bool Profiled => profile != null && profile.IsBaked;

        // The look lives on the profile when there is one, so a glass is configured in
        // its asset rather than on every instance of it in every scene.
        private float Bulge => Profiled ? profile.surfaceBulge : surfaceBulge;
        private float CapDepth => Profiled ? profile.maxCapDepth : maxCapDepth;
        private float Headroom => Profiled ? profile.brimHeadroom : brimHeadroom;
        private float Allowance => Profiled ? profile.surfaceAllowance : surfaceAllowance;
        private float EvenBands => Profiled ? profile.evenBandHeights : evenBandHeights;
        private float JunctionCurve => Profiled ? profile.innerJunctionCurve : innerJunctionCurve;
        private float JunctionDepth => Profiled ? profile.innerJunctionDepth : innerJunctionDepth;
        private float InteriorHeight => Profiled ? profile.interiorBounds.height : interiorHeight;

        public int UnitCount => units.Count;
        public bool IsEmpty => units.Count == 0;
        public bool IsFull => units.Count >= capacity;
        public int FreeSpace => Mathf.Max(0, capacity - units.Count);
        public IReadOnlyList<Color> Units => units;

        public float DisplayVolume
        {
            get => displayVolume;
            set => displayVolume = Mathf.Clamp(value, 0f, capacity);
        }

        /// <summary>World position of the pour lip.</summary>
        public Vector3 MouthWorld => transform.TransformPoint(new Vector3(mouthLocal.x, mouthLocal.y, 0f));

        /// <summary>
        /// Where the liquid actually leaves the vessel, in world space.
        ///
        /// A narrow neck pours through its centre, so the two are the same. An open rim
        /// does not: it tips out over whichever edge is facing the target, and a stream
        /// drawn from the centre of the rim starts in mid air well above the glass.
        /// </summary>
        public Vector3 PourLipWorld(float targetWorldX)
        {
            Vector2 local = PourMouthLocal(targetWorldX);
            return transform.TransformPoint(new Vector3(local.x, local.y, 0f));
        }

        /// <summary>World height of the top waterline. Used as the landing height of a stream.</summary>
        public float SurfaceWorldY =>
            transform.position.y + surfaceLocalY * Mathf.Abs(transform.lossyScale.y);

        /// <summary>Fraction of the interior the liquid is allowed to occupy.</summary>
        public float UsableFill =>
            Mathf.Clamp(Profiled ? profile.maxFillFraction : maxFillFraction, 0.5f, 1f);

        public Color TopColor => units.Count == 0 ? Color.clear : units[units.Count - 1];

        /// <summary>How many identical units sit on top of the stack.</summary>
        public int TopRunLength
        {
            get
            {
                if (units.Count == 0) return 0;
                Color top = units[units.Count - 1];
                int run = 1;
                for (int i = units.Count - 2; i >= 0; i--)
                {
                    if (!Same(units[i], top)) break;
                    run++;
                }
                return run;
            }
        }

        public bool IsComplete => units.Count == capacity && TopRunLength == capacity;

        public bool CanReceive(Color color)
        {
            if (IsFull) return false;
            return units.Count == 0 || Same(TopColor, color);
        }

        public void SetUnits(IEnumerable<Color> newUnits)
        {
            units.Clear();
            if (newUnits != null)
            {
                foreach (Color color in newUnits)
                {
                    if (units.Count >= capacity) break;
                    units.Add(color);
                }
            }
            displayVolume = units.Count;
            contentVersion++;
        }

        public void Push(Color color)
        {
            if (IsFull) return;
            units.Add(color);
            contentVersion++;
        }

        public Color Pop()
        {
            if (units.Count == 0) return Color.clear;
            Color c = units[units.Count - 1];
            units.RemoveAt(units.Count - 1);
            contentVersion++;
            return c;
        }

        public void RemoveTop(int count)
        {
            for (int i = 0; i < count; i++) Pop();
        }

        /// <summary>
        /// One shot ripple on the top surface, plus a short flash across it. Both decay
        /// on their own; call this when something lands in the vessel.
        /// </summary>
        public void Kick(float wave)
        {
            waveAmplitude = Mathf.Max(waveAmplitude, wave);
            capFlash = Mathf.Max(capFlash, 0.55f);
        }

        /// <summary>Pushes every renderer of this bottle in front of (or behind) the others.</summary>
        public void SetSortingOffset(int offset)
        {
            // Writing sortingOrder is all this does. It never touches the shape, the
            // mask or any generated sprite, so bringing a vessel to the front while it
            // pours cannot cost an art rebuild.
            CacheRenderers();
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] == null) continue;
                cachedRenderers[i].sortingOrder = cachedBaseOrders[i] + offset;
            }
        }

        /// <summary>
        /// Drops the sorting order cache only. Call this after adding or removing a
        /// child renderer, so the selection lift keeps the whole bottle together.
        /// </summary>
        public void InvalidateRenderers() => cachedRenderers = null;

        private void CacheRenderers()
        {
            if (cachedRenderers != null && cachedRenderers.Length > 0 && cachedRenderers[0] != null) return;
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
            cachedBaseOrders = new int[cachedRenderers.Length];
            for (int i = 0; i < cachedRenderers.Length; i++)
                cachedBaseOrders[i] = cachedRenderers[i].sortingOrder;
        }

        private void OnEnable()
        {
            if (displayVolume < 0f) displayVolume = units.Count;
            Invalidate();
        }

        private void OnDisable()
        {
            ReleaseGenerated();
            ReleaseQuad();
        }

        private void OnValidate()
        {
            capacity = Mathf.Clamp(capacity, 1, MaxBands);
            mouthHalfWidth = Mathf.Max(0f, mouthHalfWidth);
            surfaceBulge = Mathf.Clamp(surfaceBulge, 0.02f, 0.20f);
            maxCapDepth = Mathf.Clamp(maxCapDepth, 0.01f, 0.30f);
            brimHeadroom = Mathf.Clamp(brimHeadroom, 0f, 0.50f);
            maxFillFraction = Mathf.Clamp(maxFillFraction, 0.50f, 1f);
            innerJunctionCurve = Mathf.Clamp01(innerJunctionCurve);
            while (units.Count > capacity) units.RemoveAt(units.Count - 1);
            displayVolume = displayVolume < 0f
                ? units.Count
                : Mathf.Clamp(displayVolume, 0f, capacity);
            Invalidate();
        }

        /// <summary>Marks shape and content caches dirty. The rebuild itself happens in LateUpdate.</summary>
        public void Invalidate()
        {
            interiorPolygon = null;
            quadRectValid = false;
            fittedToArt = false;
            lastBuiltAngle = float.NaN;
            lastBuiltVolume = float.NaN;
            builtContentVersion = -1;
            cachedRenderers = null;
        }

        private void LateUpdate() => Refresh();

        /// <summary>
        /// Recomputes waterlines and pushes them to the shader. Called every LateUpdate;
        /// call it manually if you drive the bottle from outside the normal player loop.
        /// </summary>
        public void Refresh()
        {
            EnsurePolygon();
            EnsureRenderer();
            EnsureQuad();
            UpdateSlosh();

            float angle = NormalizeAngle(transform.eulerAngles.z) - sloshAngle;
            float volume = Mathf.Clamp(displayVolume, 0f, capacity);

            bool dirty = builtContentVersion != contentVersion
                         || !Mathf.Approximately(lastBuiltAngle, angle)
                         || Mathf.Abs(lastBuiltVolume - volume) > 1e-4f
                         || waveAmplitude > 0.0001f
                         || capFlash > 0.0001f;

            if (dirty)
            {
                BuildBands(angle, volume);
                lastBuiltAngle = angle;
                lastBuiltVolume = volume;
                builtContentVersion = contentVersion;
            }

            if (Application.isPlaying)
            {
                waveAmplitude = Mathf.MoveTowards(waveAmplitude, 0f, Time.deltaTime * 0.045f);
                // Roughly 0.15s of flash, which is short enough to read as an impact
                // rather than as the liquid changing colour.
                capFlash = Mathf.MoveTowards(capFlash, 0f, Time.deltaTime * 3.6f);
            }
        }

        private void UpdateSlosh()
        {
            float angle = NormalizeAngle(transform.eulerAngles.z);
            float dt = Application.isPlaying ? Time.deltaTime : 0f;
            if (dt <= 0f)
            {
                sloshAngle = 0f;
                sloshVelocity = 0f;
                previousAngle = angle;
                return;
            }

            float angularVelocity = Mathf.DeltaAngle(previousAngle, angle) / dt;
            previousAngle = angle;

            // The surface lags behind the glass. A small counter tilt driven by angular
            // velocity reproduces the residual slant you see while the bottle is turning.
            float target = Mathf.Clamp(-angularVelocity * sloshGain, -sloshMaxAngle, sloshMaxAngle);
            sloshVelocity += (target - sloshAngle) * sloshStiffness * dt;
            sloshVelocity *= Mathf.Exp(-sloshDamping * dt);
            sloshAngle = Mathf.Clamp(sloshAngle + sloshVelocity * dt, -sloshMaxAngle * 1.5f, sloshMaxAngle * 1.5f);
        }

        private void BuildBands(float angle, float volume)
        {
            if (liquidRenderer == null) return;

            bool baked = Profiled;
            float minY, maxY;
            if (baked)
            {
                // Nothing is rotated and nothing is measured. The table was built from
                // this very polygon at bake time and answers both questions directly.
                minY = profile.upright.minY;
                maxY = profile.upright.maxY;
            }
            else
            {
                VesselFillMath.Rotate(interiorPolygon, angle, rotatedPolygon);
                VesselFillMath.VerticalExtent(rotatedPolygon, out minY, out maxY);
            }

            GroupUnits();

            // The top face is an ellipse centred on the waterline, so its far rim sits
            // half a cap above it. Hold every waterline below that much headroom or the
            // rim gets sliced off flat by the brim instead of reading as a surface.
            // Capped as a share of the interior rather than as a height: clamping a
            // height would leave the chord belonging to a waterline we no longer use.
            float ceilingFill = baked ? profile.tilted.CeilingFillAt(angle) : 1f;
            float ceiling = baked ? 0f : SurfaceCeiling(rotatedPolygon, minY, maxY);

            int bandCount = 0;
            float shownPrevious = 0f;
            surfaceLocalY = minY;

            for (int g = 0; g < groupTops.Count && bandCount < MaxBands; g++)
            {
                float shown = Mathf.Min(groupTops[g], volume);
                if (shown <= shownPrevious + 1e-4f) break;

                // The free surface and the junctions under it answer two different
                // questions, so they are asked separately. The surface has to move by the
                // same amount for every unit poured; the junctions only have to split the
                // column that is currently there into equal looking slabs.
                bool isSurface = shown >= volume - 1e-4f;
                float even = isSurface ? SurfaceFraction(volume) : JunctionFraction(shown, volume);
                float fraction = Mathf.Lerp(shown / capacity, even, EvenBands) * UsableFill;

                float level, centerX, half;
                if (baked)
                {
                    profile.tilted.Sample(angle, Mathf.Min(fraction, ceilingFill),
                        out level, out centerX, out half);
                }
                else
                {
                    level = VesselFillMath.LevelForFraction(rotatedPolygon, polygonArea, fraction);
                    level = Mathf.Min(level, ceiling);
                    half = VesselFillMath.HalfWidthAt(rotatedPolygon, level, out centerX);
                }

                Color c = groupColors[g];
                Color cap = LiquidPalette.CapFor(c);
                bandColors[bandCount] = new Vector4(c.r, c.g, c.b, 1f);
                bandCaps[bandCount] = new Vector4(cap.r, cap.g, cap.b, 1f);
                bandInfo[bandCount] = new Vector4(level, centerX, half, 0f);
                bandCount++;

                surfaceLocalY = level;
                shownPrevious = shown;
                if (shown >= volume - 1e-4f) break;
            }

            for (int i = bandCount; i < MaxBands; i++)
            {
                bandColors[i] = Vector4.zero;
                bandCaps[i] = Vector4.zero;
                bandInfo[i] = Vector4.zero;
            }

            block ??= new MaterialPropertyBlock();
            liquidRenderer.GetPropertyBlock(block);
            block.SetVectorArray(BandColorId, bandColors);
            block.SetVectorArray(BandCapId, bandCaps);
            block.SetVectorArray(BandInfoId, bandInfo);
            block.SetFloat(BandCountId, bandCount);
            block.SetFloat(AngleId, angle * Mathf.Deg2Rad);
            block.SetFloat(BulgeId, Bulge);
            block.SetFloat(InnerCurveId, JunctionCurve);
            block.SetFloat(InnerBulgeId, JunctionDepth);
            block.SetFloat(InnerMaxId, Mathf.Max(0.01f, InteriorHeight * 0.22f));
            block.SetFloat(BulgeMaxId, Mathf.Max(0.005f, InteriorHeight * CapDepth));
            block.SetFloat(WaveId, waveAmplitude);
            block.SetFloat(CapFlashId, capFlash);
            block.SetVector(MaskUvId, maskUv);
            block.SetVector(QuadSizeId, new Vector4(quadRect.width, quadRect.height, 0f, 0f));
            block.SetVector(InteriorId, new Vector4(
                Mathf.Max(0.01f, quadRect.width * 0.5f),
                Mathf.Max(0.01f, quadRect.height * 0.5f), 0f, 0f));

            Texture mask = Profiled
                ? profile.interiorMask
                : maskSprite != null ? maskSprite.texture : generatedMask;
            if (mask != null) block.SetTexture(MaskTexId, mask);
            liquidRenderer.SetPropertyBlock(block);

            liquidRenderer.enabled = bandCount > 0;
        }

        /// <summary>
        /// Highest waterline whose elliptical top face still fits under the brim.
        /// The cap depth depends on the chord, which depends on the level, so this
        /// settles the fixed point in a couple of passes.
        /// </summary>
        private float SurfaceCeiling(IList<Vector2> polygon, float minY, float maxY)
        {
            float span = Mathf.Max(0.001f, maxY - minY);
            float limit = Mathf.Min(interiorHeight * maxCapDepth, span * 0.45f);
            float depth = limit;

            for (int i = 0; i < 3; i++)
            {
                float probe = Mathf.Clamp(maxY - depth, minY, maxY);
                float half = VesselFillMath.HalfWidthAt(polygon, probe, out _);
                depth = Mathf.Min(2f * half * surfaceBulge, limit);
            }

            // Only the part of the cap that is NOT allowed into the open mouth has to
            // be reserved below the brim; the rest is drawn above the interior outline.
            float reserved = depth * (1f - Mathf.Clamp01(surfaceAllowance)) * 1.06f;

            // Two rules, whichever leaves more room. A full vessel in the reference art
            // never reaches its own brim: measured over a full bottle, the interior runs
            // 423px and the liquid stops at 394px, so a fixed share of the height stays
            // empty no matter how tall or wide the vessel is. The cap still has to fit
            // under the brim as well, which is the binding rule for a wide bowl.
            float headroom = Mathf.Max(interiorHeight * brimHeadroom, reserved);
            return Mathf.Max(minY, maxY - headroom);
        }

        /// <summary>
        /// Where the visible liquid column starts. A cone tapers to a point and its last
        /// stretch disappears behind the glass line that closes the bowl, so counting
        /// that stretch as usable height crushes the bottom colour: it is handed the same
        /// span as the one above it while a third of that span is somewhere you cannot
        /// see. The fitter measures the real height off the drawing; the chord estimate
        /// is only a fallback for vessels it never saw.
        /// </summary>
        private float VisibleFloor(float low, float high) =>
            Profiled ? profile.upright.floorY
                : visibleBottomLocal > Unmeasured + 1f
                    ? Mathf.Clamp(visibleBottomLocal, low, high)
                    : VisibleBottom(interiorPolygon, low, high);

        /// <summary>
        /// Upright waterline of the free surface for a display volume. Linear in visible
        /// height from the floor to the ceiling, which is the whole point: every unit
        /// poured has to raise the surface by the same number of pixels, or the fill
        /// animation stalls near the top and a half unit stops being readable.
        /// </summary>
        private float SurfaceLevelUpright(float volume)
        {
            float floor, ceiling;
            if (Profiled)
            {
                floor = profile.upright.floorY;
                ceiling = profile.upright.ceilingY;
            }
            else
            {
                VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
                ceiling = SurfaceCeiling(interiorPolygon, low, high);
                floor = VisibleFloor(low, high);
            }
            return Mathf.Lerp(floor, ceiling, Mathf.Clamp01(volume / capacity));
        }

        /// <summary>
        /// Volume fraction under the free surface. Expressed as a fraction rather than a
        /// height because the caller applies it to the *rotated* interior, where the same
        /// fraction is the same amount of liquid at any tilt.
        /// </summary>
        private float SurfaceFraction(float volume) => AreaFraction(SurfaceLevelUpright(volume));

        /// <summary>
        /// Volume fraction under the junction that sits on top of <paramref name="units"/>
        /// units, given that <paramref name="volume"/> units are in the vessel.
        ///
        /// Equal waterline spacing does not read as equal slabs, because only the top band
        /// shows a surface: it carries the whole elliptical cap above its own waterline,
        /// so it looks a cap taller than everything under it. The junctions are therefore
        /// spread over the span the eye actually sees — floor up to the brim of the cap —
        /// while the surface itself stays exactly where the fill rule put it.
        /// </summary>
        private float JunctionFraction(float units, float volume)
        {
            if (volume <= 1e-4f) return 0f;

            float floor;
            float surface = SurfaceLevelUpright(volume);
            float capHalf;
            if (Profiled)
            {
                floor = profile.upright.floorY;
                capHalf = profile.upright.CapHalfDepthAt(surface);
            }
            else
            {
                VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
                floor = VisibleFloor(low, high);
                capHalf = TopCapHalfDepth(interiorPolygon, surface);
            }

            return AreaFraction(Mathf.Lerp(floor, surface + capHalf, Mathf.Clamp01(units / volume)));
        }

        private float AreaFraction(float level) => Profiled
            ? profile.upright.AreaFractionAt(level)
            : Mathf.Clamp01(VesselFillMath.AreaBelow(interiorPolygon, level) / Mathf.Max(polygonArea, 1e-5f));

        /// <summary>
        /// Lowest level whose chord is still a usable fraction of the widest one.
        /// Everything under it is the tip of the vessel, drawn over by its own outline.
        /// </summary>
        private float VisibleBottom(IList<Vector2> polygon, float low, float high)
        {
            if (visibleBottomChord <= 0.001f) return low;

            float widest = 0f;
            for (int i = 0; i <= 16; i++)
            {
                float y = Mathf.Lerp(low, high, i / 16f);
                widest = Mathf.Max(widest, VesselFillMath.HalfWidthAt(polygon, y, out _));
            }
            if (widest <= 0.0001f) return low;

            float wanted = widest * visibleBottomChord;
            float lo = low;
            float hi = high;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (VesselFillMath.HalfWidthAt(polygon, mid, out _) < wanted) lo = mid;
                else hi = mid;
            }
            return hi;
        }

        /// <summary>Half depth of the surface ellipse at a given waterline.</summary>
        private float TopCapHalfDepth(IList<Vector2> polygon, float level)
        {
            float half = VesselFillMath.HalfWidthAt(polygon, level, out _);
            return Mathf.Min(2f * half * surfaceBulge, interiorHeight * maxCapDepth);
        }

        private void GroupUnits()
        {
            groupColors.Clear();
            groupTops.Clear();
            for (int i = 0; i < units.Count; i++)
            {
                if (groupColors.Count > 0 && Same(groupColors[groupColors.Count - 1], units[i]))
                    groupTops[groupTops.Count - 1] = i + 1;
                else
                {
                    groupColors.Add(units[i]);
                    groupTops.Add(i + 1);
                }
            }
        }

        private void EnsurePolygon()
        {
            if (interiorPolygon != null && interiorPolygon.Length >= 3) return;

            // A baked profile already knows the shape. Nothing is traced here, which is
            // the whole point: tracing a sprite means reading its pixels, and a built
            // player should never be doing that on the frame a level opens.
            if (Profiled)
            {
                interiorPolygon = profile.interiorPolygon;
                polygonArea = profile.polygonArea;
                mouthLocal = profile.mouthLocal;
                mouthHalfWidth = profile.mouthHalfWidth;
                visibleBottomLocal = profile.visibleBottomLocal;
                capacity = Mathf.Clamp(profile.capacity, 1, MaxBands);
                return;
            }

            // Trace the drawing the first time the shape is needed, not in Awake. A bottle
            // built in code gets its components before its fields, so an Awake-time fit
            // reads an empty glassArt and silently falls back to the stock capsule. Doing
            // it lazily means the art can be assigned whenever - from the inspector, from
            // a spawner, from a level loader - and the fit still happens exactly once.
            if (fitToArt && glassArt != null && !fittedToArt
                && (customInteriorPolygon == null || customInteriorPolygon.Length < 3))
            {
                fittedToArt = true;
                if (GlassInteriorFitter.Apply(this, glassArt, GlassInteriorFitter.Settings.Default))
                {
                    var shell = GetComponent<BottleShell>();
                    if (shell != null && shell.frontOverride == null)
                    {
                        shell.frontOverride = glassArt;
                        shell.drawNeck = false;
                    }
                }
            }
            if (interiorPolygon != null && interiorPolygon.Length >= 3) return;
            if (customInteriorPolygon != null && customInteriorPolygon.Length >= 3)
                interiorPolygon = (Vector2[])customInteriorPolygon.Clone();
            else
                interiorPolygon = VesselFillMath.BottleInterior(
                    interiorWidth, interiorHeight, interiorBottom,
                    bottomCornerRadius, topCornerRadius, 8);
            polygonArea = VesselFillMath.Area(interiorPolygon);
        }

        private void EnsureRenderer()
        {
            if (liquidRenderer != null && liquidFilter != null) return;

            Transform found = transform.Find("Liquid");
            if (found == null)
            {
                var go = new GameObject("Liquid");
                go.transform.SetParent(transform, false);
                found = go.transform;
            }
            liquidRoot = found;
            liquidRoot.localPosition = Vector3.zero;
            liquidRoot.localRotation = Quaternion.identity;
            liquidRoot.localScale = Vector3.one;

            liquidFilter = liquidRoot.GetComponent<MeshFilter>();
            if (liquidFilter == null) liquidFilter = liquidRoot.gameObject.AddComponent<MeshFilter>();
            liquidRenderer = liquidRoot.GetComponent<MeshRenderer>();
            if (liquidRenderer == null) liquidRenderer = liquidRoot.gameObject.AddComponent<MeshRenderer>();

            liquidRenderer.shadowCastingMode = ShadowCastingMode.Off;
            liquidRenderer.receiveShadows = false;
            liquidRenderer.lightProbeUsage = LightProbeUsage.Off;
            liquidRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            liquidRenderer.sortingLayerName = sortingLayer;
            liquidRenderer.sortingOrder = sortingOrder;

            if (liquidMaterial == null) liquidMaterial = SharedMaterial();
            liquidRenderer.sharedMaterial = liquidMaterial;
        }

        private static Material shared;

        private static Material SharedMaterial()
        {
            if (shared != null) return shared;
            Shader shader = Shader.Find("LiquidSort/BottleLiquid");
            if (shader == null)
            {
                Debug.LogError("LiquidSort: shader 'LiquidSort/BottleLiquid' not found.");
                return null;
            }
            shared = new Material(shader) { name = "LiquidSortBottle", hideFlags = HideFlags.DontSave };
            return shared;
        }

        private void EnsureQuad()
        {
            Rect wanted = ComputeQuadRect();
            bool meshStale = quad == null || !quadRectValid || quadRect != wanted
                             || liquidFilter.sharedMesh != quad;
            bool maskStale = !Profiled && maskSprite == null && generatedMask == null;
            if (!meshStale && !maskStale) return;

            quadRect = wanted;
            quadRectValid = true;

            if (meshStale)
            {
                if (quad == null) quad = new Mesh { name = "LiquidQuad", hideFlags = HideFlags.DontSave };
                quad.Clear();
                quad.vertices = new[]
                {
                    new Vector3(quadRect.xMin, quadRect.yMin, 0f),
                    new Vector3(quadRect.xMax, quadRect.yMin, 0f),
                    new Vector3(quadRect.xMax, quadRect.yMax, 0f),
                    new Vector3(quadRect.xMin, quadRect.yMax, 0f)
                };
                quad.uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(1f, 1f), new Vector2(0f, 1f)
                };
                quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                quad.RecalculateBounds();
                liquidFilter.sharedMesh = quad;
            }

            if (Profiled)
            {
                maskUv = new Vector4(0f, 0f, 1f, 1f);
                ReleaseGenerated();
            }
            else if (maskSprite != null)
            {
                Rect tr = maskSprite.textureRect;
                Texture t = maskSprite.texture;
                maskUv = new Vector4(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height);
                ReleaseGenerated();
            }
            else
            {
                maskUv = new Vector4(0f, 0f, 1f, 1f);
                ReleaseGenerated();
                generatedMask = BottleArtFactory.MaskTexture(interiorPolygon, quadRect, maskPixelsPerUnit);
            }

            builtContentVersion = -1;
        }

        private Rect ComputeQuadRect()
        {
            if (Profiled) return profile.QuadRect;
            if (maskSprite != null)
            {
                Bounds b = maskSprite.bounds;
                return new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
            }

            EnsurePolygon();
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < interiorPolygon.Length; i++)
            {
                Vector2 p = interiorPolygon[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            // Room for the part of the top face that rises above the interior outline.
            // The generated mask is rasterised into this same rect, so growing it keeps
            // mask and polygon aligned; it only adds transparent margin.
            float pad = 0.02f + interiorHeight * maxCapDepth * Mathf.Clamp01(surfaceAllowance) * 1.15f;
            return new Rect(minX - pad, minY - pad, (maxX - minX) + pad * 2f, (maxY - minY) + pad * 2f);
        }

        private void ReleaseGenerated()
        {
            if (generatedMask == null) return;
            if (Application.isPlaying) Destroy(generatedMask);
            else DestroyImmediate(generatedMask);
            generatedMask = null;
        }

        private void ReleaseQuad()
        {
            if (quad == null) return;
            if (liquidFilter != null && liquidFilter.sharedMesh == quad)
                liquidFilter.sharedMesh = null;
            if (Application.isPlaying) Destroy(quad);
            else DestroyImmediate(quad);
            quad = null;
            quadRectValid = false;
        }

        /// <summary>Interior cross section in bottle local space. Drives both fill math and mask.</summary>
        public Vector2[] InteriorPolygon
        {
            get { EnsurePolygon(); return interiorPolygon; }
        }

        /// <summary>Local space bounds of the interior polygon, padded by a texel or two.</summary>
        public Rect InteriorBounds => ComputeQuadRect();

        /// <summary>Local pour lip on the side facing a world-space target.</summary>
        public Vector2 PourMouthLocal(float targetWorldX)
        {
            if (mouthHalfWidth <= 0.0001f) return mouthLocal;
            float x = targetWorldX < transform.position.x ? -mouthHalfWidth : mouthHalfWidth;
            return new Vector2(x, mouthLocal.y);
        }

        /// <summary>Tilt magnitude at which this bottle starts to pour, for its current fill.</summary>
        public float SpillAngle()
        {
            EnsurePolygon();
            float fraction = Mathf.Lerp(
                Mathf.Clamp01(displayVolume / capacity),
                SurfaceFraction(displayVolume), EvenBands) * UsableFill;

            // Straight out of the table. The search this replaces ran twenty six
            // bisections, each of which ran another twenty eight over the polygon, and
            // allocated a list to rotate into - every frame, for the whole pour.
            if (Profiled) return profile.upright.SpillAngleFor(fraction);

            Vector2 spillMouth = mouthHalfWidth > 0.0001f
                ? new Vector2(-mouthHalfWidth, mouthLocal.y)
                : mouthLocal;
            return VesselFillMath.SpillAngle(interiorPolygon, spillMouth, fraction);
        }

        private static float NormalizeAngle(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        public static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.01f
                   && Mathf.Abs(a.g - b.g) < 0.01f
                   && Mathf.Abs(a.b - b.b) < 0.01f;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            EnsurePolygon();
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.8f);
            for (int i = 0, j = interiorPolygon.Length - 1; i < interiorPolygon.Length; j = i++)
            {
                Gizmos.DrawLine(
                    transform.TransformPoint(interiorPolygon[j]),
                    transform.TransformPoint(interiorPolygon[i]));
            }
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(MouthWorld, 0.06f);
        }
#endif
    }
}
