using SynForm.Api.Data;
using SynForm.Api.Endpoints;
using SynForm.Api.Extraction;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<LayoutRepo>();
builder.Services.AddSingleton<LookupRepo>();
builder.Services.AddSingleton<RecordRepo>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<OllamaProvider>();
builder.Services.AddSingleton<OpenAiProvider>();
builder.Services.AddHttpClient();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:4200", "capacitor://localhost", "http://localhost")
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Apply db/*.sql migrations (schema + seed) at startup.
var sqlDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "db"));
app.Services.GetRequiredService<Db>().Migrate(sqlDir);

app.MapLayoutEndpoints();
app.MapLookupEndpoints();
app.MapRecordEndpoints();
app.MapExtractEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run("http://localhost:5266");
