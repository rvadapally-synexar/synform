using System.Text.Json;
using System.Text.RegularExpressions;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

public sealed class Layer1Result
{
    public Dictionary<string, JsonElement> Values { get; } = [];
    public Dictionary<string, double> Confidences { get; } = [];
    public List<string> ClearedFields { get; } = [];
    public double ConsumedRatio { get; set; }
    public string UnmatchedText { get; set; } = "";
}

/// <summary>
/// Layer 1 — deterministic, layout-compiled resolver (spec §7). No LLM.
/// Handles field-targeted utterances ("ASA three", "weight 82 kilos", "BP 120 over 80")
/// and correction commands ("change weight to 85", "clear allergies").
/// </summary>
public sealed class Layer1Resolver
{
    private sealed record CompiledField(FieldDef Field, List<string> Phrases, List<OptionItem> Options);

    private readonly List<CompiledField> _fields;
    private static readonly Regex BpRegex = new(
        @"(\d{2,3})\s*(?:over|/)\s*(\d{2,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberRegex = new(
        @"(\d+(?:\.\d+)?)\s*(kg|kgs|kilos?|kilograms?|lbs?|pounds?|cm|centimet(?:er|re)s?|in|inches|bpm|beats)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Minutes may be colon- or space-separated: phrase normalization turns "9:30" into "9 30".
    private static readonly Regex TimeRegex = new(
        @"(\d{1,2})(?:[:\s](\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Layer1Resolver(LayoutDef layout, IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        _fields = layout.Fields
            .Where(f => f.ComputedFrom == null)
            .Select(f =>
            {
                var phrases = (f.Aliases ?? []).Append(f.Label)
                    .Select(TextNormalization.Normalize)
                    .Where(p => p != "")
                    .OrderByDescending(p => p.Length) // longest phrase wins
                    .ToList();
                var options = f.Options?.Inline
                    ?? (f.Options?.LookupKey is { } key
                        ? lookups.GetValueOrDefault(key, []).Where(r => r.Active)
                            .Select(r => new OptionItem { Value = r.Value, Label = r.Label }).ToList()
                        : []);
                return new CompiledField(f, phrases, options);
            })
            .ToList();
    }

    public Layer1Result Resolve(string input)
    {
        var result = new Layer1Result();
        var text = TextNormalization.WordsToNumbers(input);
        var totalWords = CountWords(text);
        var consumedWords = 0;
        var unmatched = new List<string>();

        foreach (var rawSegment in Regex.Split(text, @"[.,;\n]| and ").Select(s => s.Trim()).Where(s => s != ""))
        {
            var segment = rawSegment;
            var words = CountWords(segment);

            // Correction commands.
            var clear = Regex.Match(segment, @"^(?:please\s+)?clear\s+(.+)$", RegexOptions.IgnoreCase);
            if (clear.Success && FindField(TextNormalization.Normalize(clear.Groups[1].Value)) is { } cf)
            {
                result.ClearedFields.Add(cf.Field.Name);
                result.Values[cf.Field.Name] = JsonSerializer.SerializeToElement<object?>(null);
                result.Confidences[cf.Field.Name] = 0.95;
                consumedWords += words;
                continue;
            }
            var change = Regex.Match(segment, @"^(?:please\s+)?(?:change|set|update)\s+(.+?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
            if (change.Success && FindField(TextNormalization.Normalize(change.Groups[1].Value)) is { } chf)
            {
                if (ExtractValue(chf, change.Groups[2].Value, result)) { consumedWords += words; continue; }
            }

            // Field-targeted utterance: longest trigger phrase contained in the segment.
            var match = _fields
                .SelectMany(f => f.Phrases.Select(p => (f, p)))
                .Where(x => ContainsPhrase(segment, x.p))
                .OrderByDescending(x => x.p.Length)
                .Select(x => (CompiledField?)x.f)
                .FirstOrDefault();

            if (match is { } cfield)
            {
                var remainder = RemovePhrase(segment, cfield.Phrases.First(p => ContainsPhrase(segment, p)));
                if (ExtractValue(cfield, remainder, result)) { consumedWords += words; continue; }
            }

            unmatched.Add(rawSegment);
        }

        result.ConsumedRatio = totalWords == 0 ? 0 : (double)consumedWords / totalWords;
        result.UnmatchedText = string.Join(". ", unmatched);
        return result;
    }

    private CompiledField? FindField(string phrase)
    {
        // Exact phrase matches only — fuzzy matching across field names is dangerous
        // ("weight" vs "height" is Levenshtein distance 1). Fuzzy is reserved for option labels.
        foreach (var f in _fields)
            foreach (var p in f.Phrases)
                if (phrase == p || ContainsPhrase(phrase, p))
                    return f;
        return null;
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        Regex.IsMatch(TextNormalization.Normalize(text), $@"(?:^|\s){Regex.Escape(phrase)}(?:\s|$)");

    private static string RemovePhrase(string text, string phrase)
    {
        var normalized = TextNormalization.Normalize(text);
        var idx = normalized.IndexOf(phrase, StringComparison.Ordinal);
        return idx < 0 ? text : (normalized[..idx] + " " + normalized[(idx + phrase.Length)..]).Trim();
    }

    private static int CountWords(string s) =>
        Regex.Matches(s, @"\S+").Count;

    /// <summary>Extract a typed value for the field from the remainder text. Confidence 0.95 exact / 0.9 fuzzy.</summary>
    private bool ExtractValue(CompiledField cf, string remainder, Layer1Result result)
    {
        var f = cf.Field;
        remainder = remainder.Trim();
        if (remainder == "") return false;

        void Set(object? value, double confidence)
        {
            result.Values[f.Name] = JsonSerializer.SerializeToElement(value);
            result.Confidences[f.Name] = confidence;
        }

        switch (f.DataType)
        {
            case "bpPair":
            {
                var m = BpRegex.Match(remainder);
                if (!m.Success) return false;
                Set(new { sys = int.Parse(m.Groups[1].Value), dia = int.Parse(m.Groups[2].Value) }, 0.95);
                return true;
            }
            case "number":
            {
                var m = NumberRegex.Match(remainder);
                if (!m.Success) return false;
                var n = double.Parse(m.Groups[1].Value);
                var unit = m.Groups[2].Value.ToLowerInvariant();
                // Unit coercion to the field's declared unit.
                if (f.Unit == "kg" && (unit.StartsWith("lb") || unit.StartsWith("pound"))) n = Math.Round(n * 0.453592, 1);
                if (f.Unit == "cm" && (unit == "in" || unit.StartsWith("inch"))) n = Math.Round(n * 2.54, 1);
                Set(n, 0.95);
                return true;
            }
            case "time":
            {
                var m = TimeRegex.Match(remainder);
                if (!m.Success) return false;
                var hour = int.Parse(m.Groups[1].Value);
                var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                var meridiem = m.Groups[3].Value.Replace(".", "").ToLowerInvariant();
                // Spoken-language PM inference: "eleven last night" / "seven in the evening".
                // Getting NPO time wrong by 12 hours is a clinical error, not a nit.
                if (meridiem == "" && Regex.IsMatch(remainder, @"\b(night|tonight|evening|afternoon)\b", RegexOptions.IgnoreCase))
                    meridiem = "pm";
                if (meridiem == "pm" && hour < 12) hour += 12;
                if (meridiem == "am" && hour == 12) hour = 0;
                if (hour > 23 || minute > 59) return false;
                Set($"{hour:00}:{minute:00}", 0.95);
                return true;
            }
            case "date":
            {
                var m = Regex.Match(remainder, @"\d{4}-\d{2}-\d{2}");
                if (!m.Success) return false; // natural-language dates go to Layer 2
                Set(m.Value, 0.95);
                return true;
            }
            case "boolean":
            {
                var norm = TextNormalization.Normalize(remainder);
                if (Regex.IsMatch(norm, @"\b(yes|true|verified|confirmed|done)\b")) { Set(true, 0.95); return true; }
                if (Regex.IsMatch(norm, @"\b(no|false|not)\b")) { Set(false, 0.95); return true; }
                return false;
            }
            case "string[]":
            {
                var matched = MatchOptions(cf, remainder, multiple: true);
                if (matched.Count == 0) return false;
                Set(matched.Select(m => m.Value).ToArray(), matched.Min(m => m.Score) >= 1.0 ? 0.95 : 0.9);
                return true;
            }
            default: // string
            {
                if (cf.Options.Count > 0)
                {
                    var matched = MatchOptions(cf, remainder, multiple: false);
                    if (matched.Count == 0) return false;
                    Set(matched[0].Value, matched[0].Score >= 1.0 ? 0.95 : 0.9);
                    return true;
                }
                // Free text: only short, field-targeted remainders ("patient name John Smith").
                // Long remainders are narrative — leave them for Layer 2's language understanding.
                if (CountWords(remainder) > 6) return false;
                Set(remainder.Trim(), 0.9);
                return true;
            }
        }
    }

    private static List<(string Value, double Score)> MatchOptions(CompiledField cf, string remainder, bool multiple)
    {
        var norm = TextNormalization.Normalize(remainder);
        var found = new List<(string Value, double Score)>();
        foreach (var opt in cf.Options)
        {
            var byValue = ContainsPhrase(norm, TextNormalization.Normalize(opt.Value));
            var byLabel = ContainsPhrase(norm, TextNormalization.Normalize(opt.Label));
            var fuzzy = TextNormalization.FuzzyScore(norm, opt.Label) ?? TextNormalization.FuzzyScore(norm, opt.Value);
            if (byValue || byLabel) found.Add((opt.Value, 1.0));
            else if (fuzzy != null) found.Add((opt.Value, fuzzy.Value));
        }
        // Digit shortcut for numbered options ("ASA 3", "class 2").
        if (found.Count == 0)
        {
            var digit = Regex.Match(norm, @"\b(\d{1,2})\b");
            if (digit.Success && cf.Options.FirstOrDefault(o => o.Value == digit.Groups[1].Value) is { } byDigit)
                found.Add((byDigit.Value, 1.0));
        }
        if (!multiple && found.Count > 1) found = [found.OrderByDescending(x => x.Score).First()];
        return found;
    }
}
