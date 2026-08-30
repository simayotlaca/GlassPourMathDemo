using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// RectTransform'u cihazin GUVENLI ALANINA oturtur — centik, punch-hole,
    /// yuvarlatilmis kose ve alttaki gesture bar'in disinda kalan bolge.
    ///
    /// BartenderSort ekip projesinden taşındı, davranış birebir. iPhone 13 gibi
    /// centikli bir cihazda portre kurulumun tamamlayicisi: Game view'i 1170x2532'ye
    /// almak yalniz cerceveyi degistirir, ust ve alt seritlerin altina giren UI'yi
    /// kurtarmaz.
    ///
    /// Nicin capa (anchor) ile: capalar ebeveyn dikdortgenine ORANLIDIR, bu yuzden
    /// CanvasScaler'in olcek katsayisindan bagimsiz calisir. Piksel offset'i
    /// yazsaydik olcek degistiginde bosluk kayardi.
    ///
    /// Editorde de calisir (ExecuteAlways) ki Game view'de Simulator ile centikli bir
    /// cihaz secildiginde sonuc aninda gorulsun.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class BsSafeArea : MonoBehaviour
    {
        [Tooltip("Ust kenari guvenli alana cek (centik).")]
        public bool ApplyTop = true;
        [Tooltip("Alt kenari guvenli alana cek (gesture bar).")]
        public bool ApplyBottom = true;
        [Tooltip("Yan kenarlari guvenli alana cek (yatayda kulak, portrede genelde gereksiz).")]
        public bool ApplySides = true;

        private RectTransform rectTransform;
        private Rect lastSafe;
        private int lastWidth;
        private int lastHeight;

        private void OnEnable()
        {
            rectTransform = (RectTransform)transform;
            lastWidth = lastHeight = 0;      // ilk Apply'i zorla
            Apply();
        }

        private void Update()
        {
            // Yon degisimi, katlanabilir cihaz, bolunmus ekran: hepsi safeArea'yi
            // degistirir. Karsilastirma ucuz, her karede yapilabilir.
            if (Screen.width == lastWidth && Screen.height == lastHeight
                && Screen.safeArea == lastSafe) return;
            Apply();
        }

        private void Apply()
        {
            if (rectTransform == null) rectTransform = (RectTransform)transform;
            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0) return;          // ilk karede 0 gelebiliyor

            Rect safe = Screen.safeArea;
            lastSafe = safe;
            lastWidth = w;
            lastHeight = h;

            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;
            min.x /= w; min.y /= h;
            max.x /= w; max.y /= h;

            // Kapatilan kenarlar tam ekranda kalsin.
            if (!ApplySides) { min.x = 0f; max.x = 1f; }
            if (!ApplyBottom) min.y = 0f;
            if (!ApplyTop) max.y = 1f;

            // Bozuk deger gelirse (bazi emulatorler) dokunma.
            if (min.x < 0f || min.y < 0f || max.x > 1f || max.y > 1f
                || max.x <= min.x || max.y <= min.y) return;

            rectTransform.anchorMin = min;
            rectTransform.anchorMax = max;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }
    }
}
