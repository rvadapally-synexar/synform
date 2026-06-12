using System.Text;
using System.Text.Json;
using SynForm.Api.Data;
using SynForm.Api.Extraction;

namespace SynForm.Api.Endpoints;

public static class ExtractEndpoints
{
    public record ExtractBody(string LayoutKey, int? LayoutVersion, string InputType, string? Text, string? ImageBase64);

    public static void MapExtractEndpoints(this IEndpointRouteBuilder app)
    {
        // One seam, all modalities: text, transcript, image (spec §7).
        app.MapPost("/api/extract", async (
            ExtractBody body, LayoutRepo layouts, LookupRepo lookups, ExtractionService extraction,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            if (body.InputType is not ("text" or "transcript" or "image"))
                return Results.BadRequest(new { error = "inputType must be text|transcript|image." });

            // Version-pinned: extraction must match the layout version the form is rendering.
            var row = body.LayoutVersion is { } v
                ? layouts.ByVersion(body.LayoutKey, v)
                : layouts.LatestPublished(body.LayoutKey);
            if (row == null) return Results.NotFound(new { error = "Layout not found." });

            var layout = row.Parse();
            try
            {
                var result = await extraction.ExtractAsync(
                    body.LayoutKey, row.Version, layout, lookups.ResolveFor(layout),
                    new ExtractionInput(body.InputType, body.Text, body.ImageBase64), ct);
                return Results.Ok(result);
            }
            catch (HttpRequestException ex)
            {
                lf.CreateLogger("Extract").LogWarning(ex, "Extraction provider error");
                return Results.Problem(statusCode: 503,
                    title: "Extraction provider error",
                    detail: ex.Message); // surface the provider's own message (billing, auth, connectivity)
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: 503, title: "Extraction not configured", detail: ex.Message);
            }
        });

        // Which STT engine is the default, and which are selectable? ALL engines are
        // push-to-talk batch in the form filler: record the whole dictation, transcribe
        // once, extract once. (Deepgram streaming exists but populated fields from
        // fragments out of context — whole-transcript is the validated UX.)
        static string ResolveEngine(IConfiguration config, VibeVoiceSttService vibeVoice, WhisperSttService whisper)
        {
            var engine = config["Transcription:Engine"]?.ToLowerInvariant().Trim();
            if (!string.IsNullOrEmpty(engine)) return engine;
            if (vibeVoice.IsConfigured) return "vibevoice";
            if (whisper.IsConfigured) return "whisper";
            if (!string.IsNullOrEmpty(config["Deepgram:ApiKey"])) return "deepgram";
            return "none";
        }

        static string[] AvailableEngines(VibeVoiceSttService vibeVoice, WhisperSttService whisper, DeepgramSttService deepgram) =>
            new[]
            {
                whisper.IsConfigured ? "whisper" : null,
                deepgram.IsConfigured ? "deepgram" : null,
                vibeVoice.IsConfigured ? "vibevoice" : null,
            }.Where(e => e != null).Select(e => e!).ToArray();

        app.MapGet("/api/stt/engine", (IConfiguration config, VibeVoiceSttService vibeVoice, WhisperSttService whisper, DeepgramSttService deepgram) =>
            Results.Ok(new
            {
                engine = ResolveEngine(config, vibeVoice, whisper),
                available = AvailableEngines(vibeVoice, whisper, deepgram),
            }));

        // Batch transcription for the push-to-talk path. Optional 'engine' form field
        // overrides the server default (the voice panel's engine dropdown).
        app.MapPost("/api/stt/transcribe", async (
            HttpRequest request, IConfiguration config,
            VibeVoiceSttService vibeVoice, WhisperSttService whisper, DeepgramSttService deepgram,
            LayoutRepo layouts, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "Multipart form with an 'audio' file is required." });

            var requested = request.Form["engine"].FirstOrDefault()?.ToLowerInvariant().Trim();
            var engine = string.IsNullOrEmpty(requested) ? ResolveEngine(config, vibeVoice, whisper) : requested;
            var file = request.Form.Files["audio"] ?? request.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var format = file.ContentType.Split('/').Last().Split(';').First(); // audio/webm;codecs=opus → webm

            // Hotword-bias the ASR with the form's own clinical vocabulary (both engines).
            var layoutKey = request.Form["layoutKey"].FirstOrDefault();
            var hotwords = layoutKey != null && layouts.LatestPublished(layoutKey) is { } row
                ? VibeVoiceSttService.HotwordsFor(row.Parse())
                : [];

            try
            {
                string text;
                switch (engine)
                {
                    case "whisper" when whisper.IsConfigured:
                        text = await whisper.TranscribeAsync(ms.ToArray(), format, hotwords, ct);
                        break;
                    case "vibevoice" when vibeVoice.IsConfigured:
                        text = await vibeVoice.TranscribeAsync(ms.ToArray(), format, hotwords, ct);
                        break;
                    case "deepgram" when deepgram.IsConfigured:
                        text = (await deepgram.TranscribeAsync(ms.ToArray(), format, hotwords, ct)).Text;
                        break;
                    default:
                        return Results.Problem(statusCode: 503, title: "Transcription not configured",
                            detail: $"Engine '{engine}' is not configured. Set Transcription:Whisper, Transcription:VibeVoice, or Deepgram:ApiKey secrets.");
                }
                return Results.Ok(new { text });
            }
            catch (HttpRequestException ex)
            {
                lf.CreateLogger("Stt").LogWarning(ex, "{Engine} transcription failed", engine);
                return Results.Problem(statusCode: 502, title: "Transcription failed", detail: ex.Message);
            }
        });

        // Deepgram temporary-token proxy: the browser never sees the real API key.
        app.MapPost("/api/stt/token", async (IConfiguration config, IHttpClientFactory http, CancellationToken ct) =>
        {
            var apiKey = config["Deepgram:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
                return Results.Problem(statusCode: 503, title: "Deepgram not configured",
                    detail: "Set Deepgram:ApiKey in appsettings to enable voice.");

            var client = http.CreateClient("deepgram");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepgram.com/v1/auth/grant")
            {
                Content = new StringContent("""{"ttl_seconds": 300}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(statusCode: 502, title: "Deepgram token grant failed");
            var token = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            return Results.Ok(token);
        });
    }
}
