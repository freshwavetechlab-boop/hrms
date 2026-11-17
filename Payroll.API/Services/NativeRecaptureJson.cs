using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Payroll.API.Services;

// MySQL normalizes numeric JSON spelling. Compare mathematical number tokens
// exactly, without decimal/double conversion or tolerance, while retaining the
// independently byte-hashed original documents. Only Draft recapture uses this.
internal static class NativeRecaptureJson
{
    internal static bool Equivalent(JsonNode? left, JsonNode? right) => Equivalent(left?.ToJsonString() ?? "null", right?.ToJsonString() ?? "null");
    internal static bool Equivalent(string left, string right)
    {
        try
        {
            using var a = JsonDocument.Parse(left); using var b = JsonDocument.Parse(right);
            return Same(a.RootElement, b.RootElement);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or OverflowException) { return false; }
    }

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var x = Properties(a); var y = Properties(b);
                return x is not null && y is not null && x.Count == y.Count
                    && x.All(p => y.TryGetValue(p.Key, out var value) && Same(p.Value, value));
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
            case JsonValueKind.Number:
                return Number(a.GetRawText()) is string value && value == Number(b.GetRawText());
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Null: case JsonValueKind.True: case JsonValueKind.False: return true;
            default: return false;
        }
    }

    private static Dictionary<string, JsonElement>? Properties(JsonElement value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!result.TryAdd(property.Name, property.Value)) return null;
        return result;
    }

    private static string? Number(string token)
    {
        if (token.Length is < 1 or > 256) return null;
        var negative = token[0] == '-'; if (negative) token = token[1..];
        var exponentIndex = token.IndexOfAny(['e', 'E']); var exponent = 0;
        if (exponentIndex >= 0)
        {
            var spelling = token[(exponentIndex + 1)..];
            if (spelling.Length > 7 || !int.TryParse(spelling, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is < -100000 or > 100000) return null;
            token = token[..exponentIndex];
        }
        var dot = token.IndexOf('.');
        if (dot >= 0) { exponent -= token.Length - dot - 1; token = token.Remove(dot, 1); }
        token = token.TrimStart('0'); if (token.Length == 0) return "0";
        var end = token.Length; while (end > 0 && token[end - 1] == '0') { end--; exponent++; }
        return (negative ? "-" : "") + token[..end] + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
