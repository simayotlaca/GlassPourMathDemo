using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GlassPourDemo
{
    [Serializable]
    public struct LiquidLayer
    {
        public Color color;
        [Range(0f, 1f)] public float fraction;

        public LiquidLayer(Color color, float fraction)
        {
            this.color = color;
            this.fraction = fraction;
        }
    }

    public sealed class GlassVessel : MonoBehaviour
    {
        private const int GlassBaseSortingOrder = -20;
        private const int LiquidSortingOrder = 0;
        private const int FrameFxSortingOrder = 1000;
        private const float SurfaceAspect = 0.13f;
        private const float MinimumSurfaceHeight = 0.16f;
        private const float MaximumSurfaceHeight = 0.30f;
        private const float BottomCurveAspect = 0.10f;
        private const float MinimumBottomCurveHeight = 0.08f;
        private const float MaximumBottomCurveHeight = 0.20f;

        public Sprite frameFx;
        public List<LiquidLayer> layers = new List<LiquidLayer>();

        public static readonly Vector2[] InteriorPolygon =
        {
            new Vector2(-1.18f, 1.16f), new Vector2(-1.05f, 0.87f),
            new Vector2(-0.85f, 0.48f), new Vector2(-0.63f, 0.10f),
            new Vector2(-0.43f, -0.09f), new Vector2(-0.20f, -0.16f),
            new Vector2(0.20f, -0.16f), new Vector2(0.43f, -0.09f),
            new Vector2(0.63f, 0.10f), new Vector2(0.85f, 0.48f),
            new Vector2(1.05f, 0.87f), new Vector2(1.18f, 1.16f)
        };

        private Transform visualsRoot;
        private Transform liquidSegments;
        private SortingGroup sortingGroup;
        private readonly List<SegmentVisual> segmentVisuals = new List<SegmentVisual>();
        private int lastVisualState = int.MinValue;

        private sealed class SegmentVisual
        {
            public GameObject root;
            public Mesh bodyMesh;
            public MeshRenderer bodyRenderer;
            public GameObject bottomCurveObject;
            public Mesh bottomCurveMesh;
            public MeshRenderer bottomCurveRenderer;
            public GameObject topSurfaceObject;
            public Mesh topSurfaceMesh;
            public MeshRenderer topSurfaceRenderer;
            public GameObject surfaceInsetObject;
            public Mesh surfaceInsetMesh;
            public MeshRenderer surfaceInsetRenderer;
            public LineRenderer surfaceRim;
            public SpriteRenderer surfaceGloss;
            public MaterialPropertyBlock bodyColorBlock;
            public MaterialPropertyBlock bottomCurveColorBlock;
            public MaterialPropertyBlock surfaceColorBlock;
            public MaterialPropertyBlock surfaceInsetColorBlock;
        }

        public void BuildVisuals()
        {
            ReleaseSegmentMeshes();
            Transform existing = transform.Find("VisualLayers");
            if (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
            }
            segmentVisuals.Clear();
            lastVisualState = int.MinValue;
            sortingGroup = GetComponent<SortingGroup>();
            if (sortingGroup == null) sortingGroup = gameObject.AddComponent<SortingGroup>();

            visualsRoot = new GameObject("VisualLayers").transform;
            visualsRoot.SetParent(transform, false);
            BuildGlassBase();

            liquidSegments = new GameObject("LiquidSegments").transform;
            liquidSegments.SetParent(visualsRoot, false);

            var frameObject = new GameObject("FrameFX");
            frameObject.transform.SetParent(visualsRoot, false);
            var frameRenderer = frameObject.AddComponent<SpriteRenderer>();
            frameRenderer.sprite = frameFx;
            frameRenderer.sortingOrder = FrameFxSortingOrder;
        }

        public void RefreshVisuals()
        {
            if (liquidSegments == null) return;
            // Counter-rotate only the liquid container so every waterline remains
            // horizontal in world space while the glass base/frame keep tilting.
            liquidSegments.rotation = Quaternion.identity;
            int state = ComputeVisualState();
            if (state == lastVisualState) return;
            DrawLayers();
            lastVisualState = state;
        }

        private void LateUpdate() => RefreshVisuals();

        public void SetForeground(bool foreground)
        {
            if (sortingGroup == null) sortingGroup = GetComponent<SortingGroup>();
            if (sortingGroup != null) sortingGroup.sortingOrder = foreground ? 10 : 0;
        }

        private void BuildGlassBase()
        {
            var baseObject = new GameObject("GlassBase");
            baseObject.transform.SetParent(visualsRoot, false);
            var baseRenderer = baseObject.AddComponent<SpriteRenderer>();
            baseRenderer.sprite = RuntimeSpriteFactory.GlassBase(InteriorPolygon);
            baseRenderer.sortingOrder = GlassBaseSortingOrder;
        }

        private void DrawLayers()
        {
            EnsureSegmentCount(layers.Count);

            float angle = transform.eulerAngles.z;
            if (angle > 180f) angle -= 360f;
            List<Vector2> worldAlignedInterior = PolygonFillMath.Rotate(InteriorPolygon, angle);
            float lowerWaterline = PolygonFillMath.FindWaterline(worldAlignedInterior, 0f);
            float cumulative = 0f;
            int visibleIndex = 0;
            int visibleCount = CountRenderableLayers();

            for (int i = 0; i < segmentVisuals.Count; i++)
            {
                float remainingCapacity = Mathf.Max(0f, 1f - cumulative);
                float renderedFraction = i < layers.Count
                    ? Mathf.Min(Mathf.Max(0f, layers[i].fraction), remainingCapacity)
                    : 0f;
                bool active = renderedFraction > 0.0001f;
                SegmentVisual visual = segmentVisuals[i];
                visual.root.SetActive(active);
                if (!active) continue;

                cumulative += renderedFraction;
                float upperWaterline = PolygonFillMath.FindWaterline(worldAlignedInterior, cumulative);
                List<Vector2> bodyPolygon = PolygonFillMath.ClipBetweenY(
                    worldAlignedInterior, lowerWaterline, upperWaterline);

                Color opaqueColor = layers[i].color;
                opaqueColor.a = 1f;
                UpdateMesh(visual.bodyMesh, bodyPolygon);
                visual.bodyColorBlock.SetColor("_Color", opaqueColor);
                visual.bodyRenderer.SetPropertyBlock(visual.bodyColorBlock);

                float lowerSpan = PolygonFillMath.HorizontalSpan(
                    worldAlignedInterior, lowerWaterline, out float lowerCenterX);
                bool showBottomCurve = visibleIndex > 0 && lowerSpan > 0.001f;
                visual.bottomCurveObject.SetActive(showBottomCurve);
                if (showBottomCurve)
                {
                    float bottomCurveHeight = Mathf.Clamp(
                        lowerSpan * BottomCurveAspect,
                        MinimumBottomCurveHeight, MaximumBottomCurveHeight);
                    List<Vector2> bottomEllipse = BuildEllipsePolygon(
                        lowerCenterX, lowerWaterline, lowerSpan, bottomCurveHeight, 32);
                    // Only the lower half extends the upper segment into the layer below.
                    // Drawing the full ellipse creates a bright, ruler-straight UI stripe.
                    List<Vector2> lowerHalf = PolygonFillMath.ClipBetweenY(
                        bottomEllipse, lowerWaterline - bottomCurveHeight, lowerWaterline);
                    List<Vector2> clippedBottom = PolygonFillMath.IntersectConvex(
                        lowerHalf, worldAlignedInterior);
                    UpdateMesh(visual.bottomCurveMesh, clippedBottom);
                    Color boundaryColor = Color.Lerp(opaqueColor, Color.black, 0.03f);
                    visual.bottomCurveColorBlock.SetColor("_Color", boundaryColor);
                    visual.bottomCurveRenderer.SetPropertyBlock(visual.bottomCurveColorBlock);
                }

                float upperSpan = PolygonFillMath.HorizontalSpan(
                    worldAlignedInterior, upperWaterline, out float upperCenterX);
                bool isTopSegment = visibleIndex == visibleCount - 1;

                // Only the exposed top piece receives a full cap. Internal layers use
                // the shared curved boundary above, like stacked prepared cylinders.
                bool showSurface = isTopSegment && upperSpan > 0.001f;
                visual.topSurfaceObject.SetActive(showSurface);
                visual.surfaceInsetObject.SetActive(showSurface);
                visual.surfaceRim.gameObject.SetActive(showSurface);
                if (showSurface)
                {
                    float surfaceHeight = Mathf.Clamp(
                        upperSpan * SurfaceAspect, MinimumSurfaceHeight, MaximumSurfaceHeight);
                    List<Vector2> ellipse = BuildEllipsePolygon(
                        upperCenterX, upperWaterline, upperSpan, surfaceHeight, 40);
                    List<Vector2> clippedSurface = PolygonFillMath.IntersectConvex(
                        ellipse, worldAlignedInterior);
                    UpdateMesh(visual.topSurfaceMesh, clippedSurface);
                    visual.surfaceColorBlock.SetColor("_Color", opaqueColor);
                    visual.topSurfaceRenderer.SetPropertyBlock(visual.surfaceColorBlock);

                    float insetHeight = surfaceHeight * 0.72f;
                    List<Vector2> insetEllipse = BuildEllipsePolygon(
                        upperCenterX, upperWaterline + surfaceHeight * 0.035f,
                        upperSpan * 0.955f, insetHeight, 40);
                    List<Vector2> clippedInset = PolygonFillMath.IntersectConvex(
                        insetEllipse, worldAlignedInterior);
                    UpdateMesh(visual.surfaceInsetMesh, clippedInset);
                    Color surfaceColor = JuicySurfaceColor(opaqueColor);
                    visual.surfaceInsetColorBlock.SetColor("_Color", surfaceColor);
                    visual.surfaceInsetRenderer.SetPropertyBlock(visual.surfaceInsetColorBlock);
                    SetSurfaceRim(visual.surfaceRim, clippedInset, opaqueColor);
                    SetSurfaceGloss(
                        visual.surfaceGloss, upperCenterX, upperWaterline,
                        upperSpan, surfaceHeight);
                }

                // Gloss stays local to the exposed liquid surface; it is not a glass overlay.
                visual.surfaceGloss.gameObject.SetActive(showSurface);

                lowerWaterline = upperWaterline;
                visibleIndex++;
            }
        }

        private int CountRenderableLayers()
        {
            int count = 0;
            float remainingCapacity = 1f;
            for (int i = 0; i < layers.Count; i++)
            {
                float renderedFraction = Mathf.Min(
                    Mathf.Max(0f, layers[i].fraction), remainingCapacity);
                if (renderedFraction <= 0.0001f) continue;
                count++;
                remainingCapacity -= renderedFraction;
                if (remainingCapacity <= 0.0001f) break;
            }
            return count;
        }

        private int ComputeVisualState()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + transform.eulerAngles.z.GetHashCode();
                hash = hash * 31 + layers.Count;
                for (int i = 0; i < layers.Count; i++)
                {
                    hash = hash * 31 + layers[i].fraction.GetHashCode();
                    hash = hash * 31 + layers[i].color.GetHashCode();
                }
                return hash;
            }
        }

        private void EnsureSegmentCount(int count)
        {
            while (segmentVisuals.Count < count)
            {
                int index = segmentVisuals.Count;
                int order = LiquidSortingOrder + index * 8;
                var visual = new SegmentVisual();

                visual.root = new GameObject("LiquidSegment_" + index);
                visual.root.transform.SetParent(liquidSegments, false);

                var bodyObject = new GameObject("OpaqueBody");
                bodyObject.transform.SetParent(visual.root.transform, false);
                var meshFilter = bodyObject.AddComponent<MeshFilter>();
                visual.bodyMesh = new Mesh { name = "LiquidBodyMesh_" + index };
                visual.bodyMesh.MarkDynamic();
                meshFilter.sharedMesh = visual.bodyMesh;
                visual.bodyRenderer = bodyObject.AddComponent<MeshRenderer>();
                visual.bodyRenderer.sharedMaterial = RuntimeSpriteFactory.JuicyLiquidMaterial();
                visual.bodyRenderer.sortingOrder = order;
                visual.bodyColorBlock = new MaterialPropertyBlock();

                visual.bottomCurveObject = new GameObject("BottomCurve");
                visual.bottomCurveObject.transform.SetParent(visual.root.transform, false);
                var bottomFilter = visual.bottomCurveObject.AddComponent<MeshFilter>();
                visual.bottomCurveMesh = new Mesh { name = "LiquidBottomCurveMesh_" + index };
                visual.bottomCurveMesh.MarkDynamic();
                bottomFilter.sharedMesh = visual.bottomCurveMesh;
                visual.bottomCurveRenderer = visual.bottomCurveObject.AddComponent<MeshRenderer>();
                visual.bottomCurveRenderer.sharedMaterial = RuntimeSpriteFactory.OpaqueSpriteMaterial();
                visual.bottomCurveRenderer.sortingOrder = order + 1;
                visual.bottomCurveColorBlock = new MaterialPropertyBlock();

                visual.topSurfaceObject = new GameObject("TopSurface");
                visual.topSurfaceObject.transform.SetParent(visual.root.transform, false);
                var surfaceFilter = visual.topSurfaceObject.AddComponent<MeshFilter>();
                visual.topSurfaceMesh = new Mesh { name = "LiquidSurfaceMesh_" + index };
                visual.topSurfaceMesh.MarkDynamic();
                surfaceFilter.sharedMesh = visual.topSurfaceMesh;
                visual.topSurfaceRenderer = visual.topSurfaceObject.AddComponent<MeshRenderer>();
                visual.topSurfaceRenderer.sharedMaterial = RuntimeSpriteFactory.OpaqueSpriteMaterial();
                visual.topSurfaceRenderer.sortingOrder = order + 2;
                visual.surfaceColorBlock = new MaterialPropertyBlock();

                visual.surfaceInsetObject = new GameObject("TopSurfaceLight");
                visual.surfaceInsetObject.transform.SetParent(visual.root.transform, false);
                var insetFilter = visual.surfaceInsetObject.AddComponent<MeshFilter>();
                visual.surfaceInsetMesh = new Mesh { name = "LiquidSurfaceLightMesh_" + index };
                visual.surfaceInsetMesh.MarkDynamic();
                insetFilter.sharedMesh = visual.surfaceInsetMesh;
                visual.surfaceInsetRenderer = visual.surfaceInsetObject.AddComponent<MeshRenderer>();
                visual.surfaceInsetRenderer.sharedMaterial = RuntimeSpriteFactory.OpaqueSpriteMaterial();
                visual.surfaceInsetRenderer.sortingOrder = order + 3;
                visual.surfaceInsetColorBlock = new MaterialPropertyBlock();

                visual.surfaceRim = CreateLineRenderer(
                    visual.root.transform, "SurfaceRim", order + 4, true, 0.014f);
                visual.surfaceGloss = CreateEllipseRenderer(
                    visual.root.transform, "SurfaceGlossFX", order + 5);

                segmentVisuals.Add(visual);
            }
        }

        private static SpriteRenderer CreateEllipseRenderer(Transform parent, string name, int order)
        {
            var ellipseObject = new GameObject(name);
            ellipseObject.transform.SetParent(parent, false);
            var renderer = ellipseObject.AddComponent<SpriteRenderer>();
            renderer.sprite = RuntimeSpriteFactory.Ellipse();
            renderer.sortingOrder = order;
            return renderer;
        }

        private static LineRenderer CreateLineRenderer(
            Transform parent, string name, int order, bool loop, float width)
        {
            var lineObject = new GameObject(name);
            lineObject.transform.SetParent(parent, false);
            var renderer = lineObject.AddComponent<LineRenderer>();
            renderer.sharedMaterial = RuntimeSpriteFactory.OpaqueSpriteMaterial();
            renderer.useWorldSpace = false;
            renderer.loop = loop;
            renderer.widthMultiplier = width;
            renderer.numCapVertices = 4;
            renderer.numCornerVertices = 3;
            renderer.positionCount = 0;
            renderer.sortingOrder = order;
            return renderer;
        }

        private static List<Vector2> BuildEllipsePolygon(
            float centerX, float centerY, float width, float height, int pointCount)
        {
            var polygon = new List<Vector2>(pointCount);
            float radiusX = width * 0.5f;
            float radiusY = height * 0.5f;
            for (int i = 0; i < pointCount; i++)
            {
                float angle = i * Mathf.PI * 2f / pointCount;
                polygon.Add(new Vector2(
                    centerX + Mathf.Cos(angle) * radiusX,
                    centerY + Mathf.Sin(angle) * radiusY));
            }
            return polygon;
        }

        private static void SetSurfaceRim(
            LineRenderer renderer, IReadOnlyList<Vector2> polygon, Color baseColor)
        {
            renderer.positionCount = polygon.Count;
            for (int i = 0; i < polygon.Count; i++)
                renderer.SetPosition(i, new Vector3(polygon[i].x, polygon[i].y, 0f));
            Color rim = Color.Lerp(baseColor, Color.white, 0.34f);
            rim.a = 0.48f;
            renderer.startColor = rim;
            renderer.endColor = rim;
        }

        private static Color JuicySurfaceColor(Color baseColor)
        {
            Color.RGBToHSV(baseColor, out float hue, out float saturation, out float value);
            Color result = Color.HSVToRGB(
                hue, saturation * 0.82f, Mathf.Lerp(value, 1f, 0.65f));
            result.a = 1f;
            return result;
        }

        private static void SetSurfaceGloss(
            SpriteRenderer renderer, float centerX, float y,
            float span, float surfaceHeight)
        {
            renderer.color = new Color(1f, 0.95f, 0.78f, 0.34f);
            renderer.transform.localPosition = new Vector3(
                centerX - span * 0.06f, y - surfaceHeight * 0.14f, 0f);
            float heightScale = surfaceHeight * 0.12f / renderer.sprite.bounds.size.y;
            renderer.transform.localScale = new Vector3(span * 0.46f, heightScale, 1f);
        }

        private static void UpdateMesh(Mesh mesh, IReadOnlyList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                mesh.Clear();
                return;
            }

            var vertices = new Vector3[polygon.Count];
            var uv = new Vector2[polygon.Count];
            var colors = new Color[polygon.Count];
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < polygon.Count; i++)
            {
                minX = Mathf.Min(minX, polygon[i].x);
                maxX = Mathf.Max(maxX, polygon[i].x);
                minY = Mathf.Min(minY, polygon[i].y);
                maxY = Mathf.Max(maxY, polygon[i].y);
            }
            float width = Mathf.Max(0.0001f, maxX - minX);
            float height = Mathf.Max(0.0001f, maxY - minY);
            for (int i = 0; i < polygon.Count; i++)
            {
                vertices[i] = new Vector3(polygon[i].x, polygon[i].y, 0f);
                uv[i] = new Vector2(
                    (polygon[i].x - minX) / width,
                    (polygon[i].y - minY) / height);
                colors[i] = Color.white;
            }

            var triangles = new int[(polygon.Count - 2) * 3];
            for (int i = 0; i < polygon.Count - 2; i++)
            {
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = i + 2;
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        private void ReleaseSegmentMeshes()
        {
            for (int i = 0; i < segmentVisuals.Count; i++)
            {
                Mesh mesh = segmentVisuals[i].bodyMesh;
                ReleaseMesh(mesh);
                ReleaseMesh(segmentVisuals[i].bottomCurveMesh);
                ReleaseMesh(segmentVisuals[i].topSurfaceMesh);
                ReleaseMesh(segmentVisuals[i].surfaceInsetMesh);
            }
        }

        private static void ReleaseMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }

        private void OnDestroy() => ReleaseSegmentMeshes();
    }
}
