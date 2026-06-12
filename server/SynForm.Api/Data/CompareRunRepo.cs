using Dapper;

namespace SynForm.Api.Data;

// Typed mapping everywhere — Dapper's dynamic rows are case-sensitive against
// PostgreSQL's lowercase-folded aliases (silent-null landmine).
public sealed record CompareRunRow(
    Guid Id, DateTime CreatedAt, string? LayoutKey, int? LayoutVersion,
    string AudioFormat, int AudioBytes, double? AudioSeconds, string AudioPath, string? Note,
    string? WhisperText, int? WhisperMs, string? WhisperError,
    string? DeepgramText, int? DeepgramMs, string? DeepgramError,
    string? DeepgramRequestId, string? DeepgramModel,
    double? Divergence, int? Conflicts, string? Diff, string? Extraction);

public sealed record CompareRunSummary(
    Guid Id, DateTime CreatedAt, string? LayoutKey, int AudioBytes, double? AudioSeconds, string? Note,
    int? WhisperMs, string? WhisperError, int? DeepgramMs, string? DeepgramError,
    double? Divergence, int? Conflicts);

public sealed class CompareRunRepo(Db db)
{
    private const string Cols = """
        id, created_at AS createdat, layout_key AS layoutkey, layout_version AS layoutversion,
        audio_format AS audioformat, audio_bytes AS audiobytes,
        audio_seconds::float8 AS audioseconds,  -- REAL materializes as Single; records declare double
        audio_path AS audiopath, note,
        whisper_text AS whispertext, whisper_ms AS whisperms, whisper_error AS whispererror,
        deepgram_text AS deepgramtext, deepgram_ms AS deepgramms, deepgram_error AS deepgramerror,
        deepgram_request_id AS deepgramrequestid, deepgram_model AS deepgrammodel,
        divergence::float8 AS divergence, conflicts, diff::text AS diff, extraction::text AS extraction
        """;

    public void Insert(CompareRunRow row)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO stt_compare_run (
              id, layout_key, layout_version, audio_format, audio_bytes, audio_seconds, audio_path, note,
              whisper_text, whisper_ms, whisper_error,
              deepgram_text, deepgram_ms, deepgram_error, deepgram_request_id, deepgram_model,
              divergence, conflicts, diff, extraction)
            VALUES (
              @Id, @LayoutKey, @LayoutVersion, @AudioFormat, @AudioBytes, @AudioSeconds, @AudioPath, @Note,
              @WhisperText, @WhisperMs, @WhisperError,
              @DeepgramText, @DeepgramMs, @DeepgramError, @DeepgramRequestId, @DeepgramModel,
              @Divergence, @Conflicts, @Diff::jsonb, @Extraction::jsonb)
            """, row);
    }

    public IEnumerable<CompareRunSummary> List(int limit)
    {
        using var c = db.Open();
        return c.Query<CompareRunSummary>($"""
            SELECT id, created_at AS createdat, layout_key AS layoutkey, audio_bytes AS audiobytes,
                   audio_seconds::float8 AS audioseconds, note,
                   whisper_ms AS whisperms, whisper_error AS whispererror,
                   deepgram_ms AS deepgramms, deepgram_error AS deepgramerror,
                   divergence::float8 AS divergence, conflicts
            FROM stt_compare_run ORDER BY created_at DESC LIMIT @limit
            """, new { limit });
    }

    public CompareRunRow? Get(Guid id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<CompareRunRow>(
            $"SELECT {Cols} FROM stt_compare_run WHERE id = @id", new { id });
    }
}
