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

        // Whisper deployments have low RPM quotas (capacity 1 ≈ 3 req/min) — back-to-back
        // dictations 429 routinely. Retry transient failures, honoring Retry-After.
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // Multipart content can't be reused across attempts — build fresh each try.
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = BuildContent(audio, format, hotwords),
            };
            request.Headers.Add("api-key", apiKey);

            var response = await client.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                logger.LogInformation("Whisper transcription succeeded on attempt {Attempt} — {Chars} chars", attempt, text.Length);
                return text.Trim();
            }

            var status = (int)response.StatusCode;
            var transient = status is 429 or 500 or 502 or 503;
            if (!transient || attempt >= maxAttempts)
                throw new HttpRequestException(status == 429
                    ? "Whisper is rate-limited (Azure deployment quota). Wait a few seconds and tap the mic again."
                    : $"Whisper API {status}"); // never include payload: transcript = PHI

            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
            if (delay > TimeSpan.FromSeconds(20)) delay = TimeSpan.FromSeconds(20);
            logger.LogWarning("Whisper {Status} on attempt {Attempt}/{Max} — retrying in {Delay}s",
                status, attempt, maxAttempts, delay.TotalSeconds);
            await Task.Delay(delay, ct);
        }
    }

    private static MultipartFormDataContent BuildContent(byte[] audio, string format, string[] hotwords)
    {
        var content = new MultipartFormDataContent();
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
        return content;
    }
}
