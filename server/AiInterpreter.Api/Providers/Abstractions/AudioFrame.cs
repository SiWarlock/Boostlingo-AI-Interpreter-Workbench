namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>
/// A captured audio frame (ARCH-012). Live capture streams many frames; the blob fallback wraps a
/// single recording as a one-element sequence.
/// </summary>
public sealed record AudioFrame(ReadOnlyMemory<byte> Bytes, DateTimeOffset CapturedAt);
