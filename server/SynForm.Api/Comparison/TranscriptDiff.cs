using System.Text.RegularExpressions;

namespace SynForm.Api.Comparison;

public sealed record DiffSegment(string Op, string A, string B); // equal | replace | delete | insert

public sealed record DiffResult(
    List<DiffSegment> Segments,
    int WordsA, int WordsB, int MatchingWords,
    double Divergence,                 // word edit distance / max(wordsA, wordsB) — 0 = identical
    List<DiffSegment> Disagreements);  // the replace segments: where the engines heard different words

/// <summary>
/// Word-level alignment of two transcripts via LCS. Tokens are compared case- and
/// punctuation-insensitively (so "10:15." matches "1015") but rendered verbatim.
/// Neither side is ground truth — Divergence measures disagreement, not error.
/// </summary>
public static class TranscriptDiff
{
    public static DiffResult Compute(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        var na = ta.Length;
        var nb = tb.Length;

        // LCS table on normalized tokens.
        var lcs = new int[na + 1, nb + 1];
        for (var i = na - 1; i >= 0; i--)
            for (var j = nb - 1; j >= 0; j--)
                lcs[i, j] = ta[i].Norm == tb[j].Norm
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // Backtrack into runs of equal / a-only / b-only, pairing adjacent a-only+b-only as "replace".
        var segments = new List<DiffSegment>();
        var i0 = 0; var j0 = 0;
        var eq = new List<string>(); var da = new List<string>(); var db = new List<string>();
        var matching = 0;

        void FlushNonEqual()
        {
            if (da.Count == 0 && db.Count == 0) return;
            var op = da.Count > 0 && db.Count > 0 ? "replace" : da.Count > 0 ? "delete" : "insert";
            segments.Add(new DiffSegment(op, string.Join(' ', da), string.Join(' ', db)));
            da.Clear(); db.Clear();
        }
        void FlushEqual()
        {
            if (eq.Count == 0) return;
            var run = string.Join(' ', eq);
            segments.Add(new DiffSegment("equal", run, run));
            eq.Clear();
        }

        while (i0 < na && j0 < nb)
        {
            if (ta[i0].Norm == tb[j0].Norm)
            {
                FlushNonEqual();
                eq.Add(ta[i0].Raw); matching++;
                i0++; j0++;
            }
            else
            {
                FlushEqual();
                if (lcs[i0 + 1, j0] >= lcs[i0, j0 + 1]) da.Add(ta[i0++].Raw);
                else db.Add(tb[j0++].Raw);
            }
        }
        if (i0 < na || j0 < nb) FlushEqual();
        while (i0 < na) da.Add(ta[i0++].Raw);
        while (j0 < nb) db.Add(tb[j0++].Raw);
        FlushNonEqual();
        FlushEqual();

        var editDistance = Math.Max(na, nb) == 0 ? 0 : (na - matching) + (nb - matching)
            - segments.Where(s => s.Op == "replace")
                .Sum(s => Math.Min(CountWords(s.A), CountWords(s.B))); // a replace pair is one substitution, not del+ins
        var divergence = Math.Max(na, nb) == 0 ? 0 : (double)editDistance / Math.Max(na, nb);

        return new DiffResult(
            segments, na, nb, matching,
            Math.Round(divergence, 4),
            segments.Where(s => s.Op == "replace").ToList());
    }

    private static int CountWords(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private readonly record struct Token(string Raw, string Norm);

    private static Token[] Tokenize(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new Token(w, Regex.Replace(w.ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "")))
            .Where(t => t.Norm.Length > 0 || t.Raw.Length > 0)
            .ToArray();
}
