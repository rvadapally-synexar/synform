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
                lf.CreateLogger("Extract").LogWarning(ex, "Extraction provider unreachable");
                return Results.Problem(statusCode: 503,
                    title: "Extraction provider unreachable",
                    detail: "Layer 2 LLM (Ollama/OpenAI) is not available. Check that Ollama is running or an OpenAI key is configured.");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: 503, title: "Extraction not configured", detail: ex.Message);
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
