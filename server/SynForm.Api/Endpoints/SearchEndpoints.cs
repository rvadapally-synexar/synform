using SynForm.Api.Data;
using SynForm.Api.Domain;

namespace SynForm.Api.Endpoints;

/// <summary>Typeahead + resolve for high-cardinality reference fields (options.searchKey).</summary>
public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search/{key}", (string key, string? q, int? limit, SearchRepo search) =>
        {
            if (!SearchSources.Keys.Contains(key))
                return Results.NotFound(new { error = $"Unknown search source '{key}'." });
            return Results.Ok(search.Search(key, q ?? "", limit ?? 20));
        });

        app.MapGet("/api/search/{key}/resolve", (string key, string id, SearchRepo search) =>
        {
            if (!SearchSources.Keys.Contains(key))
                return Results.NotFound(new { error = $"Unknown search source '{key}'." });
            return search.Resolve(key, id) is { } hit ? Results.Ok(hit) : Results.NotFound();
        });
    }
}
