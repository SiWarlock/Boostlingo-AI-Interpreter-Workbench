using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.OpenAI;

/// <summary>
/// The pure, deterministic surface of <see cref="OpenAiTranslationProvider"/> (C.2) — the Responses API
/// request-body builder, the SSE-event parse (one <c>data:</c> JSON payload -> a normalized
/// <see cref="SseEvent"/>), and the exception -> <see cref="TranslationFailed"/> projection. The transport
/// shell drives the network + the streaming aggregation; these helpers hold the logic (lesson §18, extended
/// for a raw-HttpClient/SSE provider). <c>internal</c> + InternalsVisibleTo keeps them unit-reachable.
/// </summary>
internal static class OpenAiTranslationMapping
{
    private const string Provider = "openai";
    private const string Stage = "translation";

    /// <summary>The SSE event kinds the provider acts on; everything else (in_progress, item/part lifecycle) is ignored.</summary>
    internal enum SseKind
    {
        Other,
        Created,
        Delta,
        Completed,
        ApiError,
    }

    internal readonly record struct SseEvent(SseKind Kind, string? Delta, int? InputTokens, int? OutputTokens);

    /// <summary>
    /// Builds the Responses API request body. Streaming + latency-protecting params (ARCH-011): the Responses
    /// API NESTS <c>reasoning.effort</c> + <c>text.verbosity</c> (top-level <c>reasoning_effort</c> is
    /// Chat-Completions-only). The faithful-interpreter <c>instructions</c> direct output-only translation;
    /// <c>input</c> is the source text. Model from the request (per-turn selection), falling back to options.
    /// </summary>
    public static object BuildRequestBody(TranslationRequest request, OpenAiTranslationOptions options) => new
    {
        model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model,
        stream = true,
        instructions = BuildInstruction(request.SourceLanguage, request.TargetLanguage),
        input = request.Text,
        reasoning = new { effort = options.ReasoningEffort },
        text = new { verbosity = options.Verbosity },
    };

    /// <summary>
    /// Parses one SSE <c>data:</c> JSON payload into a normalized <see cref="SseEvent"/>. Defensive:
    /// malformed/non-JSON (keep-alive, a stray line) -> <see cref="SseKind.Other"/> (ignored); never throws.
    /// </summary>
    public static SseEvent ParseEvent(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return new SseEvent(SseKind.Other, null, null, null);
            }

            switch (typeEl.GetString())
            {
                case "response.created":
                    return new SseEvent(SseKind.Created, null, null, null);

                case "response.output_text.delta":
                    var delta = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;
                    return new SseEvent(SseKind.Delta, delta, null, null);

                case "response.completed":
                    var (input, output) = ReadUsage(root);
                    return new SseEvent(SseKind.Completed, null, input, output);

                case "error":
                    return new SseEvent(SseKind.ApiError, null, null, null);

                default:
                    return new SseEvent(SseKind.Other, null, null, null);
            }
        }
        catch (JsonException)
        {
            return new SseEvent(SseKind.Other, null, null, null);
        }
    }

    /// <summary>
    /// Projects an HTTP/exception failure to <see cref="TranslationFailed"/> via the single ARCH-012 mapper
    /// owner with the provider/stage constants — SafeMessage never echoes the exception (ARCH-018/019). Raw
    /// HttpClient surfaces status-bearing <see cref="HttpRequestException"/> (no C.1 Deepgram Q5 gap), so
    /// 429/401/403 map correctly; an in-band SSE <c>error</c> event maps to <c>translation.unknown</c>.
    /// </summary>
    public static TranslationFailed ToFailed(Exception exception, DateTimeOffset timestamp) =>
        new(ProviderErrorMapper.Map(exception, Provider, Stage), timestamp);

    // Usage lives at response.completed -> data.response.usage.{input_tokens,output_tokens} (snake_case;
    // NOT prompt/completion_tokens). Absent -> null (B.5 cost estimator degrades; never fabricate).
    private static (int? Input, int? Output) ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            int? input = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var i) ? i : null;
            int? output = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var o) ? o : null;
            return (input, output);
        }

        return (null, null);
    }

    private static string BuildInstruction(LanguageCode source, LanguageCode target) =>
        $"You are a faithful interpreter. Translate the user's message from {Name(source)} to {Name(target)}. " +
        "Output ONLY the translation — no preamble, explanation, or quotation marks.";

    private static string Name(LanguageCode code) => code switch
    {
        LanguageCode.En => "English",
        LanguageCode.Es => "Spanish",
        _ => code.ToString(),
    };
}
