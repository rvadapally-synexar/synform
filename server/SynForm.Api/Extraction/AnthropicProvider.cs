using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

/// <summary>
/// Claude provider (Anthropic Messages API). Handles BOTH text extraction and the photo
/// modality — Claude models are vision-capable, so one key covers Layer 2 and images.
/// Structured output via output_config.format (json_schema); confidences self-reported
/// per field and clamped to 0.9 like every other Layer-2 provider.
/// </summary>
public sealed class AnthropicProvider(IHttpClientFactory http, IConfiguration config) : IExtractionProvider
{
    private const string ApiVersion = "2023-06-01";

    public bool SupportsVision => true;

    public async Task<ProviderResult> ExtractAsync(LayoutDef layout, JsonObject jsonSchema, ExtractionInput input, CancellationToken ct)
    {
        var apiKey = config["Extraction:Anthropic:ApiKey"];
        if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Extraction:Anthropic:ApiKey is not configured (or set ANTHROPIC_API_KEY).");

        var model = config["Extraction:Anthropic:Model"] ?? "claude-opus-4-8";

        JsonNode userContent = input.InputType == "image"
            ? new JsonArray(
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/jpeg",
                        ["data"] = input.ImageBase64,
                    },
                },
                new JsonObject { ["type"] = "text", ["text"] = input.Text ?? "Extract all form fields visible in this image." })
            : JsonValue.Create(input.Text ?? "");

        // No output_config json_schema here: the API rejects large layout-derived schemas as
        // "too complex" (and layouts grow with the Maintainer). Prompt-directed JSON instead —
        // the deterministic Normalizer + RecordValidator re-enforce every constraint downstream.
        var system = ExtractionPrompt.Build(layout)
            + "\nRespond with ONLY a JSON object, no markdown fences and no prose, shaped as: "
            + "{\"fieldName\": {\"value\": <value>, \"confidence\": <0..1>}} containing only the fields you extracted.";
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["system"] = system,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };

        var client = http.CreateClient("anthropic");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)response.StatusCode}: {payload}");

        // First text block contains the schema-constrained JSON.
        var doc = JsonSerializer.Deserialize<JsonElement>(payload);
        var text = doc.GetProperty("content").EnumerateArray()
            .FirstOrDefault(b => b.TryGetProperty("type", out var t) && t.GetString() == "text");
        var content = text.ValueKind == JsonValueKind.Object && text.TryGetProperty("text", out var textValue)
            ? textValue.GetString() ?? "{}"
            : "{}";
        return ExtractionPrompt.Parse(StripFences(content));
    }

    /// <summary>Defensive: strip ```json fences if the model wraps its output despite instructions.</summary>
    private static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var start = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? s[(start + 1)..end].Trim() : s;
    }
}
