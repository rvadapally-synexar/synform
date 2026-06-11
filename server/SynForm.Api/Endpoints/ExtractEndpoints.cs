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

        // Which STT engine is configured? The voice panel adapts its UX accordingly:
        // vibevoice = push-to-talk batch (Azure AI Foundry, our tenancy, Microsoft BAA),
        // deepgram = streaming with endpointing.
        app.MapGet("/api/stt/engine", (IConfiguration config, VibeVoiceSttService vibeVoice) =>
        {
            var engine = config["Transcription:Engine"];
            if (string.IsNullOrWhiteSpace(engine))
                engine = vibeVoice.IsConfigured ? "vibevoice"
                    : !string.IsNullOrEmpty(config["Deepgram:ApiKey"]) ? "deepgram"
                    : "none";
            return Results.Ok(new { engine });
        });

        // Batch transcription for the push-to-talk path (VibeVoice-ASR on Azure AI Foundry).
        app.MapPost("/api/stt/transcribe", async (
            HttpRequest request, VibeVoiceSttService vibeVoice, LayoutRepo layouts,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "Multipart form with an 'audio' file is required." });
            if (!vibeVoice.IsConfigured)
                return Results.Problem(statusCode: 503, title: "VibeVoice not configured",
                    detail: "Set Transcription:VibeVoice:Endpoint and ApiKey in user-secrets.");

            var file = request.Form.Files["audio"] ?? request.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var format = file.ContentType.Split('/').Last().Split(';').First(); // audio/webm;codecs=opus → webm

            // Hotword-bias the ASR with the form's own clinical vocabulary.
            var layoutKey = request.Form["layoutKey"].FirstOrDefault();
            var hotwords = Array.Empty<string>();
            if (layoutKey != null && layouts.LatestPublished(layoutKey) is { } row)
                hotwords = VibeVoiceSttService.HotwordsFor(row.Parse());

            try
            {
                var text = await vibeVoice.TranscribeAsync(ms.ToArray(), format, hotwords, ct);
                return Results.Ok(new { text });
            }
            catch (HttpRequestException ex)
            {
                lf.CreateLogger("Stt").LogWarning(ex, "VibeVoice transcription failed");
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
