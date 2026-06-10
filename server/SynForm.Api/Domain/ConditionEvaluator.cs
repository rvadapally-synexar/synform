using System.Text.Json;

namespace SynForm.Api.Domain;

public static class ConditionEvaluator
{
    public static readonly string[] Operators = ["eq", "neq", "in", "contains", "gt", "lt", "gte", "lte", "notEmpty"];

    /// <summary>Evaluate a condition against the current values map.</summary>
    public static bool Evaluate(Condition cond, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (cond.And is { Count: > 0 }) return cond.And.All(c => Evaluate(c, values));
        if (cond.Or is { Count: > 0 }) return cond.Or.Any(c => Evaluate(c, values));

        var left = Get(values, cond.Field);
        JsonElement? right = cond.ValueFromField != null ? Get(values, cond.ValueFromField) : cond.Value;

        return cond.Op switch
        {
            "notEmpty" => !IsEmpty(left),
            "eq" => Compare(left, right) == 0 || LooseEquals(left, right),
            "neq" => !(Compare(left, right) == 0 || LooseEquals(left, right)),
            "in" => right is { ValueKind: JsonValueKind.Array } arr
                       && arr.EnumerateArray().Any(item => LooseEquals(left, item)),
            "contains" => left is { ValueKind: JsonValueKind.Array } larr
                       && larr.EnumerateArray().Any(item => LooseEquals(item, right)),
            "gt" => Compare(left, right) > 0,
            "lt" => Compare(left, right) < 0,
            "gte" => Compare(left, right) >= 0,
            "lte" => Compare(left, right) <= 0,
            _ => false,
        };
    }

    /// <summary>Field names a condition references (for DAG checks and "all operands present").</summary>
    public static IEnumerable<string> ReferencedFields(Condition cond)
    {
        if (cond.And != null) foreach (var c in cond.And) foreach (var f in ReferencedFields(c)) yield return f;
        if (cond.Or != null) foreach (var c in cond.Or) foreach (var f in ReferencedFields(c)) yield return f;
        if (cond.Field != null) yield return cond.Field;
        if (cond.ValueFromField != null) yield return cond.ValueFromField;
    }

    public static bool IsEmpty(JsonElement? v) => v is null
        || v.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (v.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.Value.GetString()))
        || (v.Value.ValueKind == JsonValueKind.Array && v.Value.GetArrayLength() == 0);

    private static JsonElement? Get(IReadOnlyDictionary<string, JsonElement> values, string? field)
        => field != null && values.TryGetValue(field, out var v) ? v : null;

    /// <summary>String-insensitive equality: "3" eq 3, "yes" eq "YES".</summary>
    private static bool LooseEquals(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null) return false;
        return AsComparableString(a.Value) == AsComparableString(b.Value);
    }

    private static string AsComparableString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!.Trim().ToLowerInvariant(),
        JsonValueKind.Number => e.GetDouble().ToString("R"),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    /// <summary>Numeric compare when both parse as numbers; otherwise ordinal string compare
    /// (works for ISO dates "2026-06-10" and times "07:30"). Returns int.MinValue when incomparable.</summary>
    private static int Compare(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null || IsEmpty(a) || IsEmpty(b)) return int.MinValue;
        if (TryNum(a.Value, out var na) && TryNum(b.Value, out var nb)) return na.CompareTo(nb);
        if (a.Value.ValueKind == JsonValueKind.String && b.Value.ValueKind == JsonValueKind.String)
            return string.CompareOrdinal(a.Value.GetString(), b.Value.GetString());
        return int.MinValue;
    }

    private static bool TryNum(JsonElement e, out double n)
    {
        n = 0;
        if (e.ValueKind == JsonValueKind.Number) { n = e.GetDouble(); return true; }
        return e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), out n);
    }
}
