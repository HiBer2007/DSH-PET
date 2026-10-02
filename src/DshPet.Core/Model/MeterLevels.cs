namespace DshPet.Core.Model;

/// <summary>How urgent an allowance is right now.</summary>
public enum MeterLevel
{
    /// <summary>Nothing has been read yet - never treated as a change, so the first reading cannot alert.</summary>
    Unknown = 0,

    /// <summary>Comfortably above the warning threshold.</summary>
    Plenty = 1,

    /// <summary>Below the warning threshold but still has something left.</summary>
    Low = 2,

    /// <summary>Nothing left.</summary>
    Empty = 3,
}

/// <summary>
/// Turns "how much is left" into a level, and level changes into the events worth
/// telling the user about.
///
/// Pure and static on purpose. Alerts are edge-triggered - fired when a level
/// *changes*, not while it stays true - and getting that wrong is what turns a
/// helpful reminder into a widget that nags on every poll. It also means the
/// interesting cases (crossing the threshold, running out, refilling) are all
/// testable without a network, a clock or a meter.
/// </summary>
public static class MeterLevels
{
    /// <summary>
    /// Classifies a remaining amount against a warning threshold. Either unit
    /// works: GO passes a percentage, DeepSeek passes yuan.
    ///
    /// A non-positive threshold means "never warn", which is different from a
    /// threshold of zero - zero would warn about exactly-empty, which is what
    /// <see cref="MeterLevel.Empty"/> already covers.
    /// </summary>
    public static MeterLevel For(double remaining, double threshold)
    {
        if (double.IsNaN(remaining)) return MeterLevel.Unknown;
        if (remaining <= 0) return MeterLevel.Empty;
        if (threshold > 0 && remaining <= threshold) return MeterLevel.Low;
        return MeterLevel.Plenty;
    }

    /// <summary>
    /// What to say when the level moved from <paramref name="was"/> to <paramref name="now"/>.
    ///
    /// An automatic reminder has one job: say what is short, when it comes back, and
    /// what to do about it. It deliberately does *not* carry the remaining amount or
    /// percentage - that is the double-click's answer, and putting it here produced
    /// messages that read as a run-on ("5 小时 2 小时 42 分后重置" parses as one
    /// duration, not as a label followed by a countdown).
    /// </summary>
    /// <param name="subject">What is short, including the noun: "5 小时额度", "DeepSeek 余额".</param>
    /// <param name="resetText">When it comes back, ready to read: "2 小时 42 分后重置", or empty.</param>
    /// <param name="advice">What to do about it: "注意额度", "需要充值".</param>
    /// <param name="refillWord">How to describe the good news: "已重置", "已充值".</param>
    public static string? MessageFor(MeterLevel was, MeterLevel now, string subject,
                                     string resetText, string advice, string refillWord = "已重置")
    {
        // Unknown on either side is start-up or a failed poll, not an event. Miss
        // the "was unknown" half of this and the very first reading announces that
        // every meter has just run out.
        if (was == MeterLevel.Unknown || now == MeterLevel.Unknown || now == was) return null;

        string tail = (resetText.Length > 0 ? "，" + resetText : "")
                    + (advice.Length > 0 ? " · " + advice : "");

        if (now == MeterLevel.Empty) return subject + "用完" + tail;

        // Anything coming *up* from empty is a refill, even if it only refilled to
        // just under the threshold: reporting that as "不足" would announce bad news
        // at the exact moment the allowance came back.
        if (was == MeterLevel.Empty) return subject + refillWord;

        if (now == MeterLevel.Low) return subject + "不足" + tail;
        return null;
    }
}