using System.Runtime.CompilerServices;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// The cascade BLOB-fallback adapter (C.5, ARCH-008/011) — the documented NON-streaming path. It is a
/// thin adapter over the UNCHANGED streaming <see cref="CascadeStreamingOrchestrator"/>: it wraps the
/// uploaded blob as a single <see cref="AudioFrame"/> with the derived CONTAINER encoding (routing
/// <see cref="Providers.Deepgram.DeepgramSttProvider"/> to pre-recorded REST STT, C.1), runs the SAME
/// per-segment translation→TTS pipeline, and COLLECTS the flat <see cref="CascadeOutputEvent"/> stream
/// into one turn + cost + (response-only) audio — reusing <see cref="CascadeWsMapping.AssembleTurn"/>,
/// the cost fold, and the C.4b idempotent <see cref="SessionStore.FinalizeTurn"/> + fail-closed paths.
/// NOT a re-implementation (forbidden-pattern #4 — it consumes the same streamed IAsyncEnumerable).
///
/// <para><b>Cost:</b> the blob path does NOT price the STT stage — the pre-recorded route carries no
/// audio-duration signal in the <see cref="SttEvent"/> contract, and a processing wall-clock would be a
/// large undercount (streaming-honesty / no-synthetic-metrics, lesson §9). STT minutes are left
/// unsupplied → the composite degrades to "unavailable" (the WS path, where recording wall-clock ≈ live
/// audio duration, prices fully). Documented limitation.</para>
///
/// <para><b>Safety:</b> the uploaded blob is transcribed then dropped; the TTS audio is returned in-body
/// only — NEITHER is persisted (invariant #3; <see cref="CascadeWsMapping.AssembleTurn"/> has no audio case).</para>
/// </summary>
public sealed class CascadeOrchestrator(
    CascadeStreamingOrchestrator streaming,
    SessionStore store,
    SessionPersistenceWriter writer,
    CostEstimator costEstimator,
    IClock clock)
{
    private const string DefaultAudioContentType = "audio/mpeg";

    /// <summary>
    /// Runs the pre-recorded cascade for an uploaded blob against an existing turn. Returns the collected
    /// turn + the concatenated target audio (response-only) + the best-effort persist outcome, or
    /// <c>null</c> if the session/turn is unknown.
    /// </summary>
    public async Task<CascadeBlobResult?> RunBlobTurnAsync(CascadeBlobParams p, byte[] audio, CancellationToken ct)
    {
        var turn = store.Get(p.SessionId)?.Turns.FirstOrDefault(t => t.TurnId == p.TurnId);
        if (turn is null)
        {
            return null;
        }

        var start = new CascadeStartParams(
            p.SessionId, p.TurnId, p.Direction, p.Encoding, p.SampleRate, p.TranslationModel, p.TtsVoice);

        var collected = new List<CascadeOutputEvent>();
        var audioChunks = new List<byte[]>();
        var audioContentType = DefaultAudioContentType;

        await foreach (var ev in streaming.RunAsync(start, OneFrameAsync(audio, ct), ct).WithCancellation(ct))
        {
            // Audio is streamed in the WS path; here it's collected for the in-body response ONLY — never
            // added to `collected` (which becomes the persisted turn — invariant #3).
            if (ev is Audio a)
            {
                audioChunks.Add(a.Bytes);
                audioContentType = CascadeWsMapping.ClampContentType(a.ContentType);
            }
            else
            {
                collected.Add(ev);
            }
        }

        var cost = ComputeCost(p, start, collected);
        var completedAt = clock.UtcNow;

        var finalize = store.FinalizeTurn(p.SessionId, p.TurnId, current =>
            CascadeWsMapping.AssembleTurn(current, collected, cost, completedAt) with
            {
                AudioDurationMs = 0, // blob: the true audio duration is unknown without decoding the container
                TranslationModelUsed = p.TranslationModel,
                TtsVoiceUsed = p.TtsVoice,
            });
        if (finalize is null)
        {
            return null; // the turn vanished mid-run (race) — treat as not found
        }

        var persist = Result<string>.Success(string.Empty);
        if (finalize.Applied)
        {
            var session = store.Get(p.SessionId);
            if (session is not null)
            {
                persist = await writer.WriteAsync(session, ct); // best-effort (never throws)
            }
        }

        return new CascadeBlobResult(finalize.Turn, Concat(audioChunks), audioContentType, persist);
    }

    // STT minutes deliberately UNSUPPLIED (null) -> EstimateStt unavailable -> the composite degrades
    // wholesale to "cost unavailable" (the honest blob behavior; see the class note). Translation tokens +
    // TTS char-proxy are still folded from the collected events (the same fold the WS path uses).
    private Result<CostEstimate> ComputeCost(CascadeBlobParams p, CascadeStartParams start, IReadOnlyList<CascadeOutputEvent> collected)
    {
        var inputs = CascadeWsMapping.FoldCostInputs(collected);
        return costEstimator.EstimateCascadeTurn(
            p.TranslationModel,
            start.TtsModel,
            new CostUsage { AudioMinutes = null },
            new CostUsage { InputTokens = inputs.InputTokens, OutputTokens = inputs.OutputTokens },
            new CostUsage { Characters = inputs.TargetChars > 0 ? inputs.TargetChars : null });
    }

    private async IAsyncEnumerable<AudioFrame> OneFrameAsync(byte[] audio, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return new AudioFrame(audio, clock.UtcNow);
    }

    private static byte[] Concat(List<byte[]> chunks)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var total = chunks.Sum(c => c.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
