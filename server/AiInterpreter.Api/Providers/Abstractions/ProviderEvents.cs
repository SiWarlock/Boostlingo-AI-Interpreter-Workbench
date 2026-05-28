namespace AiInterpreter.Api.Providers.Abstractions;

// Normalized streaming provider event hierarchies (ARCH-012). Each is an `abstract record` base
// with `sealed record` cases carrying `DateTimeOffset Timestamp`; consumers (the B.4 orchestrator)
// switch exhaustively over the cases. ProviderError is reused from ProviderErrors.cs (A.3).

// --- STT ---
public abstract record SttEvent(DateTimeOffset Timestamp);

public sealed record SttStarted(DateTimeOffset Timestamp) : SttEvent(Timestamp);

public sealed record SttPartial(string Text, DateTimeOffset Timestamp) : SttEvent(Timestamp);

public sealed record SttFinal(string Text, DateTimeOffset Timestamp) : SttEvent(Timestamp);

public sealed record SttFailed(ProviderError Error, DateTimeOffset Timestamp) : SttEvent(Timestamp);

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
