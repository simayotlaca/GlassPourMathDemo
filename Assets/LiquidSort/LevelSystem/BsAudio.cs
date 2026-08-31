using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Resources/Audio altındaki Bartender seslerinin tip güvenli kimlikleri.</summary>
    public enum BsSfx
    {
        PourLoop,
        PourStart,
        PourEnd,
        GlassPickup,
        GlassSet,
        Check,
        DeliverSlide,
        Invalid,
        Win,
        Fail,
        ButtonClick,
        ButtonBack,
        TabSwitch,
        SliderTick,
        ToggleOn,
        ToggleOff,
        LevelNodePop,
        MapAdvance,
    }

    /// <summary>
    /// Kaynak BartenderSort oyununun çalışan ses hattı.
    ///
    /// Klipleri Resources/Audio altından adla yükler; eksik klip normal ve sessiz bir
    /// durumdur. Tek-atımlık efektler büyüyen bir kaynak havuzunda, dökme gibi sürekli
    /// efektler ise generation korumalı bir lease üzerinde çalar. Böylece eski bir sunumun
    /// gecikmiş cleanup'ı yeni turun loop'unu kesemez.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BsAudio : MonoBehaviour
    {
        public const string MusicPreferenceKey = "LiquidSort.Bartender.Settings.Music";
        public const string SoundPreferenceKey = "LiquidSort.Bartender.Settings.Sound";

        private const string ResourceDirectory = "Audio/";
        private const string BgmName = "BGM_Bar_Loop";

        private static readonly Dictionary<BsSfx, string> ClipNames =
            new Dictionary<BsSfx, string>
            {
                { BsSfx.PourLoop, "SFX_Pour_Loop" },
                { BsSfx.PourStart, "SFX_Pour_Start" },
                { BsSfx.PourEnd, "SFX_Pour_End" },
                { BsSfx.GlassPickup, "SFX_GlassPickup" },
                { BsSfx.GlassSet, "SFX_GlassSet" },
                { BsSfx.Check, "SFX_Check" },
                { BsSfx.DeliverSlide, "SFX_DeliverSlide" },
                { BsSfx.Invalid, "SFX_Invalid" },
                { BsSfx.Win, "SFX_Win" },
                { BsSfx.Fail, "SFX_Fail" },
                { BsSfx.ButtonClick, "SFX_ButtonClick" },
                { BsSfx.ButtonBack, "SFX_ButtonBack" },
                { BsSfx.TabSwitch, "SFX_TabSwitch" },
                { BsSfx.SliderTick, "SFX_SliderTick" },
                { BsSfx.ToggleOn, "SFX_ToggleOn" },
                { BsSfx.ToggleOff, "SFX_ToggleOff" },
                { BsSfx.LevelNodePop, "SFX_LevelNodePop" },
                { BsSfx.MapAdvance, "SFX_MapAdvance" },
            };

        public static BsAudio Instance { get; private set; }

        [Header("Levels")]
        [Range(0f, 1f)] public float SfxVolume = 1f;
        [Range(0f, 1f)] public float BgmVolume = 0.5f;

        public bool SfxEnabled { get; private set; } = true;
        public bool MusicEnabled { get; private set; } = true;
        public bool Muted { get; private set; }

        private readonly Dictionary<BsSfx, AudioClip> clips =
            new Dictionary<BsSfx, AudioClip>();
        private readonly List<AudioSource> oneShotPool = new List<AudioSource>();

        private AudioSource bgmSource;
        private AudioSource loopSource;
        private AudioClip bgmClip;
        private int loopUsers;
        private int loopGeneration = 1;
        private bool loopsPaused;
        private BsSfx requestedLoop;
        private float requestedLoopVolume = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState() => Instance = null;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            LoadPreferences();
            LoadClips();
            EnsureAudioListener();

            bgmSource = CreateSource("BGM", true);
            loopSource = CreateSource("Loop", true);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Sahnede servis yoksa ayrı bir GameObject üzerinde kurar.</summary>
        public static BsAudio Ensure()
        {
            if (Instance != null) return Instance;
            var owner = new GameObject("BsAudio");
            return owner.AddComponent<BsAudio>();
        }

        /// <summary>Tek atımlık efekt. Klip eksikse bilerek hiçbir şey yapmaz.</summary>
        public void Play(BsSfx sfx, float volume = 1f, float pitch = 1f)
        {
            if (!CanPlaySfx || !clips.TryGetValue(sfx, out AudioClip clip)) return;

            AudioSource source = FreeOneShotSource();
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume) * SfxVolume;
            source.pitch = pitch;
            source.Play();
        }

        public static void UI(BsSfx sfx, float pitch = 1f) =>
            Ensure()?.Play(sfx, 1f, pitch);

        /// <summary>
        /// Bir loop kullanıcısı edinir. Klip veya ses tercihi o anda kapalı olsa bile lease
        /// sayılır; tercih tekrar açıldığında hâlâ geçerli sunum varsa loop devam eder.
        /// </summary>
        public LoopLease AcquireLoop(BsSfx sfx, float volume = 1f)
        {
            int generation = loopGeneration;
            loopUsers++;
            requestedLoop = sfx;
            requestedLoopVolume = Mathf.Clamp01(volume);
            if (!loopsPaused) StartLoop(sfx, requestedLoopVolume);
            return new LoopLease(this, generation);
        }

        public void PauseLoops()
        {
            loopsPaused = true;
            if (loopSource != null && loopSource.isPlaying) loopSource.Pause();
        }

        public void ResumeLoops()
        {
            if (!loopsPaused) return;
            loopsPaused = false;
            if (loopUsers <= 0 || !CanPlaySfx) return;

            if (loopSource != null && loopSource.clip != null) loopSource.UnPause();
            if (loopSource != null && !loopSource.isPlaying)
                StartLoop(requestedLoop, requestedLoopVolume);
        }

        /// <summary>Level değişiminde bütün eski loop lease'lerini bayatlatır.</summary>
        public void InvalidateLoops()
        {
            loopGeneration = loopGeneration == int.MaxValue ? 1 : loopGeneration + 1;
            loopUsers = 0;
            loopsPaused = false;
            StopLoop();
        }

        public void StartBgm()
        {
            if (bgmSource == null || !CanPlayMusic || bgmClip == null
                || bgmSource.isPlaying)
                return;

            bgmSource.clip = bgmClip;
            bgmSource.volume = BgmVolume;
            bgmSource.Play();
        }

        public void StopBgm()
        {
            if (bgmSource != null && bgmSource.isPlaying) bgmSource.Stop();
        }

        public void SetSfxEnabled(bool enabled)
        {
            if (SfxEnabled == enabled) return;
            SfxEnabled = enabled;
            if (!CanPlaySfx)
            {
                // Devam eden kısa bir button click'i kesme; yalnız sahipli loop'u durdur.
                StopLoop();
                return;
            }

            if (loopUsers > 0 && !loopsPaused)
                StartLoop(requestedLoop, requestedLoopVolume);
        }

        public void SetMusicEnabled(bool enabled)
        {
            if (MusicEnabled == enabled) return;
            MusicEnabled = enabled;
            if (CanPlayMusic) StartBgm();
            else StopBgm();
        }

        public void ApplyPreferences(bool soundEnabled, bool musicEnabled)
        {
            SetSfxEnabled(soundEnabled);
            SetMusicEnabled(musicEnabled);
        }

        public void SetMuted(bool muted)
        {
            if (Muted == muted) return;
            Muted = muted;
            if (Muted)
            {
                StopLoop();
                StopBgm();
                return;
            }

            if (SfxEnabled && loopUsers > 0 && !loopsPaused)
                StartLoop(requestedLoop, requestedLoopVolume);
            if (MusicEnabled) StartBgm();
        }

        private bool CanPlaySfx => !Muted && SfxEnabled;
        private bool CanPlayMusic => !Muted && MusicEnabled;

        private void LoadPreferences()
        {
            SfxEnabled = BartenderSettingsStore.SoundOn;
            MusicEnabled = BartenderSettingsStore.MusicOn;
        }

        private void LoadClips()
        {
            int missing = 0;
            foreach (KeyValuePair<BsSfx, string> pair in ClipNames)
            {
                AudioClip clip = Resources.Load<AudioClip>(ResourceDirectory + pair.Value);
                if (clip != null) clips[pair.Key] = clip;
                else missing++;
            }

            bgmClip = Resources.Load<AudioClip>(ResourceDirectory + BgmName);
            if (bgmClip == null) missing++;

            if (missing > 0)
                Debug.Log($"[BartenderAudio] Ses hattı hazır — "
                          + $"{clips.Count + (bgmClip != null ? 1 : 0)}/"
                          + $"{ClipNames.Count + 1} klip yüklü; eksikler sessiz geçecek.",
                    this);
        }

        private void EnsureAudioListener()
        {
            if (FindFirstObjectByType<AudioListener>() == null)
                gameObject.AddComponent<AudioListener>();
        }

        private AudioSource CreateSource(string sourceName, bool loop)
        {
            var owner = new GameObject(sourceName);
            owner.transform.SetParent(transform, false);
            AudioSource source = owner.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            return source;
        }

        private AudioSource FreeOneShotSource()
        {
            for (int i = 0; i < oneShotPool.Count; i++)
            {
                if (!oneShotPool[i].isPlaying) return oneShotPool[i];
            }

            AudioSource source = CreateSource($"SFX {oneShotPool.Count + 1}", false);
            oneShotPool.Add(source);
            return source;
        }

        private void StartLoop(BsSfx sfx, float volume)
        {
            if (loopSource == null || !CanPlaySfx
                || !clips.TryGetValue(sfx, out AudioClip clip))
                return;
            if (loopSource.isPlaying && loopSource.clip == clip) return;

            loopSource.clip = clip;
            loopSource.volume = Mathf.Clamp01(volume) * SfxVolume;
            loopSource.Play();
        }

        private void StopLoop()
        {
            if (loopSource != null && loopSource.isPlaying) loopSource.Stop();
        }

        private void ReleaseLoop(int generation)
        {
            if (generation != loopGeneration || loopUsers <= 0) return;
            loopUsers--;
            if (loopUsers == 0) StopLoop();
        }

        public sealed class LoopLease : IDisposable
        {
            private BsAudio owner;
            private readonly int generation;

            internal LoopLease(BsAudio owner, int generation)
            {
                this.owner = owner;
                this.generation = generation;
            }

            public void Dispose()
            {
                BsAudio current = owner;
                if (current == null) return;
                owner = null;
                current.ReleaseLoop(generation);
            }
        }
    }
}
