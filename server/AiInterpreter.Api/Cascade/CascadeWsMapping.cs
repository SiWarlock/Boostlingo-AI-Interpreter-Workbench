using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// The pure, deterministic surface of <see cref="CascadeWebSocketEndpoint"/> (C.4a) — the ARCH-009 server-message
/// mapping, the cost emit/degrade decision, the turn-lifecycle stamp, and the turn assembly. The endpoint is a thin
/// transport shell (socket accept, frame loop, PCM-&gt;AudioFrame Channel bridge — manual-smoke); these helpers hold
/// the decision logic and are unit-TDD'd (lesson §18/§20 over a WS boundary). The SECURITY <c>start</c>-frame
/// validation lives separately in <see cref="CascadeStartValidation"/>. <c>internal</c> + InternalsVisibleTo keeps
/// these unit-reachable.
/// </summary>
internal static class CascadeWsMapping
{
    // Known audio MIME types we forward verbatim (normalized lowercase); anything else is clamped to the
    // default so a garbage/injected provider Content-Type can't cross the wire (C.4b, ARCH-009 hygiene).
    private const string DefaultAudioContentType = "audio/mpeg";
    private static readonly HashSet<string> AllowedAudioContentTypes = new(StringComparer.Ordinal)
    {
        "audio/mpeg", "audio/wav", "audio/pcm", "audio/ogg", "audio/opus", "audio/aac", "audio/flac",
    };

    /// <summary>Maps a <see cref="CascadeOutputEvent"/> to the exact ARCH-009 server message (camelCase via JsonDefaults).</summary>
    public static object ToServerMessage(CascadeOutputEvent ev, string turnId) => ev switch
    {
        Transcript t => (object)new { type = "transcript", segment = t.Segment },
        Latency l => new { type = "latency", @event = l.Event },
        // ContentType is provider-sourced — clamp it to the audio allowlist before it crosses the wire.
        Audio a => new { type = "audio", contentType = ClampContentType(a.ContentType), seq = a.Seq, base64 = Convert.ToBase64String(a.Bytes) },
        Error e => new { type = "error", error = e.ProviderError },
        // J.1 — the per-utterance resolved direction (bidir only); the FE stamps the live turn's direction off it.
        Direction dir => new { type = "direction", direction = dir.Resolved },
        Done d => new { type = "done", turnId, status = d.Status },
        _ => throw new ArgumentOutOfRangeException(nameof(ev), ev.GetType().Name, "Unknown cascade output event."),
    };

    /// <summary>
    /// Clamps a provider-sourced audio Content-Type to a known audio allowlist (case-insensitive, trimmed,
    /// parameters stripped by exact-match-or-fallback). An unknown/garbage/empty/null value falls back to
    /// <c>audio/mpeg</c> (C.4b — the cascade emits mp3 in practice; this is defense-in-depth at the boundary).
    /// </summary>
    public static string ClampContentType(string? contentType)
    {
        var normalized = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        return AllowedAudioContentTypes.Contains(normalized) ? normalized : DefaultAudioContentType;
    }

    /// <summary>
    /// Folds the accumulated cascade events into the cost-usage inputs: translation tokens summed ADDITIVELY
    /// across segments (one <c>translation.final</c> per segment — overwriting would undercount a multi-segment
    /// turn, the C.4a fix this pins) and target characters accumulated across target finals. Absent tokens stay
    /// null (cost degrades, never fabricated 0). Pure — the C.4b extraction of the C.4a inline accumulation.
    /// </summary>
    public static CascadeCostInputs FoldCostInputs(IReadOnlyList<CascadeOutputEvent> events)
    {
        int? inputTokens = null;
        int? outputTokens = null;
        long targetChars = 0;

        foreach (var ev in events)
        {
            switch (ev)
            {
                case Latency l when l.Event.Name == LatencyEventNames.TranslationFinal:
                    if (l.Event.Metadata.TryGetValue("inputTokens", out var it) && int.TryParse(it, out var iv))
                    {
                        inputTokens = (inputTokens ?? 0) + iv;
                    }

                    if (l.Event.Metadata.TryGetValue("outputTokens", out var ot) && int.TryParse(ot, out var ov))
                    {
                        outputTokens = (outputTokens ?? 0) + ov;
                    }

                    break;

                case Transcript t when t.Segment is { Role: "target", IsFinal: true }:
                    targetChars += t.Segment.Text.Length;
                    break;
            }
        }

        return new CascadeCostInputs(inputTokens, outputTokens, targetChars);
    }

