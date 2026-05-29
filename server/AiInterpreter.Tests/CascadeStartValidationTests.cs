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
        Assert.Equal("gpt-5.4-nano", p.TranslationModel);
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
        var parsed = CascadeStartValidation.ParseStart("{ not json");

        Assert.Null(parsed.Params);
        Assert.Equal("cascade.invalid_audio", parsed.Error!.Code);
    }

    private static string StartJson(string encoding) =>
        $$"""
        {"type":"start","sessionId":"s1","turnId":"turn_1","direction":{"source":"en","target":"es"},"encoding":"{{encoding}}","sampleRate":48000,"translationModel":"gpt-5.4-nano","ttsVoice":"alloy"}
        """;
}
