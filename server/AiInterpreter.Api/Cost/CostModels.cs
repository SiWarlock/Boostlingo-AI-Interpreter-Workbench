namespace AiInterpreter.Api.Cost;

/// <summary>
/// Per-stage usage units fed to <see cref="CostEstimator"/> (ARCH-014). All fields are nullable —
/// the pricing basis being estimated determines which are read. Area-local input record (not
/// persisted/wire): C.4/B.7 build it from the turn's metrics/provider usage before estimating.
/// </summary>
public sealed record CostUsage
{
    /// <summary>Audio minutes — STT audio-minute basis; also the TTS approx-minute fallback.</summary>
    public decimal? AudioMinutes { get; init; }

    /// <summary>Token counts — translation (tokens basis) + TTS text-input.</summary>
    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    /// <summary>Character count — tts-1/tts-1-hd characters basis.</summary>
    public long? Characters { get; init; }

    /// <summary>TTS audio-output / text-input token counts — gpt-4o-mini-tts audio_output_tokens basis;
    /// also the realtime OUTPUT audio-token count (053-C2a exact-count path — identical meaning).</summary>
    public int? AudioOutputTokens { get; init; }

    public int? TextInputTokens { get; init; }

    /// <summary>Realtime audio seconds — converted to tokens before the per-million rate is applied.</summary>
    public decimal? AudioInputSeconds { get; init; }

    /// <summary>Realtime cached-input seconds (billed at the cached rate when configured).</summary>
    public decimal? CachedAudioInputSeconds { get; init; }

    public decimal? AudioOutputSeconds { get; init; }

    /// <summary>
    /// Realtime EXACT audio-token counts from the data channel's <c>response.done.usage</c> (053-C2a) —
    /// priced directly at the per-million audio rates (no audio-seconds × factor estimate) when present.
    /// <see cref="AudioOutputTokens"/> (above) carries the realtime OUTPUT audio tokens. Text tokens are
    /// deliberately absent (disclosed-unpriced — no text rates configured).
    /// </summary>
    public int? AudioInputTokens { get; init; }

    /// <summary>Cached input-AUDIO tokens (<c>cached_tokens_details.audio_tokens</c>) — a SUBSET of
    /// <see cref="AudioInputTokens"/>, priced at the cached rate and removed from the full-rate base (094).
    /// NOT the total <c>cached_tokens</c> (which would include text).</summary>
    public int? CachedAudioInputTokens { get; init; }
}
