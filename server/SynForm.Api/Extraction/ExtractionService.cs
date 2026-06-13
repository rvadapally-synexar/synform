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

            // Routing rule: Layer 1 resolved ≥1 field AND consumed ≥70% of input → the LLM
            // doesn't need the full input. But a non-trivial unresolved remainder still goes
            // to Layer 2 on its own ("he is here for a colonoscopy at 10.15" carries fields
            // Layer 1 can't see) — the layered design is cheapest-first, not cheapest-only.
            var fullCoverage = l1.Values.Count >= 1 && l1.ConsumedRatio >= 0.7;
            var remainderWords = l1.UnmatchedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var skipLlm = fullCoverage && remainderWords < 4;
            logger.LogInformation("Extract {Key} v{Version}: layer1 resolved {Count} fields, consumed {Ratio:P0}, remainder {Words} words — {Decision}",
                layoutKey, layoutVersion, l1.Values.Count, l1.ConsumedRatio, remainderWords,
                skipLlm ? "skipping LLM" : fullCoverage ? "LLM on remainder only" : "LLM on full input");

            if (skipLlm)
            {
                layer = "layer1";
                unmatched = l1.UnmatchedText is "" ? null : l1.UnmatchedText;
            }
            else
            {
                var llmInput = fullCoverage ? input with { Text = l1.UnmatchedText } : input;
                var provider = ResolveProvider(forceVision: false);
                var llm = await provider.ExtractAsync(layout, Schema(layoutKey, layoutVersion, layout, lookups), llmInput, ct);
                foreach (var (k, v) in llm.Values)
                {
                    if (!values.TryGetValue(k, out var existing))
                    { values[k] = v; confidences[k] = llm.Confidences.GetValueOrDefault(k, 0.7); }
                    // Scalars: Layer 1 (0.95) wins. Arrays: union — Layer 1 often catches only the
                    // first item of an "X and Y" list, while the LLM sees the full phrase.
                    else if (existing.ValueKind == JsonValueKind.Array && v.ValueKind == JsonValueKind.Array)
                    {
                        var merged = existing.EnumerateArray().Concat(v.EnumerateArray())
                            .Select(e => e.Clone()).DistinctBy(e => e.GetRawText()).ToList();
                        values[k] = JsonSerializer.SerializeToElement(merged);
                    }
                }
                layer = l1.Values.Count > 0 ? "layer1+layer2" : "layer2";
            }
        }

        // Search-resolve: map spoken values for high-cardinality reference fields to the
        // registry id/canonical (physician name → NPI). Ambiguous matches get low confidence
        // so the UI flags them for human confirmation. Runs before normalization.
        ResolveSearchFields(layout, values, confidences);

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

    /// <summary>
    /// Resolve spoken values for search-backed reference fields against their index.
    /// search (single) → top hit's id, confidence drops when ambiguous; searchMulti → each
    /// item canonicalized (kept as-is if no hit). tags are free text and skipped here.
    /// </summary>
    private void ResolveSearchFields(
        LayoutDef layout, Dictionary<string, System.Text.Json.JsonElement> values, Dictionary<string, double> confidences)
    {
        var search = services.GetRequiredService<SearchRepo>();
        foreach (var f in layout.Fields)
        {
            if (f.Options?.SearchKey is not { } key) continue;
            if (f.ControlType is not ("search" or "searchMulti")) continue; // tags stay free text
            if (!values.TryGetValue(f.Name, out var v)) continue;

            if (f.ControlType == "search" && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var term = v.GetString()!.Trim();
                if (term.Length == 0) continue;
                if (search.Resolve(key, term) is not null) continue; // already a valid id
                var hits = search.Search(key, term, 2);
                if (hits.Count == 0)
                {
                    values.Remove(f.Name);
                    confidences.Remove(f.Name);
                    logger.LogInformation("Search-resolve {Field}: '{Term}' matched nothing in {Key} — dropped", f.Name, term, key);
                    continue;
                }
                values[f.Name] = System.Text.Json.JsonSerializer.SerializeToElement(hits[0].Value);
                confidences[f.Name] = hits.Count == 1 ? 0.8 : 0.6; // ambiguous → forces confirm
                logger.LogInformation("Search-resolve {Field}: '{Term}' → {Value} ({N} candidates)", f.Name, term, hits[0].Value, hits.Count);
            }
            else if (f.ControlType == "searchMulti" && v.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var resolved = v.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .Select(term => search.Search(key, term, 1) is { Count: > 0 } h ? h[0].Value : term)
                    .Distinct()
                    .ToList();
                if (resolved.Count > 0)
                {
                    values[f.Name] = System.Text.Json.JsonSerializer.SerializeToElement(resolved);
                    confidences[f.Name] = 0.85;
                }
                else { values.Remove(f.Name); confidences.Remove(f.Name); }
            }
        }
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
