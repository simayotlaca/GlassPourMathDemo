using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>
    /// The falling column of liquid. A procedural strip mesh built in world space:
    /// a short bezier off the pour lip, then a straight fall. Its head crosses the gap
    /// on a fixed short visual beat; the receiver uses that same delay for its fill.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PourStream : MonoBehaviour
    {
        private const float WidthEpsilon = 0.0001f;

        public Material material;
        public float width = 0.085f;
        public float tipWidth = 0.055f;
        [Tooltip("The reference column reaches the receiving surface almost immediately; this is a visual travel time, not a physics simulation.")]
        public float headTravelTime = 0.055f;
        [Tooltip("How long the last piece of the stream takes to clear after emission stops.")]
        public float tailTravelTime = 0.060f;
        [Tooltip("Short width ramp that prevents the column popping on at full thickness.")]
        public float flowRampTime = 0.045f;
        public float lipDrop = 0.16f;
        public float minimumFall = 0.05f;
        public int segments = 20;
        public int sortingOrder = 40;
        public string sortingLayer = "Default";

        private MeshFilter filter;
        private MeshRenderer meshRenderer;
        private Mesh mesh;

        private LiquidBottle source;
        private LiquidBottle target;
        private Color color = Color.white;

        private bool active;
        private bool emitting;
        private bool everLanded;
        private float headY, tailY;
        private float headProgress, tailProgress, age, landedAge;
        private uint landingVersion;
        private float landY, fallX;
        private Vector3 lip;
        private float activeWidth, activeTipWidth;
        private float activeLipDrop, activeMinimumFall;

        private readonly List<Vector3> vertices = new List<Vector3>(64);
        private readonly List<Color32> colors = new List<Color32>(64);
        private readonly List<Vector2> uvs = new List<Vector2>(64);
        private readonly List<int> triangles = new List<int>(160);

        public bool Active => active;
        // Latched for the whole pour. The tail can finish and hide the renderer in the
        // same frame as impact; callers must still know that contact really happened.
        public bool HasLanded => everLanded;
        /// <summary>
        /// Time since the visible head actually latched to the receiving surface. It is
        /// exactly zero for the impact frame and keeps advancing after the detached tail
        /// disappears, so receiver animation never has to reconstruct contact from an
        /// authored travel-time estimate.
        /// </summary>
        public float LandedAge => everLanded ? Mathf.Max(0f, landedAge) : 0f;
        /// <summary>
        /// Monotonic impact id. Polling clients can distinguish one deterministic landing
        /// from the next without subscribing a per-pour delegate.
        /// </summary>
        public uint LandingVersion => landingVersion;
        public float TravelTime => Mathf.Max(0.001f, headTravelTime);

        /// <summary>World x the falling column occupies, so the target can put its splash there.</summary>
        public float FallX => fallX;

        private void Awake() => EnsureRenderer();

        private void EnsureRenderer()
        {
            if (meshRenderer != null) return;

            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;

            filter = GetComponent<MeshFilter>();
            if (filter == null) filter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            mesh = new Mesh { name = "PourStream", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;

            if (material == null) material = BottleArtFactory.UnlitVertexColor();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.sortingLayerName = sortingLayer;
            meshRenderer.sortingOrder = sortingOrder;
            meshRenderer.enabled = false;
        }

        public void Begin(LiquidBottle from, LiquidBottle to, Color liquidColor) =>
            Begin(from, to, liquidColor, width, tipWidth);

        public void Begin(LiquidBottle from, LiquidBottle to, Color liquidColor,
            float bodyWidth, float leadingWidth) =>
            Begin(from, to, liquidColor, bodyWidth, leadingWidth, 1f);

        /// <summary>
        /// Starts a stream whose shared Royal-authored world distances follow the board
        /// and safe-area scale. Widths are already supplied in final vessel world units;
        /// only the shared curve/drop constants use <paramref name="referenceDistanceScale"/>.
        /// </summary>
        public void Begin(LiquidBottle from, LiquidBottle to, Color liquidColor,
            float bodyWidth, float leadingWidth, float referenceDistanceScale)
        {
            EnsureRenderer();
            source = from;
            target = to;
            color = liquidColor;
            // Numerical guards must not become visible world-size floors. Otherwise the
            // stream stops shrinking while a compact/safe-area-fitted glass keeps shrinking.
            activeWidth = Mathf.Max(WidthEpsilon, bodyWidth);
            activeTipWidth = Mathf.Clamp(leadingWidth, WidthEpsilon, activeWidth);
            float distanceScale = Mathf.Max(0.0001f, referenceDistanceScale);
            activeLipDrop = Mathf.Max(0.0001f, lipDrop * distanceScale);
            activeMinimumFall = Mathf.Max(0.0001f, minimumFall * distanceScale);

            UpdateEndpoints();
            headY = lip.y;
            tailY = lip.y;
            headProgress = 0f;
            tailProgress = 0f;
            age = 0f;
            landedAge = 0f;
            everLanded = false;
            active = true;
            emitting = true;
            meshRenderer.enabled = true;
            Refresh(0f);
        }

        public void StopEmitting()
        {
            if (!active || !emitting) return;

            // Freeze the column in world space. The source is free to start returning while
            // the last drop clears; otherwise the tail follows the moving glass like elastic.
            UpdateEndpoints();
            emitting = false;
            tailProgress = 0f;
            tailY = lip.y;
        }

        public void Cancel()
        {
            active = false;
            emitting = false;
            if (meshRenderer != null) meshRenderer.enabled = false;
        }

        private void OnDisable() => Cancel();

        private void OnDestroy()
        {
            Cancel();
            if (filter != null && filter.sharedMesh == mesh) filter.sharedMesh = null;
            if (mesh != null)
            {
                if (Application.isPlaying) Destroy(mesh);
                else DestroyImmediate(mesh);
                mesh = null;
            }
        }

        private void LateUpdate()
        {
            float dt = Mathf.Max(0f, Time.deltaTime);
            if (!active)
            {
                // The target may still be finishing its delayed fill after the short
                // detached tail has vanished. Keep the latched impact clock alive.
                if (everLanded) landedAge += dt;
                return;
            }
            Refresh(dt);
        }

        private void Refresh(float dt)
        {
            if (source == null || target == null) { Cancel(); return; }

            // Do this before testing the head. A landing gets age zero for its whole
            // latch frame; only subsequent frames advance the receiver clock.
            if (everLanded) landedAge += dt;

            if (emitting) UpdateEndpoints();
            else
            {
                // The detached tail keeps its lip and x path in world space, but the
                // receiving surface may still rise during the short in-flight delay.
                landY = Mathf.Min(target.SurfaceWorldY, lip.y - activeMinimumFall);
            }
            age += Mathf.Max(0f, dt);

            if (headProgress < 1f)
            {
                headProgress = Mathf.MoveTowards(headProgress, 1f,
                    dt / Mathf.Max(0.001f, headTravelTime));
                if (headProgress >= 1f)
                {
                    headProgress = 1f;
                    everLanded = true;
                    landedAge = 0f;
                    unchecked { landingVersion++; }
                }
                float eased = 1f - Mathf.Pow(1f - headProgress, 3f);
                headY = Mathf.Lerp(lip.y, landY, eased);
            }
            else headY = landY;

            if (emitting)
            {
                tailY = lip.y;
            }
            else
            {
                tailProgress = Mathf.MoveTowards(tailProgress, 1f,
                    dt / Mathf.Max(0.001f, tailTravelTime));
                float eased = tailProgress * tailProgress * (3f - 2f * tailProgress);
                tailY = Mathf.Lerp(lip.y, landY, eased);
                if (tailProgress >= 0.999f) { Cancel(); return; }
            }

            BuildMesh();
        }

        private void UpdateEndpoints()
        {
            lip = source.PourLipWorld(target.transform.position.x);
            landY = Mathf.Min(target.SurfaceWorldY, lip.y - activeMinimumFall);
            // Aim at the authored mouth, not the GameObject pivot. They are not the same
            // on handled/asymmetric vessels.
            fallX = Mathf.Lerp(lip.x, target.MouthWorld.x, 0.85f);
        }

        private void BuildMesh()
        {
            vertices.Clear();
            colors.Clear();
            uvs.Clear();
            triangles.Clear();

            int count = Mathf.Max(3, segments);
            float top = Mathf.Max(tailY, headY);
            float bottom = headY;
            bool drawStrip = top - bottom >= 0.0005f;
            bool drawDrop = !emitting && tailProgress > 0.42f;
            if (!drawStrip && !drawDrop) { meshRenderer.enabled = false; return; }

            meshRenderer.enabled = true;
            Color32 body = color;
            Color32 bright = Color.Lerp(color, Color.white, 0.28f);
            float ramp = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01(age / Mathf.Max(0.001f, flowRampTime)));
            if (!emitting) ramp *= Mathf.Lerp(1f, 0.72f, tailProgress);

            if (drawStrip)
            {
                Vector2 previous = PointAt(top);
                for (int i = 0; i <= count; i++)
                {
                    float y = Mathf.Lerp(top, bottom, i / (float)count);
                    Vector2 p = PointAt(y);
                    Vector2 next = PointAt(Mathf.Lerp(top, bottom,
                        Mathf.Min(1f, (i + 1) / (float)count)));

                    Vector2 tangent = next - previous;
                    if (tangent.sqrMagnitude < 1e-8f) tangent = Vector2.down;
                    tangent.Normalize();
                    Vector2 normal = new Vector2(-tangent.y, tangent.x);
                    previous = p;

                    float toHead = Mathf.InverseLerp(0.22f, 0f, y - headY);
                    float w = Mathf.Lerp(activeWidth, activeTipWidth, toHead * toHead)
                              * ramp * 0.5f;

                    vertices.Add(new Vector3(p.x - normal.x * w, p.y - normal.y * w, 0f));
                    vertices.Add(new Vector3(p.x + normal.x * w, p.y + normal.y * w, 0f));
                    colors.Add(bright);
                    colors.Add(body);
                    uvs.Add(new Vector2(0f, i / (float)count));
                    uvs.Add(new Vector2(1f, i / (float)count));

                    if (i > 0)
                    {
                        int b = (i - 1) * 2;
                        triangles.Add(b); triangles.Add(b + 2); triangles.Add(b + 1);
                        triangles.Add(b + 1); triangles.Add(b + 2); triangles.Add(b + 3);
                    }
                }
            }

            if (drawDrop)
            {
                float appear = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.72f, tailProgress));
                float vanish = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.82f, 1f, tailProgress));
                AddDrop(PointAt(tailY), activeTipWidth * 0.52f * appear * vanish, bright, body);
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        private void AddDrop(Vector2 centre, float radius, Color32 bright, Color32 body)
        {
            if (radius <= 0.0005f) return;

            const int sides = 8;
            int centreIndex = vertices.Count;
            vertices.Add(new Vector3(centre.x, centre.y, 0f));
            colors.Add(body);
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int i = 0; i <= sides; i++)
            {
                float angle = i / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float y = Mathf.Sin(angle) * radius * 1.22f;
                vertices.Add(new Vector3(centre.x + x, centre.y + y, 0f));
                colors.Add(i < sides / 2 ? bright : body);
                uvs.Add(new Vector2(x / (radius * 2f) + 0.5f, y / (radius * 2.44f) + 0.5f));

                if (i == 0) continue;
                triangles.Add(centreIndex);
                triangles.Add(centreIndex + i);
                triangles.Add(centreIndex + i + 1);
            }
        }

        /// <summary>Path of the stream: a short bezier off the lip, then a vertical fall.</summary>
        private Vector2 PointAt(float y)
        {
            float drop = Mathf.Max(0.0001f, activeLipDrop);
            float t = (lip.y - y) / drop;
            if (t >= 1f) return new Vector2(fallX, y);

            t = Mathf.Clamp01(t);
            Vector2 p0 = new Vector2(lip.x, lip.y);
            Vector2 p2 = new Vector2(fallX, lip.y - drop);
            Vector2 p1 = new Vector2(Mathf.Lerp(lip.x, fallX, 0.8f), lip.y);
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * p1 + t * t * p2;
        }
    }
}
