using System.Text.Json;
using SynForm.Api.Data;
using SynForm.Api.Domain;

namespace SynForm.Api.Extraction;

public sealed class ExtractResponse
{
    public Dictionary<string, JsonElement> Values { get; init; } = [];
    public Dictionary<string, double> Confidences { get; init; } = [];
    public List<string> ClearedFields { get; init; } = [];
    public List<SkippedField> Skipped { get; init; } = [];
    public string? UnmatchedText { get; init; }
    public string Layer { get; init; } = "";   // "layer1" | "layer1+layer2" | "layer2" — verifiable in logs/demos
}

public sealed record SkippedField(string Field, string Reason);

/// <summary>Orchestrates the extraction pipeline: Layer 1 → routing rule → Layer 2 → normalizer (spec §7).</summary>
public sealed class ExtractionService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<ExtractionService> logger)
{
    // Layer 1 resolvers are pure functions of (layout version, lookups); cache per published version.
    private readonly Dictionary<string, Layer1Resolver> _resolverCache = [];
    private readonly object _cacheLock = new();

    public async Task<ExtractResponse> ExtractAsync(
        string layoutKey, int layoutVersion, LayoutDef layout,
        IReadOnlyDictionary<string, List<LookupRow>> lookups,
        ExtractionInput input, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>();
        var confidences = new Dictionary<string, double>();
        var cleared = new List<string>();
        var skipped = new List<SkippedField>();
        string layer;
        string? unmatched = null;

        if (input.InputType == "image")
        {
            // Photo always routes to the vision-capable provider. (A local VLM would slot in here.)
            var provider = ResolveProvider(forceVision: true);
            var llm = await provider.ExtractAsync(layout, Schema(layoutKey, layoutVersion, layout, lookups), input, ct);
            foreach (var (k, v) in llm.Values) { values[k] = v; confidences[k] = llm.Confidences.GetValueOrDefault(k, 0.7); }
            layer = "layer2";
        }
        else
        {
            var resolver = GetResolver(layoutKey, layoutVersion, layout, lookups);
            var l1 = resolver.Resolve(input.Text ?? "");
            foreach (var (k, v) in l1.Values) { values[k] = v; confidences[k] = l1.Confidences.GetValueOrDefault(k, 0.95); }
            cleared.AddRange(l1.ClearedFields);

            // Routing rule: Layer 1 resolved ≥1 field AND consumed ≥70% of input → skip the LLM.
            var skipLlm = l1.Values.Count >= 1 && l1.ConsumedRatio >= 0.7;
            logger.LogInformation("Extract {Key} v{Version}: layer1 resolved {Count} fields, consumed {Ratio:P0} — {Decision}",
                layoutKey, layoutVersion, l1.Values.Count, l1.ConsumedRatio, skipLlm ? "skipping LLM" : "routing to LLM");

            if (skipLlm)
            {
                layer = "layer1";
                unmatched = l1.UnmatchedText is "" ? null : l1.UnmatchedText;
            }
            else
            {
                var provider = ResolveProvider(forceVision: false);
                var llm = await provider.ExtractAsync(layout, Schema(layoutKey, layoutVersion, layout, lookups), input, ct);
                foreach (var (k, v) in llm.Values)
                    if (!values.ContainsKey(k)) // Layer 1 matches (0.95) win over LLM guesses
                    { values[k] = v; confidences[k] = llm.Confidences.GetValueOrDefault(k, 0.7); }
                layer = l1.Values.Count > 0 ? "layer1+layer2" : "layer2";
            }
        }

        // Normalizer — deterministic, applies to both layers' output.
        var normalized = Normalizer.Normalize(layout, lookups, values, skipped);

        return new ExtractResponse
        {
            Values = normalized,
            Confidences = confidences.Where(kv => normalized.ContainsKey(kv.Key) || cleared.Contains(kv.Key))
                                     .ToDictionary(kv => kv.Key, kv => kv.Value),
            ClearedFields = cleared,
            Skipped = skipped,
            UnmatchedText = unmatched,
            Layer = layer,
        };
    }

    private Layer1Resolver GetResolver(string key, int version, LayoutDef layout, IReadOnlyDictionary<string, List<LookupRow>> lookups)
    {
        var cacheKey = $"{key}:{version}";
        lock (_cacheLock)
        {
            if (!_resolverCache.TryGetValue(cacheKey, out var resolver))
                _resolverCache[cacheKey] = resolver = new Layer1Resolver(layout, lookups);
            return resolver;
        }
    }

    private IExtractionProvider ResolveProvider(bool forceVision)
    {
        var name = config["Extraction:Provider"] ?? "ollama";
        IExtractionProvider provider = name switch
        {
            "anthropic" => services.GetRequiredService<AnthropicProvider>(),
            "openai" => services.GetRequiredService<OpenAiProvider>(),
            _ => services.GetRequiredService<OllamaProvider>(),
        };
        if (!forceVision || provider.SupportsVision) return provider;
        // Configured provider can't do images (Ollama): fall back to a vision-capable one.
        return !string.IsNullOrEmpty(config["Extraction:Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            ? services.GetRequiredService<AnthropicProvider>()
            : services.GetRequiredService<OpenAiProvider>();
    }

    private static System.Text.Json.Nodes.JsonObject Schema(
        string key, int version, LayoutDef layout, IReadOnlyDictionary<string, List<LookupRow>> lookups)
        => SchemaGenerator.Generate(key, version, layout, lookups);
}
