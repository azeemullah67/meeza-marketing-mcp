using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meeza.Marketing.Shared.Utilities;

public static class Json
{
    /// <summary>Output options for tool responses. Nulls are written so "unavailable" stays visible to Claude.</summary>
    public static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Output);

    // ---- Tolerant readers: platform APIs send numbers as strings (int64 in Google APIs, all numerics in Meta insights).

    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>Follows a dotted path, e.g. "metrics.costMicros".</summary>
    public static JsonElement? Path(this JsonElement e, string path)
    {
        JsonElement? cur = e;
        foreach (var part in path.Split('.'))
        {
            cur = cur?.Prop(part);
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? Str(this JsonElement e, string path)
    {
        var v = e.Path(path);
        return v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.Value.GetRawText(),
            _ => null,
        };
    }

    public static decimal? Dec(this JsonElement e, string path) => ParseDecimal(e.Path(path));

    public static long? Long(this JsonElement e, string path)
    {
        var d = ParseDecimal(e.Path(path));
        return d is null ? null : (long)Math.Round(d.Value);
    }

    public static bool? Bool(this JsonElement e, string path)
    {
        var v = e.Path(path);
        return v?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.Value.GetString(), out var b) => b,
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string path)
    {
        var v = e.Path(path);
        return v is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
    }

    private static decimal? ParseDecimal(JsonElement? v) => v?.ValueKind switch
    {
        JsonValueKind.Number when v.Value.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => (decimal)v.Value.GetDouble(),
        JsonValueKind.String when decimal.TryParse(v.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
