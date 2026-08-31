using System;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Backwards-compatible coin facade. The unified progress store is the only owner of
    /// the durable balance; existing booster presenters can keep their current API.
    /// </summary>
    public static class BartenderEconomy
    {
        public const int DefaultStartingCoins = BartenderProgressService.DefaultStartingCoins;

        public static int Coins => BartenderProgressService.Coins;

        public static event Action<int> CoinsChanged
        {
            add => BartenderProgressService.CoinsChanged += value;
            remove => BartenderProgressService.CoinsChanged -= value;
        }

        public static bool CanAfford(int cost) =>
            BartenderProgressService.CanAfford(cost);

        public static bool TrySpendCoins(int cost, out string rejectionReason) =>
            BartenderProgressService.TrySpendCoins(cost, out rejectionReason);

        public static bool TryGrantCoins(int amount, out string rejectionReason) =>
            BartenderProgressService.TryGrantCoins(amount, out rejectionReason);
    }
}
