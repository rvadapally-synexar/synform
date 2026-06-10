using System.Text.Json;
using System.Text.RegularExpressions;

namespace SynForm.Api.Domain;

public sealed record LookupRow(string Value, string Label, string? ParentKey, bool Active);

public sealed class RecordValidationResult
{
    public Dictionary<string, JsonElement> CleanValues { get; } = [];
    public Dictionary<string, List<string>> Errors { get; } = [];
    public List<string> DroppedFields { get; } = [];   // unknown or hidden — stripped, not errored
    public bool IsValid => Errors.Count == 0;

    public void AddError(string field, string message)
    {
        if (!Errors.TryGetValue(field, out var list)) Errors[field] = list = [];
        list.Add(message);
    }
}

/// <summary>Full server-side validation on save (spec §4.5). The server never trusts the client:
/// hidden-field values are stripped, computed fields are recomputed, cascade pairs are checked.</summary>
public static class RecordValidator
{
    public static RecordValidationResult Validate(
        LayoutDef layout,
        Dictionary<string, JsonElement> submitted,
        IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        var result = new RecordValidationResult();
        var fieldsByName = layout.Fields.ToDictionary(f => f.Name);

        // 1. Unknown fields are dropped.
        foreach (var key in submitted.Keys)
            if (!fieldsByName.ContainsKey(key)) result.DroppedFields.Add(key);
        var values = submitted.Where(kv => fieldsByName.ContainsKey(kv.Key))
                              .ToDictionary(kv => kv.Key, kv => kv.Value);

        // 2. Visibility to fixpoint (chained visibleWhen): hidden fields contribute no value.
        for (var pass = 0; pass < layout.Fields.Count; pass++)
        {
            var removed = false;
            foreach (var f in layout.Fields.Where(f => f.VisibleWhen != null))
                if (!ConditionEvaluator.Evaluate(f.VisibleWhen!, values) && values.Remove(f.Name))
                {
                    result.DroppedFields.Add(f.Name);
                    removed = true;
                }
            if (!removed) break;
        }

        // 3. Computed fields: server recomputes; submitted values for them are ignored.
        foreach (var f in layout.Fields.Where(f => f.ComputedFrom != null))
        {
            values.Remove(f.Name);
            var computed = ComputedFns.Compute(f.ComputedFrom!, values);
            if (computed != null) values[f.Name] = JsonSerializer.SerializeToElement(computed);
        }

        // 4. Per-field validation.
        foreach (var f in layout.Fields)
        {
            var visible = f.VisibleWhen == null || ConditionEvaluator.Evaluate(f.VisibleWhen, values);
            if (!visible) continue;

            values.TryGetValue(f.Name, out var raw);
            JsonElement? value = values.ContainsKey(f.Name) ? raw : null;

            var required = f.RequiredWhen != null
                ? ConditionEvaluator.Evaluate(f.RequiredWhen, values)
                : f.Required;
            if (required && ConditionEvaluator.IsEmpty(value))
            {
                result.AddError(f.Name, $"{f.Label} is required.");
                continue;
            }
            if (ConditionEvaluator.IsEmpty(value)) continue;

            ValidateValue(f, value!.Value, lookups, values, result);
        }

        // 5. Cross-field rules: assertion must hold; fires only when every referenced field has a value.
        foreach (var rule in layout.CrossFieldRules)
        {
            var refs = ConditionEvaluator.ReferencedFields(rule.Condition).Distinct().ToList();
            var allPresent = refs.All(r => values.TryGetValue(r, out var v) && !ConditionEvaluator.IsEmpty(v));
            if (allPresent && !ConditionEvaluator.Evaluate(rule.Condition, values))
                foreach (var r in refs)
                    result.AddError(r, rule.Message);
        }

        foreach (var kv in values) result.CleanValues[kv.Key] = kv.Value;
        return result;
    }

