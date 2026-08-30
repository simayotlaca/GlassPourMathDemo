using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// The only bridge between campaign data and glass artwork. Levels know logical
    /// glass types; this asset decides which of the project's baked vessel profiles
    /// renders each type. Keeping the mapping here lets the scene and level data stay
    /// untouched when final artwork is replaced.
    /// </summary>
    [CreateAssetMenu(
        menuName = "Liquid Sort/Bartender Glass Library",
        fileName = "BartenderGlassLibrary")]
    public sealed class BartenderGlassLibrary : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public GlassType type;
            public VesselProfile profile;
            [Min(0.01f)] public float scale;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();
        [SerializeField] private GlassVisualTheme theme;
        [SerializeField] private bool restyleLine;
        [SerializeField] private Color hiddenLayerColor = new Color(0.29f, 0.31f, 0.36f, 1f);

        public IReadOnlyList<Entry> Entries => entries;
        public GlassVisualTheme Theme => theme;
        public bool RestyleLine => restyleLine;
        public Color HiddenLayerColor => hiddenLayerColor;

        public bool TryGet(GlassType type, out VesselProfile profile, out float scale)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.type != type) continue;
                profile = entry.profile;
                scale = Mathf.Max(0.01f, entry.scale);
                return profile != null;
            }

            profile = null;
            scale = 1f;
            return false;
        }

        public bool TryValidate(GlassType type, out string reason)
        {
            if (!TryGet(type, out VesselProfile profile, out _))
            {
                reason = $"{BsRules.DisplayName(type)} için VesselProfile bağlı değil.";
                return false;
            }

            if (!profile.IsBaked)
            {
                reason = $"'{profile.name}' profili bake edilmemiş.";
                return false;
            }

            int expected = BsRules.Capacity(type);
            if (profile.capacity != expected)
            {
                reason = $"'{profile.name}' kapasitesi {profile.capacity}; "
                       + $"{BsRules.DisplayName(type)} için {expected} olmalı.";
                return false;
            }

            reason = null;
            return true;
        }

        private void OnValidate()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                entry.scale = Mathf.Max(0.01f, entry.scale);
                entries[i] = entry;
            }
        }
    }
}
