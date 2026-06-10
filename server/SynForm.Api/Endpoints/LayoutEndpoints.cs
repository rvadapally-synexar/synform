using System.Text.Json;
using System.Text.Json.Nodes;
using SynForm.Api.Data;
using SynForm.Api.Domain;

namespace SynForm.Api.Endpoints;

public static class LayoutEndpoints
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerOptions.Default) { WriteIndented = true };

    public record SaveDraftBody(JsonElement Json, DateTime? ExpectedUpdatedAt);
    public record CreateLayoutBody(string LayoutKey, string Title);
    public record CloneBody(string NewLayoutKey, string? Title);

    public static void MapLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/layouts");

        // List all layouts: latest published version + whether a draft exists (Maintainer home).
        g.MapGet("/", (LayoutRepo layouts) =>
        {
            var summary = layouts.All()
                .GroupBy(l => l.LayoutKey)
                .Select(grp => new
                {
                    layoutKey = grp.Key,
                    title = grp.OrderByDescending(l => l.Version).First().Parse().Title,
                    latestPublishedVersion = grp.Where(l => l.Status == "published").Select(l => (int?)l.Version).Max(),
                    draftVersion = grp.Where(l => l.Status == "draft").Select(l => (int?)l.Version).FirstOrDefault(),
                    publishedAt = grp.Where(l => l.Status == "published").Select(l => l.PublishedAt).Max(),
                });
            return Results.Ok(summary);
        });

        g.MapGet("/{key}", (string key, LayoutRepo layouts) =>
            layouts.LatestPublished(key) is { } row ? Results.Ok(ToDto(row)) : Results.NotFound());

        g.MapGet("/{key}/versions", (string key, LayoutRepo layouts) =>
            Results.Ok(layouts.All().Where(l => l.LayoutKey == key)
                .Select(l => new { l.Version, l.Status, l.PublishedAt, l.UpdatedAt })));

        g.MapGet("/{key}/versions/{version:int}", (string key, int version, LayoutRepo layouts) =>
            layouts.ByVersion(key, version) is { } row ? Results.Ok(ToDto(row)) : Results.NotFound());

        g.MapGet("/{key}/draft", (string key, LayoutRepo layouts) =>
            layouts.Draft(key) is { } row ? Results.Ok(ToDto(row)) : Results.NotFound());

        // Create a brand-new layout (draft v1, empty field list).
        g.MapPost("/", (CreateLayoutBody body, LayoutRepo layouts) =>
        {
            if (string.IsNullOrWhiteSpace(body.LayoutKey))
                return Results.BadRequest(new { error = "layoutKey is required" });
            if (layouts.All().Any(l => l.LayoutKey == body.LayoutKey))
                return Results.Conflict(new { error = $"Layout '{body.LayoutKey}' already exists." });
            var def = new LayoutDef { Title = body.Title };
            var row = layouts.CreateDraft(body.LayoutKey, 1, JsonSerializer.Serialize(def, Json.Options));
            return Results.Created($"/api/layouts/{body.LayoutKey}/draft", ToDto(row));
        });

        // Explicit user action: create draft vN+1 from the latest published version.
        g.MapPost("/{key}/draft", (string key, LayoutRepo layouts) =>
        {
            if (layouts.Draft(key) != null)
                return Results.Conflict(new { error = "A draft already exists for this layout." });
            var published = layouts.LatestPublished(key);
            if (published == null) return Results.NotFound(new { error = "No published version to draft from." });
            var row = layouts.CreateDraft(key, published.Version + 1, published.Json);
            return Results.Created($"/api/layouts/{key}/draft", ToDto(row));
        });

        // Save draft JSON (optimistic concurrency via expectedUpdatedAt).
        g.MapPut("/{key}/draft", (string key, SaveDraftBody body, LayoutRepo layouts) =>
        {
            var draft = layouts.Draft(key);
            if (draft == null) return Results.NotFound(new { error = "No draft exists. Create one first." });

            LayoutDef def;
            try { def = JsonSerializer.Deserialize<LayoutDef>(body.Json.GetRawText(), Json.Options)!; }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid layout JSON: {ex.Message}" }); }

            var expected = body.ExpectedUpdatedAt ?? draft.UpdatedAt;
            var updated = layouts.UpdateDraft(key, JsonSerializer.Serialize(def, Json.Options), expected);
            return updated != null
                ? Results.Ok(ToDto(updated))
                : Results.Conflict(new { error = "Draft was modified elsewhere. Reload and reapply your changes." });
        });

        g.MapDelete("/{key}/draft", (string key, LayoutRepo layouts) =>
            layouts.DiscardDraft(key) ? Results.NoContent() : Results.NotFound());

        // Publish: run §4.6 validation, then the version becomes immutable.
        g.MapPost("/{key}/publish", (string key, LayoutRepo layouts, LookupRepo lookups) =>
        {
            var draft = layouts.Draft(key);
            if (draft == null) return Results.NotFound(new { error = "No draft to publish." });

            var def = draft.Parse();
            var previous = layouts.LatestPublished(key)?.Parse();
            var errors = LayoutValidator.Validate(def, lookups.KeySet(), previous);
            if (errors.Count > 0) return Results.UnprocessableEntity(new { errors });

            var published = layouts.Publish(key);
            return Results.Ok(ToDto(published!));
        });

        // Clone an existing layout under a new key (draft v1).
        g.MapPost("/{key}/clone", (string key, CloneBody body, LayoutRepo layouts) =>
        {
            var source = layouts.Draft(key) ?? layouts.LatestPublished(key);
            if (source == null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.NewLayoutKey) || layouts.All().Any(l => l.LayoutKey == body.NewLayoutKey))
                return Results.Conflict(new { error = "newLayoutKey missing or already exists." });
            var def = source.Parse();
            if (body.Title != null) def.Title = body.Title;
            var row = layouts.CreateDraft(body.NewLayoutKey, 1, JsonSerializer.Serialize(def, Json.Options));
            return Results.Created($"/api/layouts/{body.NewLayoutKey}/draft", ToDto(row));
        });

        // JSON Schema — the machine/agent surface. ?version=N | "draft" | omitted (latest published).
        g.MapGet("/{key}/schema", (string key, string? version, LayoutRepo layouts, LookupRepo lookupRepo) =>
        {
            var row = version switch
            {
                null or "" or "latestPublished" => layouts.LatestPublished(key),
                "draft" => layouts.Draft(key),
                _ => int.TryParse(version, out var v) ? layouts.ByVersion(key, v) : null,
            };
            if (row == null) return Results.NotFound();
            var def = row.Parse();
            JsonObject schema = SchemaGenerator.Generate(key, row.Version, def, lookupRepo.ResolveFor(def));
            return Results.Text(schema.ToJsonString(Indented), "application/json");
        });
    }

    private static object ToDto(LayoutRow row) => new
    {
        row.LayoutKey,
        row.Version,
        row.Status,
        row.UpdatedAt,
        row.PublishedAt,
        Json = JsonSerializer.Deserialize<JsonElement>(row.Json),
    };
}
