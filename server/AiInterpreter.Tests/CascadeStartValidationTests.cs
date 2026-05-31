using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// C.4a SECURITY commit — the WS start-frame parse + the encoding allowlist (ARCH-009 protocol / ARCH-019
// header-injection guard). Isolated from the rest of C.4a's mapping so the safety validation commits alone.
public class CascadeStartValidationTests
{
    [Fact]
    public void start_frame_parses_to_cascade_params()
    {
        var parsed = CascadeStartValidation.ParseStart(StartJson(encoding: "linear16"));

        Assert.Null(parsed.Error);
        var p = parsed.Params!;
        Assert.Equal("s1", p.SessionId);
        Assert.Equal("turn_1", p.TurnId);
        Assert.Equal(LanguageCode.En, p.Direction.Source);
        Assert.Equal(LanguageCode.Es, p.Direction.Target);
        Assert.Equal("linear16", p.Encoding);
        Assert.Equal(48000, p.SampleRate);
        Assert.Equal("gpt-5-nano", p.TranslationModel);
        Assert.Equal("alloy", p.TtsVoice);
    }

    [Theory] // ARCH-019: the orchestrator interpolates Encoding into audio/{encoding} -> an unvalidated value
             // is a header-injection surface at the real provider. Reject anything off the closed set.
    [InlineData("mp3")]
    [InlineData("mp3; x-inject")]
    [InlineData("webm")]
    [InlineData("")]
    public void encoding_allowlist_rejects_invalid(string encoding)
    {
        var parsed = CascadeStartValidation.ParseStart(StartJson(encoding: encoding));

        Assert.Null(parsed.Params); // CascadeStartParams NOT built for an invalid encoding
        Assert.NotNull(parsed.Error);
        Assert.Equal("cascade.invalid_audio", parsed.Error!.Code);
        Assert.False(parsed.Error.Retryable);
    }

    [Theory]
    [InlineData("linear16")]
    [InlineData("pcm")]
    public void encoding_allowlist_accepts_valid(string encoding)
    {
        var parsed = CascadeStartValidation.ParseStart(StartJson(encoding: encoding));

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Params);
        Assert.Equal(encoding, parsed.Params!.Encoding);
    }

    [Fact]
    public void malformed_start_json_rejected()
    {
        // C.4b Q5: malformed JSON is a malformed *request* frame, not bad *audio* — distinct, honest code.
        var parsed = CascadeStartValidation.ParseStart("{ not json");

        Assert.Null(parsed.Params);
        Assert.Equal("cascade.invalid_request", parsed.Error!.Code);
        Assert.False(parsed.Error.Retryable);
    }

    [Theory] // C.4b field validation (ARCH-019): missing/blank ids + non-positive sample rate are rejected
             // BEFORE building CascadeStartParams (today they fall through to empty-string/0).
    [InlineData("", "turn_1", 48000)]   // blank sessionId
    [InlineData("s1", "", 48000)]       // blank turnId
    [InlineData("s1", "turn_1", 0)]     // sampleRate <= 0
    [InlineData("s1", "turn_1", -1)]    // negative sampleRate
    public void start_frame_rejects_missing_fields(string sessionId, string turnId, int sampleRate)
    {
        var parsed = CascadeStartValidation.ParseStart(StartJsonFull(sessionId, turnId, "linear16", sampleRate));

        Assert.Null(parsed.Params); // not built
        Assert.Equal("cascade.invalid_request", parsed.Error!.Code);
        Assert.False(parsed.Error.Retryable);
    }

    [Fact] // ARCH-019 / lesson §16: cap client-supplied start-frame strings — sessionId/turnId are echoed
           // (turnId in every `done`) + used as store keys; an unbounded value is a reflection/alloc surface.
    public void start_frame_rejects_oversized_field()
    {
        var huge = new string('x', 300);
        var parsed = CascadeStartValidation.ParseStart(StartJsonFull(huge, "turn_1", "linear16", 48000));

        Assert.Null(parsed.Params); // not built
        Assert.Equal("cascade.invalid_request", parsed.Error!.Code);
        Assert.False(parsed.Error.Retryable);
    }

    [Fact]
    public void valid_full_start_frame_builds_params()
    {
        var parsed = CascadeStartValidation.ParseStart(StartJsonFull("s1", "turn_1", "linear16", 16000));

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Params);
        Assert.Equal(16000, parsed.Params!.SampleRate);
    }

    [Fact] // ARCH-018 error-shape precision: malformed JSON vs disallowed encoding vs missing field each map
           // to their distinct rejection code (a single parametrized Invalid(...) factory backs them).
    public void start_frame_message_precision()
    {
        Assert.Equal("cascade.invalid_request",
            CascadeStartValidation.ParseStart("{ not json").Error!.Code);                        // malformed
        Assert.Equal("cascade.invalid_audio",
            CascadeStartValidation.ParseStart(StartJsonFull("s1", "turn_1", "mp3", 48000)).Error!.Code); // bad encoding (SECURITY allowlist)
        Assert.Equal("cascade.invalid_request",
            CascadeStartValidation.ParseStart(StartJsonFull("", "turn_1", "linear16", 48000)).Error!.Code); // missing field
    }

    private static string StartJson(string encoding) => StartJsonFull("s1", "turn_1", encoding, 48000);

    private static string StartJsonFull(string sessionId, string turnId, string encoding, int sampleRate) =>
        $$"""
        {"type":"start","sessionId":"{{sessionId}}","turnId":"{{turnId}}","direction":{"source":"en","target":"es"},"encoding":"{{encoding}}","sampleRate":{{sampleRate}},"translationModel":"gpt-5-nano","ttsVoice":"alloy"}
        """;
}
