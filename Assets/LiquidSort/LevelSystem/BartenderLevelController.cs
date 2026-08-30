using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public enum BartenderLevelState
    {
        Unloaded,
        Playing,
        Paused,
        Won,
        Failed,
        CampaignComplete
    }

    public enum BartenderFailureReason
    {
        None,
        NoLegalMoves,
        OrderTimedOut,
        PresentationDesynced
    }

    /// <summary>
    /// Thin runtime host for the imported BartenderSort campaign. BsBoard remains the
    /// sole gameplay authority; LiquidBottle and PourAnimator are presentation ports.
    /// No source scene, UI, prefab, tween package, economy or menu code is required.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderLevelController : MonoBehaviour
    {
        private const string DefaultLibraryResource = "BartenderGlassLibrary";
        private const string DefaultPaletteResource = "BsPalette";

        [Header("Campaign")]
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool resumeSavedProgress = true;
        [SerializeField, Min(1)] private int startingLevelNumber = 1;
        [SerializeField] private string progressKey = "LiquidSort.Bartender.NextLevelSlot";

        [Header("Glass mapping")]
        [SerializeField] private BartenderGlassLibrary glassLibrary;
        [SerializeField] private BsPalette palette;

        [Header("Scene ports")]
        [SerializeField] private Transform glassRoot;
        [SerializeField] private PourAnimator pourAnimator;
        [SerializeField] private Camera boardCamera;

        [Header("Runtime layout")]
        [SerializeField] private Vector2 boardCenter = new Vector2(0f, 0.25f);
        [SerializeField] private Vector2 glassSpacing = new Vector2(1.55f, 2.05f);
        [SerializeField] private float pickPadding = 0.15f;
        [SerializeField] private float selectionLift = 0.18f;
        [SerializeField] private float selectionSpeed = 14f;

        [Header("Temporary input (replaceable by final UI)")]
        [SerializeField] private bool allowPointerInput = true;
        [SerializeField] private bool clickMatchedGlassAgainToDeliver = true;

        private static List<BsLevel> cachedCampaign;

        private readonly List<LiquidBottle> bottleViews = new List<LiquidBottle>();
        private readonly Dictionary<LiquidBottle, int> idByBottle =
            new Dictionary<LiquidBottle, int>();
        private readonly Dictionary<int, LiquidBottle> bottleById =
            new Dictionary<int, LiquidBottle>();
        private readonly Dictionary<LiquidBottle, float> homeYByBottle =
            new Dictionary<LiquidBottle, float>();
        private readonly Dictionary<OrderDef, float> orderRemaining =
            new Dictionary<OrderDef, float>(ReferenceComparer<OrderDef>.Instance);
        private readonly List<OrderDef> timerRemovalScratch = new List<OrderDef>();
        private readonly List<Color> colorScratch = new List<Color>(LiquidBottle.MaxBands);

        private Transform runtimeRoot;
        private LiquidBottle selected;
        private PendingPour pendingPour;
        private int generation;

        public BsLevel CurrentLevel { get; private set; }
        public BsBoard Board { get; private set; }
        public int CurrentCampaignSlot { get; private set; } = -1;
        public BartenderLevelState State { get; private set; } = BartenderLevelState.Unloaded;
        public BartenderFailureReason FailureReason { get; private set; }
        public int TimedOutOrderSlot { get; private set; } = -1;
        public IReadOnlyList<LiquidBottle> BottleViews => bottleViews;
        public int CampaignCount => Campaign.Count;

        /// <summary>
        /// Zero-based slot of the next unlocked level. CampaignCount is a valid sentinel
        /// meaning that the campaign is complete; it is intentionally not clamped back
        /// to the final playable slot.
        /// </summary>
        public int NextUnlockedCampaignSlot => Mathf.Clamp(
            PlayerPrefs.GetInt(progressKey, 0), 0, Campaign.Count);

        public event Action<BsLevel> LevelLoaded;
        public event Action BoardChanged;
        public event Action OrdersChanged;
        public event Action<BartenderLevelState> StateChanged;
        public event Action<int, int, OrderDef> GlassDelivered;

        private static List<BsLevel> Campaign
        {
            get
            {
                if (cachedCampaign != null) return cachedCampaign;

                BsLevel[] found = Resources.LoadAll<BsLevel>("Levels");
                cachedCampaign = new List<BsLevel>(found);
                cachedCampaign.Sort((a, b) => a.Index.CompareTo(b.Index));
                return cachedCampaign;
            }
        }

        private void Start()
        {
            ResolveDependencies();
            if (!loadOnStart) return;

            int slot = resumeSavedProgress
                ? NextUnlockedCampaignSlot
                : FindCampaignSlot(startingLevelNumber);

            if (slot >= Campaign.Count)
            {
                SetState(BartenderLevelState.CampaignComplete);
                return;
            }

            if (slot < 0) slot = 0;
            LoadCampaignSlot(slot);
        }

        private void OnEnable()
        {
            SubscribeToAnimator();
        }

        private void OnDisable()
        {
            UnsubscribeFromAnimator();
            if (pourAnimator != null) pourAnimator.CancelActivePour();
            pendingPour = default;
            SetSelected(null);
        }

        private void OnDestroy()
        {
            DestroyRuntimeViews();
        }

        private void Update()
        {
            if (State != BartenderLevelState.Playing) return;

            TickOrderDeadlines(Time.unscaledDeltaTime);
            if (State != BartenderLevelState.Playing) return;

            AnimateSelection();
            if (!allowPointerInput || pendingPour.Active
                || (pourAnimator != null && pourAnimator.Busy)
                || !Input.GetMouseButtonDown(0))
                return;

            HandlePointer(Input.mousePosition);
        }

        public bool LoadLevelNumber(int oneBasedLevelNumber)
        {
            int slot = FindCampaignSlot(oneBasedLevelNumber);
            return slot >= 0 && LoadCampaignSlot(slot);
        }

        public bool LoadCampaignSlot(int zeroBasedSlot)
        {
            ResolveDependencies();
            if (zeroBasedSlot < 0 || zeroBasedSlot >= Campaign.Count)
            {
                Debug.LogError($"LiquidSort level slotu bulunamadı: {zeroBasedSlot}.", this);
                return false;
            }

            BsLevel level = Campaign[zeroBasedSlot];
            if (!TryValidateLevel(level, out string error))
            {
                Debug.LogError($"Level {level.Index} yüklenmedi: {error}", this);
                return false;
            }

            unchecked { generation++; }
            pendingPour = default;
            if (pourAnimator != null) pourAnimator.CancelActivePour();
            SetSelected(null);
            DestroyRuntimeViews();

            CurrentCampaignSlot = zeroBasedSlot;
            CurrentLevel = level;
            Board = BsBoard.FromLevel(level);
            FailureReason = BartenderFailureReason.None;
            TimedOutOrderSlot = -1;

            SpawnRuntimeViews();
            ResetOrderDeadlines();
            SetState(BartenderLevelState.Playing);
            LevelLoaded?.Invoke(level);
            BoardChanged?.Invoke();
            OrdersChanged?.Invoke();
            EvaluateTerminalState();
            return true;
        }

        public bool ReloadCurrentLevel()
        {
            return CurrentCampaignSlot >= 0 && LoadCampaignSlot(CurrentCampaignSlot);
        }

        public bool LoadNextLevel()
        {
            int next = CurrentCampaignSlot + 1;
            if (next < 0) next = 0;
            if (next >= Campaign.Count)
            {
                UnloadInternal(BartenderLevelState.CampaignComplete);
                return false;
            }
            return LoadCampaignSlot(next);
        }

        public void UnloadLevel()
        {
            UnloadInternal(BartenderLevelState.Unloaded);
        }

        public bool Pause()
        {
            if (State != BartenderLevelState.Playing || pendingPour.Active
                || (pourAnimator != null && pourAnimator.Busy))
                return false;
            SetSelected(null);
            SetState(BartenderLevelState.Paused);
            return true;
        }

        public bool Resume()
        {
            if (State != BartenderLevelState.Paused) return false;
            SetState(BartenderLevelState.Playing);
            return true;
        }

        public PourResult CanPour(LiquidBottle source, LiquidBottle target)
        {
            if (Board == null || !TryGetModel(source, out RtGlass sourceModel)
                              || !TryGetModel(target, out RtGlass targetModel))
                return PourResult.Fail("Bardak level modeline bağlı değil");
            return Board.CanPour(sourceModel, targetModel);
        }

        public bool TryStartPour(LiquidBottle source, LiquidBottle target,
                                 out string rejectionReason)
        {
            rejectionReason = null;
            if (State != BartenderLevelState.Playing)
            {
                rejectionReason = "Level oynanır durumda değil";
                return false;
            }

            if (ExpireOrderIfNeeded() || pendingPour.Active
                || (pourAnimator != null && pourAnimator.Busy))
            {
                rejectionReason = "Başka bir işlem sürüyor";
                return false;
            }

            if (!TryGetModel(source, out RtGlass sourceModel)
                || !TryGetModel(target, out RtGlass targetModel))
            {
                rejectionReason = "Bardak level modeline bağlı değil";
                return false;
            }

            PourResult rule = Board.CanPour(sourceModel, targetModel);
            if (!rule.Success)
            {
                rejectionReason = rule.Reason;
                return false;
            }

            SyncBottleIfDifferent(sourceModel, source);
            SyncBottleIfDifferent(targetModel, target);
            float homeY = homeYByBottle.TryGetValue(source, out float cached)
                ? cached
                : source.transform.position.y;

            if (pourAnimator == null
                || !pourAnimator.TryStartPour(source, target, rule.Amount, homeY, false))
            {
                rejectionReason = "Dökme animasyonu başlatılamadı";
                return false;
            }

            pendingPour = new PendingPour
            {
                Active = true,
                Generation = generation,
                OperationId = pourAnimator.ActiveOperationId,
                SourceId = sourceModel.Id,
                TargetId = targetModel.Id,
                ExpectedAmount = rule.Amount,
                ExpectedColor = rule.Color
            };
            SetSelected(null);
            return true;
        }

        public int MatchedOrderSlot(LiquidBottle bottle)
        {
            return Board != null && TryGetModel(bottle, out RtGlass model)
                ? Board.MatchedSlot(model)
                : -1;
        }

        public bool TryDeliver(LiquidBottle bottle, out int slotIndex)
        {
            slotIndex = -1;
            if (State != BartenderLevelState.Playing || ExpireOrderIfNeeded()
                || pendingPour.Active || (pourAnimator != null && pourAnimator.Busy)
                || !TryGetModel(bottle, out RtGlass model))
                return false;

            int matched = Board.MatchedSlot(model);
            if (matched < 0) return false;
            OrderDef deliveredOrder = Board.Slots[matched];
            int deliveredGlassId = model.Id;
            if (!Board.Deliver(model, out slotIndex)) return false;

            idByBottle.Remove(bottle);
            bottleById.Remove(deliveredGlassId);
            homeYByBottle.Remove(bottle);
            bottle.gameObject.SetActive(false);
            if (selected == bottle) selected = null;

            RefreshOrderDeadlinesAfterDelivery();
            SyncAllViews();
            GlassDelivered?.Invoke(deliveredGlassId, slotIndex, deliveredOrder);
            BoardChanged?.Invoke();
            OrdersChanged?.Invoke();
            EvaluateTerminalState();
            return true;
        }

        public OrderDef OrderAtSlot(int slotIndex)
        {
            return Board != null && Board.Slots != null
                && slotIndex >= 0 && slotIndex < Board.Slots.Length
                ? Board.Slots[slotIndex]
                : null;
        }

        public bool TryGetOrderTimeRemaining(int slotIndex, out float remaining,
                                             out float duration)
        {
            OrderDef order = OrderAtSlot(slotIndex);
            duration = order != null ? order.TimeLimit : 0f;
            if (order != null && orderRemaining.TryGetValue(order, out remaining))
                return true;
            remaining = 0f;
            return false;
        }

        public bool TryValidateLevel(BsLevel level, out string error)
        {
            if (level == null)
            {
                error = "Level asseti boş.";
                return false;
            }
            if (glassLibrary == null)
            {
                error = "BartenderGlassLibrary bulunamadı.";
                return false;
            }
            if (palette == null || palette.Count == 0)
            {
                error = "BsPalette bulunamadı veya boş.";
                return false;
            }

            for (int i = 0; i < palette.Count; i++)
            {
                for (int j = i + 1; j < palette.Count; j++)
                {
                    if (!LiquidBottle.Same(palette.ColorAt(i), palette.ColorAt(j))) continue;
                    error = $"Palet renkleri {i} ve {j}, LiquidBottle için ayırt edilemiyor.";
                    return false;
                }
            }

            if (level.Glasses == null || level.Glasses.Count == 0)
            {
                error = "Levelda bardak yok.";
                return false;
            }

            for (int i = 0; i < level.Glasses.Count; i++)
            {
                GlassDef glass = level.Glasses[i];
                if (glass == null)
                {
                    error = $"Bardak {i} boş.";
                    return false;
                }
                if (!glassLibrary.TryValidate(glass.Type, out error)) return false;
                if (glass.Layers.Count > glass.Capacity)
                {
                    error = $"Bardak {i}, kapasitesinden fazla katman taşıyor.";
                    return false;
                }
                for (int layer = 0; layer < glass.Layers.Count; layer++)
                {
                    int color = glass.Layers[layer].Color;
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Bardak {i}, katman {layer}: renk indeksi {color} geçersiz.";
                    return false;
                }
            }

            if (level.Orders == null || level.Orders.Count == 0)
            {
                error = "Levelda sipariş yok.";
                return false;
            }

            for (int i = 0; i < level.Orders.Count; i++)
            {
                OrderDef order = level.Orders[i];
                if (order == null)
                {
                    error = $"Sipariş {i} boş.";
                    return false;
                }
                if (!glassLibrary.TryValidate(order.Glass, out error)) return false;
                if (order.Contents.Count != order.Capacity)
                {
                    error = $"Sipariş {i}, {order.Capacity} yerine "
                          + $"{order.Contents.Count} birim içeriyor.";
                    return false;
                }
                for (int content = 0; content < order.Contents.Count; content++)
                {
                    int color = order.Contents[content];
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Sipariş {i}, içerik {content}: renk indeksi {color} geçersiz.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private void ResolveDependencies()
        {
            if (glassLibrary == null)
                glassLibrary = Resources.Load<BartenderGlassLibrary>(DefaultLibraryResource);
            if (palette == null)
                palette = Resources.Load<BsPalette>(DefaultPaletteResource);
            if (boardCamera == null) boardCamera = Camera.main;
            if (pourAnimator == null) pourAnimator = GetComponent<PourAnimator>();
            if (pourAnimator == null) pourAnimator = gameObject.AddComponent<PourAnimator>();
            SubscribeToAnimator();
        }

        private void SubscribeToAnimator()
        {
            if (pourAnimator == null) return;
            pourAnimator.PourFinished -= HandlePourFinished;
            pourAnimator.PourFinished += HandlePourFinished;
        }

        private void UnsubscribeFromAnimator()
        {
            if (pourAnimator != null) pourAnimator.PourFinished -= HandlePourFinished;
        }

        private void SpawnRuntimeViews()
        {
            var rootObject = new GameObject("Runtime Level Glasses");
            runtimeRoot = rootObject.transform;
            runtimeRoot.SetParent(glassRoot != null ? glassRoot : transform, false);

            int count = Board.Glasses.Count;
            int columns = Mathf.Max(1, CurrentLevel.ColumnsPerRow);
            for (int i = 0; i < count; i++)
            {
                RtGlass model = Board.Glasses[i];
                glassLibrary.TryGet(model.Type, out VesselProfile profile, out float scale);

                var glassObject = new GameObject($"Glass_{model.Id:00}_{model.Type}");
                glassObject.transform.SetParent(runtimeRoot, false);
                glassObject.transform.localPosition = LayoutPosition(i, count, columns);
                glassObject.transform.localScale = Vector3.one * scale;

                var bottle = glassObject.AddComponent<LiquidBottle>();
                bottle.profile = profile;
                bottle.capacity = model.Capacity;
                bottle.sortingOrder = 1;
                bottle.Invalidate();

                var shell = glassObject.AddComponent<BottleShell>();
                shell.backOverride = profile.back;
                shell.drawNeck = false;
                shell.restyleLine = glassLibrary.RestyleLine;
                shell.theme = glassLibrary.Theme;

                bottleViews.Add(bottle);
                idByBottle.Add(bottle, model.Id);
                bottleById.Add(model.Id, bottle);
                homeYByBottle.Add(bottle, glassObject.transform.position.y);
                SyncBottleIfDifferent(model, bottle);
                bottle.Refresh();
                shell.Build();
            }
        }

        private Vector3 LayoutPosition(int index, int total, int columns)
        {
            int row = index / columns;
            int column = index % columns;
            int firstInRow = row * columns;
            int rowCount = Mathf.Min(columns, total - firstInRow);
            float centeredColumn = column - (rowCount - 1) * 0.5f;
            return new Vector3(
                boardCenter.x + centeredColumn * glassSpacing.x,
                boardCenter.y - row * glassSpacing.y,
                0f);
        }

        private void HandlePointer(Vector3 screenPoint)
        {
            LiquidBottle hit = Pick(screenPoint);
            if (hit == null)
            {
                SetSelected(null);
                return;
            }

            if (selected == null)
            {
                if (!hit.IsEmpty) SetSelected(hit);
                return;
            }

            if (hit == selected)
            {
                if (clickMatchedGlassAgainToDeliver && TryDeliver(hit, out _)) return;
                SetSelected(null);
                return;
            }

            LiquidBottle source = selected;
            if (TryStartPour(source, hit, out _)) return;
            SetSelected(hit.IsEmpty ? null : hit);
        }

        private LiquidBottle Pick(Vector3 screenPoint)
        {
            if (boardCamera == null) return null;
            Vector3 world = boardCamera.ScreenToWorldPoint(screenPoint);
            LiquidBottle best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < bottleViews.Count; i++)
            {
                LiquidBottle bottle = bottleViews[i];
                if (bottle == null || !bottle.gameObject.activeInHierarchy) continue;
                Vector3 local = bottle.transform.InverseTransformPoint(
                    new Vector3(world.x, world.y, bottle.transform.position.z));
                Rect bounds = bottle.InteriorBounds;
                if (local.x < bounds.xMin - pickPadding || local.x > bounds.xMax + pickPadding
                    || local.y < bounds.yMin - pickPadding || local.y > bounds.yMax + pickPadding)
                    continue;

                float distance = Mathf.Abs(local.x - bounds.center.x);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = bottle;
            }
            return best;
        }

        private void AnimateSelection()
        {
            float follow = 1f - Mathf.Exp(-selectionSpeed * Time.unscaledDeltaTime);
            for (int i = 0; i < bottleViews.Count; i++)
            {
                LiquidBottle bottle = bottleViews[i];
                if (bottle == null || !bottle.gameObject.activeSelf
                    || !homeYByBottle.TryGetValue(bottle, out float home))
                    continue;
                bool isSelected = bottle == selected;
                Vector3 position = bottle.transform.position;
                position.y = Mathf.Lerp(position.y,
                    isSelected ? home + selectionLift : home, follow);
                bottle.transform.position = position;
                BottleShell shell = bottle.GetComponent<BottleShell>();
                if (shell != null)
                    shell.highlight = Mathf.Lerp(shell.highlight, isSelected ? 1f : 0f, follow);
            }
        }

        private void SetSelected(LiquidBottle next)
        {
            if (selected != null && homeYByBottle.TryGetValue(selected, out float oldHome))
            {
                Vector3 oldPosition = selected.transform.position;
                oldPosition.y = oldHome;
                selected.transform.position = oldPosition;
                BottleShell oldShell = selected.GetComponent<BottleShell>();
                if (oldShell != null) oldShell.highlight = 0f;
            }
            selected = next;
        }

        private void HandlePourFinished(int operationId, PourOutcome outcome)
        {
            if (!pendingPour.Active || pendingPour.OperationId != operationId) return;
            PendingPour completed = pendingPour;
            pendingPour = default;
            if (completed.Generation != generation || Board == null) return;

            RtGlass source = Board.GlassById(completed.SourceId);
            RtGlass target = Board.GlassById(completed.TargetId);
            if (outcome != PourOutcome.Completed)
            {
                SyncAllViews();
                return;
            }

            PourResult result = Board.Pour(source, target);
            if (!result.Success || result.Amount != completed.ExpectedAmount
                || result.Color != completed.ExpectedColor)
            {
                Debug.LogError("Dökme sunumu ile BsBoard modeli ayrıştı; level durduruldu.", this);
                SyncAllViews();
                Fail(BartenderFailureReason.PresentationDesynced, -1);
                return;
            }

            if (bottleById.TryGetValue(source.Id, out LiquidBottle sourceView))
                SyncBottleIfDifferent(source, sourceView);
            if (bottleById.TryGetValue(target.Id, out LiquidBottle targetView))
                SyncBottleIfDifferent(target, targetView);
            BoardChanged?.Invoke();
            EvaluateTerminalState();
        }

        private void SyncAllViews()
        {
            if (Board == null) return;
            for (int i = 0; i < Board.Glasses.Count; i++)
            {
                RtGlass model = Board.Glasses[i];
                if (bottleById.TryGetValue(model.Id, out LiquidBottle view))
                    SyncBottleIfDifferent(model, view);
            }
        }

        private void SyncBottleIfDifferent(RtGlass model, LiquidBottle view)
        {
            if (model == null || view == null) return;
            colorScratch.Clear();
            for (int i = 0; i < model.Layers.Count; i++)
            {
                Layer layer = model.Layers[i];
                colorScratch.Add(layer.Hidden
                    ? glassLibrary.HiddenLayerColor
                    : palette.ColorAt(layer.Color));
            }

            bool same = view.capacity == model.Capacity && view.UnitCount == colorScratch.Count;
            if (same)
            {
                for (int i = 0; i < colorScratch.Count; i++)
                {
                    if (LiquidBottle.Same(view.Units[i], colorScratch[i])) continue;
                    same = false;
                    break;
                }
            }

            view.capacity = model.Capacity;
            if (!same) view.SetUnits(colorScratch);
        }

        private bool TryGetModel(LiquidBottle bottle, out RtGlass model)
        {
            model = null;
            return bottle != null && idByBottle.TryGetValue(bottle, out int id)
                && Board != null && (model = Board.GlassById(id)) != null;
        }

        private void ResetOrderDeadlines()
        {
            orderRemaining.Clear();
            RefreshOrderDeadlinesAfterDelivery();
        }

        private void RefreshOrderDeadlinesAfterDelivery()
        {
            if (Board == null || Board.Slots == null)
            {
                orderRemaining.Clear();
                return;
            }

            timerRemovalScratch.Clear();
            foreach (KeyValuePair<OrderDef, float> pair in orderRemaining)
            {
                if (!ContainsOrderReference(Board.Slots, pair.Key))
                    timerRemovalScratch.Add(pair.Key);
            }
            for (int i = 0; i < timerRemovalScratch.Count; i++)
                orderRemaining.Remove(timerRemovalScratch[i]);

            if (!Board.TimedOrdersEnabled) return;
            for (int i = 0; i < Board.Slots.Length; i++)
            {
                OrderDef order = Board.Slots[i];
                if (order == null || order.TimeLimit <= 0f || orderRemaining.ContainsKey(order))
                    continue;
                orderRemaining.Add(order, order.TimeLimit);
            }
        }

        private void TickOrderDeadlines(float delta)
        {
            if (Board == null || !Board.TimedOrdersEnabled || delta <= 0f) return;
            for (int slot = 0; slot < Board.Slots.Length; slot++)
            {
                OrderDef order = Board.Slots[slot];
                if (order == null || !orderRemaining.TryGetValue(order, out float remaining))
                    continue;
                remaining -= delta;
                orderRemaining[order] = remaining;
                if (remaining > 0f) continue;
                Fail(BartenderFailureReason.OrderTimedOut, slot);
                return;
            }
        }

        private bool ExpireOrderIfNeeded()
        {
            if (Board == null || !Board.TimedOrdersEnabled) return false;
            for (int slot = 0; slot < Board.Slots.Length; slot++)
            {
                OrderDef order = Board.Slots[slot];
                if (order != null && orderRemaining.TryGetValue(order, out float remaining)
                                  && remaining <= 0f)
                {
                    Fail(BartenderFailureReason.OrderTimedOut, slot);
                    return true;
                }
            }
            return false;
        }

        private void EvaluateTerminalState()
        {
            if (State != BartenderLevelState.Playing || Board == null) return;
            if (Board.IsWin())
            {
                int unlocked = Mathf.Max(NextUnlockedCampaignSlot, CurrentCampaignSlot + 1);
                PlayerPrefs.SetInt(progressKey, Mathf.Min(unlocked, Campaign.Count));
                PlayerPrefs.Save();
                SetSelected(null);
                SetState(BartenderLevelState.Won);
            }
            else if (Board.IsFail())
            {
                Fail(BartenderFailureReason.NoLegalMoves, -1);
            }
        }

        private void Fail(BartenderFailureReason reason, int timedOutSlot)
        {
            if (State != BartenderLevelState.Playing) return;

            // A deadline may expire while the presentation is still animating a move.
            // Invalidate the transaction before cancelling: CancelActivePour notifies its
            // listeners synchronously, and a stale completion must never mutate a failed board.
            pendingPour = default;
            if (pourAnimator != null) pourAnimator.CancelActivePour();

            FailureReason = reason;
            TimedOutOrderSlot = timedOutSlot;
            SetSelected(null);
            SetState(BartenderLevelState.Failed);
        }

        private void SetState(BartenderLevelState next)
        {
            if (State == next) return;
            State = next;
            StateChanged?.Invoke(next);
        }

        private void UnloadInternal(BartenderLevelState finalState)
        {
            unchecked { generation++; }
            pendingPour = default;
            if (pourAnimator != null) pourAnimator.CancelActivePour();
            SetSelected(null);
            DestroyRuntimeViews();
            Board = null;
            CurrentLevel = null;
            CurrentCampaignSlot = -1;
            orderRemaining.Clear();
            FailureReason = BartenderFailureReason.None;
            TimedOutOrderSlot = -1;
            SetState(finalState);
        }

        private void DestroyRuntimeViews()
        {
            bottleViews.Clear();
            idByBottle.Clear();
            bottleById.Clear();
            homeYByBottle.Clear();
            if (runtimeRoot == null) return;
            if (Application.isPlaying)
            {
                // Destroy is deferred until end-of-frame; hide the old hierarchy now so a
                // same-frame reload cannot briefly overlap it with the newly spawned level.
                runtimeRoot.gameObject.SetActive(false);
                Destroy(runtimeRoot.gameObject);
            }
            else DestroyImmediate(runtimeRoot.gameObject);
            runtimeRoot = null;
        }

        private int FindCampaignSlot(int oneBasedLevelNumber)
        {
            for (int i = 0; i < Campaign.Count; i++)
                if (Campaign[i] != null && Campaign[i].Index == oneBasedLevelNumber)
                    return i;
            return -1;
        }

        private static bool ContainsOrderReference(OrderDef[] slots, OrderDef wanted)
        {
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i], wanted)) return true;
            return false;
        }

        private struct PendingPour
        {
            public bool Active;
            public int Generation;
            public int OperationId;
            public int SourceId;
            public int TargetId;
            public int ExpectedAmount;
            public int ExpectedColor;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
