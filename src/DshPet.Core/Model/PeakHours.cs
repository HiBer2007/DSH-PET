using System;
using System.Collections.Generic;

namespace DshPet.Core.Model
{
    /// <summary>
    /// Peak or off-peak right now, from the providers' published windows.
    ///
    /// OpenCode GO states them as 01:00-04:00 and 06:00-10:00 UTC, Monday to Friday.
    /// The DeepSeek API states the same hours as Beijing time 09:00-12:00 and
    /// 14:00-18:00, Monday to Friday - the identical window, which is why one rule
    /// covers both. Off-peak costs half, so this is not decoration: it is the price of
    /// the next call.
    ///
    /// DeepSeek additionally excludes Chinese statutory holidays - "Monday through
    /// Friday, excluding Chinese public holidays ... including weekends and Chinese
    /// public holidays in full". GO's wording stops at "Monday through Friday" and
    /// never mentions holidays.
    ///
    /// That is not just looser prose on GO's side: GO's own invoice charges peak on a
    /// holiday. On 2026-10-02, a Friday inside the National Day break, calls placed
    /// 09:45-11:59 Beijing (inside the published peak window) cost exactly
    /// input*0.30 + output*1.20 + cacheRead*0.006 - the peak rate, matching to the
    /// cent on all 281 of them - while the 12:39-12:59 lunch calls came in at exactly
    /// half, the off-peak rate. So GO stays peak on a holiday weekday, and the holiday
    /// table is applied on the DeepSeek side only. Read from
    /// /console/api/request-logs, which reports cost and token counts per call.
    /// </summary>
    public static class PeakHours
    {
        /// <summary>
        /// Chinese statutory holidays that fall on a weekday, as MM-dd, per year.
        ///
        /// Only weekday holidays are listed, because weekends are off-peak anyway, and
        /// the make-up workdays (调休) are not listed at all: the DeepSeek wording is
        /// "Monday to Friday, excluding statutory holidays", so a Saturday is off-peak
        /// whether or not it has been designated a working day.
        ///
        /// There is no official API for this - the State Council publishes a notice once
        /// a year - so the dates are embedded. The 2026 row was read from
        /// https://timor.tech/api/holiday/year/2026 (39 entries: 19 weekday holidays and
        /// 6 make-up workdays) and cross-checked against the arrangement. Add a row when
        /// the next notice is published; until then that year's holidays read as peak,
        /// which errs towards warning about an expense rather than hiding one.
        /// </summary>
        static readonly Dictionary<int, string[]> WeekdayHolidays =
            new Dictionary<int, string[]> {
                { 2026, new[] {
                    "01-01", "01-02",                                     // 元旦
                    "02-16", "02-17", "02-18", "02-19", "02-20", "02-23",  // 春节
                    "04-06",                                              // 清明
                    "05-01", "05-04", "05-05",                            // 劳动节
                    "06-19",                                              // 端午
                    "09-25",                                              // 中秋
                    "10-01", "10-02", "10-05", "10-06", "10-07",          // 国庆
                } },
            };

        /// <summary>True when a statutory holiday falls on this Beijing date.</summary>
        public static bool IsHoliday(DateTime utcNow)
        {
            DateTime beijing = utcNow.AddHours(8);
            string[] days;
            if (!WeekdayHolidays.TryGetValue(beijing.Year, out days)) return false;
            string key = beijing.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < days.Length; i++)
                if (days[i] == key) return true;
            return false;
        }

        /// <param name="excludeHolidays">
        /// True for the DeepSeek API, whose wording excludes statutory holidays. GO
        /// does not mention them and does not bill that way either, so GO stays peak
        /// on a holiday weekday.
        /// </param>
        public static bool IsPeak(DateTime utcNow, bool excludeHolidays = true)
        {
            DateTime beijing = utcNow.AddHours(8);
            if (beijing.DayOfWeek == DayOfWeek.Saturday || beijing.DayOfWeek == DayOfWeek.Sunday)
                return false;
            if (excludeHolidays && IsHoliday(utcNow)) return false;

            double hour = beijing.Hour + beijing.Minute / 60.0 + beijing.Second / 3600.0;
            return (hour >= 9.0 && hour < 12.0) || (hour >= 14.0 && hour < 18.0);
        }

        /// <summary>
        /// One character, the panel's tag: "峰" or "谷". Two characters ("峰时") cost
        /// width on a row that already carries the product name, and the tag has to sit
        /// beside that name at a fixed spot rather than wherever the name leaves room.
        /// </summary>
        public static string Label(DateTime utcNow, bool excludeHolidays = true)
        {
            return IsPeak(utcNow, excludeHolidays) ? "峰" : "谷";
        }
    }
}
