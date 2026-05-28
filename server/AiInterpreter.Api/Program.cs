using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Realtime;

var builder = WebApplication.CreateBuilder(args);

const string corsPolicyName = "frontend";

// Flat ARCH-028 operator env vars -> PascalCase Options sections (A.2). Only PRESENT keys are
// mapped, so absent env vars leave the A.3 inline defaults intact (inline = source of truth).
var sectionOverrides = BuildSectionOverrides(builder.Configuration);
if (sectionOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(sectionOverrides);
}

// Provider Options (A.2) — section-bound. Standard provider keys stay backend-only (ARCH-019).
builder.Services.Configure<DeepgramOptions>(builder.Configuration.GetSection(DeepgramOptions.SectionName));
builder.Services.Configure<OpenAiTranslationOptions>(builder.Configuration.GetSection(OpenAiTranslationOptions.SectionName));
builder.Services.Configure<OpenAiTtsOptions>(builder.Configuration.GetSection(OpenAiTtsOptions.SectionName));
builder.Services.Configure<RealtimeOptions>(builder.Configuration.GetSection(RealtimeOptions.SectionName));

// Pricing (A.4) — load once at startup; degrade-safe so a missing/invalid file never blocks
// startup (ARCH-018). B.5's CostEstimator consumes the Result.
var pricingPath = builder.Configuration["PRICING_CONFIG_PATH"] ?? "../../config/pricing.json";
builder.Services.AddSingleton(PricingLoader.Load(pricingPath));

// Metrics layer (B.3) — injectable clock + the latency factory/aggregator (ARCH-013). The factory
// is the first IClock consumer; the production consumer of the trio is the B.4 cascade orchestrator
// (available-in-DI now, entry-point wiring deferred — named, not a silent gap).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<LatencyEventFactory>();
builder.Services.AddSingleton<MetricsAggregator>();

// Shared JSON contract (A.3) on the HTTP pipeline — camelCase + enum-as-string + explicit null,
// the same contract persistence uses, so API and persisted JSON cannot diverge.
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));

// CORS — local frontend origin only (ARCH-019); never AllowAnyOrigin.
var frontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
builder.Services.AddCors(o => o.AddPolicy(
    corsPolicyName,
    p => p.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod()));

// OpenAPI/Swagger — Development only. Both the registration AND the middleware are gated, so a
// Production host exposes no schema even if one guard were later changed.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// WebSocket support — the cascade WS endpoint lands in C.4; A.5 only enables support. NOTE: a WS
// upgrade bypasses the CORS middleware, so C.4's handshake must validate the Origin itself.
app.UseWebSockets();
app.UseCors(corsPolicyName);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// ARCH-028 flat operator env vars -> "Section:Property" overrides for the A.2 Options sections.
// OPENAI_API_KEY feeds all three OpenAI-backed services (one account key). Non-env Option fields
// (SmartFormat, Channels, ReasoningEffort, ...) intentionally stay on their A.3 inline defaults.
static Dictionary<string, string?> BuildSectionOverrides(IConfiguration config)
{
    (string Flat, string[] Targets)[] map =
    [
        ("OPENAI_API_KEY", ["OpenAiTranslation:ApiKey", "OpenAiTts:ApiKey", "Realtime:ApiKey"]),
        ("OPENAI_REALTIME_MODEL", ["Realtime:Model"]),
        ("OPENAI_REALTIME_VOICE", ["Realtime:Voice"]),
        ("OPENAI_REALTIME_TRANSCRIPTION_MODEL", ["Realtime:TranscriptionModel"]),
        ("OPENAI_TRANSLATION_MODEL", ["OpenAiTranslation:Model"]),
        ("OPENAI_TTS_MODEL", ["OpenAiTts:Model"]),
        ("OPENAI_TTS_VOICE", ["OpenAiTts:Voice"]),
        ("OPENAI_TTS_FORMAT", ["OpenAiTts:ResponseFormat"]),
        ("DEEPGRAM_API_KEY", ["Deepgram:ApiKey"]),
        ("DEEPGRAM_STT_MODEL", ["Deepgram:Model"]),
        ("DEEPGRAM_STT_LANGUAGE", ["Deepgram:Language"]),
        ("DEEPGRAM_ENCODING", ["Deepgram:Encoding"]),
        ("DEEPGRAM_SAMPLE_RATE", ["Deepgram:SampleRate"]),
        ("STT_TIMEOUT_SECONDS", ["Deepgram:TimeoutSeconds"]),
        ("TRANSLATION_TIMEOUT_SECONDS", ["OpenAiTranslation:TimeoutSeconds"]),
        ("TTS_TIMEOUT_SECONDS", ["OpenAiTts:TimeoutSeconds"]),
        ("REALTIME_TOKEN_TIMEOUT_SECONDS", ["Realtime:TokenTimeoutSeconds"]),
    ];

    var overrides = new Dictionary<string, string?>();
    foreach (var (flat, targets) in map)
    {
        var value = config[flat];
        if (string.IsNullOrWhiteSpace(value))
        {
            continue; // whitespace-only env var is "unset" — leave the A.3 inline default intact.
        }

        foreach (var target in targets)
        {
            overrides[target] = value;
        }
    }

    return overrides;
}

// Exposed as public so WebApplicationFactory<Program> can host the app in integration tests
// (top-level-statement Program is otherwise internal).
public partial class Program { }
