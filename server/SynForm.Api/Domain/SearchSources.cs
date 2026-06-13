namespace SynForm.Api.Domain;

/// <summary>
/// The registry of high-cardinality search sources a layout may bind a field to via
/// options.searchKey. This is the single source of truth for "what can be searched" —
/// LayoutValidator rejects unknown keys at publish time, and SearchRepo implements the
/// query per key. In production these map to real indexes (the NPI registry, a formulary);
/// in the POC each is a seeded sample table or an existing lookup list.
/// </summary>
public static class SearchSources
{
    public static readonly IReadOnlySet<string> Keys =
        new HashSet<string> { "physician", "medication", "comorbidity" };
}
