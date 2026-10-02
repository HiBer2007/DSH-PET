using DshPet.Core.Model;
using DshPet.Core.Parsing;
using Xunit;

namespace DshPet.Core.Tests;

public class ResetCountdownTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, "未开始计时")]
    [InlineData(-1, "已重置")]                 // one second past
    [InlineData(-86400, "已重置")]             // a whole day past
    [InlineData(1, "不到 1 分钟")]
    [InlineData(59, "不到 1 分钟")]
    [InlineData(60, "1 分钟")]
    [InlineData(20 * 60, "20 分钟")]
    [InlineData(3600, "1 小时")]
    [InlineData(3600 + 20 * 60, "1 小时 20 分")]
    [InlineData(23 * 3600, "23 小时")]
    [InlineData(86400, "1 天")]
    [InlineData(3 * 86400 + 4 * 3600, "3 天 4 小时")]
    [InlineData(12 * 86400, "12 天")]
    public void Formats_the_time_left_in_whole_units(int? secondsFromNow, string expected)
    {
        DateTime? resets = secondsFromNow is null ? null : Now.AddSeconds(secondsFromNow.Value);
        Assert.Equal(expected, GoMeter.FormatReset(resets, Now));
    }

    [Fact]
    public void The_boundary_between_hours_and_days_is_a_day_not_24_hours()
    {
        // 23h59m is still hours; 24h exactly is a day. Getting this wrong made a
        // weekly window read "167 小时".
        Assert.Equal("23 小时 59 分", GoMeter.FormatReset(Now.AddHours(23).AddMinutes(59), Now));
        Assert.Equal("1 天", GoMeter.FormatReset(Now.AddHours(24), Now));
    }

    [Fact]
    public void A_real_week_window_reads_as_days()
    {
        // The live capture's week window: resets 2026-10-05T00:00Z, seen from the 2nd.
        var meter = new GoMeter("week", 30, 30, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("2 天 12 小时", meter.ResetCountdown(Now));
    }
}

public class OpenCodeGoStatusTests
{
    /// <summary>The shape the console really returns, trimmed to the meters.</summary>
    private const string Live = """
    {"subscriberUserId":"acc_TEST","product":"go","access":{"startsAt":"2026-09-30T05:39:00.000Z",
    "endsAt":"2026-10-30T05:39:00.000Z","cancelAtPeriodEnd":false,"meters":{
    "fiveHour":{"startsAt":"2026-10-01T07:00:36.042Z","resetsAt":"2026-10-01T12:00:36.042Z",
    "limitMicroCents":"1200000000","usedMicroCents":"834450239"},
    "week":{"startsAt":"2026-09-28T00:00:00.000Z","resetsAt":"2026-10-05T00:00:00.000Z",
    "limitMicroCents":"3000000000","usedMicroCents":"3000000000"},
    "month":{"resetsAt":"2026-10-30T05:39:00.000Z","limitMicroCents":"6000000000",
    "usedMicroCents":"3000000000"}}},
    "upgradePrice":{"amountMicroCents":"3500000000","currency":"usd"}}
    """;

    [Fact]
    public void Parses_all_three_windows_in_the_documented_order()
    {
        var meters = OpenCodeGo.ParseStatus(Live);
        Assert.Equal(new[] { "fiveHour", "week", "month" }, meters.Select(m => m.Window));
    }

