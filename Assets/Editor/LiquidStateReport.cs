using System.Text;
using LiquidSort;
using UnityEditor;
using UnityEngine;

namespace LiquidSortEditor
{
    /// <summary>
    /// One-click read-only dump of what every visible glass in the open scene is actually
    /// drawing. It exists because the difference between "the volume is wrong" and "the
    /// volume is right and something else looks wrong" is one number - the waterline as a
    /// share of the vessel's own interior - and hunting for it in the Inspector means
    /// expanding thirty-four pooled objects.
    ///
    /// Reads only. It never bakes, rebuilds, instantiates or moves anything.
    /// </summary>
    public static class LiquidStateReport
    {
        // Clicking a menu item at the right moment of a running game is its own small
        // skill, and the interesting moment - the frame a level finishes seating its
        // glasses - is easy to miss. In Play mode the report therefore fires itself the
        // first time glasses appear, and again whenever the board changes, so the numbers
        // are in the Console without anyone having to catch the moment.
        private static int lastReportedCount = -1;
        private static int automaticReportsLeft;

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            lastReportedCount = -1;
            // Enough to cover the menu, the first board and a couple of level changes,
            // but bounded so a long session cannot fill the Console.
            automaticReportsLeft = 6;
        }

        private static void Watch()
        {
            if (!Application.isPlaying || automaticReportsLeft <= 0) return;

            int count = Object.FindObjectsByType<LiquidBottle>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
            if (count == 0 || count == lastReportedCount) return;

            lastReportedCount = count;
            automaticReportsLeft--;
            Report();
        }

        [MenuItem("Tools/LiquidSort/Report Liquid State")]
        public static void Report()
        {
            LiquidBottle[] bottles = Object.FindObjectsByType<LiquidBottle>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var sb = new StringBuilder();
            sb.AppendLine($"[LiquidStateReport] {bottles.Length} aktif bardak "
                        + $"({(Application.isPlaying ? "PLAY" : "EDIT")} mode)");
            sb.AppendLine(
                "ad | profil | Baked | kapasite | birim | displayVolume | "
              + "dunyaOlcek | rho | suCizgisi%");

            int unbaked = 0;
            int capacityMismatch = 0;

            foreach (LiquidBottle b in bottles)
            {
                VesselProfile p = b.profile;
                bool baked = b.Profiled;
                if (!baked) unbaked++;

                string capacityNote = "";
                if (p != null && p.capacity != b.capacity)
                {
                    capacityMismatch++;
                    capacityNote = $"  <-- profil kapasitesi {p.capacity}!";
                }

                float worldScale = VesselPresentationMath.PlanarWorldScale(b.transform);
                float rho = VesselPresentationMath.RelativeToRoyalReference(b.transform, p);

                string levelPercent = "-";
                if (baked && p.upright != null && p.upright.IsValid)
                {
                    float scaleY = Mathf.Abs(b.transform.lossyScale.y);
                    if (scaleY > 1e-5f)
                    {
                        float localY = (b.SurfaceWorldY - b.transform.position.y) / scaleY;
                        float span = p.upright.maxY - p.upright.minY;
                        if (span > 1e-5f)
                            levelPercent =
                                $"{100f * (localY - p.upright.minY) / span:0.0}%";
                    }
                }

                sb.AppendLine(
                    $"{b.name} | {(p != null ? p.name : "YOK")} | {baked} | "
                  + $"{b.capacity} | {b.UnitCount} | {b.DisplayVolume:0.###} | "
                  + $"{worldScale:0.####} | {rho:0.####} | {levelPercent}{capacityNote}");
            }

            // The two states that silently change every waterline in the game, called out
            // rather than left for the reader to spot in a thirty-four line table.
            if (unbaked > 0)
                sb.AppendLine($"\nUYARI: {unbaked} bardak Profiled=false. Bu bardaklar bake "
                            + "edilmis tabloyu DEGIL, component'teki serbest alanlari ve "
                            + "calisma-zamani poligon aramasini kullanir; su cizgisi Royal "
                            + "ile tutmaz.");
            if (capacityMismatch > 0)
                sb.AppendLine($"\nUYARI: {capacityMismatch} bardagin capacity degeri profil "
                            + "kapasitesinden farkli. Birim yuksekligi capacity'ye bolundugu "
                            + "icin bu dogrudan yanlis dolulukla sonuclanir.");

            Debug.Log(sb.ToString());
        }
    }
}
