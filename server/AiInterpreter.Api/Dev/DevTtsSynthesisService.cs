using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Dev;

/// <summary>The dev TTS request body (<c>POST /api/dev/tts</c>): one utterance + its language.</summary>
public sealed record DevTtsRequest(string Text, LanguageCode Language);

/// <summary>The synthesis outcome status — success (audio bytes) or a degraded failure (safe error).</summary>
public enum DevTtsSynthesisStatus
{
    Ok,
    Failed,
}

/// <summary>
/// The result of a dev synthesis call. <see cref="Status"/> drives the boundary mapping: <c>Ok</c> carries the
/// complete <see cref="Audio"/> + <see cref="ContentType"/>; <c>Failed</c> carries an already-safe
/// <see cref="ProviderError"/> (the over-cap 400 or a provider failure) — never partial bytes.
/// </summary>
public sealed record DevTtsSynthesisOutcome(
    DevTtsSynthesisStatus Status,
    byte[]? Audio,
    string? ContentType,
    ProviderError? Error)
{
    public static DevTtsSynthesisOutcome Success(byte[] audio, string contentType) =>
        new(DevTtsSynthesisStatus.Ok, audio, contentType, null);

    public static DevTtsSynthesisOutcome Failure(ProviderError error) =>
        new(DevTtsSynthesisStatus.Failed, null, null, error);
}

/// <summary>
/// Synthesizes one text utterance to complete audio bytes via the existing <see cref="ITtsProvider"/> — the
/// dev-only soak-harness support seam (G.4-BE). It exists so the browser harness can cache + reuse the scripted
/// EN/ES audio WITHOUT the OpenAI key ever reaching the browser (safety invariant #1).
/// </summary>
public interface IDevTtsSynthesisService
{
    Task<DevTtsSynthesisOutcome> SynthesizeAsync(string text, LanguageCode language, CancellationToken ct);
}

/// <summary>
/// Collects the streamed <see cref="ITtsProvider"/> events into one complete audio payload (ARCH-012). Thin
/// (ARCH-008) — provider logic stays in the provider; this owns only the stream-collection + the boundary
/// degrade. STATELESS: no <c>SessionStore</c>/<c>SessionPersistenceWriter</c> dependency — dev synthesis
/// persists NOTHING (safety invariant #3, structural; the §28 stateless-transcribe precedent extended to a dev tool).
/// </summary>
public sealed class DevTtsSynthesisService : IDevTtsSynthesisService
{
    // Request the lossless WAV format regardless of the configured default (mp3): the harness decodes the
    // complete buffer for deterministic 1x-real-time injection, so a clean lossless container is preferable.
    private const string SynthResponseFormat = "wav";

    // No real turn exists for a synthetic synthesis; these placeholders only shape the provider-side request/log,
    // never persisted here (the service is stateless).
    private const string PlaceholderSessionId = "soak";
    private const string PlaceholderTurnId = "synth";

    private readonly ITtsProvider _provider;
    private readonly OpenAiTtsOptions _options;

    public DevTtsSynthesisService(ITtsProvider provider, IOptions<OpenAiTtsOptions> options)
    {
        _provider = provider;
        _options = options.Value;
    }

    public async Task<DevTtsSynthesisOutcome> SynthesizeAsync(
        string text, LanguageCode language, CancellationToken ct)
    {
        // SAFETY/bound (ARCH-011/019): the 4096-char input cap is a boundary chokepoint — reject an over-length
        // input HERE (reusing the provider's own CapExceeded -> 400 invalid_request mapping) BEFORE any provider
        // call, so the rejection is deterministic + identical to the real provider, with no partial bytes.
        if ((text?.Length ?? 0) > OpenAiTtsMapping.MaxInputChars)
        {
            return DevTtsSynthesisOutcome.Failure(OpenAiTtsMapping.CapExceeded(DateTimeOffset.UtcNow).Error);
        }

        // Empty Voice -> OpenAiTtsMapping.ResolveVoice selects VoiceByLanguage[language] (the §38 voice-by-target
        // pattern), so EN lines synthesize with the EN voice + ES lines with the ES voice. Model from options.
        var request = new TtsRequest(
            Text: text ?? string.Empty,
            TargetLanguage: language,
            Voice: string.Empty,
            Model: _options.Model,
            ResponseFormat: SynthResponseFormat,
            Instructions: null,
            SessionId: PlaceholderSessionId,
            TurnId: PlaceholderTurnId);

        var chunks = new List<TtsAudioChunk>();
        string? contentType = null;

        await foreach (var ev in _provider.SynthesizeAsync(request, ct))
        {
            switch (ev)
            {
                case TtsFirstAudio firstAudio:
                    contentType ??= firstAudio.ContentType;
                    break;
                case TtsAudioChunk chunk:
                    chunks.Add(chunk);
                    break;
                case TtsComplete complete:
                    // Fall back to the terminal content-type for a conformant no-chunk variant
                    // (Started->Complete with no TtsFirstAudio) — never NRE on content-type (§17).
                    contentType ??= complete.ContentType;
                    break;
                case TtsFailed failed:
                    // Degrade to the already-safe ProviderError; discard any bytes received before the failure.
                    return DevTtsSynthesisOutcome.Failure(failed.Error);
            }
        }

        // Concatenate strictly in Seq order (the stream yields in order; ordering defensively honors the contract).
        var audio = chunks
            .OrderBy(c => c.Seq)
            .SelectMany(c => c.Bytes)
            .ToArray();

        return DevTtsSynthesisOutcome.Success(
            audio, contentType ?? OpenAiTtsMapping.ResolveContentType(null, SynthResponseFormat));
    }
}
