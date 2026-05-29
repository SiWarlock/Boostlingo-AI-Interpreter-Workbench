using AiInterpreter.Api.Cascade;

namespace AiInterpreter.Tests;

// C.5 SAFETY commit (invariant #5, ARCH-019 item 8): the blob-upload validator. Size cap + a container
// content-type allowlist enforced BEFORE any provider call; a violation -> cascade.invalid_audio with the
// right HTTP status (413 oversized / 415 unsupported-or-empty). A supported type also yields the derived
// container encoding (NON-linear16 -> routes to the Deepgram REST pre-recorded path, C.1). Pure + unit-TDD'd.
public class CascadeUploadValidationTests
{
    [Theory] // each supported container type within the cap -> valid, and the derived encoding is NOT linear16.
    [InlineData("audio/webm")]
    [InlineData("audio/ogg")]
    [InlineData("audio/wav")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/mp4")]
    [InlineData("audio/webm; codecs=opus")] // a browser MediaRecorder upload — base type allowlisted
    [InlineData("audio/ogg;codecs=opus")]   // no space after ';'
    public void upload_allowlist_accepts_supported_types(string contentType)
    {
        var check = CascadeUploadValidation.Validate(contentType, length: 1024, CascadeUploadValidation.DefaultMaxBytes);

        Assert.True(check.Ok);
        Assert.Null(check.Error);
        Assert.NotEqual("linear16", check.Encoding); // routes to the pre-recorded REST path, not live PCM
        Assert.False(string.IsNullOrWhiteSpace(check.Encoding));
    }

    [Fact]
    public void upload_rejects_oversized()
    {
        var check = CascadeUploadValidation.Validate(
            "audio/webm", length: CascadeUploadValidation.DefaultMaxBytes + 1, CascadeUploadValidation.DefaultMaxBytes);

        Assert.False(check.Ok);
        Assert.Equal(413, check.StatusCode);
        Assert.Equal("cascade.invalid_audio", check.Error!.Code);
        Assert.False(check.Error.Retryable);
    }

    [Fact]
    public void upload_cap_is_configurable()
    {
        // The cap is a parameter (the controller wires it from config, default DefaultMaxBytes): a tiny cap
        // rejects a payload the default would accept.
        var check = CascadeUploadValidation.Validate("audio/webm", length: 100, maxBytes: 10);

        Assert.False(check.Ok);
        Assert.Equal(413, check.StatusCode);
    }

    [Theory] // off-allowlist content types -> 415 unsupported.
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("audio/x-evil")]
    public void upload_rejects_unsupported_type(string contentType)
    {
        var check = CascadeUploadValidation.Validate(contentType, length: 1024, CascadeUploadValidation.DefaultMaxBytes);

        Assert.False(check.Ok);
        Assert.Equal(415, check.StatusCode);
        Assert.Equal("cascade.invalid_audio", check.Error!.Code);
    }

    [Theory] // missing/empty file (and a null content-type) -> rejected.
    [InlineData("audio/webm", 0)]
    [InlineData(null, 1024)]
    public void upload_rejects_missing_or_empty_file(string? contentType, long length)
    {
        var check = CascadeUploadValidation.Validate(contentType, length, CascadeUploadValidation.DefaultMaxBytes);

        Assert.False(check.Ok);
        Assert.Equal(415, check.StatusCode);
        Assert.Equal("cascade.invalid_audio", check.Error!.Code);
    }
}
