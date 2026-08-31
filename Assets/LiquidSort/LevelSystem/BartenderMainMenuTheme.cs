using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Main-menu artwork is kept outside Resources so the original high-resolution
    /// files have one canonical copy. This tiny Resources asset carries the runtime
    /// references and makes the menu independent from scene/builder serialization.
    /// </summary>
    public sealed class BartenderMainMenuTheme : ScriptableObject
    {
        [Header("Background")]
        public Sprite Background;

        [Header("Typography")]
        public Font UiFont;

        [Header("Top HUD")]
        public Sprite ResourceFrame;
        public Sprite Heart;
        public Sprite CoinCocktail;
        public Sprite AddButton;

        [Header("Primary action")]
        public Sprite PlayFrame;

        [Header("Settings")]
        public Sprite SettingsPanel;
        public Sprite SettingsFrame;
        public Sprite SettingsGear;
        public Sprite MusicIcon;
        public Sprite SoundIcon;
        public Sprite VibrationIcon;
        public Sprite MuteSlash;
    }
}
