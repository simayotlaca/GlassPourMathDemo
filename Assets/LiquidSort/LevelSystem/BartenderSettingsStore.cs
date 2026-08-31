using System;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Shared, persistent settings authority for menu and in-game presenters.</summary>
    public static class BartenderSettingsStore
    {
        private enum RuntimeChannel
        {
            Music,
            Sound,
            Vibration,
        }

        private const string VibrationPreferenceKey =
            "LiquidSort.Bartender.Settings.Vibration";

        private static bool loaded;
        private static bool mutationInProgress;
        private static bool musicOn = true;
        private static bool soundOn = true;
        private static bool vibrationOn = true;

        public static bool MusicOn { get { EnsureLoaded(); return musicOn; } }
        public static bool SoundOn { get { EnsureLoaded(); return soundOn; } }
        public static bool VibrationOn { get { EnsureLoaded(); return vibrationOn; } }

        public static event Action SettingsChanged;

        public static bool ToggleMusic() => SetMusicOn(!MusicOn);
        public static bool ToggleSound() => SetSoundOn(!SoundOn);
        public static bool ToggleVibration() => SetVibrationOn(!VibrationOn);

        public static bool SetMusicOn(bool enabled)
        {
            EnsureLoaded();
            Commit(BsAudio.MusicPreferenceKey, enabled, ref musicOn,
                RuntimeChannel.Music);
            return musicOn;
        }

        public static bool SetSoundOn(bool enabled)
        {
            EnsureLoaded();
            Commit(BsAudio.SoundPreferenceKey, enabled, ref soundOn,
                RuntimeChannel.Sound);
            return soundOn;
        }

        public static bool SetVibrationOn(bool enabled)
        {
            EnsureLoaded();
            Commit(VibrationPreferenceKey, enabled, ref vibrationOn,
                RuntimeChannel.Vibration);
            return vibrationOn;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            loaded = false;
            mutationInProgress = false;
            musicOn = true;
            soundOn = true;
            vibrationOn = true;
            SettingsChanged = null;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            musicOn = PlayerPrefs.GetInt(BsAudio.MusicPreferenceKey, 1) != 0;
            soundOn = PlayerPrefs.GetInt(BsAudio.SoundPreferenceKey, 1) != 0;
            vibrationOn = PlayerPrefs.GetInt(VibrationPreferenceKey, 1) != 0;
            loaded = true;
        }

        private static bool Commit(string key, bool enabled, ref bool current,
                                   RuntimeChannel channel)
        {
            if (mutationInProgress || current == enabled) return false;
            bool previous = current;
            mutationInProgress = true;
            try
            {
                PlayerPrefs.SetInt(key, enabled ? 1 : 0);
                PlayerPrefs.Save();
                current = enabled;
                ApplyRuntime(channel, enabled);
                InvokeSafely(SettingsChanged);
                return true;
            }
            catch (Exception exception)
            {
                try { PlayerPrefs.SetInt(key, previous ? 1 : 0); }
                catch { /* Preserve the original persistence error. */ }
                Debug.LogException(exception);
                return false;
            }
            finally
            {
                mutationInProgress = false;
            }
        }

        private static void ApplyRuntime(RuntimeChannel channel, bool enabled)
        {
            if (channel == RuntimeChannel.Vibration) return;
            try
            {
                BsAudio audio = BsAudio.Ensure();
                if (channel == RuntimeChannel.Music) audio?.SetMusicEnabled(enabled);
                else if (channel == RuntimeChannel.Sound) audio?.SetSfxEnabled(enabled);
            }
            catch (Exception exception)
            {
                // The setting is already durable. Audio projection may recover when the
                // next scene/audio host is created, so never roll back the preference.
                Debug.LogException(exception);
            }
        }

        private static void InvokeSafely(Action handlers)
        {
            if (handlers == null) return;
            Delegate[] subscribers = handlers.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { ((Action)subscribers[i]).Invoke(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }
    }
}
