using System.Text.Json;
using SynForm.Api.Data;
using SynForm.Api.Domain;

namespace SynForm.Api.Endpoints;

public static class RecordEndpoints
{
    public record SaveRecordBody(
        Guid? Id,                       // client-generated for idempotent saves; server mints if absent
        string LayoutKey,
        int LayoutVersion,
        Dictionary<string, JsonElement> Values,
        Dictionary<string, JsonElement>? Provenance,
        JsonElement? Context);

    public static void MapRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/records");

        g.MapPost("/", (SaveRecordBody body, LayoutRepo layouts, LookupRepo lookups, RecordRepo records) =>
            Save(body, body.Id ?? Guid.NewGuid(), layouts, lookups, records, created: true));

        g.MapPut("/{id:guid}", (Guid id, SaveRecordBody body, LayoutRepo layouts, LookupRepo lookups, RecordRepo records) =>
            records.ById(id) == null
                ? Results.NotFound()
                : Save(body, id, layouts, lookups, records, created: false));

        g.MapGet("/", (string? layoutKey, RecordRepo records) =>
            Results.Ok(records.List(layoutKey).Select(ToDto)));

        g.MapGet("/{id:guid}", (Guid id, RecordRepo records) =>
            records.ById(id) is { } row ? Results.Ok(ToDto(row)) : Results.NotFound());

        g.MapDelete("/{id:guid}", (Guid id, RecordRepo records) =>
            records.Delete(id) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult Save(SaveRecordBody body, Guid id, LayoutRepo layouts, LookupRepo lookups, RecordRepo records, bool created)
    {
        // Records validate against — and stamp — the exact published version the form rendered.
        var layoutRow = layouts.ByVersion(body.LayoutKey, body.LayoutVersion);
        if (layoutRow is null || layoutRow.Status != "published")
            return Results.UnprocessableEntity(new { errors = new { _form = new[] { $"Layout {body.LayoutKey} v{body.LayoutVersion} is not a published version." } } });

        var layout = layoutRow.Parse();
        var result = RecordValidator.Validate(layout, body.Values, lookups.ResolveFor(layout));
        if (!result.IsValid)
            return Results.UnprocessableEntity(new { errors = result.Errors, dropped = result.DroppedFields });

        // Provenance: keep entries only for fields that survived validation; shape-check source.
        var fieldNames = layout.Fields.Select(f => f.Name).ToHashSet();
        var provenance = (body.Provenance ?? [])
            .Where(kv => fieldNames.Contains(kv.Key) && result.CleanValues.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var row = records.Upsert(id, body.LayoutKey, body.LayoutVersion,
            JsonSerializer.Serialize(result.CleanValues),
            JsonSerializer.Serialize(provenance),
            body.Context?.GetRawText());

        var dto = ToDto(row);
        return created ? Results.Created($"/api/records/{row.Id}", new { recordId = row.Id, dropped = result.DroppedFields, record = dto })
                       : Results.Ok(new { recordId = row.Id, dropped = result.DroppedFields, record = dto });
    }

    private static object ToDto(FormRecordRow row) => new
    {
        row.Id,
        row.LayoutKey,
        row.LayoutVersion,
        Values = JsonSerializer.Deserialize<JsonElement>(row.FieldValues),
        Provenance = JsonSerializer.Deserialize<JsonElement>(row.Provenance),
        Context = row.Context == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.Context),
        row.CreatedAt,
        row.UpdatedAt,
    };
}
