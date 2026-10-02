using DshPet.Core.Model;
using Xunit;

namespace DshPet.Core.Tests;

public class MeterLevelsTests
{
    [Theory]
    [InlineData(100, 20, MeterLevel.Plenty)]
    [InlineData(21, 20, MeterLevel.Plenty)]
    [InlineData(20, 20, MeterLevel.Low)]      // the threshold itself counts as low
    [InlineData(1, 20, MeterLevel.Low)]
    [InlineData(0, 20, MeterLevel.Empty)]
    [InlineData(-5, 20, MeterLevel.Empty)]    // an over-spent meter clamps to empty
    public void Classifies_against_the_threshold(double remaining, double threshold, MeterLevel expected)
    {
        Assert.Equal(expected, MeterLevels.For(remaining, threshold));
    }

    [Fact]
    public void An_unknown_reading_is_never_a_level()
    {
        Assert.Equal(MeterLevel.Unknown, MeterLevels.For(double.NaN, 20));
    }

    [Fact]
    public void A_threshold_of_zero_disables_the_low_warning()
    {
        // "Never warn" has to be expressible, otherwise turning reminders off would
        // mean setting a threshold of zero and being told about every empty meter
        // twice.
        Assert.Equal(MeterLevel.Plenty, MeterLevels.For(0.0001, 0));
        Assert.Equal(MeterLevel.Plenty, MeterLevels.For(50, -1));
        Assert.Equal(MeterLevel.Empty, MeterLevels.For(0, 0));
    }

    [Fact]
    public void Dropping_below_the_threshold_speaks_once()
    {
        // What is short, when it returns, what to do - and nothing else. The
        // remaining amount belongs to the double-click; carrying it here produced
        // messages that read as a run-on.
        Assert.Equal("本周额度不足，3 天后重置 · 注意额度",
            MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Low, "本周额度", "3 天后重置", "注意额度"));
    }

    [Fact]
    public void Staying_low_says_nothing()
    {
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Low, MeterLevel.Low, "本周额度", "3 天后重置", "注意额度"));
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Empty, MeterLevel.Empty, "本周额度", "3 天后重置", "注意额度"));
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Plenty, "本周额度", "3 天后重置", "注意额度"));
    }

    [Fact]
    public void Running_out_and_refilling_each_speak_once()
    {
        Assert.Equal("本周额度用完，3 天后重置 · 注意额度",
            MeterLevels.MessageFor(MeterLevel.Low, MeterLevel.Empty, "本周额度", "3 天后重置", "注意额度"));
        // The refill is good news, so it carries no advice and no countdown: there is
        // nothing to do about it and nothing to wait for.
        Assert.Equal("本周额度已重置",
            MeterLevels.MessageFor(MeterLevel.Empty, MeterLevel.Plenty, "本周额度", "3 天后重置", "注意额度"));
        Assert.Equal("DeepSeek 余额已充值",
            MeterLevels.MessageFor(MeterLevel.Empty, MeterLevel.Plenty, "DeepSeek 余额", "", "需要充值", "已充值"));
    }

    [Fact]
    public void A_meter_with_no_countdown_still_says_what_to_do()
    {
        // DeepSeek has no reset - it is a balance, and the only answer is to top it
        // up. An empty resetText must not leave a dangling comma.
        Assert.Equal("DeepSeek 余额不足 · 需要充值",
            MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Low, "DeepSeek 余额", "", "需要充值"));
        Assert.Equal("DeepSeek 余额用完 · 需要充值",
            MeterLevels.MessageFor(MeterLevel.Low, MeterLevel.Empty, "DeepSeek 余额", "", "需要充值"));
    }

    [Fact]
    public void Coming_up_from_empty_is_a_refill_even_if_it_stops_below_the_threshold()
    {
        // A refill that lands just under the threshold is still a refill. Reporting
        // it as "不足" would announce bad news at the exact moment the allowance came
        // back - and this is not hypothetical: the weekly window refills to a small
        // fraction when little of it has been spent yet.
        Assert.Equal("本周额度已重置",
            MeterLevels.MessageFor(MeterLevel.Empty, MeterLevel.Low, "本周额度", "3 天后重置", "注意额度"));
    }

    [Fact]
    public void Dropping_from_plenty_straight_to_empty_says_so()
    {
        // Possible when a poll gap covers a whole burst of spending.
        Assert.Equal("本周额度用完，3 天后重置 · 注意额度",
            MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Empty, "本周额度", "3 天后重置", "注意额度"));
    }

    [Fact]
    public void The_first_reading_never_alerts()
    {
        // Unknown -> anything is start-up, not a change.
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Unknown, MeterLevel.Empty, "本周额度", "3 天后重置", "注意额度"));
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Unknown, MeterLevel.Plenty, "本周额度", "3 天后重置", "注意额度"));
        Assert.Null(MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Unknown, "本周额度", "3 天后重置", "注意额度"));
    }

    [Fact]
    public void DeepSeek_uses_the_same_rules_with_yuan()
    {
        Assert.Equal(MeterLevel.Plenty, MeterLevels.For(20.0, 5.0));
        Assert.Equal(MeterLevel.Low, MeterLevels.For(4.99, 5.0));
        Assert.Equal(MeterLevel.Empty, MeterLevels.For(0.0, 5.0));
        Assert.Equal("DeepSeek 余额不足 · 需要充值",
            MeterLevels.MessageFor(MeterLevel.Plenty, MeterLevel.Low, "DeepSeek 余额", "", "需要充值"));
    }
}