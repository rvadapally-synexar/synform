using System.Text.RegularExpressions;

namespace SynForm.Api.Domain;

/// <summary>Publish-time validation (spec §4.6 + v1.1 additions). Fail fast at design time.</summary>
public static class LayoutValidator
{
    public static readonly string[] ControlTypes =
        ["text", "textarea", "number", "dropdown", "multiselect", "radio", "checkbox", "checkboxGroup", "date", "time", "bpPair",
         "search", "searchMulti", "tags"];

    public static readonly string[] DataTypes = ["string", "number", "boolean", "date", "time", "string[]", "bpPair"];

    // Controls whose options come from a static list (inline XOR lookup), loaded up front.
    private static readonly string[] OptionControls = ["dropdown", "multiselect", "radio", "checkboxGroup"];
    // Controls backed by a remote search index — options are NEVER preloaded.
    private static readonly string[] SearchControls = ["search", "searchMulti"];
    private static readonly Regex CamelCase = new("^[a-z][a-zA-Z0-9]*$", RegexOptions.Compiled);

    /// <param name="existingLookupKeys">All lookup_keys present in lookup_item.</param>
    /// <param name="previousPublished">The latest published version of the same layout, if any (cross-version integrity).</param>
    public static List<string> Validate(LayoutDef layout, ISet<string> existingLookupKeys, LayoutDef? previousPublished)
    {
        var errors = new List<string>();
        var names = new HashSet<string>();

        foreach (var f in layout.Fields)
        {
            if (!CamelCase.IsMatch(f.Name)) errors.Add($"Field '{f.Name}': name must be camelCase.");
            if (!names.Add(f.Name)) errors.Add($"Field '{f.Name}': duplicate name.");
            if (!ControlTypes.Contains(f.ControlType)) errors.Add($"Field '{f.Name}': unknown controlType '{f.ControlType}'.");
            if (!DataTypes.Contains(f.DataType)) errors.Add($"Field '{f.Name}': unknown dataType '{f.DataType}'.");

            var isOptionControl = OptionControls.Contains(f.ControlType);
            var isSearchControl = SearchControls.Contains(f.ControlType);
            var isTags = f.ControlType == "tags";
            var hasInline = f.Options?.Inline is { Count: > 0 };
            var hasLookup = !string.IsNullOrEmpty(f.Options?.LookupKey);
            var hasSearch = !string.IsNullOrEmpty(f.Options?.SearchKey);
            if (isOptionControl && hasInline == hasLookup)
                errors.Add($"Field '{f.Name}': option controls need exactly one of options.inline / options.lookupKey.");
            // search/searchMulti MUST have a searchKey and nothing else; tags MAY have a searchKey
            // (for typeahead suggestions) but is always free-entry.
            if (isSearchControl && !hasSearch)
                errors.Add($"Field '{f.Name}': controlType '{f.ControlType}' requires options.searchKey.");
            if (isSearchControl && (hasInline || hasLookup))
                errors.Add($"Field '{f.Name}': search controls take only options.searchKey, not inline/lookup.");
            if (isTags && (hasInline || hasLookup))
                errors.Add($"Field '{f.Name}': tags controls take only an optional options.searchKey.");
            if (!isOptionControl && !isSearchControl && !isTags && (hasInline || hasLookup || hasSearch))
                errors.Add($"Field '{f.Name}': controlType '{f.ControlType}' does not take options.");
            if (hasLookup && !existingLookupKeys.Contains(f.Options!.LookupKey!))
                errors.Add($"Field '{f.Name}': lookupKey '{f.Options!.LookupKey}' does not exist.");
            if (hasSearch && !SearchSources.Keys.Contains(f.Options!.SearchKey!))
                errors.Add($"Field '{f.Name}': searchKey '{f.Options!.SearchKey}' is not a registered search source.");
            if (f.MustBeTrue && f.ControlType != "checkbox")
                errors.Add($"Field '{f.Name}': mustBeTrue is only valid on checkbox controls.");
            if (f.Pattern != null)
            {
                try { _ = new Regex(f.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { errors.Add($"Field '{f.Name}': invalid regex pattern."); }
            }
        }

        // Reference resolution + dependency edges (filterBy, conditions, computedFrom).
        var edges = new List<(string From, string To)>(); // To depends on From
        foreach (var f in layout.Fields)
        {
            void CheckRef(string? target, string what)
            {
                if (target == null) return;
                if (!names.Contains(target)) errors.Add($"Field '{f.Name}': {what} references unknown field '{target}'.");
                else edges.Add((target, f.Name));
            }
            CheckRef(f.FilterBy, "filterBy");
            foreach (var cond in new[] { f.VisibleWhen, f.EnabledWhen, f.RequiredWhen }.Where(c => c != null))
                foreach (var rf in ConditionEvaluator.ReferencedFields(cond!).Distinct())
                    CheckRef(rf, "condition");
            if (f.ComputedFrom != null)
            {
                if (f.ComputedFrom.Fn != "bmi") errors.Add($"Field '{f.Name}': unknown computed fn '{f.ComputedFrom.Fn}'.");
                foreach (var input in f.ComputedFrom.Inputs) CheckRef(input, "computedFrom");
            }
            if (f.VisibleWhen?.Op != null && !ConditionEvaluator.Operators.Contains(f.VisibleWhen.Op))
                errors.Add($"Field '{f.Name}': unknown operator '{f.VisibleWhen.Op}'.");
        }

        foreach (var rule in layout.CrossFieldRules)
            foreach (var rf in ConditionEvaluator.ReferencedFields(rule.Condition).Distinct())
                if (!names.Contains(rf))
                    errors.Add($"Rule '{rule.Id}': references unknown field '{rf}'.");

        // DAG check with cycle naming.
        var cycle = FindCycle(names, edges);
        if (cycle != null) errors.Add($"Dependency cycle: {string.Join(" -> ", cycle)}.");

        // Alias collisions make Layer-1 extraction ambiguous (case-insensitive, across labels too).
        var phrases = new Dictionary<string, string>();
        foreach (var f in layout.Fields)
            foreach (var phrase in (f.Aliases ?? []).Append(f.Label).Select(a => a.Trim().ToLowerInvariant()).Where(a => a != "").Distinct())
            {
                if (phrases.TryGetValue(phrase, out var owner) && owner != f.Name)
                    errors.Add($"Alias collision: '{phrase}' is claimed by both '{owner}' and '{f.Name}'.");
                else phrases[phrase] = f.Name;
            }

        // Cross-version integrity: carried-over names keep their dataType (records stamped with old versions stay meaningful).
        if (previousPublished != null)
            foreach (var f in layout.Fields)
            {
                var prev = previousPublished.Fields.FirstOrDefault(p => p.Name == f.Name);
                if (prev != null && prev.DataType != f.DataType)
                    errors.Add($"Field '{f.Name}': dataType changed from '{prev.DataType}' to '{f.DataType}' — names are stable contracts; add a new field instead.");
            }

        return errors;
    }

    private static List<string>? FindCycle(IEnumerable<string> nodes, List<(string From, string To)> edges)
    {
        var adj = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());
        var state = nodes.ToDictionary(n => n, _ => 0); // 0 white, 1 gray, 2 black
        var stack = new Stack<string>();

        List<string>? Dfs(string n)
        {
            state[n] = 1;
            stack.Push(n);
            foreach (var m in adj.GetValueOrDefault(n, []))
            {
                if (!state.ContainsKey(m)) continue;
                if (state[m] == 1)
                {
                    var cycle = stack.TakeWhile(x => x != m).Append(m).Reverse().Append(m).ToList();
                    return cycle;
                }
                if (state[m] == 0 && Dfs(m) is { } found) return found;
            }
            stack.Pop();
            state[n] = 2;
            return null;
        }

        foreach (var n in state.Keys.ToList())
            if (state[n] == 0 && Dfs(n) is { } cycle) return cycle;
        return null;
    }
}
