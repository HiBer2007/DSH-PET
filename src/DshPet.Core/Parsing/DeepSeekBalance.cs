using System.Globalization;
using System.Text.Json;

namespace DshPet.Core.Parsing;

/// <summary>GET https://api.deepseek.com/user/balance</summary>
public static class DeepSeekBalance
{
    public const string DefaultUrl = "https://api.deepseek.com/user/balance";

    /// <summary>
    /// Total CNY balance, or NaN when the payload carries no usable CNY entry.
    ///
    /// The PowerShell generation scanned this payload as text, which worked but
    /// only because the two fields happened to appear in a convenient order; this
    /// reads the structure, so a field reorder or an extra currency cannot
    /// silently pick the wrong number.
    /// </summary>
    public static double ParseTotalCny(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return double.NaN;
        try
        {
            using var doc = JsonDocument.Parse(json!);   // net48 refs lack [NotNullWhen]
            // TryGetProperty on a non-object throws, so the shape is checked first.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return double.NaN;
            if (!doc.RootElement.TryGetProperty("balance_infos", out var infos) ||
                infos.ValueKind != JsonValueKind.Array)
                return double.NaN;

            foreach (var info in infos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object) continue;
                if (!info.TryGetProperty("currency", out var currency) ||
                    currency.ValueKind != JsonValueKind.String ||
                    !string.Equals(currency.GetString(), "CNY", StringComparison.Ordinal))
                    continue;
                if (!info.TryGetProperty("total_balance", out var total)) return double.NaN;
                return Decimalish(total);
            }
        }
        catch (JsonException)
        {
            return double.NaN;
        }
        return double.NaN;
    }

    private static double Decimalish(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float,
                                                CultureInfo.InvariantCulture, out double s) ? s : double.NaN,
        JsonValueKind.Number => e.TryGetDouble(out double d) ? d : double.NaN,
        _ => double.NaN,
    };
}
