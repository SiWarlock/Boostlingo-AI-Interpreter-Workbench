using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Config;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Http.Features;

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

// Cost estimator (B.5) — first consumer of the pricing singleton; branches on pricing basis and
// degrades to "estimate unavailable" on missing config. Entry-point consumers are C.4 (WS cost
// message) + B.7 (summary aggregation) — available-in-DI now, not a silent gap.
builder.Services.AddSingleton<CostEstimator>();

// Evaluation (B.6) — WER calculator + scripted-phrase store (degrade-don't-crash load). Consumer is
// F.1 (evaluation endpoints); available-in-DI now. EVALUATION_PHRASES_PATH overrides the default
// content location (copied next to the host via the Api csproj).
builder.Services.AddSingleton<WerCalculator>();
var phrasesPath = builder.Configuration["EVALUATION_PHRASES_PATH"]
    ?? Path.Combine(AppContext.BaseDirectory, "Evaluation", "evaluation-phrases.json");
builder.Services.AddSingleton(new EvaluationPhraseStore(phrasesPath));
// F.1 — evaluation endpoints (EvaluationController): phrase listing + WER compute (hypothesis-length
// cap before the DP allocation, ARCH-019) + the optional turn-attach/persist. Reuses the store +
// calculator above + the SessionStore/PersistenceWriter registered below.
builder.Services.AddSingleton<EvaluationService>();

// Metrics layer (B.3) — injectable clock + the latency factory/aggregator (ARCH-013). The factory
// is the first IClock consumer; the production consumer of the trio is the B.4 cascade orchestrator
// (available-in-DI now, entry-point wiring deferred — named, not a silent gap).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<LatencyEventFactory>();
builder.Services.AddSingleton<MetricsAggregator>();

// Sessions (B.7a) — in-memory store (server-side id source) + persistence writer (write JSON under
// SESSION_DATA_DIR; two-layer path-traversal guard; never persists secrets/raw audio — ARCH-016/019).
// SESSION_DATA_DIR is a directly-read flat path (like PRICING_CONFIG_PATH / EVALUATION_PHRASES_PATH),
// not a section-bound Option. Entry-point consumers are B.9 (SessionsController) + C.4 (WS turn
// persist) + B.7b (summary) — available-in-DI now, not a silent gap.
builder.Services.AddSingleton<SessionStore>();
var sessionDataDir = builder.Configuration["SESSION_DATA_DIR"] ?? "../../data/sessions";
builder.Services.AddSingleton(new SessionPersistenceWriter(sessionDataDir));

// Session summary (B.7b) — pure read-only aggregation of a session into SessionSummary (reuses the
// B.3 MetricsAggregator per turn). Stateless singleton; entry-point consumers are B.9 (GET …/summary
// + the /end snapshot) + F.3 (ComparisonSummary) — available-in-DI now, not a silent gap.
builder.Services.AddSingleton<SessionSummaryService>();

// Session lifecycle service (B.9c-i) — orchestrates create/get/end/summary over the store + summary +
// writer for SessionsController. Behind ISessionService (the controller test seam, lesson §15).
builder.Services.AddSingleton<ISessionService, SessionService>();

// Cascade real providers (C.4a) — DI-swapped real<->fake by KEY PRESENCE (mirrors the B.9b ConfigService
// rule: a stage is real iff its key is configured), with an explicit USE_FAKE_PROVIDERS override for a
// keyless local run. The orchestrator + provider interfaces are UNCHANGED (the B.1/B.4 seam proven). The
// OpenAI providers get a typed HttpClient (BaseAddress = the OpenAI API); Deepgram uses its SDK via IOptions.
var useFakeProviders = builder.Configuration.GetValue<bool>("USE_FAKE_PROVIDERS");
var deepgramConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{DeepgramOptions.SectionName}:ApiKey"]);
var translationConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{OpenAiTranslationOptions.SectionName}:ApiKey"]);
var ttsConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{OpenAiTtsOptions.SectionName}:ApiKey"]);

if (!useFakeProviders && deepgramConfigured)
{
    builder.Services.AddSingleton<ISttProvider, DeepgramSttProvider>();
}
else
{
    builder.Services.AddSingleton<ISttProvider>(_ => new FakeSttProvider());
}

if (!useFakeProviders && translationConfigured)
{
    builder.Services.AddHttpClient<ITranslationProvider, OpenAiTranslationProvider>(c => c.BaseAddress = new Uri("https://api.openai.com/"));
}
else
{
    builder.Services.AddSingleton<ITranslationProvider>(_ => new FakeTranslationProvider());
}

if (!useFakeProviders && ttsConfigured)
{
    builder.Services.AddHttpClient<ITtsProvider, OpenAiTtsProvider>(c => c.BaseAddress = new Uri("https://api.openai.com/"));
}
else
{
    builder.Services.AddSingleton<ITtsProvider>(_ => new FakeTtsProvider());
}

// Realtime ephemeral-credential broker (E.1, SAFETY invariants #1/#2) — registered UNCONDITIONALLY (it
// reads the standard key at call time + short-circuits to a sanitized failure when absent; key-presence DI
// would 404 the route instead of returning a clean error). Typed HttpClient → the OpenAI API; the standard
// key is Bearer-only and the minted ek_… is response-only (never persisted/logged). Consumer: E.3 browser.
builder.Services.AddHttpClient<RealtimeClientSecretService>(c => c.BaseAddress = new Uri("https://api.openai.com/"));

