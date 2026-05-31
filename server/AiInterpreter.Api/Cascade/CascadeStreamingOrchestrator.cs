using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// Drives the streaming cascade pipeline (ARCH-011): <c>STT (partials/finals) → per-finalized-segment
/// translation (streamed) → TTS (streamed)</c>. Emits a flat, transport-agnostic
/// <see cref="CascadeOutputEvent"/> stream — C.4 adapts it to the WS wire (ARCH-009).
///
/// <para><b>Streaming honesty (load-bearing):</b> each provider stream is consumed with
/// <c>await foreach</c>; source partials, target tokens, and TTS chunks are emitted as they arrive
/// (before <see cref="Done"/>). Each <c>first_*</c> latency event is stamped on the <i>real</i> first
/// arrival via <see cref="LatencyEventFactory.Stamp"/> — never synthesized or relabeled from a
/// completion (forbidden-pattern #3/#4; ARCH-011/013).</para>
///
/// <para><b>Nested per-segment loop:</b> on each finalized STT segment, that segment's
/// translation→TTS sub-pipeline runs to completion before the next STT event is consumed — so a
/// multi-segment turn streams per segment in arrival order without buffering the whole utterance
/// (the full-utterance blocking ARCH-011 forbids). Sequential per segment; concurrent interleaving
/// is a deferred refinement (ARCH-025).</para>
///
/// <para><b>Failure handling:</b> empty STT final short-circuits (no translation/TTS); a stage
/// failure or timeout keeps the upstream transcripts, skips downstream, and ends the turn
/// <see cref="TurnStatus.Failed"/> (ARCH-018). Each stage has its own linked-CTS timeout; the STT
/// stage's timeout is reset before each event (a per-event idle timeout) so downstream wall-clock
/// doesn't count against the next STT event (ARCH-012).</para>
/// </summary>
public sealed class CascadeStreamingOrchestrator(
    ISttProvider stt,
    ITranslationProvider translation,
    ITtsProvider tts,
    LatencyEventFactory factory,
    IClock clock,
    ILogger<CascadeStreamingOrchestrator> logger)
{
    private const string SttProviderLabel = "deepgram";
    private const string OpenAiProviderLabel = "openai";
    private const string SttLanguage = "multi";
    private const string RoleSource = "source";
    private const string RoleTarget = "target";
    private const string TtsResponseFormat = "mp3";
    private const string TtsDefaultContentType = "audio/mpeg";

    private static readonly TimeSpan DefaultSttTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTranslationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultTtsTimeout = TimeSpan.FromSeconds(30);

    private sealed class StageOutcome
    {
        public bool Failed { get; set; }

        public string? FinalText { get; set; }
    }

    public async IAsyncEnumerable<CascadeOutputEvent> RunAsync(
        CascadeStartParams p,
        IAsyncEnumerable<AudioFrame> audioFrames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The per-turn server origin — all server-stamped relativeMs are measured from here (ARCH-013).
        var origin = clock.UtcNow;
        var sttTimeout = p.SttTimeout ?? DefaultSttTimeout;

        yield return new Latency(factory.Create(
            LatencyEventNames.CascadeAudioReceived, LatencyStage.Capture, ClockSource.Server, origin, origin,
            new Dictionary<string, string> { ["provider"] = SttProviderLabel }));

        var sttRequest = new SttRequest(
            audioFrames, $"audio/{p.Encoding}", p.Encoding, p.SampleRate,
            p.Direction.Source, SttLanguage, p.SessionId, p.TurnId);

        using var sttCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var sttStream = stt.TranscribeAsync(sttRequest, sttCts.Token).GetAsyncEnumerator(sttCts.Token);

        var sttFirstPartialStamped = false;
        var segmentIndex = 0;
        // A partial was seen but not yet finalized. If the STT stream ENDS while this is set, a final was
        // lost — fail closed (Q4). A clean end with no pending partial (silence) stays Completed.
        var pendingPartial = false;
        // 052: empty-final tolerance. Deepgram emits spurious empty/whitespace finals around real speech
        // (leading silence / VAD boundary / a trailing empty AFTER the content). SKIP them and fail
        // cascade.empty_transcript only when ≥1 empty final arrived but NO non-empty final ever did — so a
        // spurious empty can't kill a correct turn, yet a genuinely-empty turn still fails closed. Pure
        // silence (no final at all) leaves sawEmptyFinal false → still Completes (Q4), unchanged.
        var sawNonEmptyFinal = false;
        var sawEmptyFinal = false;
        var autoEnd = false; // I.1 — set when auto-VAD (p.AutoVad) sees a Deepgram utterance-end terminal.

        // The terminal fail-closed decision (§22/§31), shared by the stream-end (`ended`) path AND the I.1
        // auto-VAD utterance-end path: a dangling partial (lost final) → cascade.unknown; ONLY empty finals
        // (≥1 empty, none with content) → cascade.empty_transcript; otherwise null (clean → Completed).
        ProviderError? TerminalFailure() =>
            pendingPartial ? ProviderErrorMapper.Unknown(SttProviderLabel, "stt")
            : sawEmptyFinal && !sawNonEmptyFinal ? ProviderErrorMapper.EmptyTranscript(SttProviderLabel)
            : null;

        while (true)
        {
            SttEvent? current = null;
            ProviderError? sttTimeoutError = null;
            var ended = false;

            // Arm the idle timer for THIS wait only. CancelAfter cannot un-cancel a CTS that has
            // already fired, so the timer must be disarmed (below) before the translation/TTS
            // sub-pipeline runs — otherwise its wall-clock would fire the STT timer and the next
            // segment's MoveNextAsync would see an already-cancelled token (a spurious timeout).
            sttCts.CancelAfter(sttTimeout);
            try
            {
                if (await sttStream.MoveNextAsync())
                {
                    current = sttStream.Current;
                }
                else
                {
                    ended = true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The idle timer fired (not caller cancellation) → a real STT-stage timeout.
                sttTimeoutError = ProviderErrorMapper.Timeout(SttProviderLabel, "stt");
            }
            finally
            {
                // Disarm: the timer must not run during downstream translation/TTS. Infinite cancels
                // the pending callback; a no-op if it already fired (we exit via the catch then).
                sttCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (sttTimeoutError is not null)
            {
                yield return new Error(sttTimeoutError);
                yield return new Done(TurnStatus.Failed);
                yield break;
            }

            if (ended)
            {
                // Stream ended: fail closed on a dangling partial (lost final) or ONLY-empty-finals (§22/§31,
                // 052); a clean end (no pending partial; ≥1 non-empty, or pure silence) → Completed below.
                if (TerminalFailure() is { } failure)
                {
                    yield return new Error(failure);
                    yield return new Done(TurnStatus.Failed);
                    yield break;
                }

                break;
            }

            switch (current)
            {
                case SttStarted:
                    yield return Stamp(LatencyEventNames.SttStarted, LatencyStage.Stt, origin, SttProviderLabel);
                    break;

                case SttPartial partial:
                    // 069: SKIP a spurious empty/whitespace partial. Deepgram emits them around real speech and
                    // at teardown (§30); a TRAILING empty partial (after a completed segment) would set
                    // pendingPartial → the stream-end §22 reads it as a lost final → stt.unknown → false-FAIL a
                    // successful turn (+ poison errorCount). Extends §31's empty-FINAL skip one level up: no
                    // pendingPartial, no first-partial stamp, no empty source segment. A NON-empty partial that
                    // never finalizes still fails closed (§22) — only empties are skipped.
                    if (string.IsNullOrWhiteSpace(partial.Text))
                    {
                        break;
                    }

                    pendingPartial = true;
                    if (!sttFirstPartialStamped)
                    {
                        sttFirstPartialStamped = true;
                        yield return Stamp(LatencyEventNames.SttFirstPartial, LatencyStage.Stt, origin, SttProviderLabel);
                    }

                    yield return Seg($"src-{segmentIndex}", RoleSource, partial.Text, false, SttProviderLabel, partial.Timestamp);
                    break;

                case SttFinal final:
                    pendingPartial = false; // this segment's partials are now finalized (even if the final is empty)

                    // 052: SKIP a spurious empty/whitespace final — do NOT stamp stt.final, emit a source
                    // segment, translate, or fail. (Skipping the stamp also keeps a late TRAILING empty final
                    // from inflating the stt.final latency.) Fail-closed-if-all-empty is at stream end above.
                    if (string.IsNullOrWhiteSpace(final.Text))
                    {
                        sawEmptyFinal = true;
                        break;
                    }

                    sawNonEmptyFinal = true;
                    yield return Stamp(LatencyEventNames.SttFinal, LatencyStage.Stt, origin, SttProviderLabel);
                    yield return Seg($"src-{segmentIndex}", RoleSource, final.Text, true, SttProviderLabel, final.Timestamp);

                    var translationOutcome = new StageOutcome();
                    await foreach (var ev in TranslateSegmentAsync(p, origin, final.Text, segmentIndex, translationOutcome, ct))
                    {
                        yield return ev;
                    }

                    if (translationOutcome.Failed)
                    {
                        yield return new Done(TurnStatus.Failed);
                        yield break;
                    }

                    // FinalText is guaranteed set on the non-failed path now (a terminal-less translation
                    // stream fails closed above); the pattern guard is belt-and-suspenders.
                    if (translationOutcome.FinalText is { } targetText)
                    {
                        var ttsOutcome = new StageOutcome();
                        await foreach (var ev in SynthesizeSegmentAsync(p, origin, targetText, ttsOutcome, ct))
                        {
                            yield return ev;
                        }

                        if (ttsOutcome.Failed)
                        {
                            yield return new Done(TurnStatus.Failed);
                            yield break;
                        }
                    }

                    segmentIndex++;
                    break;

                case SttFailed failed:
                    yield return new Error(failed.Error);
                    yield return new Done(TurnStatus.Failed);
                    yield break;

                case SttUtteranceEnd when p.AutoVad:
                    // I.1 (Phase I) — auto-VAD: Deepgram's utterance-end (detected silence) is the turn-terminal.
                    autoEnd = true;
                    break;

                case SttUtteranceEnd:
                    // auto-VAD OFF (manual) — ignore the endpointing marker; finalize on the client `stop` only.
                    break;
            }

            if (autoEnd)
            {
                // I.1 — the auto-VAD terminal: the SAME §22/§31 fail-closed decision as a stream-end, then
                // complete via the post-loop terminal (the WS endpoint routes the Done through FinalizeTurn).
                if (TerminalFailure() is { } failure)
                {
                    yield return new Error(failure);
                    yield return new Done(TurnStatus.Failed);
                    yield break;
                }

                break;
            }
        }

        yield return Stamp(LatencyEventNames.TurnCompleted, LatencyStage.Overall, origin, provider: null);
        yield return new Done(TurnStatus.Completed);
    }

    private async IAsyncEnumerable<CascadeOutputEvent> TranslateSegmentAsync(
        CascadeStartParams p,
        DateTimeOffset origin,
        string sourceText,
        int segmentIndex,
        StageOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new TranslationRequest(
            sourceText, p.Direction.Source, p.Direction.Target, p.TranslationModel, p.SessionId, p.TurnId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(p.TranslationTimeout ?? DefaultTranslationTimeout);
        await using var stream = translation.TranslateAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);

        var firstTokenStamped = false;
        var accumulated = new StringBuilder();

        while (true)
        {
            TranslationEvent? current = null;
            ProviderError? timeoutError = null;
            var ended = false;

            try
            {
                if (await stream.MoveNextAsync())
                {
                    current = stream.Current;
                }
                else
                {
                    ended = true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timeoutError = ProviderErrorMapper.Timeout(OpenAiProviderLabel, "translation");
            }

            if (timeoutError is not null)
            {
                outcome.Failed = true;
                yield return new Error(timeoutError);
                yield break;
            }

            if (ended)
            {
                // The stream ended without a TranslationFinal (the final-case yield-breaks, so reaching here
                // means no terminal arrived). Translation is required for a non-empty segment → fail closed
                // (C.4b, ARCH-011/018), rather than silently skipping TTS and completing the turn.
                outcome.Failed = true;
                yield return new Error(ProviderErrorMapper.Unknown(OpenAiProviderLabel, "translation"));
                yield break;
            }

            switch (current)
            {
                case TranslationStarted:
                    yield return Stamp(LatencyEventNames.TranslationStarted, LatencyStage.Translation, origin, OpenAiProviderLabel);
                    break;

                case TranslationPartial partial:
                    if (!firstTokenStamped)
                    {
                        firstTokenStamped = true;
                        yield return Stamp(LatencyEventNames.TranslationFirstToken, LatencyStage.Translation, origin, OpenAiProviderLabel);
                    }

                    accumulated.Append(partial.TextDelta);
                    yield return Seg($"tgt-{segmentIndex}", RoleTarget, accumulated.ToString(), false, OpenAiProviderLabel, partial.Timestamp);
                    break;

                case TranslationFinal final:
                    // C.4 FORK-1a: surface the translation token usage on the translation.final LatencyEvent's
                    // Metadata so the WS layer can build CostUsage (the CascadeOutputEvent stream carries no
                    // raw tokens). Minimal stamp enrichment — no contract change (Metadata is an existing field).
                    yield return StampTranslationFinal(origin, final);
                    yield return Seg($"tgt-{segmentIndex}", RoleTarget, final.Text, true, OpenAiProviderLabel, final.Timestamp);
                    outcome.FinalText = final.Text;
                    yield break;

                case TranslationFailed failed:
                    outcome.Failed = true;
                    yield return new Error(failed.Error);
                    yield break;
            }
        }
    }

    private async IAsyncEnumerable<CascadeOutputEvent> SynthesizeSegmentAsync(
        CascadeStartParams p,
        DateTimeOffset origin,
        string targetText,
        StageOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new TtsRequest(
            targetText, p.Direction.Target, p.TtsVoice, p.TtsModel, TtsResponseFormat, null, p.SessionId, p.TurnId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(p.TtsTimeout ?? DefaultTtsTimeout);
        await using var stream = tts.SynthesizeAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);

        var contentType = TtsDefaultContentType;
        var completed = false; // set on TtsComplete; if the stream ends without it → fail closed (C.4b)
        DateTimeOffset? ttsStartedAt = null; // 057c — captured to log the tts.first_audio - tts.started delta

        while (true)
        {
            TtsEvent? current = null;
            ProviderError? timeoutError = null;
            var ended = false;

            try
            {
                if (await stream.MoveNextAsync())
                {
                    current = stream.Current;
                }
                else
                {
                    ended = true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timeoutError = ProviderErrorMapper.Timeout(OpenAiProviderLabel, "tts");
            }

            if (timeoutError is not null)
            {
                outcome.Failed = true;
                yield return new Error(timeoutError);
                yield break;
            }

            if (ended)
            {
                // A real provider yields TtsComplete then ends, so reaching `ended` WITHOUT having seen
                // complete means the audio stream ended early (truncated) → fail closed (C.4b, ARCH-011/018).
                if (!completed)
                {
                    outcome.Failed = true;
                    yield return new Error(ProviderErrorMapper.Unknown(OpenAiProviderLabel, "tts"));
                }

                yield break;
            }

            switch (current)
            {
                case TtsStarted:
                    var ttsStarted = Stamp(LatencyEventNames.TtsStarted, LatencyStage.Tts, origin, OpenAiProviderLabel);
                    ttsStartedAt = ttsStarted.Event.Timestamp;
                    yield return ttsStarted;
                    break;

                case TtsFirstAudio first:
                    contentType = first.ContentType;
                    var ttsFirstAudio = Stamp(LatencyEventNames.TtsFirstAudio, LatencyStage.Tts, origin, OpenAiProviderLabel);
                    // 057c — diagnostic only: the live 0 ms (tts.started == tts.first_audio) is a provider-
                    // synchronous-yield / clock-resolution artifact, NOT a stamping bug (the stamps are on
                    // distinct provider events). Log the real delta so the live smoke quantifies it. No fix.
                    if (ttsStartedAt is { } startedAt)
                    {
                        logger.LogDebug(
                            "TTS first-audio delta: {DeltaMs} ms (tts.first_audio - tts.started)",
                            (ttsFirstAudio.Event.Timestamp - startedAt).TotalMilliseconds);
                    }

                    yield return ttsFirstAudio;
                    break;

                case TtsAudioChunk chunk:
                    yield return new Audio(chunk.Bytes, chunk.Seq, contentType);
                    break;

                case TtsComplete:
                    completed = true;
                    yield return Stamp(LatencyEventNames.TtsComplete, LatencyStage.Tts, origin, OpenAiProviderLabel);
                    break;

                case TtsFailed failed:
                    outcome.Failed = true;
                    yield return new Error(failed.Error);
                    yield break;
            }
        }
    }

    private Latency Stamp(string name, LatencyStage stage, DateTimeOffset origin, string? provider) =>
        new(factory.Stamp(
            name, stage, ClockSource.Server, origin,
            provider is null ? null : new Dictionary<string, string> { ["provider"] = provider }));

    // translation.final carries the token usage in Metadata (when the provider reports it) so the C.4 WS
    // layer can price the translation stage; absent tokens are simply omitted (cost degrades, never fabricated).
    private Latency StampTranslationFinal(DateTimeOffset origin, TranslationFinal final)
    {
        var metadata = new Dictionary<string, string> { ["provider"] = OpenAiProviderLabel };
        if (final.InputTokens is int inputTokens)
        {
            metadata["inputTokens"] = inputTokens.ToString(CultureInfo.InvariantCulture);
        }

        if (final.OutputTokens is int outputTokens)
        {
            metadata["outputTokens"] = outputTokens.ToString(CultureInfo.InvariantCulture);
        }

        return new Latency(factory.Stamp(LatencyEventNames.TranslationFinal, LatencyStage.Translation, ClockSource.Server, origin, metadata));
    }

    private static Transcript Seg(string id, string role, string text, bool isFinal, string provider, DateTimeOffset timestamp) =>
        new(new TranscriptSegment(id, role, text, isFinal, provider, timestamp, ClockSource.Server));
}
