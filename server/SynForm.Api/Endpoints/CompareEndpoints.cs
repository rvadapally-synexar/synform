using System.Diagnostics;
using System.Text.Json;
using SynForm.Api.Comparison;
using SynForm.Api.Data;
using SynForm.Api.Extraction;

namespace SynForm.Api.Endpoints;

/// <summary>
/// STT A/B harness: one audio clip → Azure Whisper + Deepgram batch in parallel →
/// word-level diff → optional extraction comparison → persisted run (observability).
/// Neither engine is ground truth; the diff measures disagreement.
/// </summary>
public static class CompareEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapCompareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/stt/compare", async (
            HttpRequest request, IWebHostEnvironment env,
            WhisperSttService whisper, DeepgramSttService deepgram,
            LayoutRepo layouts, LookupRepo lookups, ExtractionService extraction,
            CompareRunRepo runs, CancellationToken ct) =>
        {
            if (!whisper.IsConfigured || !deepgram.IsConfigured)
                return Results.Problem(statusCode: 503, title: "Both engines must be configured",
                    detail: $"whisper configured: {whisper.IsConfigured}, deepgram configured: {deepgram.IsConfigured}");

            if (!request.HasFormContentType || request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "Multipart form with an 'audio' file is required." });

            var file = request.Form.Files["audio"] ?? request.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var audio = ms.ToArray();
            var format = (file.ContentType ?? "audio/webm").Split('/').Last().Split(';').First() switch
            {
                "x-wav" => "wav", "wave" => "wav", "mpeg" => "mp3", "x-m4a" => "m4a", var f => f,
            };

            var layoutKey = request.Form["layoutKey"].FirstOrDefault();
            var note = request.Form["note"].FirstOrDefault();
            var doExtract = !string.Equals(request.Form["extract"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase);

            var layoutRow = string.IsNullOrWhiteSpace(layoutKey) ? null : layouts.LatestPublished(layoutKey!);
            string[] hotwords = layoutRow != null ? VibeVoiceSttService.HotwordsFor(layoutRow.Parse()) : [];

            var id = Guid.NewGuid();
            var dir = Path.Combine(env.ContentRootPath, "data", "stt-compare");
            Directory.CreateDirectory(dir);
            var audioPath = Path.Combine(dir, $"{id}.{format}");
            await File.WriteAllBytesAsync(audioPath, audio, ct);

            // Identical bytes + identical vocabulary biasing to both engines, in parallel.
            async Task<(string? Text, int Ms, string? Error, DeepgramBatchResult? Dg)> Run(bool isDeepgram)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (isDeepgram)
                    {
                        var r = await deepgram.TranscribeAsync(audio, format, hotwords, ct);
                        return (r.Text, (int)sw.ElapsedMilliseconds, null, r);
                    }
                    var text = await whisper.TranscribeAsync(audio, format, hotwords, ct);
                    return (text, (int)sw.ElapsedMilliseconds, null, null);
                }
                catch (Exception ex)
                {
                    return (null, (int)sw.ElapsedMilliseconds, ex.Message, null);
                }
            }

            var results = await Task.WhenAll(Run(isDeepgram: false), Run(isDeepgram: true));
            var w = results[0];
            var d = results[1];

            var diff = w.Text != null && d.Text != null ? TranscriptDiff.Compute(w.Text, d.Text) : null;

            // Level 2: does the disagreement survive extraction? (The metric that matters.)
            object? extractionComparison = null;
            int? conflicts = null;
            if (doExtract && layoutRow != null && w.Text != null && d.Text != null)
            {
                var layout = layoutRow.Parse();
                var resolved = lookups.ResolveFor(layout);

                async Task<(ExtractResponse? R, string? Err)> Ext(string text)
                {
                    try
                    {
                        return (await extraction.ExtractAsync(layoutRow.LayoutKey, layoutRow.Version, layout, resolved,
                            new ExtractionInput("transcript", text, null), ct), null);
                    }
                    catch (Exception ex) { return (null, ex.Message); }
                }

                var (ew, ewErr) = await Ext(w.Text);
                var (ed, edErr) = await Ext(d.Text);

                var fields = new List<object>();
                if (ew != null && ed != null)
                {
                    var count = 0;
                    foreach (var k in ew.Values.Keys.Union(ed.Values.Keys).OrderBy(k => k))
                    {
                        var va = ew.Values.TryGetValue(k, out var x) ? x.GetRawText() : null;
                        var vb = ed.Values.TryGetValue(k, out var y) ? y.GetRawText() : null;
                        var status = va == null ? "deepgram-only" : vb == null ? "whisper-only" : va == vb ? "agree" : "conflict";
                        if (status != "agree") count++;
                        fields.Add(new { field = k, whisper = va, deepgram = vb, status });
                    }
                    conflicts = count;
                }
                extractionComparison = new
                {
                    whisperError = ewErr,
                    deepgramError = edErr,
                    whisperLayer = ew?.Layer,
                    deepgramLayer = ed?.Layer,
                    fields,
                };
            }

            runs.Insert(new CompareRunRow(
                id, DateTime.UtcNow, layoutRow?.LayoutKey, layoutRow?.Version,
                format, audio.Length, d.Dg?.AudioSeconds, audioPath, note,
                w.Text, w.Ms, w.Error,
                d.Text, d.Ms, d.Error, d.Dg?.RequestId, d.Dg?.Model,
                diff?.Divergence, conflicts,
                diff != null ? JsonSerializer.Serialize(diff, JsonOpts) : null,
                extractionComparison != null ? JsonSerializer.Serialize(extractionComparison, JsonOpts) : null));

            return Results.Ok(new
            {
                id,
                layoutKey = layoutRow?.LayoutKey,
                layoutVersion = layoutRow?.Version,
                audioBytes = audio.Length,
                audioSeconds = d.Dg?.AudioSeconds,
                format,
                whisper = new { text = w.Text, ms = w.Ms, error = w.Error },
                deepgram = new { text = d.Text, ms = d.Ms, error = d.Error, model = d.Dg?.Model, requestId = d.Dg?.RequestId },
                diff,
                extraction = extractionComparison,
            });
        });

        app.MapGet("/api/stt/compare/runs", (int? limit, CompareRunRepo runs) =>
            Results.Ok(runs.List(Math.Clamp(limit ?? 50, 1, 500))));

        app.MapGet("/api/stt/compare/runs/{id:guid}", (Guid id, CompareRunRepo runs) =>
        {
            var row = runs.Get(id);
            if (row == null) return Results.NotFound();
            return Results.Ok(new
            {
                id = row.Id,
                createdAt = row.CreatedAt,
                layoutKey = row.LayoutKey,
                layoutVersion = row.LayoutVersion,
                audioFormat = row.AudioFormat,
                audioBytes = row.AudioBytes,
                audioSeconds = row.AudioSeconds,
                note = row.Note,
                whisper = new { text = row.WhisperText, ms = row.WhisperMs, error = row.WhisperError },
                deepgram = new { text = row.DeepgramText, ms = row.DeepgramMs, error = row.DeepgramError, model = row.DeepgramModel, requestId = row.DeepgramRequestId },
                divergence = row.Divergence,
                conflicts = row.Conflicts,
                diff = row.Diff != null ? (JsonElement?)JsonSerializer.Deserialize<JsonElement>(row.Diff) : null,
                extraction = row.Extraction != null ? (JsonElement?)JsonSerializer.Deserialize<JsonElement>(row.Extraction) : null,
            });
        });

        app.MapGet("/api/stt/compare/runs/{id:guid}/audio", (Guid id, CompareRunRepo runs) =>
        {
            var row = runs.Get(id);
            if (row == null || !File.Exists(row.AudioPath)) return Results.NotFound();
            return Results.File(row.AudioPath, $"audio/{row.AudioFormat}");
        });
    }
}
