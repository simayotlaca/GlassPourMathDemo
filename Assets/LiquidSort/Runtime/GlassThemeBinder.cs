using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// The scene end of the theme: hands one <see cref="GlassVisualTheme"/> to every
    /// glass under it, and draws the one thing a single glass cannot draw for itself —
    /// the panel the whole puzzle sits on.
    ///
    /// Applying is a rebuild, not a per frame cost. The shells hash the theme along with
    /// their other settings, so a theme that has not changed repaints nothing, and none
    /// of this runs while a pour is playing.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GlassThemeBinder : MonoBehaviour
    {
        private const string PanelName = "PlayfieldPanel";

        [Tooltip("Empty falls back to GlassVisualTheme.Settings.Default, which is neutral rather than tied to any one background.")]
        public GlassVisualTheme theme;

        [Tooltip("Soft panel behind the glasses. Only the strip the puzzle occupies is quieted; the rest of the table is left alone.")]
        public bool drawPanel = true;
        public int panelOrder = -5;
        public float panelPixelsPerUnit = 64f;

        private BottleShell[] shells;
        private SpriteRenderer panel;
        private Sprite generatedPanel;
        private int builtHash;
        private bool built;

        private GlassVisualTheme.Settings Theme =>
            theme != null ? theme.settings : GlassVisualTheme.Settings.Default;

        private void OnEnable() { built = false; shells = null; }

        private void OnDisable() => ReleasePanel();

        private void OnValidate() { built = false; shells = null; }

        private void LateUpdate()
        {
            int hash = CurrentHash();
            if (built && hash == builtHash) return;
            Apply();
            // After, not before: Apply is what fills the shell cache the hash reads, so
            // hashing first would settle a frame late and re-apply once for nothing.
            builtHash = CurrentHash();
            built = true;
        }

        /// <summary>Pushes the theme onto every glass in this hierarchy and rebuilds the panel.</summary>
        public void Apply()
        {
            // Scanned here and nowhere else. The hash below used to call
            // GetComponentsInChildren every LateUpdate, which allocates a fresh array
            // each time for a hierarchy that changes about once a scene load.
            shells = GetComponentsInChildren<BottleShell>(true);
            for (int i = 0; i < shells.Length; i++)
            {
                if (shells[i] == null) continue;
                shells[i].theme = theme;
            }

            BuildPanel(shells);
        }

        private void BuildPanel(BottleShell[] shells)
        {
            GlassVisualTheme.Settings settings = Theme;
            if (!drawPanel || settings.panelAlpha <= 0.001f || shells.Length == 0)
            {
                ReleasePanel();
                if (panel != null) panel.enabled = false;
                return;
            }

            // The panel spans what the glasses actually occupy, so adding a column or
            // moving the rows does not need it retuned by hand.
            if (!TryMeasure(shells, out Bounds world))
            {
                ReleasePanel();
                return;
            }

            Vector3 localCentre = transform.InverseTransformPoint(world.center);
            var rect = new Rect(
                -world.size.x * 0.5f - settings.panelPadding,
                -world.size.y * 0.5f - settings.panelPadding,
                world.size.x + settings.panelPadding * 2f,
                world.size.y + settings.panelPadding * 2f);

            Transform found = transform.Find(PanelName);
            if (found == null)
            {
                var go = new GameObject(PanelName);
                go.transform.SetParent(transform, false);
                found = go.transform;
            }
            found.localPosition = new Vector3(localCentre.x, localCentre.y, 0f);
            found.localRotation = Quaternion.identity;
            found.localScale = Vector3.one;

            panel = found.GetComponent<SpriteRenderer>();
            if (panel == null) panel = found.gameObject.AddComponent<SpriteRenderer>();
            panel.enabled = true;
            panel.sortingOrder = panelOrder;

            Color tint = settings.panelColor;
            tint.a = settings.panelAlpha;

            ReleasePanel();
            generatedPanel = BottleArtFactory.Panel(rect, panelPixelsPerUnit, tint,
                settings.panelCornerRadius);
            panel.sprite = generatedPanel;
        }

        private bool TryMeasure(BottleShell[] shells, out Bounds world)
        {
            world = default;
            bool any = false;
            for (int i = 0; i < shells.Length; i++)
            {
                if (shells[i] == null) continue;
                var bottle = shells[i].GetComponent<LiquidBottle>();
                if (bottle == null) continue;

                Rect r = bottle.InteriorBounds;
                // Corners, because a tilted glass's interior is not axis aligned.
                for (int c = 0; c < 4; c++)
                {
                    var corner = new Vector3(
                        c < 2 ? r.xMin : r.xMax,
                        (c % 2) == 0 ? r.yMin : r.yMax, 0f);
                    Vector3 p = bottle.transform.TransformPoint(corner);
                    if (!any) { world = new Bounds(p, Vector3.zero); any = true; }
                    else world.Encapsulate(p);
                }
            }
            return any;
        }

        private int CurrentHash()
        {
            unchecked
            {
                int hash = Theme.Hash();
                hash = hash * 31 + drawPanel.GetHashCode();
                hash = hash * 31 + panelOrder;
                hash = hash * 31 + panelPixelsPerUnit.GetHashCode();
                hash = hash * 31 + (shells != null ? shells.Length : 0);
                return hash;
            }
        }

        private void ReleasePanel()
        {
            if (generatedPanel == null) return;
            Sprite releasing = generatedPanel;
            generatedPanel = null;
            if (panel != null && panel.sprite == releasing) panel.sprite = null;
            Texture2D tex = releasing.texture;
            if (Application.isPlaying) { Destroy(releasing); if (tex != null) Destroy(tex); }
            else { DestroyImmediate(releasing); if (tex != null) DestroyImmediate(tex); }
        }
    }
}
