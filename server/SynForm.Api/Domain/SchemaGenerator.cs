using System.Text.Json.Nodes;

namespace SynForm.Api.Domain;

/// <summary>Generates JSON Schema (draft 2020-12) from a layout — the machine/agent surface (spec §6).</summary>
public static class SchemaGenerator
{
    public static JsonObject Generate(
        string layoutKey, int version, LayoutDef layout,
        IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var f in layout.Fields)
        {
            var prop = FieldSchema(f, lookups);
            var descriptions = new List<string>();
            if (f.Unit != null) descriptions.Add($"Unit: {f.Unit}.");
            if (f.Options?.SearchKey is { } sk)
                descriptions.Add(f.ControlType == "search"
                    ? $"Return the {f.Label} exactly as spoken (e.g. a person's name) as a plain string; the server resolves it against the {sk} registry — do not invent an id."
                    : $"Return each value as a plain string as spoken; the server resolves against the {sk} source.");
            if (f.ControlType == "tags")
                descriptions.Add("Free-text list: return each item the clinician mentions as a separate string. Values are NOT restricted to a fixed set.");
            if (f.RequiredWhen != null) descriptions.Add("Conditionally required (see layout requiredWhen).");
            if (f.VisibleWhen != null) descriptions.Add("Conditionally visible; omit unless the condition holds.");
            if (f.ComputedFrom != null) descriptions.Add($"Derived ({f.ComputedFrom.Fn}); server computes — do not submit.");
            if (f.FilterBy != null) descriptions.Add($"Allowed values depend on '{f.FilterBy}'.");
            if (descriptions.Count > 0) prop["description"] = string.Join(" ", descriptions);
            prop["title"] = f.Label;
            if (f.ComputedFrom != null) prop["readOnly"] = true;
            properties[f.Name] = prop;

            if (f.Required && f.RequiredWhen == null && f.ComputedFrom == null) required.Add(f.Name);
            if (f.MustBeTrue) required.Add(f.Name);
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"synform:{layoutKey}:v{version}",
            ["title"] = layout.Title ?? layoutKey,
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject FieldSchema(FieldDef f, IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        JsonArray? EnumValues()
        {
            if (f.Options?.Inline is { Count: > 0 } inline)
                return new JsonArray(inline.Select(o => (JsonNode)o.Value).ToArray());
            if (f.Options?.LookupKey is { } key)
                return new JsonArray(lookups.GetValueOrDefault(key, [])
                    .Where(r => r.Active).Select(r => (JsonNode)r.Value).ToArray());
            return null;
        }

        switch (f.DataType)
        {
            case "number":
                var num = new JsonObject { ["type"] = "number" };
                if (f.Min is { ValueKind: System.Text.Json.JsonValueKind.Number } min) num["minimum"] = min.GetDouble();
                if (f.Max is { ValueKind: System.Text.Json.JsonValueKind.Number } max) num["maximum"] = max.GetDouble();
                return num;
            case "boolean":
                var b = new JsonObject { ["type"] = "boolean" };
                if (f.MustBeTrue) b["const"] = true;
                return b;
            case "date":
                return new JsonObject { ["type"] = "string", ["format"] = "date" };
            case "time":
                return new JsonObject { ["type"] = "string", ["pattern"] = "^([01][0-9]|2[0-3]):[0-5][0-9]$" };
            case "string[]":
                var items = new JsonObject { ["type"] = "string" };
                if (EnumValues() is { } arrEnum) items["enum"] = arrEnum;
                return new JsonObject { ["type"] = "array", ["items"] = items };
            case "bpPair":
                var bounds = f.BpBounds ?? new BpBounds();
                return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sys"] = new JsonObject { ["type"] = "number", ["minimum"] = bounds.SysMin, ["maximum"] = bounds.SysMax },
                        ["dia"] = new JsonObject { ["type"] = "number", ["minimum"] = bounds.DiaMin, ["maximum"] = bounds.DiaMax },
                    },
                    ["required"] = new JsonArray("sys", "dia"),
                };
            default: // string
                var s = new JsonObject { ["type"] = "string" };
                if (f.Pattern != null) s["pattern"] = f.Pattern;
                if (EnumValues() is { } strEnum) s["enum"] = strEnum;
                return s;
        }
    }
}
