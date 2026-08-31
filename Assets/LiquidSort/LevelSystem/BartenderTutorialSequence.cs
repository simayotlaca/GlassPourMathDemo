using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Optional designer-authored sequence. The runtime ships with a code fallback for
    /// Level 1, while a Resources/Tutorials/RoyalFirstPour asset can replace its copy and
    /// glass anchors without changing the tutorial engine.
    /// </summary>
    [CreateAssetMenu(menuName = "Bartender Sort/Tutorial Sequence",
                    fileName = "RoyalFirstPour")]
    public sealed class BartenderTutorialSequence : ScriptableObject
    {
        public string TutorialId = "royal_first_pour";
        [Min(1)] public int Version = 1;
        [Min(0)] public int CampaignSlot;
        [Min(1)] public int LevelNumber = 1;
        [Header("Completion Copy")]
        public string CompletionEyebrow = string.Empty;
        public string CompletionTitle = "HAZIRSIN!";
        [TextArea(1, 2)]
        public string CompletionDetail = string.Empty;
        public List<BartenderTutorialStep> Steps = new List<BartenderTutorialStep>();

        public bool Matches(BartenderLevelController controller)
        {
            return controller != null && controller.CurrentLevel != null
                && controller.CurrentCampaignSlot == CampaignSlot
                && controller.CurrentLevel.Index == LevelNumber;
        }

        private void OnValidate()
        {
            Version = Mathf.Max(1, Version);
            CampaignSlot = Mathf.Max(0, CampaignSlot);
            LevelNumber = Mathf.Max(1, LevelNumber);
            if (string.IsNullOrWhiteSpace(TutorialId)) TutorialId = "tutorial";
            if (Steps == null) Steps = new List<BartenderTutorialStep>();
        }
    }
}
