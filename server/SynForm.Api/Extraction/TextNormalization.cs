using System.Text.RegularExpressions;

namespace SynForm.Api.Extraction;

public static class TextNormalization
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>
    /// Replace spelled-out numbers (0–500, enough for vitals incl. BP 260 / weight 400) with digits.
    /// "one twenty over eighty" → "120 over 80"; "eighty two" → "82"; "two hundred ten" → "210".
    /// </summary>
    public static string WordsToNumbers(string text)
    {
        var words = Regex.Split(text, @"(\s+|[.,;!?])");
        var output = new List<string>();
        var i = 0;
        while (i < words.Length)
        {
            if (string.IsNullOrWhiteSpace(words[i]) || Regex.IsMatch(words[i], @"^[.,;!?]$")) { output.Add(words[i]); i++; continue; }

            var (value, consumed) = ParseNumber(words, i);
            if (consumed > 0) { output.Add(value.ToString()); i += consumed; }
            else { output.Add(words[i]); i++; }
        }
        return string.Concat(output);
    }

    /// <summary>Parse a spelled number starting at index i (skipping whitespace tokens). Returns (value, raw tokens consumed incl. whitespace).</summary>
    private static (int Value, int Consumed) ParseNumber(string[] words, int start)
    {
        int total = 0, current = 0, i = start, lastNumberEnd = -1;
        bool any = false;

        while (i < words.Length)
        {
            var w = words[i].Trim().TrimEnd('-');
            if (w == "" ) { i++; continue; }
            var lower = w.ToLowerInvariant();

            if (Units.TryGetValue(lower, out var unit))
            {
                current += unit; any = true; lastNumberEnd = i + 1; i++;
            }
            else if (Tens.TryGetValue(lower, out var tens))
            {
                if (any && current is >= 1 and <= 9)
                    current = current * 100 + tens; // "one twenty" → 120 (BP speech pattern)
                else
                    current += tens;
                any = true; lastNumberEnd = i + 1; i++;
            }
            else if (lower == "hundred" && any)
            {
                current = (current == 0 ? 1 : current) * 100; lastNumberEnd = i + 1; i++;
            }
            else if (lower == "and" && any) { i++; }
            else break;
        }

        if (!any) return (0, 0);
        total += current;
        return total is >= 0 and <= 500 ? (total, lastNumberEnd - start) : (0, 0);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Fuzzy match: exact → 1.0; Levenshtein ≤2 on normalized strings → 0.9; else null.</summary>
    public static double? FuzzyScore(string input, string candidate)
    {
        var a = Normalize(input);
        var b = Normalize(candidate);
        if (a == b) return 1.0;
        if (a.Length >= 3 && Levenshtein(a, b) <= 2) return 0.9;
        return null;
    }

    public static string Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}
