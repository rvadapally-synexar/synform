using SynForm.Api.Data;
using SynForm.Api.Endpoints;
using SynForm.Api.Extraction;

var builder = WebApplication.CreateBuilder(args);

// Secrets (API keys) live in user-secrets or appsettings.local.json (gitignored),
// never in the tracked appsettings.json. Loaded unconditionally — this POC runs
// without an ASPNETCORE_ENVIRONMENT set.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<LayoutRepo>();
builder.Services.AddSingleton<LookupRepo>();
builder.Services.AddSingleton<RecordRepo>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<OllamaProvider>();
builder.Services.AddSingleton<OpenAiProvider>();
builder.Services.AddSingleton<AnthropicProvider>();
builder.Services.AddSingleton<VibeVoiceSttService>();
builder.Services.AddSingleton<WhisperSttService>();
builder.Services.AddSingleton<DeepgramSttService>();
builder.Services.AddSingleton<CompareRunRepo>();
builder.Services.AddHttpClient();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// Dev POC: any origin (the app is served over the LAN for iPad testing; no credentials in use).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();   // wwwroot: the STT compare observability page (/stt-compare.html)

// Apply db/*.sql migrations (schema + seed) at startup.
var sqlDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "db"));
app.Services.GetRequiredService<Db>().Migrate(sqlDir);

app.MapLayoutEndpoints();
app.MapLookupEndpoints();
app.MapRecordEndpoints();
app.MapExtractEndpoints();
app.MapCompareEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run("http://0.0.0.0:5266");   // bind all interfaces so LAN devices (iPad) can reach the API
