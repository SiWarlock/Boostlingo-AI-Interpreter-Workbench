using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// SECURITY boundary (C.5, ARCH-019 item 8 / key-safety-rule #5): validates the blob upload to
/// <c>POST /api/cascade/turn</c> BEFORE any provider call — a size cap + a container content-type
/// allowlist. A violation → <c>cascade.invalid_audio</c> with the HTTP status the controller surfaces
/// (413 oversized / 415 unsupported-or-empty). A supported type yields the derived <b>container</b>
/// encoding (NON-<c>linear16</c>), which routes <see cref="Providers.Deepgram.DeepgramSttProvider"/> to
/// its REST pre-recorded path (C.1). Pure + unit-TDD'd; the cap is a parameter (the controller wires it
/// from config, default <see cref="DefaultMaxBytes"/>) so it is configurable per ARCH-019.
///
/// This is distinct from the WS <see cref="CascadeStartValidation"/> <c>encoding</c> allowlist
/// (<c>{linear16,pcm}</c>, raw PCM only) — the blob path accepts recorded containers, never raw PCM.
/// </summary>
internal static class CascadeUploadValidation
{
    /// <summary>Default upload size cap (~10MB). The controller may override it from configuration.</summary>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    // Allowed blob container content-types -> the derived (non-linear16) encoding token. Deepgram
    // auto-detects the container on the REST path, so the token only needs to route (not transcode).
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = "webm",
        ["audio/ogg"] = "ogg",
        ["audio/wav"] = "wav",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "mp4",
    };

    /// <summary>The outcome: valid (with the derived container encoding) or a rejection (status + error).</summary>
    public readonly record struct UploadCheck(bool Ok, int StatusCode, ProviderError? Error, string Encoding);

    /// <summary>
    /// Validates the upload's size + content-type against <paramref name="maxBytes"/> + the allowlist.
    /// Order: oversized → 413; empty/zero-length → 415; off-allowlist (or null) content-type → 415.
    /// </summary>
    public static UploadCheck Validate(string? contentType, long length, long maxBytes)
    {
        if (length > maxBytes)
        {
            return Reject(413, "The uploaded audio exceeds the maximum allowed size.");
        }

        if (length <= 0)
        {
            return Reject(415, "The uploaded audio is empty or missing.");
        }

        // Strip MIME parameters before the allowlist lookup: a browser MediaRecorder upload sends e.g.
        // "audio/webm; codecs=opus", whose base type IS on the allowlist (ARCH-030 — the common capture path).
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        if (string.IsNullOrEmpty(mediaType) || !Allowed.TryGetValue(mediaType, out var encoding))
        {
            return Reject(415, "The uploaded audio content type is not supported.");
        }

        return new UploadCheck(true, 200, null, encoding);
    }

    private static UploadCheck Reject(int statusCode, string message) => new(
        false,
        statusCode,
        new ProviderError("cascade", "cascade", "cascade.invalid_audio", message, Retryable: false, statusCode),
        string.Empty);
}
