using System.Collections.Generic;

namespace DshPet.Core.Model;

/// <summary>
/// Which allowance window actually decides whether the next call can be made.
///
/// GO's three windows are nested, not separate purses: the five hour allowance is 20%
/// of the monthly one and the week is 50%, and a call spends against all three at
/// once. So a window with money still in it can be unusable anyway - once the week is
/// spent, the five hour window refilling changes nothing, and a call made the moment
/// it refills is refused. The honest reading of that five hour window is "nothing,
/// until the week comes back", and that is what this works out.
/// </summary>
public static class MeterGates
{
    /// <summary>
    /// The three windows, narrowest first. This is the tier order: index 2 can block
    /// index 1, which can block index 0, and never the other way round.
    /// </summary>
    public static readonly string[] Tiers = { "fiveHour", "week", "month" };

    /// <summary>
    /// The highest window above <paramref name="window"/> that has run out, or -1 when
    /// none of them has.
    ///
    /// Highest rather than nearest, because the highest one refills last: with both
    /// the week and the month empty it is the month's reset that returns the account
    /// to service, and quoting the week's would promise quota a week early.
    /// </summary>
    /// <param name="remainingPercent">
    /// Remaining percentage per window, in <see cref="Tiers"/> order. A NaN means
    /// "no reading for this window yet" and never blocks: an unknown allowance must
    /// not be reported as an empty one.
    /// </param>
    /// <param name="window">Index of the window being asked about.</param>
    public static int Blocker(IReadOnlyList<double> remainingPercent, int window)
    {
        for (int j = Tiers.Length - 1; j > window; j--)
        {
            if (j >= remainingPercent.Count) continue;
            double pct = remainingPercent[j];
            if (!double.IsNaN(pct) && pct <= 0) return j;
        }
        return -1;
    }
}
