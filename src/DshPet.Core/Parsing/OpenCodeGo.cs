using System.Globalization;
using System.Text.Json;
using DshPet.Core.Model;

namespace DshPet.Core.Parsing;

/// <summary>
/// The opencode.ai console API: the GO allowance meters and the per-call log.
///
/// Both parsers are total: malformed input yields "nothing found" instead of an
/// exception, because the callers turn that into a status line rather than a
/// crash. Values the API sends as decimal strings are accepted as numbers too.
/// </summary>
public static class OpenCodeGo
{
    /// <summary>The order the widget indexes its meters in.</summary>
    public static readonly string[] WindowOrder = { "fiveHour", "week", "month" };

    public const string StatusUrl = "https://opencode.ai/console/api/go/status";

    public const string LogsUrlBase =
        "https://opencode.ai/console/api/request-logs?category=inference&limit=100&since=";

    /// <summary>GET /console/api/go/status - access.meters.*, in dollars.</summary>
    public static IReadOnlyList<GoMeter> ParseStatus(string? json)
    {
        var meters = new List<GoMeter>(3);
        if (string.IsNullOrWhiteSpace(json)) return meters;
        try
        {
            using var doc = JsonDocument.Parse(json!);   // net48 refs lack [NotNullWhen]
            // TryGetProperty on a non-object throws rather than returning false, so
            // every hop is kind-checked first: a payload of the wrong shape must
            // read as "no meters", never as a crash.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return meters;
            if (!doc.RootElement.TryGetProperty("access", out var access)) return meters;
            if (access.ValueKind != JsonValueKind.Object) return meters;
            if (!access.TryGetProperty("meters", out var m) || m.ValueKind != JsonValueKind.Object) return meters;

            foreach (string name in WindowOrder)
            {
                if (!m.TryGetProperty(name, out var meter)) continue;
                if (meter.ValueKind != JsonValueKind.Object) continue;
                double limit = MicroCentsToUsd(meter, "limitMicroCents");
                double used = MicroCentsToUsd(meter, "usedMicroCents");
                if (double.IsNaN(limit) || double.IsNaN(used)) continue;
                meters.Add(new GoMeter(name, limit, used, Utc(meter, "resetsAt")));
            }
        }
        catch (JsonException)
        {
            return new List<GoMeter>(0);
        }
        return meters;
    }

    /// <summary>
    /// An ISO-8601 instant from the payload, as UTC. The API sends
    /// <c>"2026-10-05T00:00:00.000Z"</c>, but null is common and meaningful - the
    /// five hour window reports no reset time until its first call of the period.
    /// Anything unparseable is treated as absent rather than guessed at.
    /// </summary>
    private static DateTime? Utc(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        string raw = value.GetString() ?? "";
        DateTime parsed;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            return null;
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>GET /console/api/request-logs - the items array.</summary>
    public static IReadOnlyList<RequestLogEntry> ParseLogs(string? json)
    {
        var entries = new List<RequestLogEntry>();
        if (string.IsNullOrWhiteSpace(json)) return entries;
        try
        {
            using var doc = JsonDocument.Parse(json!);   // net48 refs lack [NotNullWhen]
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return entries;
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = String(item, "id");
                if (id.Length == 0) continue;            // an item without an id cannot be de-duplicated
                entries.Add(new RequestLogEntry(
                    id,
                    (long)Number(item, "startedAt"),
                    String(item, "outcome"),
                    String(item, "model"),
                    Number(item, "cost"),
                    (long)Number(item, "durationMs")));
            }
        }
        catch (JsonException)
        {
            return new List<RequestLogEntry>(0);
        }
        return entries;
    }

    // ------------------------------------------------------------- helpers ---

    private static double MicroCentsToUsd(JsonElement parent, string name)
    {
        double micro = Number(parent, name);
        return double.IsNaN(micro) ? double.NaN : micro / GoMeter.MicroCentsPerUsd;
    }

    /// <summary>A number, or a number that the API sent as a decimal string.</summary>
    private static double Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e)) return double.NaN;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out double d) ? d : double.NaN;
            case JsonValueKind.String:
                return double.TryParse(e.GetString(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out double s) ? s : double.NaN;
            default:
                return double.NaN;                        // null, missing, object, array
        }
    }

    private static string String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";
}
