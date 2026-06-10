using SynForm.Api.Data;

namespace SynForm.Api.Endpoints;

public static class LookupEndpoints
{
    public record LookupBody(string LookupKey, string Value, string Label, string? ParentKey, int SortOrder = 0, bool Active = true);
    public record LookupUpdateBody(string Label, string? ParentKey, int SortOrder, bool Active);

    public static void MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/lookups");

        g.MapGet("/", (LookupRepo lookups) => Results.Ok(lookups.Keys()));

        g.MapGet("/{key}", (string key, string? parent, bool? includeInactive, LookupRepo lookups) =>
            Results.Ok(lookups.ByKey(key, parent, includeInactive ?? false)));

        g.MapPost("/", (LookupBody body, LookupRepo lookups) =>
        {
            if (string.IsNullOrWhiteSpace(body.LookupKey) || string.IsNullOrWhiteSpace(body.Value))
                return Results.BadRequest(new { error = "lookupKey and value are required." });
            try
            {
                var row = lookups.Create(body.LookupKey, body.Value, body.Label, body.ParentKey, body.SortOrder, body.Active);
                return Results.Created($"/api/lookups/{body.LookupKey}", row);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = $"'{body.Value}' already exists in '{body.LookupKey}'." });
            }
        });

        g.MapPut("/{id:guid}", (Guid id, LookupUpdateBody body, LookupRepo lookups) =>
            lookups.Update(id, body.Label, body.ParentKey, body.SortOrder, body.Active) is { } row
                ? Results.Ok(row) : Results.NotFound());

        // Soft delete: published layouts and saved records may reference items, so we deactivate.
        g.MapDelete("/{id:guid}", (Guid id, LookupRepo lookups) =>
            lookups.Deactivate(id) ? Results.NoContent() : Results.NotFound());
    }
}
