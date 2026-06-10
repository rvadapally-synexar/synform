using System.Text.Json;
using Dapper;
using SynForm.Api.Domain;

namespace SynForm.Api.Data;

public sealed record LayoutRow(Guid Id, string LayoutKey, int Version, string Status, string Json,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? PublishedAt)
{
    public LayoutDef Parse() => JsonSerializer.Deserialize<LayoutDef>(Json, Domain.Json.Options)!;
}

public sealed record LookupItemRow(Guid Id, string LookupKey, string Value, string Label,
    string? ParentKey, int SortOrder, bool Active);

public sealed record FormRecordRow(Guid Id, string LayoutKey, int LayoutVersion,
    string FieldValues, string Provenance, string? Context, DateTime CreatedAt, DateTime UpdatedAt);

public sealed class LayoutRepo(Db db)
{
    private const string Cols = "id, layout_key AS layoutkey, version, status, json::text AS json, created_at AS createdat, updated_at AS updatedat, published_at AS publishedat";

    public IEnumerable<LayoutRow> All()
    {
        using var c = db.Open();
        return c.Query<LayoutRow>($"SELECT {Cols} FROM form_layout ORDER BY layout_key, version DESC");
    }

    public LayoutRow? LatestPublished(string key)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LayoutRow>(
            $"SELECT {Cols} FROM form_layout WHERE layout_key = @key AND status = 'published' ORDER BY version DESC LIMIT 1", new { key });
    }

    public LayoutRow? ByVersion(string key, int version)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LayoutRow>(
            $"SELECT {Cols} FROM form_layout WHERE layout_key = @key AND version = @version", new { key, version });
    }

    public LayoutRow? Draft(string key)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LayoutRow>(
            $"SELECT {Cols} FROM form_layout WHERE layout_key = @key AND status = 'draft'", new { key });
    }

    public LayoutRow CreateDraft(string key, int version, string json)
    {
        using var c = db.Open();
        return c.QuerySingle<LayoutRow>(
            $"INSERT INTO form_layout (layout_key, version, status, json) VALUES (@key, @version, 'draft', @json::jsonb) RETURNING {Cols}",
            new { key, version, json });
    }

    /// <summary>Optimistic concurrency: update succeeds only if the caller saw the latest updated_at.</summary>
    public LayoutRow? UpdateDraft(string key, string json, DateTime expectedUpdatedAt)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LayoutRow>(
            $"""
            UPDATE form_layout SET json = @json::jsonb, updated_at = now()
            WHERE layout_key = @key AND status = 'draft'
              AND date_trunc('milliseconds', updated_at) = date_trunc('milliseconds', @expectedUpdatedAt::timestamptz)
            RETURNING {Cols}
            """,
            new { key, json, expectedUpdatedAt });
    }

    public bool DiscardDraft(string key)
    {
        using var c = db.Open();
        return c.Execute("DELETE FROM form_layout WHERE layout_key = @key AND status = 'draft'", new { key }) > 0;
    }

    public LayoutRow? Publish(string key)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LayoutRow>(
            $"UPDATE form_layout SET status = 'published', published_at = now(), updated_at = now() WHERE layout_key = @key AND status = 'draft' RETURNING {Cols}",
            new { key });
    }
}

public sealed class LookupRepo(Db db)
{
    public IEnumerable<LookupItemRow> ByKey(string key, string? parent, bool includeInactive)
    {
        using var c = db.Open();
        return c.Query<LookupItemRow>(
            """
            SELECT id, lookup_key AS lookupkey, value, label, parent_key AS parentkey, sort_order AS sortorder, active
            FROM lookup_item
            WHERE lookup_key = @key
              AND (@parent::text IS NULL OR parent_key = @parent)
              AND (@includeInactive OR active)
            ORDER BY sort_order, label
            """, new { key, parent, includeInactive });
    }

    public IEnumerable<string> Keys()
    {
        using var c = db.Open();
        return c.Query<string>("SELECT DISTINCT lookup_key FROM lookup_item ORDER BY lookup_key");
    }

