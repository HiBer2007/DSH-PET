namespace DshPet.Core.Model;

/// <summary>
/// One OpenCode GO allowance window. The API reports money in micro-cents
/// (100,000,000 per US dollar); this type carries dollars.
/// </summary>
/// <param name="Window">API name: <c>fiveHour</c>, <c>week</c> or <c>month</c>.</param>
/// <param name="LimitUsd">Total allowance of the window.</param>
/// <param name="UsedUsd">How much of it is spent.</param>
/// <param name="ResetsAtUtc">
/// When the window rolls over, as reported by the API's <c>resetsAt</c>. Null is
/// normal and means "not scheduled yet": the five hour window starts on first use,
/// so before the first call of a period it has no reset time at all.
/// </param>
public sealed record GoMeter(string Window, double LimitUsd, double UsedUsd, DateTime? ResetsAtUtc = null)
{
    /// <summary>Micro-cents per US dollar, as documented by the budgets API.</summary>
    public const double MicroCentsPerUsd = 100_000_000.0;

    public double RemainingUsd => Math.Max(0, LimitUsd - UsedUsd);

    /// <summary>Percentage of the window still available, clamped to 0..100.</summary>
    public double RemainingPercent
    {
        get
        {
            if (LimitUsd <= 0) return double.NaN;
            double pct = (LimitUsd - UsedUsd) / LimitUsd * 100.0;
            // no Math.Clamp on net48
            return pct < 0 ? 0 : pct > 100 ? 100 : pct;
        }
    }

    /// <summary>Short label for the tablet: 5H / 周 / 月.</summary>
    public string ShortLabel => Window switch
    {
        "week" => "周",
        "month" => "月",
        _ => "5H",
    };

    /// <summary>Long label for the right-click menu.</summary>
    public string LongLabel => Window switch
    {
        "week" => "本周",
        "month" => "本月",
        _ => "5 小时",
    };

    /// <summary>
    /// How long until this window refills, as a short phrase for the speech bubble
    /// ("3天4小时", "1小时20分", "已重置").
    /// </summary>
    public string ResetCountdown(DateTime nowUtc) => FormatReset(ResetsAtUtc, nowUtc);

    /// <summary>
    /// Formats a reset time relative to <paramref name="nowUtc"/>.
    ///
    /// Static and total, so it can be tested without a meter, a network or a
    /// clock: every branch here is a boundary the widget will hit in real use
    /// (a window that has already rolled over, one about to, one days away).
    /// </summary>
    public static string FormatReset(DateTime? resetsAtUtc, DateTime nowUtc)
    {
        if (resetsAtUtc is null) return "未开始计时";

        TimeSpan left = resetsAtUtc.Value - nowUtc;
        if (left.TotalSeconds <= 0) return "已重置";
        if (left.TotalMinutes < 1) return "不到 1 分钟";
        if (left.TotalHours < 1) return (int)left.TotalMinutes + " 分钟";
        if (left.TotalDays < 1)
            return (int)left.TotalHours + " 小时" +
                   (left.Minutes > 0 ? " " + left.Minutes + " 分" : "");
        return (int)left.TotalDays + " 天" +
               (left.Hours > 0 ? " " + left.Hours + " 小时" : "");
    }
}