// The local frontend origin (ARCH-019) — the single source for both CORS (below) and the cascade WS
// Origin check (C.4b; the WS upgrade bypasses CORS, so the endpoint validates Origin itself).
var frontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";

// Bound the request body to the largest audio-upload cap + a small multipart-envelope headroom (C.5/F.1b,
// ARCH-019): an oversized upload is rejected at the framework boundary instead of buffered to the 128MB
// default. The PRECISE per-file cap + the *.invalid_audio 413 still come from CascadeUploadValidation in
// each controller; this just closes the buffering/DoS window. The backstop is the MAX of every audio
// route's cap (cascade /turn = CASCADE_MAX_UPLOAD_BYTES, evaluation /transcribe = EVAL_MAX_UPLOAD_BYTES
// which falls back to the cascade cap) so a higher per-route cap is never preempted by a framework 500
// before its controller can return the clean 413.
var cascadeMaxUploadBytes = builder.Configuration.GetValue<long?>("CASCADE_MAX_UPLOAD_BYTES") ?? CascadeUploadValidation.DefaultMaxBytes;
var evalMaxUploadBytes = builder.Configuration.GetValue<long?>("EVAL_MAX_UPLOAD_BYTES") ?? cascadeMaxUploadBytes;
var maxUploadBytes = Math.Max(cascadeMaxUploadBytes, evalMaxUploadBytes);
var maxRequestBodyBytes = maxUploadBytes + (1 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxRequestBodyBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBodyBytes);

// The cascade orchestrator (B.4, UNCHANGED) + the C.4a WS endpoint shell. Transient/scoped so a singleton
// never captures a transient typed-HttpClient provider (captive-dependency); each WS turn resolves fresh.
// The endpoint takes the allowed Origin (C.4b) for its pre-accept Origin validation.
builder.Services.AddTransient<CascadeStreamingOrchestrator>();
// The C.5 blob-fallback adapter (POST /api/cascade/turn via CascadeController) — reuses the streaming
// orchestrator for pre-recorded STT -> the same translation/TTS, collected into a turn. Transient so it
// never captures a transient typed-HttpClient provider (captive-dependency); each request resolves fresh.
builder.Services.AddTransient<CascadeOrchestrator>();
builder.Services.AddScoped(sp => new CascadeWebSocketEndpoint(
    sp.GetRequiredService<CascadeStreamingOrchestrator>(),
    sp.GetRequiredService<SessionStore>(),
    sp.GetRequiredService<SessionPersistenceWriter>(),
    sp.GetRequiredService<CostEstimator>(),
    sp.GetRequiredService<LatencyEventFactory>(),
    sp.GetRequiredService<IClock>(),
    frontendOrigin));

// Error sanitizer (B.8, safety invariant #4) — turns any Exception/ProviderError/Result.Error into a
// safe normalized UiError (no stack/secret/raw-payload to the client; original logged server-side).
// Injectable singleton (needs ILogger). Entry-point consumers are B.9 (global exception handler +
// Result→DTO) + C.4 (WS error frames) — available-in-DI now, not a silent gap.
builder.Services.AddSingleton<ErrorSanitizer>();

// Global exception handler (B.9a, safety invariant #4 boundary) — any unhandled exception is routed
// through the sanitizer and returned as a safe UiError, so a framework error page (stack trace in Dev)
// never reaches the client. AddProblemDetails is required for the parameterless app.UseExceptionHandler()
// to initialize without a startup exception; its writer is the fallback our handler never reaches (the
// handler always handles + emits UiError + returns true). Wired into the pipeline below.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Config endpoint (B.9b) — GET /api/config reports capability flags from key presence only (never
// values; safety invariant #1). First MVC controller: AddControllers + apply the shared JsonDefaults
// contract to MVC's (separate) JsonOptions so controller JSON matches the camelCase/enum/explicit-null
// contract the minimal-API + persistence paths use. Consumer is D.2 (config-gating).
builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddControllers().AddJsonOptions(o => JsonDefaults.Apply(o.JsonSerializerOptions));

// Shared JSON contract (A.3) on the HTTP pipeline — camelCase + enum-as-string + explicit null,
// the same contract persistence uses, so API and persisted JSON cannot diverge.
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));

// CORS — local frontend origin only (ARCH-019); never AllowAnyOrigin. (frontendOrigin resolved above,
// shared with the cascade WS Origin check.)
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

// Global sanitizing exception handler FIRST (B.9a) — wraps the whole pipeline so any unhandled
// exception (from minimal-API routes today, controllers in B.9b/c) is returned as a safe UiError,
// never a framework error page (ARCH-018/019, safety invariant #4).
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// WebSocket support. NOTE: a WS upgrade bypasses the CORS middleware, so the cascade handshake's Origin
// validation is C.4b's job (the endpoint validates it itself); C.4a wires the core protocol.
app.UseWebSockets();
app.UseCors(corsPolicyName);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Cascade streaming WS (C.4a, ARCH-009) — the real-provider cascade turn entry point (C.1/C.2/C.3 reachable here).
app.Map("/api/cascade/stream", (HttpContext ctx, CascadeWebSocketEndpoint endpoint) => endpoint.HandleAsync(ctx));

app.MapControllers();

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
