using System;
using DshPet.Core.Model;
using Xunit;

namespace DshPet.Core.Tests
{
    public class PeakHoursTests
    {
        // Beijing 09:00-12:00 and 14:00-18:00 on weekdays is the same window GO
        // publishes as 01:00-04:00 and 06:00-10:00 UTC, so both providers are covered
        // by one rule.
        //
        // The dates chosen here avoid the holiday table on purpose: 2026-10-01..10-07
        // is National Day, and a test that used those dates to prove "weekday = peak"
        // would be proving the opposite of what it says.
        static DateTime Beijing(int month, int day, int hour, int minute = 0)
        {
            // Build the UTC instant from Beijing wall time: subtract the 8 hour offset.
            return new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-8);
        }

        // 2026-10-08 is a Thursday after the National Day break.
        [Fact]
        public void Weekday_mornings_and_afternoons_are_peak()
        {
            Assert.True(PeakHours.IsPeak(Beijing(10, 8, 9, 0)));
            Assert.True(PeakHours.IsPeak(Beijing(10, 8, 11, 59)));
            Assert.True(PeakHours.IsPeak(Beijing(10, 8, 14, 0)));
            Assert.True(PeakHours.IsPeak(Beijing(10, 8, 17, 59)));
        }

        [Fact]
        public void The_gaps_and_the_night_are_off_peak()
        {
            Assert.False(PeakHours.IsPeak(Beijing(10, 8, 8, 59)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 8, 12, 0)));      // lunch
            Assert.False(PeakHours.IsPeak(Beijing(10, 8, 13, 59)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 8, 18, 0)));      // evening, half price
            Assert.False(PeakHours.IsPeak(Beijing(10, 8, 3, 0)));
        }

        [Fact]
        public void Weekends_are_off_peak_all_day()
        {
            // 2026-10-10 is a Saturday and also a make-up workday; 10-11 is a Sunday.
            // Both are off-peak: the DeepSeek wording is "Monday to Friday, excluding
            // holidays", so a Saturday is off-peak whether or not it is worked.
            Assert.False(PeakHours.IsPeak(Beijing(10, 10, 10, 0)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 10, 15, 0)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 11, 10, 0)));
        }

        [Fact]
        public void Statutory_holidays_are_off_peak_for_deepseek_only()
        {
            // 2026-10-01 is a Thursday inside the National Day break, 10-05 the Monday.
            // The DeepSeek docs say "Monday through Friday, excluding Chinese public
            // holidays"; GO's say only "Monday through Friday". GO is not merely quiet
            // about it either - on 2026-10-02 (a Friday in the same break) its invoice
            // charged 09:45-11:59 Beijing calls at the full peak rate, so the two
            // providers really do disagree about the price of a holiday call.
            Assert.True(PeakHours.IsHoliday(Beijing(10, 1, 10, 0)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 1, 10, 0), true));    // DeepSeek: off
            Assert.True(PeakHours.IsPeak(Beijing(10, 1, 10, 0), false));    // GO: no such clause
            Assert.Equal("谷", PeakHours.Label(Beijing(10, 1, 10, 0), true));
            Assert.Equal("峰", PeakHours.Label(Beijing(10, 1, 10, 0), false));

            Assert.True(PeakHours.IsHoliday(Beijing(10, 5, 15, 0)));
            Assert.False(PeakHours.IsPeak(Beijing(10, 5, 15, 0), true));
            Assert.True(PeakHours.IsPeak(Beijing(10, 5, 15, 0), false));
        }

        [Fact]
        public void An_ordinary_weekday_is_not_a_holiday()
        {
            Assert.False(PeakHours.IsHoliday(Beijing(10, 8, 10, 0)));
            // A year with no table is simply "no holidays known" rather than an error.
            Assert.False(PeakHours.IsHoliday(new DateTime(2019, 6, 5, 2, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void The_label_is_the_single_character_the_panel_shows()
        {
            Assert.Equal("峰", PeakHours.Label(Beijing(10, 8, 10, 0)));
            Assert.Equal("谷", PeakHours.Label(Beijing(10, 8, 20, 0)));
        }

        [Fact]
        public void The_rule_is_read_in_utc_not_local_time()
        {
            // The window is defined in UTC (GO) and in Beijing time (DeepSeek), and
            // those are the same window - so the answer must not depend on where the
            // machine thinks it is. 01:30 UTC is 09:30 Beijing: peak.
            Assert.True(PeakHours.IsPeak(new DateTime(2026, 10, 8, 1, 30, 0, DateTimeKind.Utc)));
            Assert.False(PeakHours.IsPeak(new DateTime(2026, 10, 8, 5, 30, 0, DateTimeKind.Utc)));
        }
    }
}
