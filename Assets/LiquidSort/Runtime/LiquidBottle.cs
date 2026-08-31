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
        private static readonly int AngleId = Shader.PropertyToID("_Angle");
        private static readonly int SplashAmpId = Shader.PropertyToID("_SplashAmp");
        private static readonly int SplashXId = Shader.PropertyToID("_SplashX");
        private static readonly int SplashLifeId = Shader.PropertyToID("_SplashLife");
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
        [Tooltip("Gap between the liquid surface and the brim, measured in top-face depths. Keeps a set of different vessels consistent where a share of height cannot.")]
        [Range(0f, 8f)] public float brimGapCaps = 3.2f;
        [Tooltip("A full vessel stops short of the brim so its top face never gets clipped away by the rim.")]
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Tooltip("How much of the top face may rise above the interior outline, into the open mouth. 1 lets the whole back rim sit there.")]
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("1 gives every unit the same height, like the reference bottles. 0 gives every unit the same volume, which makes the top slice of a cone thin.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [Tooltip("Curvature of shared colour boundaries. The measured Magic Sort look uses 1 with a shallow 0.098 depth; 0 is available only for deliberately straight bands. The uppermost free surface remains elliptical.")]
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Tooltip("How deep the arc between two colours sags, as a fraction of the chord. Measured off the reference: 14px on a 143px chord.")]
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;

        [Header("Slosh")]
        public float sloshGain = 0.015f;
        public float sloshMaxAngle = 2.5f;
        public float sloshStiffness = 150f;
        public float sloshDamping = 18f;

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

        // A transfer is animated before it is committed to the puzzle model. The target
        // therefore needs to draw the arriving colour without temporarily adding it to
        // its logical stack. Keeping this as a count + colour avoids a second list and any
        // per-transfer allocation.
        private object transferReservationOwner;
        private int transferReservationId;
        private Color receivePreviewColor;
        private int receivePreviewCount;
        private int modelVersion;

        private Renderer[] cachedRenderers;
        private int[] cachedBaseOrders;

        private float sloshAngle;
        private float sloshVelocity;
        private float previousAngle;
        private float lastBuiltAngle = float.NaN;
        private float lastBuiltVolume = float.NaN;
        private int lastBuiltLookHash = int.MinValue;
        private int contentVersion;
        private int builtContentVersion = -1;
        private float surfaceLocalY;
        private float waveAmplitude;
        private bool waveActive;
        // Public so an offscreen preview can pose the splash without entering play mode.
        [System.NonSerialized] public float splashAmplitude;
        [System.NonSerialized] public float splashX;
        [System.NonSerialized, Range(0f, 1f)] public float splashLife;
        private const float SplashDuration = 0.18f;
        private float splashPeak;
        private float splashAge = SplashDuration;
        private bool splashActive;
        private Material validatedContractMaterial;
        private Shader validatedContractShader;
        private bool liquidContractValid;
        private bool liquidContractErrorLogged;

        /// <summary>True once a baked profile is driving this vessel.</summary>
        public bool Profiled => profile != null && profile.IsBaked;

        // The look lives on the profile when there is one, so a glass is configured in
        // its asset rather than on every instance of it in every scene.
        private float Bulge => Profiled ? profile.surfaceBulge : surfaceBulge;
        private float CapDepth => Profiled ? profile.maxCapDepth : maxCapDepth;
        private float Headroom => Profiled ? profile.brimHeadroom : brimHeadroom;
        private float GapCaps => Profiled ? profile.brimGapCaps : brimGapCaps;
        private float Allowance => Profiled ? profile.surfaceAllowance : surfaceAllowance;
        private float EvenBands => Profiled ? profile.evenBandHeights : evenBandHeights;
        private float JunctionCurve => Profiled ? profile.innerJunctionCurve : innerJunctionCurve;
        private float JunctionDepth => Profiled
            ? profile.innerJunctionDepth
            : innerJunctionDepth;
        private float InteriorHeight => Profiled ? profile.interiorBounds.height : interiorHeight;

        public int UnitCount => units.Count;
        public bool IsEmpty => units.Count == 0;
        public bool IsFull => units.Count >= capacity;
        public int FreeSpace => Mathf.Max(0, capacity - units.Count);
        public IReadOnlyList<Color> Units => units;
        public bool IsTransferReserved => transferReservationOwner != null;
        internal int ModelVersion => modelVersion;

        private int VisualUnitCount => units.Count + receivePreviewCount;

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

        /// <summary>
        /// Colour currently touching the visible floor of the vessel. Unlike Units[0],
        /// this also covers an empty receiver while its incoming liquid is still only a
        /// visual preview, so the glass-base reflection appears during the pour rather
        /// than popping on at commit time.
        /// </summary>
        public Color VisualBottomColor
        {
            get
            {
                float volume = displayVolume >= 0f ? displayVolume : VisualUnitCount;
                if (volume <= 0.001f) return Color.clear;
                if (units.Count > 0) return units[0];
                return receivePreviewCount > 0 ? receivePreviewColor : Color.clear;
            }
        }

        /// <summary>Soft presence used to fade the coloured glass bounce at empty/full transitions.</summary>
        public float VisualBottomPresence
        {
            get
            {
                float volume = displayVolume >= 0f ? displayVolume : VisualUnitCount;
                return Mathf.Clamp01(volume / 0.35f);
            }
        }

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
            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            ClearTransientMotion();
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
            modelVersion++;
        }

        public void Push(Color color)
        {
            if (IsFull) return;
            units.Add(color);
            contentVersion++;
            modelVersion++;
        }

        public Color Pop()
        {
            if (units.Count == 0) return Color.clear;
            Color c = units[units.Count - 1];
            units.RemoveAt(units.Count - 1);
            contentVersion++;
            modelVersion++;
            return c;
        }

        public void RemoveTop(int count)
        {
            for (int i = 0; i < count; i++) Pop();
        }

        /// <summary>
        /// Locks this vessel for one transfer operation. This is deliberately stored on
        /// the vessel rather than only on the animator: two animators must not be able to
        /// move the same source or receiver at the same time.
        /// </summary>
        internal bool TryReserveTransfer(object owner, int operationId)
        {
            if (owner == null || operationId == 0) return false;
            if (transferReservationOwner == null)
            {
                transferReservationOwner = owner;
                transferReservationId = operationId;
                return true;
            }
            return ReferenceEquals(transferReservationOwner, owner)
                   && transferReservationId == operationId;
        }

        internal void ReleaseTransferReservation(object owner, int operationId)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId)
                return;
            transferReservationOwner = null;
            transferReservationId = 0;
        }

        /// <summary>
        /// Draws units that are on their way to this vessel without exposing them to the
        /// solver, save system or completion checks before the move commits.
        /// </summary>
        internal bool BeginReceivePreview(object owner, int operationId, Color color, int count)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId
                || receivePreviewCount != 0 || count <= 0
                || units.Count + count > capacity)
                return false;

            receivePreviewColor = color;
            receivePreviewCount = count;
            contentVersion++;
            return true;
        }

        internal void ClearReceivePreview(object owner, int operationId)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId || receivePreviewCount == 0)
                return;

            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            contentVersion++;
        }

        /// <summary>
        /// Commits both sides of a transfer without yielding between the remove and add.
        /// Until this succeeds, the source stack remains the single authoritative state and
        /// the receiver contains only the visual preview above.
        /// </summary>
        internal bool TryCommitTransferTo(LiquidBottle target, object owner, int operationId,
            int expectedSourceVersion, int expectedTargetVersion, Color expectedColor,
            int count, bool requireMatchingColors)
        {
            if (this == null || !isActiveAndEnabled
                || target == null || !target.isActiveAndEnabled
                || target == this || count <= 0
                || !ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId
                || !ReferenceEquals(target.transferReservationOwner, owner)
                || target.transferReservationId != operationId
                || modelVersion != expectedSourceVersion
                || target.modelVersion != expectedTargetVersion
                || target.receivePreviewCount != count
                || !Same(target.receivePreviewColor, expectedColor)
                || units.Count < count || target.units.Count + count > target.capacity)
                return false;

            for (int i = units.Count - count; i < units.Count; i++)
            {
                if (!Same(units[i], expectedColor)) return false;
            }

            if (requireMatchingColors && target.units.Count > 0
                && !Same(target.units[target.units.Count - 1], expectedColor))
                return false;

            // Any managed growth happens before either stack changes. Once capacity is
            // available, RemoveRange + Add cannot leave a normal partial transaction.
            int requiredTargetCapacity = target.units.Count + count;
            if (target.units.Capacity < requiredTargetCapacity)
                target.units.Capacity = requiredTargetCapacity;

            units.RemoveRange(units.Count - count, count);
            for (int i = 0; i < count; i++) target.units.Add(expectedColor);

            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            target.receivePreviewCount = 0;
            target.receivePreviewColor = Color.clear;
            contentVersion++;
            target.contentVersion++;
            modelVersion++;
            target.modelVersion++;
            return true;
        }

        /// <summary>
        /// The arriving stream landing. Unlike <see cref="Kick"/> this is local: the
        /// surface humps up where the stream actually hits and stays flat on the far side,
        /// which is what the reference does for the brief contact beat.
        /// </summary>
        public void Splash(float localX, float amount)
        {
            splashX = localX;
            splashPeak = Mathf.Max(splashActive ? splashAmplitude : 0f, amount);
            splashAmplitude = splashPeak;
            splashAge = 0f;
            splashLife = 0f;
            splashActive = splashPeak > 0.0001f;
        }

        public void Kick(float wave)
        {
            waveAmplitude = Mathf.Max(waveAmplitude, wave);
            waveActive = waveAmplitude > 0.0001f;
        }

        /// <summary>Clears presentation-only motion after a cancelled/reset transfer.</summary>
        internal void ClearTransientMotion()
        {
            waveAmplitude = 0f;
            waveActive = false;
            splashAmplitude = 0f;
            splashX = 0f;
            splashLife = 1f;
            splashPeak = 0f;
            splashAge = SplashDuration;
            splashActive = false;
            sloshAngle = 0f;
            sloshVelocity = 0f;
            previousAngle = NormalizeAngle(transform.eulerAngles.z);
            contentVersion++;
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

        internal void GetSortingSnapshot(out Renderer[] renderers, out int[] baseOrders)
        {
            CacheRenderers();
            renderers = cachedRenderers;
            baseOrders = cachedBaseOrders;
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
            lastBuiltLookHash = int.MinValue;
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
            if (!EnsureLiquidRenderContract()) return;
            EnsureQuad();
            UpdateSlosh();

            // Splash is an exact 0.18 s one-shot. Keeping normalized age alongside the
            // amplitude lets the shader move its analytic droplets without spawning or
            // updating any managed objects.
            if (Application.isPlaying && splashActive)
            {
                float life = Mathf.Clamp01(splashAge / SplashDuration);
                splashLife = life;
                float settle = 1f - Mathf.SmoothStep(0f, 1f, life);
                splashAmplitude = splashPeak * settle;
            }

            float angle = NormalizeAngle(transform.eulerAngles.z) - sloshAngle;
            float volume = Mathf.Clamp(displayVolume, 0f, capacity);
            int lookHash = LiquidLookHash(volume);

            bool dirty = builtContentVersion != contentVersion
                         || !Mathf.Approximately(lastBuiltAngle, angle)
                         || Mathf.Abs(lastBuiltVolume - volume) > 1e-4f
                         || lastBuiltLookHash != lookHash
                         || waveActive
                         || splashActive
                         || (!Application.isPlaying && splashAmplitude > 0.0001f);

            if (dirty)
            {
                BuildBands(angle, volume);
                lastBuiltAngle = angle;
                lastBuiltVolume = volume;
                lastBuiltLookHash = lookHash;
                builtContentVersion = contentVersion;
            }

            if (Application.isPlaying)
            {
                if (waveActive)
                {
                    // Leave waveActive set for one zero-amplitude build. Otherwise the
                    // renderer retains the last tiny non-zero property block forever.
                    if (waveAmplitude <= 0f) waveActive = false;
                    else waveAmplitude = Mathf.MoveTowards(
                        waveAmplitude, 0f, Time.deltaTime * 0.07f);
                }

                if (splashActive)
                {
                    if (splashAge >= SplashDuration)
                    {
                        // This frame already pushed life=1 and amplitude=0. The next
                        // stable frame can stop rebuilding the band data.
                        splashAge = SplashDuration;
                        splashAmplitude = 0f;
                        splashPeak = 0f;
                        splashActive = false;
                    }
                    else
                    {
                        splashAge = Mathf.Min(
                            SplashDuration, splashAge + Time.deltaTime);
                    }
                }
            }
        }

        /// <summary>
        /// Every value that changes the band waterlines or their property block. Contents,
        /// angle and volume have their own fast checks; this signature covers the asset
        /// look and shared Royal policy that previously left visually different stale
        /// blocks on identical pooled vessels after an editor/profile refresh.
        /// </summary>
        private int LiquidLookHash(float volume)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 397 + LiquidSurfaceContract.Revision;
                hash = hash * 397 + LiquidPalette.Revision;
                hash = hash * 397 + capacity;
                hash = hash * 397 + SurfaceScale(volume).GetHashCode();
                hash = hash * 397 + Bulge.GetHashCode();
                hash = hash * 397 + CapDepth.GetHashCode();
                hash = hash * 397 + Headroom.GetHashCode();
                hash = hash * 397 + GapCaps.GetHashCode();
                hash = hash * 397 + Allowance.GetHashCode();
                hash = hash * 397 + EvenBands.GetHashCode();
                hash = hash * 397 + JunctionCurve.GetHashCode();
                hash = hash * 397 + JunctionDepth.GetHashCode();
                hash = hash * 397 + UsableFill.GetHashCode();
                hash = hash * 397 + InteriorHeight.GetHashCode();

                if (Profiled)
                {
                    hash = hash * 397 + profile.GetInstanceID();
                    hash = hash * 397 + profile.visibleLiquidFloor.GetHashCode();
                    hash = hash * 397 + (profile.hasVisibleLiquidFloor ? 1 : 0);
                    hash = hash * 397 + profile.upright.floorY.GetHashCode();
                    hash = hash * 397 + profile.upright.ceilingY.GetHashCode();
                }

                return hash;
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

                // Every cumulative unit owns a fixed viewer-facing edge in the vessel. An
                // existing colour therefore keeps the same front-wall height when another
                // colour covers its lit top face; only the incoming surface moves.
                bool isSurface = shown >= volume - 1e-4f;
                float even = isSurface
                    ? SurfaceFraction(volume)
                    : JunctionFraction(shown);
                // The fixed-height branch already includes the vessel's usable-fill cap in
                // its full-stack anchor. Apply that cap only to the volume branch; applying
                // it after the lerp would scale the authored result a second time.
                float volumeFraction = shown / capacity * UsableFill;
                float fraction = Mathf.Lerp(volumeFraction, even, EvenBands);

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
            block.SetVectorArray(LiquidSurfaceContract.BandInfoId, bandInfo);
            block.SetFloat(LiquidSurfaceContract.BandCountId, bandCount);
            block.SetFloat(AngleId, angle * Mathf.Deg2Rad);
            block.SetFloat(LiquidSurfaceContract.BulgeId, Bulge);
            block.SetFloat(LiquidSurfaceContract.InnerCurveId, JunctionCurve);
            block.SetFloat(LiquidSurfaceContract.InnerBulgeId, JunctionDepth);
            block.SetFloat(LiquidSurfaceContract.SurfaceScaleId,
                LiquidSurfaceContract.ExposedSurfaceScale(volume, capacity));
            block.SetFloat(LiquidSurfaceContract.BulgeMaxId,
                Mathf.Max(0.005f, InteriorHeight * CapDepth));
            block.SetFloat(WaveId, waveAmplitude);
            // Measured on the reference: the lump stands about 15% of the *chord* proud of
            // the surface, not 15% of the vessel's height. On a tall glass those are very
            // different numbers, and the height version threw up a wave half a band tall.
            block.SetFloat(SplashAmpId, splashAmplitude * bandInfo[Mathf.Max(bandCount - 1, 0)].z * 0.30f);
            block.SetFloat(SplashXId, splashX);
            block.SetFloat(SplashLifeId, Mathf.Clamp01(splashLife));
            // Contact colour remains local. Never wash the complete top face white.
            block.SetFloat(CapFlashId, 0f);
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
            // InteriorHeight, not the serialized fallback: the share is meant to be of
            // *this* vessel's height. Multiplying by the constant 2.0 made the same
            // number mean 42% of a 1.62 tall cocktail bowl and 6% of a 3.14 tall mug,
            // which is why one stopped well below its brim and the other touched it.
            // Measured in top-face depths, not in vessel heights. A share of height
            // cannot be consistent across a set: the same 30% leaves a thin sliver over a
            // tall tumbler and half a bowl over a squat coupe, because what the eye reads
            // is the dark crescent against the *width* of the opening. The cap depth
            // already tracks the chord, so counting the gap in cap depths keeps that
            // crescent the same shape in every vessel, whatever its proportions.
            float headroom = Mathf.Max(depth * GapCaps, reserved);
            headroom = Mathf.Max(headroom, InteriorHeight * brimHeadroom);
            return Mathf.Max(minY, maxY - headroom);
        }

        /// <summary>
        /// Where the fill mapping starts: the bottom of the interior outline, because that
        /// is where the liquid is drawn from. Any floor above it is height the bottom
        /// colour receives without the mapping ever charging a unit for it, which is what
        /// made two equal units render almost two to one.
        /// </summary>
        private float VisibleFloor(float low, float high) => Profiled ? profile.upright.floorY : low;

        /// <summary>
        /// Upright waterline of the free surface for a display volume. The front edge of
        /// the top face, not its raised back rim, is the visible top of the colour's front
        /// wall. Anchoring that edge keeps a top colour the same apparent height it had
        /// while covered, without flattening either curve.
        /// </summary>
        private float SurfaceLevelUpright(float volume) =>
            WaterlineForFrontEdge(UnitFrontEdgeLevelUpright(volume), SurfaceScale(volume));

        /// <summary>
        /// The Royal reference keeps the exposed ellipse at its full authored perspective
        /// depth even for a one-unit fill. Volume changes the stable front-edge waterline,
        /// while the top-face depth remains part of the vessel's visual identity.
        /// </summary>
        private float SurfaceScale(float volume) =>
            LiquidSurfaceContract.ExposedSurfaceScale(volume, capacity);

        /// <summary>
        /// Volume fraction under the free surface. Expressed as a fraction rather than a
        /// height because the caller applies it to the *rotated* interior, where the same
        /// fraction is the same amount of liquid at any tilt.
        /// </summary>
        private float SurfaceFraction(float volume) => AreaFraction(SurfaceLevelUpright(volume));

        /// <summary>
        /// Volume fraction under the fixed waterline on top of <paramref name="units"/>
        /// cumulative units. It deliberately does not take the vessel's current total
        /// volume: adding a new colour must never resize the colours already underneath it.
        /// </summary>
        private float JunctionFraction(float units)
        {
            if (units <= 1e-4f) return 0f;
            return AreaFraction(WaterlineForFrontEdge(
                UnitFrontEdgeLevelUpright(units), JunctionCurve));
        }

        /// <summary>
        /// Front-edge level of the nth cumulative unit. This is the lower, viewer-facing
        /// edge of the exposed top ellipse and the identically directed visible edge of a
        /// covered colour boundary. The raised back half of the exposed ellipse is surface
        /// perspective, not extra colour height. The full-depth cap is anchored around
        /// this stable front edge so adding liquid never shifts an existing boundary.
        ///
        /// Positions depend only on the unit index, so a colour never moves when another
        /// lands on top of it. Profiled vessels divide their baked optical-height domain,
        /// so an opaque base does not steal height from the bottom colour, and a tapered
        /// bowl needs no trigonometry of its own: the inverse table already answers which
        /// level sits at a given visible height, whatever the silhouette.
        /// </summary>
        private float UnitFrontEdgeLevelUpright(float units)
        {
            float unitFraction = Mathf.Clamp01(units / Mathf.Max(1f, capacity));
            float fullCentre = EffectiveFullCentreUpright();

            if (Profiled && profile.upright.HasVisibleHeightMap)
            {
                VesselProfile.UprightTable table = profile.upright;
                float fullFrontEdge = fullCentre - table.CapHalfDepthAt(fullCentre);
                float visibleFloor = profile.hasVisibleLiquidFloor
                    ? profile.visibleLiquidFloor
                    : table.floorY;
                return table.LevelAtVisibleHeight(Mathf.Lerp(
                    table.VisibleHeightAt(visibleFloor),
                    table.VisibleHeightAt(fullFrontEdge), unitFraction));
            }

            float floor;
            if (Profiled)
            {
                floor = profile.upright.floorY;
            }
            else
            {
                VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
                floor = VisibleFloor(low, high);
            }

            float fullCap = Profiled
                ? profile.upright.CapHalfDepthAt(fullCentre)
                : TopCapHalfDepth(interiorPolygon, fullCentre);
            return Mathf.Lerp(floor, fullCentre - fullCap, unitFraction);
        }

        private float EffectiveFullCentreUpright()
        {
            if (Profiled)
            {
                float authoredFull = AreaFraction(profile.upright.ceilingY);
                profile.tilted.Sample(0f, authoredFull * UsableFill,
                    out float level, out _, out _);
                return Mathf.Min(level, profile.upright.ceilingY);
            }

            VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
            float ceiling = SurfaceCeiling(interiorPolygon, low, high);
            float fallbackFull = AreaFraction(ceiling);
            return VesselFillMath.LevelForFraction(interiorPolygon, polygonArea,
                fallbackFull * UsableFill);
        }

        /// <summary>
        /// Geometric waterline whose viewer-facing arc lands on a fixed front-edge level,
        /// using the shader's own depth formula. Both the exposed surface's near edge and a
        /// covered junction bend downward; only their authored curvature can differ. Three
        /// fixed passes converge without search, allocation or stack-height dependence.
        /// </summary>
        private float WaterlineForFrontEdge(float frontEdge, float curve)
        {
            float waterline = frontEdge;
            curve = Mathf.Clamp01(curve);
            for (int i = 0; i < 3; i++)
                waterline = frontEdge + SurfaceHalfDepthAt(waterline) * curve;
            return waterline;
        }

        private float SurfaceHalfDepthAt(float level)
        {
            if (Profiled) return profile.upright.CapHalfDepthAt(level);
            return TopCapHalfDepth(interiorPolygon, level);
        }

        /// <summary>Half depth of the surface ellipse at a given waterline.</summary>
        private float TopCapHalfDepth(IList<Vector2> polygon, float level)
        {
            float half = VesselFillMath.HalfWidthAt(polygon, level, out _);
            return Mathf.Min(2f * half * surfaceBulge, interiorHeight * maxCapDepth);
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


        private void GroupUnits()
        {
            groupColors.Clear();
            groupTops.Clear();
            int visualCount = VisualUnitCount;
            for (int i = 0; i < visualCount; i++)
            {
                Color color = i < units.Count ? units[i] : receivePreviewColor;
                if (groupColors.Count > 0 && Same(groupColors[groupColors.Count - 1], color))
                    groupTops[groupTops.Count - 1] = i + 1;
                else
                {
                    groupColors.Add(color);
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
            // A baked profile already carries the polygon, so re-deriving it would read the
            // source texture back at runtime for nothing — and that read is the only reason
            // the art had to keep Read/Write enabled, at a second full copy in RAM each.
            if (fitToArt && !Profiled && glassArt != null && !fittedToArt
                && (customInteriorPolygon == null || customInteriorPolygon.Length < 3))
            {
                fittedToArt = true;
                if (GlassInteriorFitter.Apply(this, glassArt, GlassInteriorFitter.Settings.Default))
                {
                    var shell = GetComponent<BottleShell>();
                    if (shell != null)
                        shell.drawNeck = false;
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

            // Profile first, then a per bottle override, and only then the legacy
            // fallback. The fallback builds a material from Shader.Find, which snapshots
            // whatever the shader defaults happened to be at the time and then never
            // updates — that is what made shader edits look like they did nothing. A
            // serialized .mat is also the only form a build is guaranteed to keep.
            Material resolved = Profiled && profile.liquidMaterial != null
                ? profile.liquidMaterial
                : liquidMaterial != null ? liquidMaterial : SharedMaterial();

            liquidRenderer.sharedMaterial = resolved;
        }

        private bool EnsureLiquidRenderContract()
        {
            Material material = liquidRenderer != null
                ? liquidRenderer.sharedMaterial
                : null;
            Shader shader = material != null ? material.shader : null;

            if (material == validatedContractMaterial
                && shader == validatedContractShader)
                return liquidContractValid;

            validatedContractMaterial = material;
            validatedContractShader = shader;
            liquidContractValid = LiquidSurfaceContract.TryValidate(
                material, out string reason);
            liquidContractErrorLogged = false;

            if (liquidContractValid) return true;

            if (liquidRenderer != null) liquidRenderer.enabled = false;
            if (!liquidContractErrorLogged)
            {
                Debug.LogError(
                    $"{name}: BottleLiquid render contract failed: {reason}. "
                    + "Liquid was hidden instead of drawing an incorrect full-depth "
                    + "surface.", this);
                liquidContractErrorLogged = true;
            }
            return false;
        }

        private static Material shared;

        /// <summary>
        /// Legacy fallback for a bottle with no profile and no material assigned. Prefer
        /// the .mat asset: this path cannot be tuned, cannot survive a shader edit without
        /// a domain reload, and relies on Shader.Find resolving in a build.
        /// </summary>
        private static Material SharedMaterial()
        {
            if (shared != null) return shared;
            Shader shader = Shader.Find(LiquidSurfaceContract.ShaderName);
            if (shader == null)
            {
                Debug.LogError(
                    $"LiquidSort: shader '{LiquidSurfaceContract.ShaderName}' not found.");
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
            // Pour the surface the player actually sees. A one-unit bowl may receive a
            // small visual lift so it does not look crushed into its tapered floor. If
            // the pose is solved from the unlifted volume instead, the visible cap sits
            // well above the active lip while the stream starts somewhere else. Using
            // the displayed fraction keeps cap, lip and stream as one connected shape;
            // profiles with no visual lift are byte-for-byte unchanged.
            float fraction = Mathf.Lerp(
                Mathf.Clamp01(displayVolume / capacity) * UsableFill,
                SurfaceFraction(displayVolume), EvenBands);

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
