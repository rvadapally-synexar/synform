using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynForm.Api.Domain;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The layout JSON contract (spec §4). layoutKey/version live on the DB row, not in the JSON.</summary>
public sealed class LayoutDef
{
    public string? Title { get; set; }
    public List<FieldDef> Fields { get; set; } = [];
    public List<CrossFieldRule> CrossFieldRules { get; set; } = [];
}

public sealed class FieldDef
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string ControlType { get; set; } = "";   // closed set, see LayoutValidator
    public string DataType { get; set; } = "";      // string|number|boolean|date|time|string[]|bpPair
    public bool Required { get; set; }
    public bool MustBeTrue { get; set; }            // checkbox-only: required means "must be true"
    public OptionsDef? Options { get; set; }
    public string? Group { get; set; }
    public int Order { get; set; }
    public string? Unit { get; set; }
    public JsonElement? Min { get; set; }           // number, or "today" for dates
    public JsonElement? Max { get; set; }
    public string? Pattern { get; set; }
    public BpBounds? BpBounds { get; set; }
    public string[]? Aliases { get; set; }
    public Condition? VisibleWhen { get; set; }
    public Condition? EnabledWhen { get; set; }
    public Condition? RequiredWhen { get; set; }
    public ComputedFrom? ComputedFrom { get; set; }
    public string? FilterBy { get; set; }
}

public sealed class OptionsDef
{
    public List<OptionItem>? Inline { get; set; }
    public string? LookupKey { get; set; }
}

public sealed class OptionItem
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class BpBounds
{
    public double SysMin { get; set; } = 60;
    public double SysMax { get; set; } = 260;
    public double DiaMin { get; set; } = 30;
    public double DiaMax { get; set; } = 160;
}

/// <summary>
/// Condition (spec §4.3). Either a leaf {field, op, value|valueFromField}
/// or a one-level composite {and:[...]} / {or:[...]}.
/// Closed operator set: eq, neq, in, contains, gt, lt, gte, lte, notEmpty.
/// </summary>
public sealed class Condition
{
    public string? Field { get; set; }
    public string? Op { get; set; }
    public JsonElement? Value { get; set; }
    public string? ValueFromField { get; set; }
    public List<Condition>? And { get; set; }
    public List<Condition>? Or { get; set; }
}

public sealed class ComputedFrom
{
    public string Fn { get; set; } = "";
    public string[] Inputs { get; set; } = [];
}

/// <summary>Cross-field rule: the condition is an assertion that must hold.
/// The rule fires (validation error) when it evaluates false and all referenced fields are non-empty.</summary>
public sealed class CrossFieldRule
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public Condition Condition { get; set; } = new();
}
