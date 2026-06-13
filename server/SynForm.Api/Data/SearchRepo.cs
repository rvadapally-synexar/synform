using Dapper;

namespace SynForm.Api.Data;

public sealed record SearchHit(string Value, string Label);

/// <summary>
/// Queries the high-cardinality search sources behind options.searchKey. Each key maps to
/// an indexed query that returns the top matches for a typed term — the form never loads
/// the whole dataset. resolve() turns a stored id back into a display label (cold load /
/// post-dictation). Production swaps these bodies for the real indexes (NPI registry, etc.).
/// </summary>
public sealed class SearchRepo(Db db)
{
    public IReadOnlyList<SearchHit> Search(string key, string term, int limit)
    {
        term = (term ?? "").Trim();
        if (term.Length == 0) return [];
        limit = Math.Clamp(limit, 1, 50);
        using var c = db.Open();

        return key switch
        {
            "physician" => c.Query<SearchHit>("""
                SELECT npi AS value,
                       name || ' — ' || specialty || ', ' || city || ' ' || state AS label
                FROM npi_physician
                WHERE name ILIKE '%' || @term || '%' OR npi LIKE @term || '%'
                ORDER BY (name ILIKE @term || '%') DESC, name
                LIMIT @limit
                """, new { term, limit }).ToList(),

            "medication" => c.Query<SearchHit>("""
                SELECT name AS value, name AS label
                FROM medication
                WHERE name ILIKE '%' || @term || '%'
                ORDER BY (name ILIKE @term || '%') DESC, name
                LIMIT @limit
                """, new { term, limit }).ToList(),

            // Free-tag suggestions: the stored value IS the human label (e.g. "GERD").
            "comorbidity" => c.Query<SearchHit>("""
                SELECT label AS value, label AS label
                FROM lookup_item
                WHERE lookup_key = 'comorbidities' AND active
                  AND (label ILIKE '%' || @term || '%' OR value ILIKE '%' || @term || '%')
                ORDER BY (label ILIKE @term || '%') DESC, sort_order
                LIMIT @limit
                """, new { term, limit }).ToList(),

            _ => [],
        };
    }

    /// <summary>One hit for a stored value (display label for cold loads). Identity for value=label sources.</summary>
    public SearchHit? Resolve(string key, string id)
    {
        using var c = db.Open();
        return key switch
        {
            "physician" => c.QuerySingleOrDefault<SearchHit>("""
                SELECT npi AS value, name || ' — ' || specialty || ', ' || city || ' ' || state AS label
                FROM npi_physician WHERE npi = @id
                """, new { id }),
            "medication" or "comorbidity" => new SearchHit(id, id),
            _ => null,
        };
    }
}
