using System.Collections.Generic;
using BartenderSort.Core;
using LiquidSort.Levels;
using NUnit.Framework;
using UnityEngine;

namespace LiquidSort.Tests.EditMode
{
    /// <summary>
    /// Campaign authoring contract consumed by OrderStripPresenter. The strip owns three
    /// card views, while the number of non-null orders available at level entry determines
    /// how many are visible. Levels 1/2/3 deliberately teach that rule as 2/3/2 cards.
    /// </summary>
    public sealed class OrderStripCampaignContractTests
    {
        [Test]
        public void Campaign_indices_are_positive_unique_and_fit_three_card_strip()
        {
            BsLevel[] levels = Resources.LoadAll<BsLevel>("Levels");
            Assert.That(levels, Is.Not.Empty,
                "Resources/Levels must contain the authored campaign.");
            var indices = new HashSet<int>();

            for (int i = 0; i < levels.Length; i++)
            {
                BsLevel level = levels[i];
                Assert.That(level, Is.Not.Null,
                    $"Campaign resource [{i}] must be a BsLevel asset.");
                Assert.That(level.Index, Is.GreaterThan(0),
                    $"{level.name} must have a positive campaign Index.");
                Assert.That(indices.Add(level.Index), Is.True,
                    $"Campaign Index {level.Index} is duplicated by {level.name}.");
                Assert.That(level.OrderSlots, Is.InRange(1, 3),
                    $"Level {level.Index} requests {level.OrderSlots} order slots, "
                    + "but the authored order strip supports one to three cards.");
            }
        }

        [Test]
        public void Levels_one_to_three_keep_two_three_two_visible_card_contract()
        {
            BsLevel[] levels = Resources.LoadAll<BsLevel>("Levels");
            int[] expectedVisibleCards = { 2, 3, 2 };

            for (int levelIndex = 1; levelIndex <= expectedVisibleCards.Length;
                 levelIndex++)
            {
                BsLevel level = FindLevel(levels, levelIndex);
                int expectedCount = expectedVisibleCards[levelIndex - 1];
                Assert.That(level, Is.Not.Null,
                    $"Campaign must contain Level {levelIndex}.");
                Assert.That(level.Orders, Is.Not.Null,
                    $"Level {levelIndex} must have an authored order deck.");
                Assert.That(level.Orders, Has.Count.EqualTo(expectedCount),
                    $"Level {levelIndex} intentionally starts with {expectedCount} "
                    + "visible order cards in the 2/3/2 tutorial sequence.");
                Assert.That(level.OrderSlots, Is.EqualTo(3),
                    $"Level {levelIndex} keeps three strip slots even when only "
                    + $"{expectedCount} orders are visible.");
                Assert.That(level.AllowTimedOrders, Is.False,
                    $"Level {levelIndex} must not introduce timed orders.");

                for (int orderIndex = 0; orderIndex < level.Orders.Count; orderIndex++)
                {
                    OrderDef order = level.Orders[orderIndex];
                    Assert.That(order, Is.Not.Null,
                        $"Level {levelIndex} order [{orderIndex}] must not be null.");
                    Assert.That(order.TimeLimit, Is.EqualTo(0f),
                        $"Level {levelIndex} order [{orderIndex}] must be untimed.");
                }
            }
        }

        [Test]
        public void Visible_order_cards_are_centered_without_changing_authored_spacing()
        {
            var authored = new[]
            {
                new Vector2(-220f, 156f),
                new Vector2(0f, 156f),
                new Vector2(220f, 156f)
            };

            float twoLeft = OrderStripPresenter.CenteredSlotX(authored, 2, 0);
            float twoRight = OrderStripPresenter.CenteredSlotX(authored, 2, 1);
            Assert.That(twoLeft, Is.EqualTo(-110f).Within(0.001f));
            Assert.That(twoRight, Is.EqualTo(110f).Within(0.001f));
            Assert.That((twoLeft + twoRight) * 0.5f,
                Is.EqualTo(0f).Within(0.001f));

            Assert.That(OrderStripPresenter.CenteredSlotX(authored, 1, 0),
                Is.EqualTo(0f).Within(0.001f));
            Assert.That(OrderStripPresenter.CenteredSlotX(authored, 3, 0),
                Is.EqualTo(-220f).Within(0.001f));
            Assert.That(OrderStripPresenter.CenteredSlotX(authored, 3, 1),
                Is.EqualTo(0f).Within(0.001f));
            Assert.That(OrderStripPresenter.CenteredSlotX(authored, 3, 2),
                Is.EqualTo(220f).Within(0.001f));
        }

        [Test]
        public void Timed_order_schedule_matches_levels_fifteen_to_thirty()
        {
            BsLevel[] levels = Resources.LoadAll<BsLevel>("Levels");

            for (int levelIndex = 1; levelIndex <= 30; levelIndex++)
            {
                BsLevel level = FindLevel(levels, levelIndex);
                Assert.That(level, Is.Not.Null,
                    $"Campaign must contain Level {levelIndex}.");
                Assert.That(level.Orders, Is.Not.Null.And.Not.Empty,
                    $"Level {levelIndex} must have an authored order deck.");

                bool timed = levelIndex >= 15;
                Assert.That(level.AllowTimedOrders, Is.EqualTo(timed),
                    $"Level {levelIndex} timed-order feature gate drifted.");

                float expectedSeconds = levelIndex <= 14
                    ? 0f
                    : levelIndex <= 20
                        ? 35f
                        : levelIndex <= 25 ? 40f : 45f;

                for (int orderIndex = 0; orderIndex < level.Orders.Count; orderIndex++)
                {
                    OrderDef order = level.Orders[orderIndex];
                    Assert.That(order, Is.Not.Null,
                        $"Level {levelIndex} order [{orderIndex}] must not be null.");
                    Assert.That(float.IsNaN(order.TimeLimit)
                                || float.IsInfinity(order.TimeLimit), Is.False,
                        $"Level {levelIndex} order [{orderIndex}] timer must be finite.");
                    Assert.That(order.TimeLimit, Is.EqualTo(expectedSeconds),
                        $"Level {levelIndex} order [{orderIndex}] timer schedule drifted.");
                    if (timed)
                    {
                        Assert.That(order.TimeLimit, Is.GreaterThan(0f),
                            $"Level {levelIndex} order [{orderIndex}] must be timed.");
                    }
                }
            }
        }

        private static BsLevel FindLevel(IReadOnlyList<BsLevel> levels, int index)
        {
            for (int i = 0; i < levels.Count; i++)
                if (levels[i] != null && levels[i].Index == index) return levels[i];
            return null;
        }
    }
}
