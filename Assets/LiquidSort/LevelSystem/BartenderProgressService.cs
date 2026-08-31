using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>A durable outcome attached to one gameplay attempt.</summary>
    public enum BartenderSettlementKind
    {
        Won = 1,
        Failed = 2,
        Abandoned = 3,
    }

    /// <summary>Distinguishes a fresh commit from a safe retry of the same receipt.</summary>
    public enum BartenderProgressCommitResult
    {
        Rejected,
        Applied,
        AlreadyApplied,
    }

    /// <summary>
    /// Single durable owner of campaign progress, lives and coins.
    ///
    /// Every gameplay result is committed together with its attempt receipt. A replay of
    /// the same receipt therefore cannot award coins or consume a second life. Lives use
    /// an absolute UTC deadline, so foreground time and time spent away from the app share
    /// the same regeneration clock.
    /// </summary>
    public static class BartenderProgressService
    {
        public const int DefaultStartingCoins = BartenderProgressTuning.StartingCoins;
        public const int MaxLives = BartenderProgressTuning.MaximumLives;
        public const int WinCoinReward = BartenderProgressTuning.CoinsPerWin;
        public const int FailureContinueCoinCost =
            BartenderProgressTuning.PaidContinueCoinCost;
        public const int FullLifeRefillCoinCost =
            BartenderProgressTuning.FullLifeRefillCoinCost;
        public static readonly TimeSpan LifeRegenerationInterval = TimeSpan.FromMinutes(10d);

        private const int CurrentVersion = 2;
        private const int SettlementHistoryLimit = 64;
        private static readonly long RefreshRetryIntervalTicks = TimeSpan.FromSeconds(5d).Ticks;
        private const string ProductionSaveFileName = "bartender_progress_v1.json";
        private const string EditorTestSaveFilePrefix =
            "bartender_progress_editor_test_v";
        private const string LegacyCoinsKey = "LiquidSort.Bartender.Coins";
        private const string LegacyProgressKey = "LiquidSort.Bartender.NextLevelSlot";

        [Serializable]
        private sealed class SettlementRecord
        {
            public string AttemptId;
            public int Kind;
            public int CampaignSlot;
            public int NextUnlockedOnWin = -1;
        }

        [Serializable]
        private sealed class ProgressData
        {
            public int Version = CurrentVersion;
            public int Coins = DefaultStartingCoins;
            public int Lives = MaxLives;
            public int NextUnlockedCampaignSlot;
            public long NextLifeUtcTicks;
            public string ActiveAttemptId = string.Empty;
            public int ActiveAttemptCampaignSlot = -1;
            public List<SettlementRecord> Settlements = new List<SettlementRecord>();
        }

        private static ProgressData data;
        private static bool loaded;
        private static bool mutationInProgress;
        private static long lastPublishedTimerSeconds = long.MinValue;
        private static long nextRefreshPersistenceRetryUtcTicks;
        private static bool persistenceDirty;
        private static bool interruptedAttemptSettlementPending;
        private static string pendingInterruptedAttemptId = string.Empty;
        private static int pendingInterruptedCampaignSlot = -1;

        public static int Coins
        {
            get
            {
                EnsureLoaded();
                return data.Coins;
            }
        }

        public static int Lives
        {
            get
            {
                Refresh();
                return data.Lives;
            }
        }

        public static bool IsLifeFull => Lives >= MaxLives;
        public static bool IsMax => IsLifeFull;

        /// <summary>Zero-based next campaign slot; the campaign count is its completion sentinel.</summary>
        public static int NextUnlockedCampaignSlot
        {
            get
            {
                EnsureLoaded();
                return data.NextUnlockedCampaignSlot;
            }
        }

        public static TimeSpan LifeTimer
        {
            get
            {
                Refresh();
                return RemainingLifeTime(data, DateTime.UtcNow.Ticks);
            }
        }

        public static event Action<int> CoinsChanged;
        public static event Action<int> LivesChanged;
        public static event Action<TimeSpan> LifeTimerChanged;
        public static event Action<int> ProgressChanged;

        public static bool CanAfford(int cost) => cost > 0 && Coins >= cost;

        public static bool TrySpendCoins(int cost, out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (cost <= 0)
            {
                rejectionReason = "Booster fiyatı geçersiz";
                return false;
            }
            if (data.Coins < cost)
            {
                rejectionReason = $"Yetersiz altın: {data.Coins}/{cost}";
                return false;
            }

            ProgressData next = Clone(data);
            next.Coins -= cost;
            return Commit(next, true, false, false, out rejectionReason);
        }

        public static bool TryGrantCoins(int amount, out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (amount <= 0)
            {
                rejectionReason = "Altın ödülü pozitif olmalı";
                return false;
            }

            long nextCoins = (long)data.Coins + amount;
            if (nextCoins > int.MaxValue)
            {
                rejectionReason = "Altın bakiyesi kapasiteyi aşıyor";
                return false;
            }

            ProgressData next = Clone(data);
            next.Coins = (int)nextCoins;
            return Commit(next, true, false, false, out rejectionReason);
        }

        /// <summary>
        /// Ana menüdeki can kartının atomik doldurma işlemi. Jeton ve can aynı
        /// kayıtta değişir; etkin bir oyun turu varken menü alışverişi yapılamaz.
        /// </summary>
        public static bool TryRefillLivesToMaximum(int coinCost,
                                                   out string rejectionReason)
        {
            Refresh();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (coinCost <= 0)
            {
                rejectionReason = "Can doldurma ücreti geçersiz";
                return false;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId))
            {
                rejectionReason = "Etkin tur sırasında can doldurulamaz";
                return false;
            }
            if (data.Lives >= MaxLives)
            {
                rejectionReason = "Can zaten dolu";
                return false;
            }
            if (data.Coins < coinCost)
            {
                rejectionReason = $"Yetersiz jeton: {data.Coins}/{coinCost}";
                return false;
            }

            ProgressData next = Clone(data);
            next.Coins -= coinCost;
            next.Lives = MaxLives;
            next.NextLifeUtcTicks = 0L;
            return Commit(next, true, true, false, out rejectionReason);
        }

        /// <summary>
        /// Failure kartındaki ücretli devam akışı için bir can satın almayı ve aynı
        /// bölümün yeni tur makbuzunu tek kalıcı işlemde açar. Böylece para düşüp yeni
        /// turun açılamadığı yarım bir durum oluşmaz.
        /// </summary>
        public static bool TryPurchaseLifeAndBeginAttempt(
            int campaignSlot, int coinCost, out string attemptId,
            out string rejectionReason)
        {
            Refresh();
            attemptId = null;
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (campaignSlot < 0)
            {
                rejectionReason = "Bölüm kimliği geçersiz";
                return false;
            }
            if (coinCost <= 0)
            {
                rejectionReason = "Devam ücreti geçersiz";
                return false;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId))
            {
                rejectionReason = "Başka bir etkin tur sonuçlandırılmayı bekliyor";
                return false;
            }
            if (data.Lives >= MaxLives)
            {
                rejectionReason = "Can zaten dolu";
                return false;
            }
            if (data.Coins < coinCost)
            {
                rejectionReason = $"Yetersiz altın: {data.Coins}/{coinCost}";
                return false;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            ProgressData next = Clone(data);
            next.Coins -= coinCost;
            next.Lives++;
            next.NextLifeUtcTicks = next.Lives >= MaxLives
                ? 0L
                : next.NextLifeUtcTicks > nowTicks
                    ? next.NextLifeUtcTicks
                    : SafeAddTicks(nowTicks, LifeRegenerationInterval.Ticks);
            next.ActiveAttemptId = Guid.NewGuid().ToString("N");
            next.ActiveAttemptCampaignSlot = campaignSlot;
            if (!Commit(next, true, true, false, out rejectionReason)) return false;
            attemptId = data.ActiveAttemptId;
            return true;
        }

        /// <summary>
        /// Opens (or restores after a same-process scene reload) the durable attempt for
        /// a slot. A new process treats a still-open receipt as a force-closed abandon.
        /// Starting does not spend a life; an empty life balance rejects Play.
        /// </summary>
        public static bool TryBeginAttempt(int campaignSlot, out string attemptId,
                                           out string rejectionReason)
        {
            Refresh();
            attemptId = null;
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (campaignSlot < 0)
            {
                rejectionReason = "Bölüm kimliği geçersiz";
                return false;
            }
            if (data.Lives <= 0)
            {
                rejectionReason = "Canın dolmasını bekle";
                return false;
            }

            if (!string.IsNullOrEmpty(data.ActiveAttemptId)
                && data.ActiveAttemptCampaignSlot == campaignSlot)
            {
                attemptId = data.ActiveAttemptId;
                return true;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId))
            {
                rejectionReason = "Başka bir etkin tur sonuçlandırılmayı bekliyor";
                return false;
            }

            ProgressData next = Clone(data);
            next.ActiveAttemptId = Guid.NewGuid().ToString("N");
            next.ActiveAttemptCampaignSlot = campaignSlot;
            if (!Commit(next, false, false, false, out rejectionReason)) return false;
            attemptId = data.ActiveAttemptId;
            return true;
        }

        /// <summary>
        /// Atomically settles an attempt. Win advances progress and grants 50 coins;
        /// failure/abandon consumes exactly one life. Identical receipt replays are safe.
        /// </summary>
        public static BartenderProgressCommitResult TrySettleAttempt(
            string attemptId, BartenderSettlementKind kind, int campaignSlot,
            int nextUnlockedOnWin, out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason))
                return BartenderProgressCommitResult.Rejected;
            if (string.IsNullOrWhiteSpace(attemptId) || campaignSlot < 0)
            {
                rejectionReason = "Tur makbuzu geçersiz";
                return BartenderProgressCommitResult.Rejected;
            }
            if (kind != BartenderSettlementKind.Won
                && kind != BartenderSettlementKind.Failed
                && kind != BartenderSettlementKind.Abandoned)
            {
                rejectionReason = "Tur sonucu geçersiz";
                return BartenderProgressCommitResult.Rejected;
            }
            int receiptProgressTarget = kind == BartenderSettlementKind.Won
                ? nextUnlockedOnWin
                : -1;
            if (kind == BartenderSettlementKind.Won
                && (campaignSlot == int.MaxValue
                    || nextUnlockedOnWin != campaignSlot + 1))
            {
                rejectionReason = "Kampanya ilerlemesi geçersiz";
                return BartenderProgressCommitResult.Rejected;
            }

            SettlementRecord existing = FindSettlement(attemptId);
            if (existing != null)
            {
                if (existing.Kind == (int)kind
                    && existing.CampaignSlot == campaignSlot
                    && existing.NextUnlockedOnWin == receiptProgressTarget)
                    return BartenderProgressCommitResult.AlreadyApplied;
                rejectionReason = "Tur makbuzu farklı bir sonuçla zaten işlendi";
                return BartenderProgressCommitResult.Rejected;
            }

            if (!string.Equals(data.ActiveAttemptId, attemptId, StringComparison.Ordinal)
                || data.ActiveAttemptCampaignSlot != campaignSlot)
            {
                rejectionReason = "Tur artık etkin değil";
                return BartenderProgressCommitResult.Rejected;
            }

            ProgressData next = Clone(data);
            bool coinsChanged = false;
            bool progressChanged = false;
            long nowTicks = DateTime.UtcNow.Ticks;
            ReconcileLives(next, nowTicks);

            if (kind == BartenderSettlementKind.Won)
            {
                if (next.Coins > int.MaxValue - WinCoinReward)
                {
                    rejectionReason = "Altın bakiyesi kapasiteyi aşıyor";
                    return BartenderProgressCommitResult.Rejected;
                }

                next.Coins += WinCoinReward;
                coinsChanged = true;
                int unlocked = Math.Max(next.NextUnlockedCampaignSlot, nextUnlockedOnWin);
                progressChanged = unlocked != next.NextUnlockedCampaignSlot;
                next.NextUnlockedCampaignSlot = unlocked;
            }
            else
            {
                if (next.Lives <= 0)
                {
                    rejectionReason = "Harcanacak can kalmadı";
                    return BartenderProgressCommitResult.Rejected;
                }

                ConsumeLife(next, nowTicks);
            }

            next.Settlements.Add(new SettlementRecord
            {
                AttemptId = attemptId,
                Kind = (int)kind,
                CampaignSlot = campaignSlot,
                NextUnlockedOnWin = receiptProgressTarget,
            });
            TrimSettlementHistory(next.Settlements);
            next.ActiveAttemptId = string.Empty;
            next.ActiveAttemptCampaignSlot = -1;

            bool livesChanged = next.Lives != data.Lives;

            if (!Commit(next, coinsChanged, livesChanged, progressChanged,
                        out rejectionReason))
                return BartenderProgressCommitResult.Rejected;
            return BartenderProgressCommitResult.Applied;
        }

        /// <summary>Reconciles absolute UTC regeneration and publishes countdown seconds.</summary>
        public static void Refresh()
        {
            EnsureLoaded();
            if (mutationInProgress) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (interruptedAttemptSettlementPending)
            {
                RetryInterruptedAttemptSettlement(nowTicks);
                if (interruptedAttemptSettlementPending)
                    PublishLifeTimerIfChanged(nowTicks);
                return;
            }
            if (!persistenceDirty && !NeedsLifeReconcile(data, nowTicks))
            {
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }
            if (nowTicks < nextRefreshPersistenceRetryUtcTicks)
            {
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }

            ProgressData next = Clone(data);
            bool livesChanged = ReconcileLives(next, nowTicks);
            bool durableChange = persistenceDirty || livesChanged
                              || next.NextLifeUtcTicks != data.NextLifeUtcTicks;

            if (durableChange)
            {
                if (!Commit(next, false, livesChanged, false, out _))
                    nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(nowTicks,
                        RefreshRetryIntervalTicks);
                return;
            }

            PublishLifeTimerIfChanged(nowTicks);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor test kolaylığı: aktif kaydın can sayısını doğrudan yazar. Üretim
        /// akışlarıyla aynı atomik commit kullanılır; böylece dosya, sayaç ve
        /// LivesChanged dinleyicileri normal oyundaki gibi güncellenir.
        /// </summary>
        public static bool EditorSetLives(int value, out string rejectionReason)
        {
            Refresh();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;

            int target = Mathf.Clamp(value, 0, MaxLives);
            long nowTicks = DateTime.UtcNow.Ticks;
            ProgressData next = Clone(data);
            next.Lives = target;
            next.NextLifeUtcTicks = target >= MaxLives
                ? 0L
                : SafeAddTicks(nowTicks, LifeRegenerationInterval.Ticks);

            bool livesChanged = next.Lives != data.Lives;
            if (!livesChanged && next.NextLifeUtcTicks == data.NextLifeUtcTicks)
                return true;
            return Commit(next, false, livesChanged, false, out rejectionReason);
        }

        /// <summary>Editor test kolaylığı: canı tavana çeker.</summary>
        public static bool EditorRefillLives(out string rejectionReason) =>
            EditorSetLives(MaxLives, out rejectionReason);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            data = null;
            loaded = false;
            mutationInProgress = false;
            lastPublishedTimerSeconds = long.MinValue;
            nextRefreshPersistenceRetryUtcTicks = 0L;
            persistenceDirty = false;
            interruptedAttemptSettlementPending = false;
            pendingInterruptedAttemptId = string.Empty;
            pendingInterruptedCampaignSlot = -1;
            CoinsChanged = null;
            LivesChanged = null;
            LifeTimerChanged = null;
            ProgressChanged = null;
            BartenderProgressRuntimeDriver.ResetRegistration();
        }

        public static void HardReset()
        {
            ResetStaticState();
            UnityEngine.PlayerPrefs.DeleteAll();
            try { if (System.IO.File.Exists(SavePath)) System.IO.File.Delete(SavePath); } catch { }
            try { if (System.IO.File.Exists(SavePath + ".tmp")) System.IO.File.Delete(SavePath + ".tmp"); } catch { }
            try { if (System.IO.File.Exists(SavePath + ".bak")) System.IO.File.Delete(SavePath + ".bak"); } catch { }
            loaded = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadBeforeFirstScene() => EnsureLoaded();

        private static bool ReconcileLives(ProgressData target, long nowTicks)
        {
            int previousLives = target.Lives;
            if (target.Lives >= MaxLives)
            {
                target.Lives = MaxLives;
                target.NextLifeUtcTicks = 0L;
                return previousLives != target.Lives;
            }

            long interval = LifeRegenerationInterval.Ticks;
            if (target.NextLifeUtcTicks <= 0L
                || target.NextLifeUtcTicks > SafeAddTicks(nowTicks, interval))
                target.NextLifeUtcTicks = SafeAddTicks(nowTicks, interval);

            if (nowTicks < target.NextLifeUtcTicks) return previousLives != target.Lives;

            long elapsed = nowTicks - target.NextLifeUtcTicks;
            long earned = 1L + elapsed / interval;
            int room = MaxLives - target.Lives;
            int applied = (int)Math.Min(room, earned);
            target.Lives += applied;
            target.NextLifeUtcTicks = target.Lives >= MaxLives
                ? 0L
                : SafeAddTicks(target.NextLifeUtcTicks, applied * interval);
            return previousLives != target.Lives;
        }

        private static bool NeedsLifeReconcile(ProgressData source, long nowTicks)
        {
            if (source.Lives >= MaxLives) return source.NextLifeUtcTicks != 0L;
            long latestValidDeadline = SafeAddTicks(nowTicks,
                LifeRegenerationInterval.Ticks);
            return source.NextLifeUtcTicks <= 0L
                || source.NextLifeUtcTicks > latestValidDeadline
                || nowTicks >= source.NextLifeUtcTicks;
        }

        private static TimeSpan RemainingLifeTime(ProgressData source, long nowTicks)
        {
            if (source == null || source.Lives >= MaxLives
                || source.NextLifeUtcTicks <= nowTicks)
                return TimeSpan.Zero;
            return TimeSpan.FromTicks(source.NextLifeUtcTicks - nowTicks);
        }

        private static void PublishLifeTimerIfChanged(long nowTicks)
        {
            TimeSpan remaining = RemainingLifeTime(data, nowTicks);
            long seconds = remaining <= TimeSpan.Zero
                ? 0L
                : (long)Math.Ceiling(remaining.TotalSeconds);
            if (seconds == lastPublishedTimerSeconds) return;
            lastPublishedTimerSeconds = seconds;
            InvokeSafely(LifeTimerChanged, TimeSpan.FromSeconds(seconds));
        }

        private static bool CanMutate(out string rejectionReason)
        {
            if (mutationInProgress)
            {
                rejectionReason = "Başka bir kayıt işlemi sürüyor";
                return false;
            }
            if (interruptedAttemptSettlementPending)
            {
                rejectionReason = "Önceki tur kapatılıyor; tekrar dene";
                return false;
            }
            rejectionReason = null;
            return true;
        }

        private static bool Commit(ProgressData next, bool coinsChanged,
                                   bool livesChanged, bool progressChanged,
                                   out string rejectionReason)
        {
            rejectionReason = null;
            if (mutationInProgress)
            {
                rejectionReason = "Başka bir kayıt işlemi sürüyor";
                return false;
            }

            mutationInProgress = true;
            try
            {
                Persist(next);
                AdoptCommittedData(next, coinsChanged, livesChanged, progressChanged);
                return true;
            }
            catch (Exception exception)
            {
                // A recoverable rename may have installed the new canonical file before
                // a later cleanup operation threw. Treat an exact on-disk match as the
                // successful atomic commit it already is; never replay its life cost.
                ProgressData persisted = TryLoad(SavePath);
                if (ProgressDataEquals(persisted, next))
                {
                    AdoptCommittedData(persisted, coinsChanged, livesChanged,
                        progressChanged);
                    return true;
                }
                rejectionReason = "Oyuncu ilerlemesi kaydedilemedi";
                Debug.LogException(exception);
                return false;
            }
            finally
            {
                mutationInProgress = false;
            }
        }

        private static void AdoptCommittedData(ProgressData committed,
                                               bool coinsChanged,
                                               bool livesChanged,
                                               bool progressChanged)
        {
            data = committed;
            persistenceDirty = false;
            nextRefreshPersistenceRetryUtcTicks = 0L;
            if (coinsChanged) InvokeSafely(CoinsChanged, data.Coins);
            if (livesChanged) InvokeSafely(LivesChanged, data.Lives);
            if (progressChanged)
                InvokeSafely(ProgressChanged, data.NextUnlockedCampaignSlot);
            PublishLifeTimerIfChanged(DateTime.UtcNow.Ticks);
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            bool isolatedEditorProfile =
                BartenderProgressTuning.IsolatedEditorTestProfileEnabled;
            ProgressData loadedData = TryLoad(SavePath);
            if (loadedData == null) loadedData = TryLoad(SavePath + ".tmp");
            if (loadedData == null) loadedData = TryLoad(SavePath + ".bak");
            if (loadedData == null)
            {
                loadedData = isolatedEditorProfile
                    ? new ProgressData
                    {
                        Coins = BartenderProgressTuning.InitialCoins,
                        Lives = BartenderProgressTuning.InitialLives,
                        NextUnlockedCampaignSlot =
                            BartenderProgressTuning.InitialCampaignSlot,
                    }
                    : new ProgressData
                    {
                        Coins = Mathf.Max(0,
                            PlayerPrefs.GetInt(LegacyCoinsKey, DefaultStartingCoins)),
                        NextUnlockedCampaignSlot = Mathf.Max(0,
                            PlayerPrefs.GetInt(LegacyProgressKey, 0)),
                    };
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            Normalize(loadedData, nowTicks);
            lastPublishedTimerSeconds = long.MinValue;

            if (HasActiveAttempt(loadedData))
            {
                string interruptedAttemptId = loadedData.ActiveAttemptId;
                int interruptedCampaignSlot = loadedData.ActiveAttemptCampaignSlot;
                if (!TryCreateInterruptedAttemptSettlement(loadedData, nowTicks,
                        out ProgressData settledData, out _))
                {
                    data = loadedData;
                    loaded = true;
                    persistenceDirty = false;
                    ArmInterruptedAttemptSettlement(interruptedAttemptId,
                        interruptedCampaignSlot, nowTicks);
                    return;
                }
                try
                {
                    Persist(settledData);
                    data = settledData;
                    loaded = true;
                    persistenceDirty = false;
                    ClearInterruptedAttemptSettlementPending();
                    return;
                }
                catch (Exception exception)
                {
                    ProgressData persisted = TryLoadResolvedInterruptedAttempt(
                        interruptedAttemptId, nowTicks);
                    if (persisted != null)
                    {
                        data = persisted;
                        loaded = true;
                        persistenceDirty = false;
                        ClearInterruptedAttemptSettlementPending();
                        return;
                    }

                    // Never expose an unpersisted life loss. Keep the original active
                    // receipt and block new mutations until the atomic settlement retries.
                    data = loadedData;
                    loaded = true;
                    persistenceDirty = false;
                    ArmInterruptedAttemptSettlement(interruptedAttemptId,
                        interruptedCampaignSlot, nowTicks);
                    Debug.LogException(exception);
                    return;
                }
            }

            data = loadedData;
            loaded = true;

            // Normalization includes offline life regeneration and recovery from a
            // temporary/backup file, so its canonical form must be made durable too.
            try
            {
                Persist(data);
                persistenceDirty = false;
            }
            catch (Exception exception)
            {
                persistenceDirty = true;
                nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(
                    nowTicks, RefreshRetryIntervalTicks);
                Debug.LogException(exception);
            }
        }

        private static string SavePath => Path.Combine(
            Application.persistentDataPath,
            BartenderProgressTuning.IsolatedEditorTestProfileEnabled
                ? EditorTestSaveFilePrefix
                  + BartenderProgressTuning.EditorTestSaveSuffix + ".json"
                : ProductionSaveFileName);

        private static ProgressData TryLoad(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonUtility.FromJson<ProgressData>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Oyuncu ilerleme kaydı okunamadı; kurtarma kaydı "
                               + "aranacak. " + exception.Message);
                return null;
            }
        }

        private static void Normalize(ProgressData target, long nowTicks)
        {
            int loadedVersion = target.Version;
            target.Version = CurrentVersion;
            target.Coins = Math.Max(0, target.Coins);
            target.Lives = Mathf.Clamp(target.Lives, 0, MaxLives);
            target.NextUnlockedCampaignSlot = Math.Max(0,
                target.NextUnlockedCampaignSlot);
            target.ActiveAttemptId = target.ActiveAttemptId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target.ActiveAttemptId)
                || target.ActiveAttemptCampaignSlot < 0)
            {
                target.ActiveAttemptId = string.Empty;
                target.ActiveAttemptCampaignSlot = -1;
            }
            target.Settlements = target.Settlements ?? new List<SettlementRecord>();
            for (int i = target.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = target.Settlements[i];
                if (record == null || string.IsNullOrWhiteSpace(record.AttemptId))
                {
                    target.Settlements.RemoveAt(i);
                    continue;
                }
                if (loadedVersion < 2)
                {
                    record.NextUnlockedOnWin = record.Kind == (int)BartenderSettlementKind.Won
                        && record.CampaignSlot < int.MaxValue
                        ? record.CampaignSlot + 1
                        : -1;
                }
            }
            TrimSettlementHistory(target.Settlements, target.ActiveAttemptId);
            ReconcileLives(target, nowTicks);
        }

        private static void Persist(ProgressData source)
        {
            string path = SavePath;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(source));
            using (var stream = new FileStream(temporaryPath, FileMode.Create,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            try
            {
                File.Replace(temporaryPath, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (NotSupportedException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (IOException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (UnauthorizedAccessException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
        }

        private static void ReplaceWithRecoverableRename(string path,
                                                         string temporaryPath)
        {
            string backupPath = path + ".bak";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(path, backupPath);
            try
            {
                File.Move(temporaryPath, path);
                File.Delete(backupPath);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backupPath))
                    File.Move(backupPath, path);
                throw;
            }
        }

        private static ProgressData Clone(ProgressData source)
        {
            var clone = new ProgressData
            {
                Version = source.Version,
                Coins = source.Coins,
                Lives = source.Lives,
                NextUnlockedCampaignSlot = source.NextUnlockedCampaignSlot,
                NextLifeUtcTicks = source.NextLifeUtcTicks,
                ActiveAttemptId = source.ActiveAttemptId,
                ActiveAttemptCampaignSlot = source.ActiveAttemptCampaignSlot,
                Settlements = new List<SettlementRecord>(source.Settlements.Count),
            };
            for (int i = 0; i < source.Settlements.Count; i++)
            {
                SettlementRecord record = source.Settlements[i];
                clone.Settlements.Add(new SettlementRecord
                {
                    AttemptId = record.AttemptId,
                    Kind = record.Kind,
                    CampaignSlot = record.CampaignSlot,
                    NextUnlockedOnWin = record.NextUnlockedOnWin,
                });
            }
            return clone;
        }

        private static bool ProgressDataEquals(ProgressData left, ProgressData right)
        {
            if (left == null || right == null
                || left.Version != right.Version
                || left.Coins != right.Coins
                || left.Lives != right.Lives
                || left.NextUnlockedCampaignSlot != right.NextUnlockedCampaignSlot
                || left.NextLifeUtcTicks != right.NextLifeUtcTicks
                || !string.Equals(left.ActiveAttemptId, right.ActiveAttemptId,
                    StringComparison.Ordinal)
                || left.ActiveAttemptCampaignSlot != right.ActiveAttemptCampaignSlot)
                return false;

            int leftCount = left.Settlements?.Count ?? 0;
            int rightCount = right.Settlements?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (int i = 0; i < leftCount; i++)
            {
                SettlementRecord a = left.Settlements[i];
                SettlementRecord b = right.Settlements[i];
                if (a == null || b == null)
                {
                    if (!ReferenceEquals(a, b)) return false;
                    continue;
                }
                if (!string.Equals(a.AttemptId, b.AttemptId,
                        StringComparison.Ordinal)
                    || a.Kind != b.Kind
                    || a.CampaignSlot != b.CampaignSlot
                    || a.NextUnlockedOnWin != b.NextUnlockedOnWin)
                    return false;
            }
            return true;
        }

        private static SettlementRecord FindSettlement(string attemptId)
            => FindSettlement(data, attemptId);

        private static SettlementRecord FindSettlement(ProgressData source,
                                                       string attemptId)
        {
            if (source?.Settlements == null) return null;
            for (int i = source.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = source.Settlements[i];
                if (string.Equals(record.AttemptId, attemptId, StringComparison.Ordinal))
                    return record;
            }
            return null;
        }

        private static bool ConsumeLife(ProgressData target, long nowTicks)
        {
            if (target == null || target.Lives <= 0) return false;
            bool wasFull = target.Lives >= MaxLives;
            target.Lives--;
            if (wasFull || target.NextLifeUtcTicks <= nowTicks)
                target.NextLifeUtcTicks = SafeAddTicks(nowTicks,
                    LifeRegenerationInterval.Ticks);
            return true;
        }

        private static bool HasActiveAttempt(ProgressData source) =>
            source != null && !string.IsNullOrWhiteSpace(source.ActiveAttemptId)
            && source.ActiveAttemptCampaignSlot >= 0;

        /// <summary>
        /// A durable active receipt loaded into a new static/process lifetime means the
        /// previous process ended before settling its round. Convert it into the same
        /// exact-once Abandoned receipt used by the pause confirmation path.
        /// </summary>
        private static bool TryCreateInterruptedAttemptSettlement(
            ProgressData source, long nowTicks, out ProgressData candidate,
            out bool livesChanged)
        {
            candidate = null;
            livesChanged = false;
            if (!HasActiveAttempt(source))
                return false;

            candidate = Clone(source);
            string attemptId = candidate.ActiveAttemptId;
            int campaignSlot = candidate.ActiveAttemptCampaignSlot;
            SettlementRecord existing = FindSettlement(candidate, attemptId);
            if (existing == null)
            {
                // Corrupt saves can contain an active receipt at zero lives. Keep the
                // receipt pending until regeneration supplies the life that is owed.
                if (!ConsumeLife(candidate, nowTicks))
                {
                    candidate = null;
                    return false;
                }
                candidate.Settlements.Add(new SettlementRecord
                {
                    AttemptId = attemptId,
                    Kind = (int)BartenderSettlementKind.Abandoned,
                    CampaignSlot = campaignSlot,
                    NextUnlockedOnWin = -1,
                });
                TrimSettlementHistory(candidate.Settlements);
            }

            candidate.ActiveAttemptId = string.Empty;
            candidate.ActiveAttemptCampaignSlot = -1;
            livesChanged = candidate.Lives != source.Lives;
            return true;
        }

        private static ProgressData TryLoadResolvedInterruptedAttempt(
            string attemptId, long nowTicks)
        {
            ProgressData persisted = TryLoad(SavePath);
            if (persisted == null) return null;
            Normalize(persisted, nowTicks);
            if (string.Equals(persisted.ActiveAttemptId, attemptId,
                    StringComparison.Ordinal)
                || FindSettlement(persisted, attemptId) == null)
                return null;
            return persisted;
        }

        private static void RetryInterruptedAttemptSettlement(long nowTicks)
        {
            if (!interruptedAttemptSettlementPending
                || nowTicks < nextRefreshPersistenceRetryUtcTicks)
                return;

            if (!string.Equals(data.ActiveAttemptId, pendingInterruptedAttemptId,
                    StringComparison.Ordinal)
                || data.ActiveAttemptCampaignSlot != pendingInterruptedCampaignSlot)
            {
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            ProgressData refreshed = Clone(data);
            ReconcileLives(refreshed, nowTicks);
            if (!TryCreateInterruptedAttemptSettlement(refreshed, nowTicks,
                    out ProgressData candidate, out _))
            {
                if (HasActiveAttempt(refreshed))
                {
                    data = refreshed;
                    nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(nowTicks,
                        RefreshRetryIntervalTicks);
                    return;
                }
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            bool livesChanged = candidate.Lives != data.Lives;
            if (Commit(candidate, false, livesChanged, false, out _))
            {
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            ProgressData persisted = TryLoadResolvedInterruptedAttempt(
                pendingInterruptedAttemptId, nowTicks);
            if (persisted != null)
            {
                int previousLives = data.Lives;
                data = persisted;
                persistenceDirty = false;
                ClearInterruptedAttemptSettlementPending();
                if (data.Lives != previousLives)
                    InvokeSafely(LivesChanged, data.Lives);
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }

            nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(nowTicks,
                RefreshRetryIntervalTicks);
        }

        private static void ClearInterruptedAttemptSettlementPending()
        {
            interruptedAttemptSettlementPending = false;
            pendingInterruptedAttemptId = string.Empty;
            pendingInterruptedCampaignSlot = -1;
            nextRefreshPersistenceRetryUtcTicks = 0L;
        }

        private static void ArmInterruptedAttemptSettlement(string attemptId,
                                                            int campaignSlot,
                                                            long nowTicks)
        {
            interruptedAttemptSettlementPending = true;
            pendingInterruptedAttemptId = attemptId;
            pendingInterruptedCampaignSlot = campaignSlot;
            nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(nowTicks,
                RefreshRetryIntervalTicks);
        }

        private static void TrimSettlementHistory(List<SettlementRecord> records,
                                                  string preserveAttemptId = null)
        {
            while (records.Count > SettlementHistoryLimit)
            {
                int removeIndex = 0;
                if (!string.IsNullOrWhiteSpace(preserveAttemptId))
                {
                    removeIndex = records.FindIndex(record => record == null
                        || !string.Equals(record.AttemptId, preserveAttemptId,
                            StringComparison.Ordinal));
                    if (removeIndex < 0) removeIndex = 0;
                }
                records.RemoveAt(removeIndex);
            }
        }

        private static long SafeAddTicks(long value, long ticks)
        {
            if (ticks > 0L && value > DateTime.MaxValue.Ticks - ticks)
                return DateTime.MaxValue.Ticks;
            if (ticks < 0L && value < DateTime.MinValue.Ticks - ticks)
                return DateTime.MinValue.Ticks;
            return value + ticks;
        }

        private static void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] subscribers = handlers.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { ((Action<T>)subscribers[i]).Invoke(value); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }
    }

    /// <summary>
    /// Hidden, scene-independent clock driver. UI presenters only observe the service;
    /// they never own regeneration. Scene/focus transitions force an immediate UTC sync.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class BartenderProgressRuntimeDriver : MonoBehaviour
    {
        private static BartenderProgressRuntimeDriver instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (instance != null) return;
            BartenderProgressRuntimeDriver existing =
                FindFirstObjectByType<BartenderProgressRuntimeDriver>(
                    FindObjectsInactive.Include);
            if (existing != null)
            {
                instance = existing;
                BartenderProgressService.Refresh();
                return;
            }
            var host = new GameObject("Bartender Progress Runtime");
            host.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(host);
            instance = host.AddComponent<BartenderProgressRuntimeDriver>();
            BartenderProgressService.Refresh();
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        internal static void ResetRegistration() => instance = null;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void Update() => BartenderProgressService.Refresh();

        private static void HandleSceneLoaded(Scene _, LoadSceneMode __) =>
            BartenderProgressService.Refresh();

        private void OnApplicationPause(bool _) => BartenderProgressService.Refresh();

        private void OnApplicationFocus(bool _) => BartenderProgressService.Refresh();

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
