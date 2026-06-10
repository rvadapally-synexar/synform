using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

public sealed record ExtractionInput(string InputType, string? Text, string? ImageBase64);

public sealed class ProviderResult
{
    public Dictionary<string, JsonElement> Values { get; init; } = [];
    public Dictionary<string, double> Confidences { get; init; } = [];
}

/// <summary>Layer 2 — LLM structured extraction, provider-agnostic (spec §7).</summary>
public interface IExtractionProvider
{
    bool SupportsVision { get; }
    Task<ProviderResult> ExtractAsync(LayoutDef layout, JsonObject jsonSchema, ExtractionInput input, CancellationToken ct);
}

public static class ExtractionPrompt
{
    /// <summary>System prompt generated from the layout — never hand-written per form (spec §13).</summary>
    public static string Build(LayoutDef layout)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You extract clinical form field values from text or images.");
        sb.AppendLine("Rules: output ONLY fields explicitly stated in the input. Never infer or guess.");
        sb.AppendLine("Handle negations: 'no smoking history' means smoker=\"no\". 'No known drug allergies' means allergies=[\"nkda\"].");
        sb.AppendLine("Omit any field you are not certain about. Output JSON matching the schema, with a confidence 0-1 per extracted field.");
        sb.AppendLine();
        sb.AppendLine("Fields:");
        foreach (var f in layout.Fields.Where(f => f.ComputedFrom == null))
        {
            sb.Append($"- {f.Name} ({f.DataType}");
            if (f.Unit != null) sb.Append($", unit {f.Unit}");
            sb.Append($"): {f.Label}");
            if (f.Aliases is { Length: > 0 }) sb.Append($". Also called: {string.Join(", ", f.Aliases)}");
            var opts = f.Options?.Inline;
            if (opts != null) sb.Append($". Allowed values: {string.Join(", ", opts.Select(o => $"\"{o.Value}\" ({o.Label})"))}");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Dates are YYYY-MM-DD. Times are 24h HH:mm. Blood pressure is {\"sys\": n, \"dia\": n}.");
        return sb.ToString();
    }

    /// <summary>Wrap the layout schema so every field becomes {value, confidence}.</summary>
    public static JsonObject WithConfidence(JsonObject layoutSchema)
    {
        var properties = new JsonObject();
        foreach (var (name, prop) in (JsonObject)layoutSchema["properties"]!.DeepClone())
        {
            if (prop is JsonObject po && po["readOnly"]?.GetValue<bool>() == true) continue;
            properties[name] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["value"] = prop!.DeepClone(),
                    ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                },
                ["required"] = new JsonArray("value", "confidence"),
            };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>Parse {field: {value, confidence}} model output. LLM confidences are clamped to 0.9 max.</summary>
    public static ProviderResult Parse(string json)
    {
        var result = new ProviderResult();
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return result; }
        if (root.ValueKind != JsonValueKind.Object) return result;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("value", out var v))
            {
                if (v.ValueKind is JsonValueKind.Null) continue;
                result.Values[prop.Name] = v.Clone();
                var conf = prop.Value.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetDouble() : 0.7;
                result.Confidences[prop.Name] = Math.Min(conf, 0.9);
            }
            else if (prop.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                result.Values[prop.Name] = prop.Value.Clone();
                result.Confidences[prop.Name] = 0.7;
            }
        }
        return result;
    }
}

public sealed class OllamaProvider(IHttpClientFactory http, IConfiguration config) : IExtractionProvider
{
    public bool SupportsVision => false;

    public async Task<ProviderResult> ExtractAsync(LayoutDef layout, JsonObject jsonSchema, ExtractionInput input, CancellationToken ct)
    {
        var baseUrl = config["Extraction:Ollama:BaseUrl"] ?? "http://localhost:11434";
        var model = config["Extraction:Ollama:Model"] ?? "qwen2.5:7b-instruct";

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = ExtractionPrompt.WithConfidence(jsonSchema),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = ExtractionPrompt.Build(layout) },
                new JsonObject { ["role"] = "user", ["content"] = input.Text ?? "" }),
            ["options"] = new JsonObject { ["temperature"] = 0 },
        };

        var client = http.CreateClient("ollama");
        var response = await client.PostAsync($"{baseUrl}/api/chat",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        var content = doc.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return ExtractionPrompt.Parse(content);
    }
}

public sealed class OpenAiProvider(IHttpClientFactory http, IConfiguration config) : IExtractionProvider
{
    public bool SupportsVision => true;

    public async Task<ProviderResult> ExtractAsync(LayoutDef layout, JsonObject jsonSchema, ExtractionInput input, CancellationToken ct)
    {
        var apiKey = config["Extraction:OpenAi:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Extraction:OpenAi:ApiKey is not configured.");

        var isImage = input.InputType == "image";
        var model = isImage
            ? config["Extraction:OpenAi:VisionModel"] ?? "gpt-4o"
            : config["Extraction:OpenAi:TextModel"] ?? "gpt-4o-mini";

        JsonNode userContent = isImage
            ? new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = input.Text ?? "Extract all form fields visible in this image." },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:image/jpeg;base64,{input.ImageBase64}" } })
            : JsonValue.Create(input.Text ?? "");

        var schema = ExtractionPrompt.WithConfidence(jsonSchema);
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "extraction", ["strict"] = false, ["schema"] = schema },
            },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = ExtractionPrompt.Build(layout) },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };

        var client = http.CreateClient("openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        var content = doc.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return ExtractionPrompt.Parse(content);
    }
}
