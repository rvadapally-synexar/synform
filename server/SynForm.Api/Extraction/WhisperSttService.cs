using System.Net.Http.Headers;
using System.Text.Json;

namespace SynForm.Api.Extraction;

/// <summary>
/// Speech-to-text via Azure OpenAI Whisper — same deployment and credentials as the main
/// Synexar app (Key Vault AzureOpenAI--Whisper--*). Runs in our Azure tenancy under the
/// Microsoft BAA ("Azure OpenAI path: IN PLACE" per the 2026-06-01 compliance memo).
/// HIPAA: never log audio bytes or transcript text — sizes and metadata only.
/// </summary>
public sealed class WhisperSttService(
    IHttpClientFactory http,
    IConfiguration config,
    ILogger<WhisperSttService> logger)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config["Transcription:Whisper:Endpoint"]) &&
        !string.IsNullOrWhiteSpace(config["Transcription:Whisper:ApiKey"]);

    public async Task<string> TranscribeAsync(byte[] audio, string format, string[] hotwords, CancellationToken ct)
    {
        var endpoint = config["Transcription:Whisper:Endpoint"]?.TrimEnd('/');
        var apiKey = config["Transcription:Whisper:ApiKey"];
        var deployment = config["Transcription:Whisper:DeploymentName"] ?? "whisper";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Whisper is not configured. Set Transcription:Whisper:Endpoint and ApiKey in user-secrets.");

        // Endpoint secret may be the resource base (https://x.openai.azure.com) or a full path.
        var url = endpoint.Contains("/openai/", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-06-01";

        logger.LogInformation("Whisper transcription — size: {Size} bytes, format: {Format}, deployment: {Deployment}",
            audio.Length, format, deployment);

        var client = http.CreateClient("whisper");
        client.Timeout = TimeSpan.FromMinutes(5);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue($"audio/{format}");
        content.Add(audioContent, "file", $"audio.{format}");
        content.Add(new StringContent("json"), "response_format");
        content.Add(new StringContent("en"), "language");
        // Vocabulary biasing: Whisper's prompt primes recognition toward the form's clinical
        // terms ("Mallampati" instead of "Malayam Party"). Keep under the ~224-token limit.
        if (hotwords.Length > 0)
            content.Add(new StringContent("Clinical pre-anesthesia dictation. Terms: " +
                string.Join(", ", hotwords.Take(60))), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", apiKey);

        var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Whisper API {(int)response.StatusCode}"); // never include payload: transcript = PHI

        using var doc = JsonDocument.Parse(payload);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        logger.LogInformation("Whisper transcription succeeded — {Chars} chars", text.Length);
        return text.Trim();
    }
}
