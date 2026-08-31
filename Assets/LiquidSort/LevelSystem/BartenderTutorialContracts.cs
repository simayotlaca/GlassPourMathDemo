using System;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>The authoritative gameplay fact that completes one tutorial step.</summary>
    public enum BartenderTutorialAction
    {
        SelectBottle,
        PourIntoBottle,
        DeliverBottle,
    }

    /// <summary>
    /// One authored coach-mark. Glass ids are the stable ids assigned from the level's
    /// Glasses list; the director validates every referenced bottle before taking input.
    /// </summary>
    [Serializable]
    public sealed class BartenderTutorialStep
    {
        public string StepId = "step";
        public BartenderTutorialAction Action = BartenderTutorialAction.SelectBottle;
        [Min(0)] public int PrimaryGlassId;
        public int SecondaryGlassId = -1;
        public string Eyebrow = "KRALİYET DERSİ";
        public string Title = "BARDAĞA DOKUN";
        [TextArea(1, 3)] public string Detail = "İlk hamleni birlikte yapalım.";

        public BartenderTutorialStep Clone()
        {
            return new BartenderTutorialStep
            {
                StepId = StepId,
                Action = Action,
                PrimaryGlassId = PrimaryGlassId,
                SecondaryGlassId = SecondaryGlassId,
                Eyebrow = Eyebrow,
                Title = Title,
                Detail = Detail,
            };
        }
    }

    /// <summary>Central interaction intents that an optional modal policy may filter.</summary>
    public enum BartenderInputIntent
    {
        BackgroundTap,
        BottleTap,
        Pour,
        Delivery,
    }

    /// <summary>A detached request; policies never receive mutable scene or board objects.</summary>
    public struct BartenderInputRequest
    {
        public BartenderInputIntent Intent;
        public int PrimaryGlassId;
        public int SecondaryGlassId;
        public int SelectedGlassId;

        public static BartenderInputRequest Background(int selectedGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.BackgroundTap,
                PrimaryGlassId = -1,
                SecondaryGlassId = -1,
                SelectedGlassId = selectedGlassId,
            };
        }

        public static BartenderInputRequest Bottle(int glassId, int selectedGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.BottleTap,
                PrimaryGlassId = glassId,
                SecondaryGlassId = -1,
                SelectedGlassId = selectedGlassId,
            };
        }

        public static BartenderInputRequest Pour(int sourceGlassId, int targetGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.Pour,
                PrimaryGlassId = sourceGlassId,
                SecondaryGlassId = targetGlassId,
                SelectedGlassId = sourceGlassId,
            };
        }

        public static BartenderInputRequest Delivery(int glassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.Delivery,
                PrimaryGlassId = glassId,
                SecondaryGlassId = -1,
                SelectedGlassId = -1,
            };
        }
    }

    /// <summary>
    /// Optional modal layer for world interactions. It is intentionally generic: a future
    /// accessibility flow or scripted demo can use the same gate without entering the
    /// controller's rule state machine.
    /// </summary>
    public interface IBartenderInputPolicy
    {
        bool Allows(BartenderInputRequest request, out string rejectionReason);
        void HandleRejected(BartenderInputRequest request, string rejectionReason);
    }

    /// <summary>Versioned, one-bit completion store for first-time tutorial sequences.</summary>
    public static class BartenderTutorialProgress
    {
        private const string Prefix = "LiquidSort.Bartender.Tutorial.";

        public static bool IsCompleted(string tutorialId, int version)
        {
            return PlayerPrefs.GetInt(Key(tutorialId, version), 0) != 0;
        }

        public static void Complete(string tutorialId, int version)
        {
            PlayerPrefs.SetInt(Key(tutorialId, version), 1);
            PlayerPrefs.Save();
        }

        public static void Reset(string tutorialId, int version)
        {
            PlayerPrefs.DeleteKey(Key(tutorialId, version));
            PlayerPrefs.Save();
        }

        private static string Key(string tutorialId, int version)
        {
            string safeId = string.IsNullOrWhiteSpace(tutorialId)
                ? "tutorial"
                : tutorialId.Trim();
            return Prefix + safeId + ".v" + Mathf.Max(1, version) + ".Completed";
        }
    }
}
