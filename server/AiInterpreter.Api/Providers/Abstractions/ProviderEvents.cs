using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.Abstractions;

// Normalized streaming provider event hierarchies (ARCH-012). Each is an `abstract record` base
// with `sealed record` cases carrying `DateTimeOffset Timestamp`; consumers (the B.4 orchestrator)
// switch exhaustively over the cases. ProviderError is reused from ProviderErrors.cs (A.3).

// --- STT ---
public abstract record SttEvent(DateTimeOffset Timestamp);

public sealed record SttStarted(DateTimeOffset Timestamp) : SttEvent(Timestamp);

public sealed record SttPartial(string Text, DateTimeOffset Timestamp) : SttEvent(Timestamp);

// J.1 (Phase J) — DetectedLanguage carries the per-utterance source language Deepgram nova-3 `multi`
// detected (dominant of the alternative's languages[]/per-word language tags; null when undetected,
// out-of-EN/ES-scope, or one-direction). The bidirectional orchestrator flips translation direction off
// it (detected → other). Trailing-defaulted so existing 2-arg construction stays unchanged (back-compat).
public sealed record SttFinal(string Text, DateTimeOffset Timestamp, LanguageCode? DetectedLanguage = null) : SttEvent(Timestamp);

public sealed record SttFailed(ProviderError Error, DateTimeOffset Timestamp) : SttEvent(Timestamp);

// I.1 (Phase I) — a Deepgram endpointing marker (utterance-end / detected silence), distinct from a
// per-segment SttFinal. No text — it is a turn-terminal SIGNAL the orchestrator honors only under auto-VAD
// (manual mode ignores it, finalizing on the client `stop`). Maps from the SDK's UtteranceEndResponse.
public sealed record SttUtteranceEnd(DateTimeOffset Timestamp) : SttEvent(Timestamp);

// --- Translation ---
public abstract record TranslationEvent(DateTimeOffset Timestamp);

public sealed record TranslationStarted(DateTimeOffset Timestamp) : TranslationEvent(Timestamp);

public sealed record TranslationPartial(string TextDelta, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);

public sealed record TranslationFinal(string Text, int? InputTokens, int? OutputTokens, DateTimeOffset Timestamp)
    : TranslationEvent(Timestamp);

public sealed record TranslationFailed(ProviderError Error, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);

// --- TTS ---
public abstract record TtsEvent(DateTimeOffset Timestamp);

public sealed record TtsStarted(DateTimeOffset Timestamp) : TtsEvent(Timestamp);

public sealed record TtsFirstAudio(string ContentType, DateTimeOffset Timestamp) : TtsEvent(Timestamp);

public sealed record TtsAudioChunk(byte[] Bytes, int Seq, DateTimeOffset Timestamp) : TtsEvent(Timestamp);

public sealed record TtsComplete(string ContentType, DateTimeOffset Timestamp) : TtsEvent(Timestamp);

public sealed record TtsFailed(ProviderError Error, DateTimeOffset Timestamp) : TtsEvent(Timestamp);
