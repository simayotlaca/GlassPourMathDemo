namespace LiquidSort.Levels
{
    /// <summary>
    /// Ekonomi ve ilerleme başlangıç değerlerinin tek authoring noktası.
    ///
    /// Editor test profili production kaydından ayrı bir dosya kullanır. Böylece testte
    /// yüksek bakiye veya ileri bir kampanya slotu seçmek gerçek oyuncu ilerlemesini
    /// değiştirmez. Seed değerleri değiştirildiğinde revision artırılarak temiz bir test
    /// profili açılabilir.
    /// </summary>
    public static class BartenderProgressTuning
    {
        // Production economy
        public const int StartingCoins = 500;
        public const int MaximumLives = 5;
        public const int CoinsPerWin = 50;
        public const int PaidContinueCoinCost = 100;

#if UNITY_EDITOR
        // TEST KAPISI: true yapıldığında yalnız Editor ayrı bir kayıt dosyası kullanır.
        // Level 15'in zero-based kampanya slotu 14'tür.
        public const bool UseIsolatedEditorTestProfile = false;
        public const int EditorTestStartingCoins = 5000;
        public const int EditorTestStartingLives = MaximumLives;
        public const int EditorTestNextUnlockedCampaignSlot = 14;

        // Test seed'i değiştiğinde bunu artırmak eski test kaydını silmeden yenisini açar.
        public const int EditorTestProfileRevision = 1;
#endif

        internal static bool IsolatedEditorTestProfileEnabled
        {
            get
            {
#if UNITY_EDITOR
                return UseIsolatedEditorTestProfile;
#else
                return false;
#endif
            }
        }

        internal static int InitialCoins => IsolatedEditorTestProfileEnabled
            ? EditorTestCoins
            : StartingCoins;

        internal static int InitialLives => IsolatedEditorTestProfileEnabled
            ? EditorTestLives
            : MaximumLives;

        internal static int InitialCampaignSlot => IsolatedEditorTestProfileEnabled
            ? EditorTestCampaignSlot
            : 0;

        internal static string EditorTestSaveSuffix
        {
            get
            {
#if UNITY_EDITOR
                return EditorTestProfileRevision.ToString();
#else
                return "0";
#endif
            }
        }

        private static int EditorTestCoins
        {
            get
            {
#if UNITY_EDITOR
                return EditorTestStartingCoins < 0 ? 0 : EditorTestStartingCoins;
#else
                return StartingCoins;
#endif
            }
        }

        private static int EditorTestLives
        {
            get
            {
#if UNITY_EDITOR
                return UnityEngine.Mathf.Clamp(
                    EditorTestStartingLives, 0, MaximumLives);
#else
                return MaximumLives;
#endif
            }
        }

        private static int EditorTestCampaignSlot
        {
            get
            {
#if UNITY_EDITOR
                return UnityEngine.Mathf.Max(0,
                    EditorTestNextUnlockedCampaignSlot);
#else
                return 0;
#endif
            }
        }
    }
}