    // 069 — the cascade TTS leg's output-audio duration is unknowable from /v1/audio/speech (it returns audio
    // bytes/SSE events, NO usage block — confirmed via the OpenAI API reference, unlike /transcriptions). For an
    // audio_output_tokens-basis model (gpt-4o-mini-tts) with no token count, estimate the OUTPUT-audio minutes
    // from the synthesized target text length at this speaking-rate constant → the estimator's
    // approxUsdPerAudioMinute fallback prices it. ESTIMATE — confirm at build (like
    // CostEstimator.RealtimeTokensPerAudioSecond); ~150 wpm × ~6 chars/word ⇒ ~900 chars/min. Sanity check on
    // the constant (NOT a billing-basis equivalence — gpt-4o-mini-tts bills per audio-output token, not per
    // char): 900 chars/min at $0.015/min ⇒ an effective ~$16.7/1M chars, a plausible speaking rate. The cascade
    // is thus ESTIMATED where realtime is exact-count (059) — disclosed.
    public const decimal TtsApproxCharsPerMinute = 900m;

    /// <summary>
    /// Builds the TTS cost-usage from the target char-proxy (069). Carries <c>Characters</c> (a characters-basis
    /// model — e.g. tts-1 — prices exactly) AND an output-audio-minutes estimate (<c>chars / TtsApproxCharsPerMinute</c>)
    /// so an <c>audio_output_tokens</c> model (gpt-4o-mini-tts) prices via the <c>approxUsdPerAudioMinute</c> fallback.
    /// Zero chars (no synthesis) → no inputs → the composite degrades honestly to null (never a synthetic $0; §9/§25).
    /// </summary>
    public static CostUsage BuildTtsCostUsage(long targetChars) => targetChars > 0
        ? new CostUsage { Characters = targetChars, AudioMinutes = targetChars / TtsApproxCharsPerMinute }
        : new CostUsage();

    /// <summary>
    /// Resolves the TTS voice actually used (069): the start frame's voice if present, else the session's
    /// configured voice (<c>ProviderProfile.TtsVoice</c>). The live frame shipped an empty voice → both the
    /// synthesis ran voice-less AND <c>ttsVoiceUsed</c> persisted as <c>""</c>; resolving here (the endpoint
    /// applies it to <see cref="CascadeStartParams"/> before the orchestrator) fixes BOTH. An explicit frame voice wins.
    /// </summary>
    public static string ResolveTtsVoice(string frameVoice, string configVoice) =>
        string.IsNullOrWhiteSpace(frameVoice) ? configVoice : frameVoice;

    /// <summary>The <c>cost</c> message on a successful estimate; <c>null</c> when unavailable (B.5 degrade — no message, no crash).</summary>
    public static object? ToCostMessageOrNull(Result<CostEstimate> cost) =>
        cost.IsSuccess ? new { type = "cost", estimate = cost.Value } : null;

    /// <summary>Stamps a turn-lifecycle event (<c>turn.recording.*</c>) as <see cref="LatencyStage.Overall"/>, server-clock.</summary>
    public static LatencyEvent RecordingEvent(LatencyEventFactory factory, string name, DateTimeOffset origin) =>
        factory.Stamp(name, LatencyStage.Overall, ClockSource.Server, origin);

    /// <summary>
    /// Assembles the final <see cref="InterpretationTurn"/> on <c>done</c> by folding the accumulated cascade events
    /// (transcripts/latency/errors) onto the base turn + the computed cost + completion time. Pure; the best-effort
    /// persist (B.7 <c>SessionPersistenceWriter</c>) is the shell's job.
    /// </summary>
    public static InterpretationTurn AssembleTurn(
        InterpretationTurn baseTurn,
        IReadOnlyList<CascadeOutputEvent> events,
        Result<CostEstimate> cost,
        DateTimeOffset completedAt)
    {
        var transcripts = new List<TranscriptSegment>(baseTurn.Transcripts);
        var latency = new List<LatencyEvent>(baseTurn.LatencyEvents);
        var errors = new List<ProviderError>(baseTurn.Errors);
        var status = baseTurn.Status;
        LanguageDirection? resolvedDirection = null; // J.1 — first resolved Direction event wins (bidir)

        foreach (var ev in events)
        {
            switch (ev)
            {
                case Transcript t: transcripts.Add(t.Segment); break;
                case Latency l: latency.Add(l.Event); break;
                case Error e: errors.Add(e.ProviderError); break;
                case Direction dir: resolvedDirection ??= dir.Resolved; break;
                case Done d: status = d.Status; break;
            }
        }

        return baseTurn with
        {
            Transcripts = transcripts,
            LatencyEvents = latency,
            Errors = errors,
            Status = status,
            // J.1 — the per-utterance RESOLVED direction (bidir) overrides the start-frame direction; absent
            // (one-direction, no Direction event) keeps the base turn's direction (byte-identical to today).
            Direction = resolvedDirection ?? baseTurn.Direction,
            CostEstimate = cost.IsSuccess ? cost.Value : null,
            CompletedAt = completedAt,
        };
    }
}

/// <summary>The cost-usage inputs folded from a turn's cascade events (C.4b). Area-local; not serialized.</summary>
internal readonly record struct CascadeCostInputs(int? InputTokens, int? OutputTokens, long TargetChars);
