using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Scene level colours for the glass, kept off the drawing itself.
    ///
    /// A traced bottle carries whatever palette it was drawn against. Ours was drawn on
    /// navy, so its blue contour looked right on the test background and foreign the
    /// moment the scene turned purple. Baking a second asset per background is the wrong
    /// answer: the silhouette, the interior polygon and the whole liquid system are
    /// identical, only four colours differ.
    ///
    /// So the drawing supplies shape and the theme supplies colour. A daytime terracotta
    /// table picks a warm grey contour, a dark bar picks a brighter one, and neither
    /// needs new code or new art.
    ///
    /// Liquid colours deliberately do not appear here. They come from the puzzle, and a
    /// theme that could tint them would let a background change what a player reads as
    /// "the green one".
    /// </summary>
    [CreateAssetMenu(menuName = "Liquid Sort/Glass Visual Theme", fileName = "GlassVisualTheme")]
    public sealed class GlassVisualTheme : ScriptableObject
    {
        [System.Serializable]
        public struct Settings
        {
            [Header("Authored glass art")]
            [Tooltip("Draws a profiled vessel's Front sprite with its original RGB/alpha instead of recolouring it through the contour shader.")]
            public bool preserveAuthoredFront;
            [Tooltip("Serialized Sprites/Default material used by the untouched authored-front path.")]
            public Material authoredFrontMaterial;

            [Header("Contour")]
            [Tooltip("Thin outer line. The darker of the two, and what gives the glass its edge.")]
            public Color contourDark;
            [Tooltip("Thin edge highlight, on the side the light comes from.")]
            public Color contourLight;
            [Tooltip("Degrees. 0 = from the right, 90 = from above.")]
            [Range(0f, 360f)] public float lightDirection;

            [Header("Glass body")]
            [Tooltip("Tint of the empty interior. Neutral by default so it does not fight the scene.")]
            public Color backTint;
            [Tooltip("How much of that tint to show. 0 leaves the interior fully transparent; the reference sits between 3% and 6%.")]
            [Range(0f, 0.25f)] public float backAlpha;

            [Header("Fake glass FX")]
            [Tooltip("Cool-white key reflection used by the rear shoulders, lit side contour and bottom lens.")]
            public Color glassKeyLight;
            [Tooltip("Cooler fill reflection used on the opposite shoulder and side contour.")]
            public Color glassFillLight;
            [Tooltip("Two soft asymmetric reflections behind the liquid. Opaque liquid covers them, so the centre colour remains untouched.")]
            [Range(0f, 0.30f)] public float shoulderStrength;
            [Tooltip("Strength of the two thin side highlights drawn only on authored glass pixels.")]
            [Range(0f, 1f)] public float sideFxStrength;
            [Tooltip("Warm upper-left mouth hotspot, restricted to authored rim pixels.")]
            [Range(0f, 1f)] public float rimHotspotStrength;
            [Tooltip("Strength of the narrow glass lens immediately under the interior floor.")]
            [Range(0f, 1f)] public float bottomLensStrength;
            [Tooltip("How strongly the bottom liquid colour is reflected into the nearby glass base.")]
            [Range(0f, 1f)] public float liquidBounceStrength;

            [Header("Painted toy glass")]
            [Tooltip("Replaces continuous realistic ramps with a few hand-directed toy/acrylic light regions. Zero preserves the normal glass look.")]
            [Range(0f, 1f)] public float paintedToyStrength;
            [Tooltip("Saturated middle colour used to flatten solid toy-glass parts such as a mug handle.")]
            public Color toyMidColor;
            [Tooltip("Cool cyan used only for the narrow opposite rim and outer handle band.")]
            public Color toyFillColor;

            [Header("Contact shadow")]
            public Color shadowColor;
            [Range(0f, 1f)] public float shadowStrength;
            [Tooltip("Broader coloured shadow behind the tight contact occlusion.")]
            public Color wideShadowColor;
            [Range(0f, 1f)] public float wideShadowStrength;
            [Tooltip("Very soft warm floor reflection under the vessel.")]
            public Color groundGlowColor;
            [Range(0f, 1f)] public float groundGlowStrength;

            [Header("Playfield panel")]
            [Tooltip("Soft panel behind the glasses. Keeps them readable over a busy table without darkening the whole scene.")]
            public Color panelColor;
            [Range(0f, 0.6f)] public float panelAlpha;
            [Tooltip("How far the panel reaches past the glasses, in world units.")]
            public float panelPadding;
            public float panelCornerRadius;

            /// <summary>Neutral values, deliberately not tuned to any one background.</summary>
            public static Settings Default => new Settings
            {
                preserveAuthoredFront = false,
                authoredFrontMaterial = null,
                // The neutral master look is deliberately blue rather than grey. A dark
                // cobalt edge survives a pale table while the cyan end of the same ramp
                // survives a night scene, so the glass does not have to be repainted for
                // each background.
                contourDark = new Color(0.035f, 0.125f, 0.300f),
                contourLight = new Color(0.430f, 0.770f, 1.000f),
                lightDirection = 120f,
                // Only a trace of cool body tint is present. The scene still shows through
                // the empty chamber; the tint merely separates it from a same-value wall.
                backTint = new Color(0.30f, 0.62f, 0.92f, 1f),
                backAlpha = 0.035f,
                glassKeyLight = new Color(0.76f, 0.94f, 1f, 1f),
                glassFillLight = new Color(0.24f, 0.60f, 1f, 1f),
                shoulderStrength = 0.09f,
                sideFxStrength = 0.58f,
                rimHotspotStrength = 0f,
                // A narrow additive lift sits only on authored pixels at the measured
                // visible liquid/glass seam. It is deliberately separate from the
                // liquid shader: liquid colours stay untouched, while a navy ridge in
                // source art no longer collapses into a black line over the first band.
                bottomLensStrength = 0.64f,
                liquidBounceStrength = 0f,
                paintedToyStrength = 0f,
                toyMidColor = new Color(0.31f, 0.47f, 0.59f, 1f),
                toyFillColor = new Color(0.27f, 0.89f, 0.96f, 1f),
                shadowColor = Color.black,
                shadowStrength = 0.36f,
                wideShadowColor = Color.black,
                wideShadowStrength = 0f,
                groundGlowColor = Color.white,
                groundGlowStrength = 0f,
                panelColor = Color.black,
                panelAlpha = 0.12f,
                panelPadding = 0.35f,
                panelCornerRadius = 0.30f
            };

            /// <summary>Only values that change an individual vessel.</summary>
            public int GlassHash()
            {
                unchecked
                {
                    int hash = preserveAuthoredFront.GetHashCode();
                    hash = hash * 31 + (authoredFrontMaterial != null
                        ? authoredFrontMaterial.GetInstanceID()
                        : 0);
                    hash = hash * 31 + contourDark.GetHashCode();
                    hash = hash * 31 + contourLight.GetHashCode();
                    hash = hash * 31 + lightDirection.GetHashCode();
                    hash = hash * 31 + backTint.GetHashCode();
                    hash = hash * 31 + backAlpha.GetHashCode();
                    hash = hash * 31 + glassKeyLight.GetHashCode();
                    hash = hash * 31 + glassFillLight.GetHashCode();
                    hash = hash * 31 + shoulderStrength.GetHashCode();
                    hash = hash * 31 + sideFxStrength.GetHashCode();
                    hash = hash * 31 + rimHotspotStrength.GetHashCode();
                    hash = hash * 31 + bottomLensStrength.GetHashCode();
                    hash = hash * 31 + liquidBounceStrength.GetHashCode();
                    hash = hash * 31 + paintedToyStrength.GetHashCode();
                    hash = hash * 31 + toyMidColor.GetHashCode();
                    hash = hash * 31 + toyFillColor.GetHashCode();
                    hash = hash * 31 + shadowColor.GetHashCode();
                    hash = hash * 31 + shadowStrength.GetHashCode();
                    hash = hash * 31 + wideShadowColor.GetHashCode();
                    hash = hash * 31 + wideShadowStrength.GetHashCode();
                    hash = hash * 31 + groundGlowColor.GetHashCode();
                    hash = hash * 31 + groundGlowStrength.GetHashCode();
                    return hash;
                }
            }

            /// <summary>Complete scene hash, including the shared playfield panel.</summary>
            public int Hash()
            {
                unchecked
                {
                    int hash = GlassHash();
                    hash = hash * 31 + panelColor.GetHashCode();
                    hash = hash * 31 + panelAlpha.GetHashCode();
                    hash = hash * 31 + panelPadding.GetHashCode();
                    hash = hash * 31 + panelCornerRadius.GetHashCode();
                    return hash;
                }
            }
        }

        public Settings settings = Settings.Default;
    }
}