    private static void ValidateValue(
        FieldDef f, JsonElement value,
        IReadOnlyDictionary<string, List<LookupRow>> lookups,
        Dictionary<string, JsonElement> allValues,
        RecordValidationResult result)
    {
        switch (f.DataType)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) { result.AddError(f.Name, $"{f.Label} must be a string."); return; }
                if (f.Pattern != null)
                {
                    var text = value.GetString()!;
                    if (text.Length > 2000) { result.AddError(f.Name, $"{f.Label} is too long."); return; }
                    try
                    {
                        if (!Regex.IsMatch(text, f.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                            result.AddError(f.Name, $"{f.Label} does not match the expected format.");
                    }
                    catch (RegexMatchTimeoutException) { result.AddError(f.Name, $"{f.Label}: pattern check timed out."); }
                }
                CheckMembership(f, [value.GetString()!], lookups, allValues, result);
                break;

            case "number":
                if (value.ValueKind != JsonValueKind.Number) { result.AddError(f.Name, $"{f.Label} must be a number."); return; }
                var n = value.GetDouble();
                var unit = f.Unit != null ? $" {f.Unit}" : "";
                if (f.Min is { ValueKind: JsonValueKind.Number } min && n < min.GetDouble())
                    result.AddError(f.Name, $"{f.Label} must be at least {min.GetDouble()}{unit}.");
                if (f.Max is { ValueKind: JsonValueKind.Number } max && n > max.GetDouble())
                    result.AddError(f.Name, $"{f.Label} must be at most {max.GetDouble()}{unit}.");
                break;

            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { result.AddError(f.Name, $"{f.Label} must be true or false."); return; }
                if (f.MustBeTrue && value.ValueKind != JsonValueKind.True)
                    result.AddError(f.Name, $"{f.Label} must be confirmed.");
                break;

            case "date":
                if (value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out var d))
                { result.AddError(f.Name, $"{f.Label} must be a date (YYYY-MM-DD)."); return; }
                var todayStr = DateOnly.FromDateTime(DateTime.Today);
                if (f.Min != null && DateBound(f.Min.Value, todayStr) is { } dmin && d < dmin)
                    result.AddError(f.Name, $"{f.Label} must be on or after {dmin:yyyy-MM-dd}.");
                if (f.Max != null && DateBound(f.Max.Value, todayStr) is { } dmax && d > dmax)
                    result.AddError(f.Name, $"{f.Label} must be on or before {dmax:yyyy-MM-dd}.");
                break;

            case "time":
                if (value.ValueKind != JsonValueKind.String || !TimeOnly.TryParseExact(value.GetString(), "HH:mm", out _))
                    result.AddError(f.Name, $"{f.Label} must be a time (HH:mm).");
                break;

            case "string[]":
                if (value.ValueKind != JsonValueKind.Array ||
                    value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                { result.AddError(f.Name, $"{f.Label} must be a list of strings."); return; }
                CheckMembership(f, value.EnumerateArray().Select(e => e.GetString()!).ToList(), lookups, allValues, result);
                break;

            case "bpPair":
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("sys", out var sys) || sys.ValueKind != JsonValueKind.Number
                    || !value.TryGetProperty("dia", out var dia) || dia.ValueKind != JsonValueKind.Number)
                { result.AddError(f.Name, $"{f.Label} must be {{sys, dia}}."); return; }
                var b = f.BpBounds ?? new BpBounds();
                if (sys.GetDouble() < b.SysMin || sys.GetDouble() > b.SysMax)
                    result.AddError(f.Name, $"Systolic must be {b.SysMin}-{b.SysMax}.");
                if (dia.GetDouble() < b.DiaMin || dia.GetDouble() > b.DiaMax)
                    result.AddError(f.Name, $"Diastolic must be {b.DiaMin}-{b.DiaMax}.");
                if (dia.GetDouble() >= sys.GetDouble())
                    result.AddError(f.Name, "Diastolic must be lower than systolic.");
                break;
        }
    }

    /// <summary>Option membership (inline or active lookup items), including the cascade pair check:
    /// a filterBy child's value must belong to the selected parent.</summary>
    private static void CheckMembership(
        FieldDef f, IReadOnlyList<string> selected,
        IReadOnlyDictionary<string, List<LookupRow>> lookups,
        Dictionary<string, JsonElement> allValues,
        RecordValidationResult result)
    {
        if (f.Options == null) return;

        if (f.Options.Inline is { Count: > 0 } inline)
        {
            foreach (var v in selected)
                if (!inline.Any(o => o.Value == v))
                    result.AddError(f.Name, $"'{v}' is not a valid option for {f.Label}.");
            return;
        }

        if (f.Options.LookupKey is { } key)
        {
            var rows = lookups.GetValueOrDefault(key, []);
            string? parentValue = null;
            if (f.FilterBy != null && allValues.TryGetValue(f.FilterBy, out var pv) && pv.ValueKind == JsonValueKind.String)
                parentValue = pv.GetString();

            foreach (var v in selected)
            {
                var row = rows.FirstOrDefault(r => r.Value == v);
                if (row is null || !row.Active)
                    result.AddError(f.Name, $"'{v}' is not a valid option for {f.Label}.");
                else if (f.FilterBy != null && parentValue != null && row.ParentKey != parentValue)
                    result.AddError(f.Name, $"'{v}' does not belong to the selected {f.FilterBy}.");
            }
        }
    }

    private static DateOnly? DateBound(JsonElement bound, DateOnly today)
    {
        if (bound.ValueKind == JsonValueKind.String)
        {
            var s = bound.GetString();
            if (s == "today") return today;
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d)) return d;
        }
        return null;
    }
}

public static class ComputedFns
{
    /// <summary>Registry of derived-field functions. POC: exactly one — bmi.</summary>
    public static double? Compute(ComputedFrom cf, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (cf.Fn != "bmi" || cf.Inputs.Length != 2) return null;
        if (!TryNumber(values, cf.Inputs[0], out var heightCm) || !TryNumber(values, cf.Inputs[1], out var weightKg)) return null;
        if (heightCm <= 0) return null;
        var m = heightCm / 100.0;
        return Math.Round(weightKg / (m * m), 1);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, JsonElement> values, string field, out double n)
    {
        n = 0;
        return values.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.Number && (n = v.GetDouble()) is not double.NaN;
    }
}
