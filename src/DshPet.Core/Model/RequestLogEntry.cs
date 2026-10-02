using System.Globalization;
using System.Text.Json;

namespace DshPet.Core.Model;

/// <summary>One row of the console request log.</summary>
/// <param name="Id">Stable id, used to make sure a call is only ever billed once.</param>
/// <param name="StartedAt">Unix milliseconds.</param>
/// <param name="Outcome"><c>succeeded</c>, <c>rejected</c>, ...</param>
/// <param name="Model">Model that served the call.</param>
/// <param name="CostUsd">What the call cost, in US dollars; NaN when the API sent null.</param>
/// <param name="DurationMs">Server-reported duration, for the --goprobe readout.</param>
public sealed record RequestLogEntry(
    string Id,
    long StartedAt,
    string Outcome,
    string Model,
    double CostUsd,
    long DurationMs)
{
    /// <summary>
    /// Whether this call moves the meter and therefore deserves a cue. Rejected
    /// calls have a null cost, and free/cached-to-zero calls report 0 - neither
    /// may produce a hit animation.
    /// </summary>
    public bool IsBillable => Outcome == "succeeded" && CostUsd > 0;
}
