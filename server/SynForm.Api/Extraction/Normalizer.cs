using System.Globalization;
using System.Text.Json;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

/// <summary>
/// Deterministic normalizer (spec §7): option fuzzy-snap or reject, unit coercion,
/// date/time → ISO. Anything failing normalization is dropped and reported as skipped.
/// Runs after both layers, before values ever reach the form.
/// </summary>
public static class Normalizer
{
    public static Dictionary<string, JsonElement> Normalize(
        LayoutDef layout,
        IReadOnlyDictionary<string, List<LookupRow>> lookups,
        Dictionary<string, JsonElement> raw,
        List<SkippedField> skipped)
    {
        var fields = layout.Fields.ToDictionary(f => f.Name);
        var result = new Dictionary<string, JsonElement>();

        foreach (var (name, value) in raw)
        {
            if (!fields.TryGetValue(name, out var f)) { skipped.Add(new(name, "unknownField")); continue; }
            if (f.ComputedFrom != null) { skipped.Add(new(name, "readOnly")); continue; }
            if (value.ValueKind is JsonValueKind.Null) { result[name] = value; continue; } // explicit clear

            var normalized = NormalizeValue(f, value, lookups);
            if (normalized != null) result[name] = normalized.Value;
            else skipped.Add(new(name, OptionField(f) ? "invalidOption" : "invalidValue"));
        }
        return result;
    }

    private static bool OptionField(FieldDef f) => f.Options != null;

    private static JsonElement? NormalizeValue(FieldDef f, JsonElement value, IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        switch (f.DataType)
        {
            case "number":
            {
                if (value.ValueKind == JsonValueKind.Number) return value;
                if (value.ValueKind == JsonValueKind.String &&
                    double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return JsonSerializer.SerializeToElement(n);
                return null;
            }
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value : null;

            case "date":
            {
                var s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (s == null) return null;
                if (DateOnly.TryParseExact(s, "yyyy-MM-dd", out _)) return value;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return JsonSerializer.SerializeToElement(dt.ToString("yyyy-MM-dd"));
                return null;
            }
            case "time":
            {
                var s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (s == null) return null;
                if (TimeOnly.TryParseExact(s, "HH:mm", out _)) return value;
                if (TimeOnly.TryParse(s, CultureInfo.InvariantCulture, out var t))
                    return JsonSerializer.SerializeToElement(t.ToString("HH:mm"));
                return null;
            }
            case "bpPair":
            {
                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("sys", out var sys) && sys.ValueKind == JsonValueKind.Number
                    && value.TryGetProperty("dia", out var dia) && dia.ValueKind == JsonValueKind.Number)
                    return JsonSerializer.SerializeToElement(new { sys = sys.GetDouble(), dia = dia.GetDouble() });
                return null;
            }
            case "string[]":
            {
                if (value.ValueKind != JsonValueKind.Array) return null;
                // Snap each item independently; keep what matches. Dropping the whole list because
                // one phrasing missed ("reflux" vs "GERD") would silently lose valid clinical data.
                var snapped = value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => SnapOption(f, item.GetString()!, lookups))
                    .Where(s => s != null)
                    .Select(s => s!)
                    .Distinct()
                    .ToList();
                return snapped.Count > 0 ? JsonSerializer.SerializeToElement(snapped) : null;
            }
            default: // string
            {
                if (value.ValueKind != JsonValueKind.String) return null;
                if (!OptionField(f)) return value;
                var s = SnapOption(f, value.GetString()!, lookups);
                return s == null ? null : JsonSerializer.SerializeToElement(s);
            }
        }
    }

    /// <summary>Snap a candidate to an option value: exact value → exact label → fuzzy label (Levenshtein ≤2) → reject.</summary>
    private static string? SnapOption(FieldDef f, string candidate, IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        var options = f.Options?.Inline?.Select(o => (o.Value, o.Label)).ToList()
            ?? (f.Options?.LookupKey is { } key
                ? lookups.GetValueOrDefault(key, []).Where(r => r.Active).Select(r => (r.Value, r.Label)).ToList()
                : []);
        if (options.Count == 0) return candidate;

        foreach (var (v, _) in options) if (v == candidate) return v;
        foreach (var (v, l) in options) if (TextNormalization.Normalize(l) == TextNormalization.Normalize(candidate)) return v;
        foreach (var (v, l) in options) if (TextNormalization.FuzzyScore(candidate, l) != null) return v;
        foreach (var (v, _) in options) if (TextNormalization.FuzzyScore(candidate, v) != null) return v;
        return null;
    }
}
