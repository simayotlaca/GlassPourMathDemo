using LiquidSort.Levels;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Can bakiyesini test ederken 10 dakikalık rejenerasyonu beklemeden değiştirmek için
/// Editor kısayolları. Aktif kayıt dosyasına yazar; Play mode açıkken ekranlar
/// LivesChanged üzerinden anında güncellenir.
/// </summary>
public static class BartenderProgressDebugMenu
{
    private const string MenuRoot = "Tools/LiquidSort/İlerleme/";

    [MenuItem(MenuRoot + "Canı doldur", false, 10)]
    private static void RefillLives()
    {
        if (BartenderProgressService.EditorRefillLives(out string rejectionReason))
        {
            ReportLives();
            return;
        }
        Debug.LogWarning("Can doldurulamadı: " + rejectionReason);
    }

    [MenuItem(MenuRoot + "+1 can", false, 11)]
    private static void GrantOneLife()
    {
        int next = BartenderProgressService.Lives + 1;
        if (next > BartenderProgressService.MaxLives)
        {
            ReportLives();
            return;
        }
        if (BartenderProgressService.EditorSetLives(next, out string rejectionReason))
        {
            ReportLives();
            return;
        }
        Debug.LogWarning("Can eklenemedi: " + rejectionReason);
    }

    [MenuItem(MenuRoot + "Rapor: can durumu", false, 30)]
    private static void ReportLives()
    {
        System.TimeSpan timer = BartenderProgressService.LifeTimer;
        string timerText = timer <= System.TimeSpan.Zero
            ? "sayaç yok"
            : $"sonraki can {timer:mm\\:ss}";
        Debug.Log($"Can: {BartenderProgressService.Lives}/"
                + $"{BartenderProgressService.MaxLives} ({timerText}), "
                + $"altın: {BartenderProgressService.Coins}");
    }
}