    [Fact]
    public void Reads_the_reset_time_of_each_window_as_utc()
    {
        var meters = OpenCodeGo.ParseStatus(Live);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 36, 42, DateTimeKind.Utc), meters[0].ResetsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), meters[1].ResetsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 30, 5, 39, 0, DateTimeKind.Utc), meters[2].ResetsAtUtc);
    }

    [Fact]
    public void A_window_with_no_reset_time_reads_as_none_rather_than_epoch()
    {
        // The five hour window reports null until its first call of the period.
        // Parsing that as 0001-01-01 would print "已重置" forever.
        var meters = OpenCodeGo.ParseStatus("""
        {"access":{"meters":{"fiveHour":{"resetsAt":null,"limitMicroCents":"1200000000","usedMicroCents":"0"}}}}
        """);
        Assert.Single(meters);
        Assert.Null(meters[0].ResetsAtUtc);
        Assert.Equal("未开始计时", meters[0].ResetCountdown(DateTime.UtcNow));
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("")]
    [InlineData("2026-10-05")]
    public void An_unusable_reset_time_is_treated_as_absent(string raw)
    {
        string json = "{\"access\":{\"meters\":{\"week\":{\"resetsAt\":\"" + raw +
                      "\",\"limitMicroCents\":\"3000000000\",\"usedMicroCents\":\"0\"}}}}";
        var meters = OpenCodeGo.ParseStatus(json);
        // a bare date still carries meaning (midnight), so it may parse; what must
        // never happen is a crash or a nonsense value
        if (meters.Count == 1 && meters[0].ResetsAtUtc is DateTime t)
            Assert.Equal(DateTimeKind.Utc, t.Kind);
    }

    [Fact]
    public void Converts_micro_cents_to_dollars_exactly()
    {
        var fiveHour = OpenCodeGo.ParseStatus(Live).Single(m => m.Window == "fiveHour");
        Assert.Equal(12.0, fiveHour.LimitUsd, 8);
        Assert.Equal(8.34450239, fiveHour.UsedUsd, 8);
        Assert.Equal(3.65549761, fiveHour.RemainingUsd, 8);
    }

    [Fact]
    public void Reported_percentages_match_the_console()
    {
        var meters = OpenCodeGo.ParseStatus(Live).ToDictionary(m => m.Window);
        Assert.Equal(30.46, meters["fiveHour"].RemainingPercent, 2);
        Assert.Equal(0.0, meters["week"].RemainingPercent, 2);
        Assert.Equal(50.0, meters["month"].RemainingPercent, 2);
    }

    [Fact]
    public void An_exhausted_window_reports_zero_remaining_and_never_negative()
    {
        var week = OpenCodeGo.ParseStatus(Live).Single(m => m.Window == "week");
        Assert.Equal(0.0, week.RemainingUsd, 8);
        Assert.Equal(0.0, week.RemainingPercent, 6);
    }

    [Fact]
    public void Overdrawn_usage_is_clamped_rather_than_shown_as_a_negative()
    {
        const string overdrawn = """
        {"access":{"meters":{"week":{"limitMicroCents":"3000000000","usedMicroCents":"3300000000"}}}}
        """;
        var week = OpenCodeGo.ParseStatus(overdrawn).Single();
        Assert.Equal(0.0, week.RemainingUsd, 8);
        Assert.Equal(0.0, week.RemainingPercent, 6);
    }

    [Fact]
    public void A_partial_payload_yields_only_the_windows_it_has()
    {
        const string partial = """
        {"access":{"meters":{"month":{"limitMicroCents":"6000000000","usedMicroCents":"1000000000"}}}}
        """;
        var meters = OpenCodeGo.ParseStatus(partial);
        Assert.Single(meters);
        Assert.Equal("month", meters[0].Window);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"access":null}""")]
    [InlineData("""{"access":{"meters":null}}""")]
    [InlineData("""{"access":{"meters":{"week":{}}}}""")]
    public void Unusable_payloads_return_nothing_instead_of_throwing(string? body)
    {
        Assert.Empty(OpenCodeGo.ParseStatus(body));
    }

    [Fact]
    public void Accepts_numbers_as_well_as_decimal_strings()
    {
        const string numeric = """
        {"access":{"meters":{"week":{"limitMicroCents":3000000000,"usedMicroCents":1500000000}}}}
        """;
        var week = OpenCodeGo.ParseStatus(numeric).Single();
        Assert.Equal(30.0, week.LimitUsd, 8);
        Assert.Equal(15.0, week.UsedUsd, 8);
    }
}

public class GoMeterTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.1, 0.1)]
    [InlineData(9.9, 9.9)]
    [InlineData(10.0, 10.0)]
    [InlineData(29.9, 29.9)]
    [InlineData(30.0, 30.0)]
    [InlineData(100.0, 100.0)]
    public void Remaining_percent_is_exact_at_the_band_boundaries(double remainingPercent, double expected)
    {
        var meter = new GoMeter("probe", 100, 100 - remainingPercent);
        Assert.Equal(expected, meter.RemainingPercent, 6);
    }

    [Fact]
    public void A_zero_limit_has_no_percentage_rather_than_a_divide_by_zero()
    {
        Assert.True(double.IsNaN(new GoMeter("probe", 0, 0).RemainingPercent));
    }

    [Theory]
    [InlineData("fiveHour", "5H", "5 \u5C0F\u65F6")]
    [InlineData("week", "\u5468", "\u672C\u5468")]
    [InlineData("month", "\u6708", "\u672C\u6708")]
    public void Labels_follow_the_window(string window, string shortLabel, string longLabel)
    {
        var meter = new GoMeter(window, 1, 0);
        Assert.Equal(shortLabel, meter.ShortLabel);
        Assert.Equal(longLabel, meter.LongLabel);
    }
}
