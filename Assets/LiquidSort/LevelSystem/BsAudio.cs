using System;
using System.Collections;
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
        private AudioSource resultSource;
        private AudioClip bgmClip;
        private Coroutine resultTransition;
        private int resultGeneration = 1;
        private float bgmMix = 1f;
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
            resultSource = CreateSource("Result", false);
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
            if (bgmSource == null || !CanPlayMusic || bgmClip == null) return;

            bgmSource.clip = bgmClip;
            ApplyBgmMix();
            if (!bgmSource.isPlaying) bgmSource.Play();
        }

        public void StopBgm()
        {
            if (bgmSource != null && bgmSource.isPlaying) bgmSource.Stop();
        }

        /// <summary>
        /// Sonuç cümlesini ayrı kaynaktan çalarken müziği hızlıca geri çeker; cümle
        /// bittiğinde loop kaldığı sample'dan yumuşakça normale döner.
        /// </summary>
        public void PlayResult(BsSfx sfx)
        {
            if (sfx != BsSfx.Win && sfx != BsSfx.Fail)
            {
                Play(sfx);
                return;
            }
            if (!CanPlaySfx || !clips.TryGetValue(sfx, out AudioClip clip)) return;

            int generation = BeginResultTransition();
            if (resultSource == null)
            {
                Play(sfx);
                return;
            }

            resultSource.clip = clip;
            resultSource.volume = SfxVolume;
            resultSource.pitch = 1f;
            resultSource.Play();
            resultTransition = StartCoroutine(
                DuckBgmForResult(generation, clip.length));
        }

        /// <summary>
        /// Retry, sonraki seviye veya ana menüye dönüşte eski sonuç cümlesini kısa
        /// crossfade ile kapatıp BGM'i normal seviyesine getirir.
        /// </summary>
        public void RestoreBgmAfterResult()
        {
            bool resultActive = resultTransition != null
                             || (resultSource != null && resultSource.isPlaying)
                             || bgmMix < 0.999f;
            if (!resultActive)
            {
                StartBgm();
                return;
            }

            int generation = BeginResultTransition();
            StartBgm();
            resultTransition = StartCoroutine(RestoreBgm(generation));
        }

        public void SetSfxEnabled(bool enabled)
        {
            if (SfxEnabled == enabled) return;
            SfxEnabled = enabled;
            if (!CanPlaySfx)
            {
                // Havuzdaki çok kısa one-shot'ları doğal kuyruğunda bırak; sahipli loop ve
                // uzun sonuç cümlesi ise ses tercihini hemen izlesin.
                StopLoop();
                if (resultSource != null && resultSource.isPlaying)
                    resultSource.Stop();
                CancelResultTransition();
                SetBgmMix(1f);
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
                if (resultSource != null && resultSource.isPlaying)
                    resultSource.Stop();
                CancelResultTransition();
                SetBgmMix(1f);
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

        private int BeginResultTransition()
        {
            resultGeneration = resultGeneration == int.MaxValue
                ? 1
                : resultGeneration + 1;
            if (resultTransition != null)
            {
                StopCoroutine(resultTransition);
                resultTransition = null;
            }
            return resultGeneration;
        }

        private void CancelResultTransition()
        {
            resultGeneration = resultGeneration == int.MaxValue
                ? 1
                : resultGeneration + 1;
            if (resultTransition == null) return;
            StopCoroutine(resultTransition);
            resultTransition = null;
        }

        private IEnumerator DuckBgmForResult(int generation, float cueLength)
        {
            const float DuckSeconds = 0.11f;
            const float DuckMix = 0.12f;
            const float TailClearance = 0.10f;
            const float RestoreSeconds = 0.72f;

            float elapsed = 0f;
            float startMix = bgmMix;
            while (elapsed < DuckSeconds && generation == resultGeneration)
            {
                elapsed += Time.unscaledDeltaTime;
                SetBgmMix(Mathf.Lerp(startMix, DuckMix,
                    Mathf.Clamp01(elapsed / DuckSeconds)));
                yield return null;
            }
            if (generation != resultGeneration) yield break;
            SetBgmMix(DuckMix);

            float hold = Mathf.Max(0f, cueLength + TailClearance - elapsed);
            while (hold > 0f && generation == resultGeneration)
            {
                hold -= Time.unscaledDeltaTime;
                yield return null;
            }
            if (generation != resultGeneration) yield break;

            elapsed = 0f;
            while (elapsed < RestoreSeconds && generation == resultGeneration)
            {
                elapsed += Time.unscaledDeltaTime;
                SetBgmMix(Mathf.Lerp(DuckMix, 1f,
                    Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01(elapsed / RestoreSeconds))));
                yield return null;
            }
            if (generation != resultGeneration) yield break;
            SetBgmMix(1f);
            resultTransition = null;
        }

        private IEnumerator RestoreBgm(int generation)
        {
            const float CrossfadeSeconds = 0.42f;
            float elapsed = 0f;
            float startMix = bgmMix;
            float resultStartVolume = resultSource != null
                ? resultSource.volume
                : 0f;

            while (elapsed < CrossfadeSeconds && generation == resultGeneration)
            {
                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(elapsed / CrossfadeSeconds));
                SetBgmMix(Mathf.Lerp(startMix, 1f, progress));
                if (resultSource != null && resultSource.isPlaying)
                    resultSource.volume = Mathf.Lerp(resultStartVolume, 0f, progress);
                yield return null;
            }
            if (generation != resultGeneration) yield break;

            if (resultSource != null)
            {
                resultSource.Stop();
                resultSource.volume = SfxVolume;
            }
            SetBgmMix(1f);
            resultTransition = null;
        }

        private void SetBgmMix(float mix)
        {
            bgmMix = Mathf.Clamp01(mix);
            ApplyBgmMix();
        }

        private void ApplyBgmMix()
        {
            if (bgmSource != null) bgmSource.volume = BgmVolume * bgmMix;
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
