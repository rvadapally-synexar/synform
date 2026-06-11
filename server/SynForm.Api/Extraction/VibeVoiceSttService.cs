using System.Net.Http.Headers;
using System.Text.Json;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

/// <summary>
/// Speech-to-text via Microsoft VibeVoice-ASR on Azure AI Foundry — the same engine
/// (and config keys) as the main Synexar medical app, so it runs in our own Azure
/// tenancy under the Microsoft BAA. Batch model: full audio in, transcript out.
/// HIPAA: never log audio bytes or transcript text — sizes and metadata only.
/// </summary>
public sealed class VibeVoiceSttService(
    IHttpClientFactory http,
    IConfiguration config,
    ILogger<VibeVoiceSttService> logger)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config["Transcription:VibeVoice:Endpoint"]) &&
        !string.IsNullOrWhiteSpace(config["Transcription:VibeVoice:ApiKey"]);

    public async Task<string> TranscribeAsync(byte[] audio, string format, string[] hotwords, CancellationToken ct)
    {
        var endpoint = config["Transcription:VibeVoice:Endpoint"];
        var apiKey = config["Transcription:VibeVoice:ApiKey"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "VibeVoice is not configured. Set Transcription:VibeVoice:Endpoint and ApiKey in user-secrets.");

        logger.LogInformation("VibeVoice transcription — size: {Size} bytes, format: {Format}, hotwords: {Count}",
            audio.Length, format, hotwords.Length);

        var client = http.CreateClient("vibevoice");
        client.Timeout = TimeSpan.FromMinutes(5);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue($"audio/{format}");
        content.Add(audioContent, "audio", $"audio.{format}");
        content.Add(new StringContent("en"), "language");
        content.Add(new StringContent("false"), "enable_diarization"); // single-speaker dictation
        if (hotwords.Length > 0)
            content.Add(new StringContent(string.Join(",", hotwords)), "hotwords");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"VibeVoice API {(int)response.StatusCode}"); // never include payload: transcript = PHI

        using var doc = JsonDocument.Parse(payload);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        logger.LogInformation("VibeVoice transcription succeeded — {Chars} chars", text.Length);
        return text.Trim();
    }

    /// <summary>Hotword vocabulary compiled from the layout: labels, aliases, option labels.
    /// Same trick as the GI hotwords file in the main app — biases ASR toward clinical field terms.</summary>
    public static string[] HotwordsFor(LayoutDef layout) =>
        layout.Fields
            .SelectMany(f => (f.Aliases ?? []).Append(f.Label)
                .Concat(f.Options?.Inline?.Select(o => o.Label) ?? []))
            .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
}
