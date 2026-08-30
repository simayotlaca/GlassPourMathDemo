using System.Collections.Generic;
using UnityEngine;

namespace GlassPourDemo
{
    public static class PolygonFillMath
    {
        public static List<Vector2> Rotate(IReadOnlyList<Vector2> polygon, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);
            var result = new List<Vector2>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 p = polygon[i];
                result.Add(new Vector2(c * p.x - s * p.y, s * p.x + c * p.y));
            }
            return result;
        }

        public static float Area(IReadOnlyList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0f;
            float twiceArea = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                twiceArea += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(twiceArea) * 0.5f;
        }

        public static List<Vector2> ClipBelowY(IReadOnlyList<Vector2> polygon, float y)
        {
            var output = new List<Vector2>();
            if (polygon == null || polygon.Count == 0) return output;

            Vector2 previous = polygon[polygon.Count - 1];
            bool previousInside = previous.y <= y;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 current = polygon[i];
                bool currentInside = current.y <= y;
                if (currentInside != previousInside)
                {
                    float denominator = current.y - previous.y;
                    float t = Mathf.Abs(denominator) < 0.000001f ? 0f : (y - previous.y) / denominator;
                    output.Add(Vector2.LerpUnclamped(previous, current, t));
                }
                if (currentInside) output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        public static List<Vector2> ClipAboveY(IReadOnlyList<Vector2> polygon, float y)
        {
            var output = new List<Vector2>();
            if (polygon == null || polygon.Count == 0) return output;

            Vector2 previous = polygon[polygon.Count - 1];
            bool previousInside = previous.y >= y;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 current = polygon[i];
                bool currentInside = current.y >= y;
                if (currentInside != previousInside)
                {
                    float denominator = current.y - previous.y;
                    float t = Mathf.Abs(denominator) < 0.000001f ? 0f : (y - previous.y) / denominator;
                    output.Add(Vector2.LerpUnclamped(previous, current, t));
                }
                if (currentInside) output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        public static List<Vector2> ClipBetweenY(
            IReadOnlyList<Vector2> polygon, float lowerY, float upperY)
        {
            if (upperY < lowerY)
            {
                float swap = lowerY;
                lowerY = upperY;
                upperY = swap;
            }

            return ClipAboveY(ClipBelowY(polygon, upperY), lowerY);
        }

        public static List<Vector2> IntersectConvex(
            IReadOnlyList<Vector2> subject, IReadOnlyList<Vector2> clipPolygon)
        {
            var output = subject == null
                ? new List<Vector2>()
                : new List<Vector2>(subject);
            if (output.Count == 0 || clipPolygon == null || clipPolygon.Count < 3)
                return output;

            float orientation = SignedArea(clipPolygon) >= 0f ? 1f : -1f;
            for (int edgeIndex = 0; edgeIndex < clipPolygon.Count; edgeIndex++)
            {
                if (output.Count == 0) break;
                Vector2 edgeStart = clipPolygon[edgeIndex];
                Vector2 edgeEnd = clipPolygon[(edgeIndex + 1) % clipPolygon.Count];
                var input = output;
                output = new List<Vector2>(input.Count + 2);

                Vector2 previous = input[input.Count - 1];
                bool previousInside = IsInsideEdge(previous, edgeStart, edgeEnd, orientation);
                for (int i = 0; i < input.Count; i++)
                {
                    Vector2 current = input[i];
                    bool currentInside = IsInsideEdge(current, edgeStart, edgeEnd, orientation);
                    if (currentInside)
                    {
                        if (!previousInside)
                            output.Add(LineIntersection(previous, current, edgeStart, edgeEnd));
                        output.Add(current);
                    }
                    else if (previousInside)
                    {
                        output.Add(LineIntersection(previous, current, edgeStart, edgeEnd));
                    }

                    previous = current;
                    previousInside = currentInside;
                }
            }

            return output;
        }

        private static float SignedArea(IReadOnlyList<Vector2> polygon)
        {
            float twiceArea = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                twiceArea += a.x * b.y - b.x * a.y;
            }
            return twiceArea * 0.5f;
        }

        private static bool IsInsideEdge(
            Vector2 point, Vector2 edgeStart, Vector2 edgeEnd, float orientation)
        {
            return orientation * Cross(edgeEnd - edgeStart, point - edgeStart) >= -0.00001f;
        }

        private static Vector2 LineIntersection(
            Vector2 segmentStart, Vector2 segmentEnd, Vector2 edgeStart, Vector2 edgeEnd)
        {
            Vector2 segment = segmentEnd - segmentStart;
            Vector2 edge = edgeEnd - edgeStart;
            float denominator = Cross(segment, edge);
            if (Mathf.Abs(denominator) < 0.000001f) return segmentEnd;
            float t = Cross(edgeStart - segmentStart, edge) / denominator;
            return segmentStart + segment * t;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        public static float FindWaterline(IReadOnlyList<Vector2> polygon, float fill01)
        {
            fill01 = Mathf.Clamp01(fill01);
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < polygon.Count; i++)
            {
                minY = Mathf.Min(minY, polygon[i].y);
                maxY = Mathf.Max(maxY, polygon[i].y);
            }
            float targetArea = Area(polygon) * fill01;
            for (int i = 0; i < 36; i++)
            {
                float middle = (minY + maxY) * 0.5f;
                if (Area(ClipBelowY(polygon, middle)) < targetArea) minY = middle;
                else maxY = middle;
            }
            return (minY + maxY) * 0.5f;
        }

        public static float HorizontalSpan(IReadOnlyList<Vector2> polygon, float y)
        {
            return HorizontalSpan(polygon, y, out _);
        }

        public static float HorizontalSpan(
            IReadOnlyList<Vector2> polygon, float y, out float centerX)
        {
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            int hits = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))
                {
                    float t = (y - a.y) / (b.y - a.y);
                    float x = Mathf.LerpUnclamped(a.x, b.x, t);
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    hits++;
                }
            }
            if (hits < 2)
            {
                centerX = 0f;
                return 0f;
            }

            centerX = (minX + maxX) * 0.5f;
            return Mathf.Max(0f, maxX - minX);
        }
    }
}