    public ISet<string> KeySet() => Keys().ToHashSet();

    /// <summary>Resolve all lookup keys a layout references → rows (for validation + schema generation).</summary>
    public Dictionary<string, List<LookupRow>> ResolveFor(LayoutDef layout)
    {
        var keys = layout.Fields.Select(f => f.Options?.LookupKey).Where(k => k != null).Distinct().ToList();
        var result = new Dictionary<string, List<LookupRow>>();
        foreach (var key in keys)
            result[key!] = ByKey(key!, parent: null, includeInactive: true)
                .Select(r => new LookupRow(r.Value, r.Label, r.ParentKey, r.Active)).ToList();
        return result;
    }

    public LookupItemRow Create(string lookupKey, string value, string label, string? parentKey, int sortOrder, bool active)
    {
        using var c = db.Open();
        return c.QuerySingle<LookupItemRow>(
            """
            INSERT INTO lookup_item (lookup_key, value, label, parent_key, sort_order, active)
            VALUES (@lookupKey, @value, @label, @parentKey, @sortOrder, @active)
            RETURNING id, lookup_key AS lookupkey, value, label, parent_key AS parentkey, sort_order AS sortorder, active
            """, new { lookupKey, value, label, parentKey, sortOrder, active });
    }

    public LookupItemRow? Update(Guid id, string label, string? parentKey, int sortOrder, bool active)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<LookupItemRow>(
            """
            UPDATE lookup_item SET label = @label, parent_key = @parentKey, sort_order = @sortOrder, active = @active
            WHERE id = @id
            RETURNING id, lookup_key AS lookupkey, value, label, parent_key AS parentkey, sort_order AS sortorder, active
            """, new { id, label, parentKey, sortOrder, active });
    }

    /// <summary>Items are never hard-deleted (published layouts and saved records may reference them). Deactivate instead.</summary>
    public bool Deactivate(Guid id)
    {
        using var c = db.Open();
        return c.Execute("UPDATE lookup_item SET active = false WHERE id = @id", new { id }) > 0;
    }
}

public sealed class RecordRepo(Db db)
{
    private const string Cols = "id, layout_key AS layoutkey, layout_version AS layoutversion, field_values::text AS fieldvalues, provenance::text AS provenance, context::text AS context, created_at AS createdat, updated_at AS updatedat";

    /// <summary>Client-generated id; idempotent upsert (offline-ready seam — a retried save can't duplicate).</summary>
    public FormRecordRow Upsert(Guid id, string layoutKey, int layoutVersion, string fieldValues, string provenance, string? context)
    {
        using var c = db.Open();
        return c.QuerySingle<FormRecordRow>(
            $"""
            INSERT INTO form_record (id, layout_key, layout_version, field_values, provenance, context)
            VALUES (@id, @layoutKey, @layoutVersion, @fieldValues::jsonb, @provenance::jsonb, @context::jsonb)
            ON CONFLICT (id) DO UPDATE SET
              field_values = EXCLUDED.field_values, provenance = EXCLUDED.provenance,
              context = EXCLUDED.context, updated_at = now()
            RETURNING {Cols}
            """, new { id, layoutKey, layoutVersion, fieldValues, provenance, context });
    }

    public FormRecordRow? ById(Guid id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<FormRecordRow>($"SELECT {Cols} FROM form_record WHERE id = @id", new { id });
    }

    public IEnumerable<FormRecordRow> List(string? layoutKey, int limit = 100)
    {
        using var c = db.Open();
        return c.Query<FormRecordRow>(
            $"SELECT {Cols} FROM form_record WHERE (@layoutKey::text IS NULL OR layout_key = @layoutKey) ORDER BY created_at DESC LIMIT @limit",
            new { layoutKey, limit });
    }

    public bool Delete(Guid id)
    {
        using var c = db.Open();
        return c.Execute("DELETE FROM form_record WHERE id = @id", new { id }) > 0;
    }
}
