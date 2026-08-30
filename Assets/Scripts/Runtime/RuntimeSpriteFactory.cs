using System.Collections.Generic;
using UnityEngine;

namespace GlassPourDemo
{
    public static class RuntimeSpriteFactory
    {
        private static Sprite ellipse;
        private static Sprite glassBase;
        private static Material opaqueSpriteMaterial;
        private static Material juicyLiquidMaterial;

        public static Sprite Ellipse()
        {
            if (ellipse != null) return ellipse;
            const int width = 128;
            const int height = 32;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "RuntimeEllipse" };
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float nx = ((x + 0.5f) / width - 0.5f) * 2f;
                float ny = ((y + 0.5f) / height - 0.5f) * 2f;
                float d = nx * nx + ny * ny;
                float alpha = Mathf.Clamp01((1.05f - d) * 18f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            texture.Apply();
            ellipse = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), width);
            return ellipse;
        }

        public static Material OpaqueSpriteMaterial()
        {
            if (opaqueSpriteMaterial != null) return opaqueSpriteMaterial;
            Shader shader = Shader.Find("Sprites/Default");
            opaqueSpriteMaterial = new Material(shader) { name = "RuntimeOpaqueLiquid" };
            opaqueSpriteMaterial.mainTexture = Texture2D.whiteTexture;
            return opaqueSpriteMaterial;
        }

        public static Material JuicyLiquidMaterial()
        {
            if (juicyLiquidMaterial != null) return juicyLiquidMaterial;
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "RuntimeJuicyLiquidShading",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                float side = Mathf.SmoothStep(0.62f, 1f, Mathf.Abs(u - 0.5f) * 2f);
                float bottom = 1f - Mathf.SmoothStep(0f, 0.30f, v);
                float shade = 1f - side * 0.04f - bottom * 0.03f;
                texture.SetPixel(x, y, new Color(shade, shade, shade, 1f));
            }

            texture.Apply();
            Shader shader = Shader.Find("Sprites/Default");
            juicyLiquidMaterial = new Material(shader) { name = "RuntimeJuicyLiquid" };
            juicyLiquidMaterial.mainTexture = texture;
            return juicyLiquidMaterial;
        }

        public static Sprite GlassBase(IReadOnlyList<Vector2> polygon)
        {
            if (glassBase != null) return glassBase;

            const int size = 512;
            const float units = 4f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "RuntimeGlassBase",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];
            Color emptyInterior = new Color(0.015f, 0.035f, 0.11f, 0.84f);
            Color reflected = new Color(0.08f, 0.50f, 0.78f, 0.90f);

            for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                Vector2 point = new Vector2(
                    ((px + 0.5f) / size - 0.5f) * units,
                    ((py + 0.5f) / size - 0.5f) * units);
                if (!PointInside(polygon, point))
                {
                    pixels[py * size + px] = new Color32(0, 0, 0, 0);
                    continue;
                }

                // The broad reflection belongs to the empty/back glass. Because this
                // sprite is behind the opaque liquid pieces, it never tints their center.
                float streakCenter = -0.68f + point.y * 0.12f;
                float distance = Mathf.Abs(point.x - streakCenter);
                float reflection = Mathf.Clamp01(1f - distance / 0.24f);
                reflection = reflection * reflection * 0.62f;
                Color pixel = Color.Lerp(emptyInterior, reflected, reflection);
                pixels[py * size + px] = pixel;
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            glassBase = Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size / units);
            return glassBase;
        }

        private static bool PointInside(IReadOnlyList<Vector2> polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                bool crosses = (a.y > point.y) != (b.y > point.y) &&
                               point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                if (crosses) inside = !inside;
            }
            return inside;
        }
    }
}
