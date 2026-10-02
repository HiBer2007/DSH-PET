using System.Collections.Generic;
using DshPet.Core.Model;
using Xunit;

namespace DshPet.Core.Tests
{
    public class MeterGatesTests
    {
        // Index order is MeterGates.Tiers: 0 = five hour, 1 = week, 2 = month.
        const int FiveHour = 0, Week = 1, Month = 2;

        static IReadOnlyList<double> Pct(double fiveHour, double week, double month)
        {
            return new[] { fiveHour, week, month };
        }

        [Fact]
        public void Nothing_blocks_while_every_window_has_room()
        {
            var pct = Pct(75, 80, 95);
            Assert.Equal(-1, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(-1, MeterGates.Blocker(pct, Week));
            Assert.Equal(-1, MeterGates.Blocker(pct, Month));
        }

        [Fact]
        public void An_empty_week_makes_the_five_hour_window_useless()
        {
            // The case this exists for: the five hour window refilled and reports 75%
            // of its own allowance, but a call spends against the week as well, and the
            // week is spent. Nothing the five hour window says is spendable.
            var pct = Pct(75, 0, 50);
            Assert.Equal(Week, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(-1, MeterGates.Blocker(pct, Week));
            Assert.Equal(-1, MeterGates.Blocker(pct, Month));
        }

        [Fact]
        public void An_empty_month_blocks_both_windows_below_it()
        {
            var pct = Pct(75, 80, 0);
            Assert.Equal(Month, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(Month, MeterGates.Blocker(pct, Week));
            Assert.Equal(-1, MeterGates.Blocker(pct, Month));
        }

        [Fact]
        public void The_highest_empty_window_is_the_one_quoted()
        {
            // Both empty: the month refills last, so naming the week would promise
            // quota up to three weeks early.
            var pct = Pct(0, 0, 0);
            Assert.Equal(Month, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(Month, MeterGates.Blocker(pct, Week));
        }

        [Fact]
        public void An_empty_window_never_blocks_one_above_it()
        {
            // The five hour window is the narrow one: it being spent says nothing about
            // the week, which is larger and rolls over more slowly.
            var pct = Pct(0, 80, 95);
            Assert.Equal(-1, MeterGates.Blocker(pct, Week));
            Assert.Equal(-1, MeterGates.Blocker(pct, Month));
        }

        [Fact]
        public void An_unread_window_is_unknown_rather_than_empty()
        {
            // A window with no reading yet reports NaN. Treating that as "empty" would
            // black out the whole panel on the first poll of a fresh subscription.
            var pct = new[] { double.NaN, double.NaN, 50.0 };
            Assert.Equal(-1, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(-1, MeterGates.Blocker(pct, Week));
        }

        [Fact]
        public void The_boundary_is_zero_not_nearly_zero()
        {
            // Reported percentages are clamped to 0 by the caller, so "<= 0" and "== 0"
            // agree there; the point of the test is that a window holding a sliver of
            // its allowance does not block. 0.0001% of $60 is still $0.00006.
            Assert.Equal(-1, MeterGates.Blocker(Pct(0.0001, 0.0001, 50), FiveHour));
            Assert.Equal(Week, MeterGates.Blocker(Pct(0, 0, 50), FiveHour));
        }

        [Fact]
        public void A_short_list_does_not_block_or_throw()
        {
            // The caller builds this from three readings, but a payload that carried
            // only the first two must not be read as "the month is empty".
            var pct = new[] { 75.0, 80.0 };
            Assert.Equal(-1, MeterGates.Blocker(pct, FiveHour));
            Assert.Equal(-1, MeterGates.Blocker(pct, Week));
        }

        [Fact]
        public void The_tier_order_is_the_one_the_panel_indexes_by()
        {
            Assert.Equal(new[] { "fiveHour", "week", "month" }, MeterGates.Tiers);
        }
    }
}
