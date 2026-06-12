using System.Net.Http.Headers;
using System.Text.Json;

namespace SynForm.Api.Extraction;

public sealed record DeepgramBatchResult(string Text, string? RequestId, double? AudioSeconds, string Model);

/// <summary>
/// Batch (prerecorded) speech-to-text via Deepgram /v1/listen — used by the A/B compare
/// harness so the identical audio bytes can be scored against Azure Whisper. The live
/// voice panel uses Deepgram's streaming WebSocket instead (temp-token flow).
/// HIPAA: never log audio bytes or transcript text — sizes and metadata only.
/// </summary>
public sealed class DeepgramSttService(
    IHttpClientFactory http,
    IConfiguration config,
    ILogger<DeepgramSttService> logger)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Deepgram:ApiKey"]);

    public string Model => config["Deepgram:Model"] ?? "nova-3-medical";

    public async Task<DeepgramBatchResult> TranscribeAsync(byte[] audio, string format, string[] hotwords, CancellationToken ct)
    {
        var apiKey = config["Deepgram:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Deepgram is not configured. Set Deepgram:ApiKey.");

        // keyterm = nova-3 vocabulary biasing, the counterpart of Whisper's prompt priming.
        var url = $"https://api.deepgram.com/v1/listen?model={Model}&smart_format=true&language=en"
            + string.Concat(hotwords.Take(60).Select(h => "&keyterm=" + Uri.EscapeDataString(h)));

        logger.LogInformation("Deepgram batch transcription — size: {Size} bytes, format: {Format}, model: {Model}",
            audio.Length, format, Model);

        var client = http.CreateClient("deepgram");
        client.Timeout = TimeSpan.FromMinutes(5);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(audio),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue($"audio/{format}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");

        var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Deepgram API {(int)response.StatusCode}"); // never include payload: transcript = PHI

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var text = root.TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels) && channels.GetArrayLength() > 0
            && channels[0].TryGetProperty("alternatives", out var alts) && alts.GetArrayLength() > 0
            && alts[0].TryGetProperty("transcript", out var t)
            ? t.GetString() ?? "" : "";

        string? requestId = null;
        double? duration = null;
        if (root.TryGetProperty("metadata", out var meta))
        {
            if (meta.TryGetProperty("request_id", out var rid)) requestId = rid.GetString();
            if (meta.TryGetProperty("duration", out var dur)) duration = dur.GetDouble();
        }

        logger.LogInformation("Deepgram transcription succeeded — {Chars} chars, {Seconds}s audio", text.Length, duration);
        return new DeepgramBatchResult(text.Trim(), requestId, duration, Model);
    }
}
