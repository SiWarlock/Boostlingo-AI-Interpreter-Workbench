using System.Runtime.CompilerServices;
using AiInterpreter.Api.Dev;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// G.4-BE — DevTtsSynthesisService unit tests (the soak-harness synthesis half). The service wraps
// ITtsProvider, collects the streamed TtsAudioChunk bytes (in Seq order) into one complete payload, and
// degrades provider/cap failures to a safe ProviderError outcome (no partial bytes, no leak). Stateless —
// no SessionStore/writer dependency (invariant #3 structural, §28 transcribe precedent). Fake providers; no real keys.
public sealed class DevTtsSynthesisServiceTests
{
    private static DevTtsSynthesisService Service(ITtsProvider provider, OpenAiTtsOptions? options = null) =>
        new(provider, Options.Create(options ?? new OpenAiTtsOptions()));

    // #1 — the streamed chunks are concatenated into one payload + the content-type comes from the stream
    // (the real response header value via TtsFirstAudio). ARCH-012 streaming contract; the harness needs the
    // whole payload to cache + decode.
    [Fact]
    public async Task synthesize_collects_chunks_and_resolves_content_type()
    {
        var service = Service(
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete, chunkCount: 3, contentType: "audio/wav"));

        var outcome = await service.SynthesizeAsync("hello", LanguageCode.En, CancellationToken.None);

        Assert.Equal(DevTtsSynthesisStatus.Ok, outcome.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, outcome.Audio);
        Assert.Equal("audio/wav", outcome.ContentType);
        Assert.Null(outcome.Error);
    }

    // #1b — Seq-ordering is genuinely pinned. FakeTtsProvider emits identical [1,2,3] per chunk, so order is
    // unobservable with it; a provider that yields DISTINCT bytes OUT OF Seq order proves the service orders
    // by Seq before concatenating (not merely arrival order).
    [Fact]
    public async Task synthesize_orders_chunks_by_seq()
    {
        var service = Service(new ReorderingTtsProvider());

        var outcome = await service.SynthesizeAsync("hi", LanguageCode.En, CancellationToken.None);

        Assert.Equal(new byte[] { 10, 20, 30 }, outcome.Audio);
    }

    // #1c (§17 tolerance) — a conformant no-chunk variant emits Started→Complete with NO TtsFirstAudio; the
    // service must still resolve the content-type (from TtsComplete) and not NRE. Low practical stakes here
    // (wav is hardcoded + real TTS always chunks) but honors the brief's FirstAudio/Complete contract.
    [Fact]
    public async Task synthesize_no_chunk_variant_resolves_content_type_from_complete()
    {
        var service = Service(new NoChunkTtsProvider());

        var outcome = await service.SynthesizeAsync("hello", LanguageCode.En, CancellationToken.None);

        Assert.Equal(DevTtsSynthesisStatus.Ok, outcome.Status);
        Assert.Equal("audio/wav", outcome.ContentType);
        Assert.Empty(outcome.Audio!);
    }

    // #2 — the synthetic TtsRequest carries an EMPTY Voice (so OpenAiTtsMapping.ResolveVoice picks
    // VoiceByLanguage[language] — the Phase-J §38 voice-by-target pattern) + the target language + Model from
    // options + the wav response-format override (Q3, deterministic lossless decode for the harness).
    [Fact]
    public async Task synthesize_builds_request_empty_voice_for_language_resolution()
    {
        var capturing = new CapturingTtsProvider();
        var options = new OpenAiTtsOptions { Model = "gpt-4o-mini-tts" };
        var service = Service(capturing, options);

        await service.SynthesizeAsync("hola", LanguageCode.Es, CancellationToken.None);

        Assert.NotNull(capturing.LastRequest);
        Assert.Equal(string.Empty, capturing.LastRequest!.Voice);
        Assert.Equal(LanguageCode.Es, capturing.LastRequest.TargetLanguage);
        Assert.Equal("gpt-4o-mini-tts", capturing.LastRequest.Model);
        Assert.Equal("wav", capturing.LastRequest.ResponseFormat);
    }

    // #3 — a provider TtsFailed degrades to a Failed outcome carrying the already-safe ProviderError (no raw
    // payload/stack/secret — invariant #4 / §13); no partial bytes are returned.
    [Fact]
    public async Task synthesize_provider_failure_degrades_sanitized()
    {
        var service = Service(new FakeTtsProvider(FakeTtsBehavior.Error));

        var outcome = await service.SynthesizeAsync("hello", LanguageCode.En, CancellationToken.None);

        Assert.Equal(DevTtsSynthesisStatus.Failed, outcome.Status);
        Assert.Null(outcome.Audio);
        Assert.NotNull(outcome.Error);
        Assert.Equal("tts.upstream_unavailable", outcome.Error!.Code);
        Assert.Equal("The tts provider is temporarily unavailable.", outcome.Error.SafeMessage);
    }

    // #4 — an over-cap input (> OpenAiTtsMapping.MaxInputChars) is rejected cleanly BEFORE any provider call
    // (no partial bytes), mapped to the same 400-class invalid_request the real provider's CapExceeded gives.
    // The provider is a throwing double: if the pre-check didn't short-circuit, the throw would surface here.
    [Fact]
    public async Task synthesize_input_over_cap_rejected_clean()
    {
        var service = Service(new ThrowingTtsProvider());

        var outcome = await service.SynthesizeAsync(
            new string('a', OpenAiTtsMapping.MaxInputChars + 1), LanguageCode.En, CancellationToken.None);

        Assert.Equal(DevTtsSynthesisStatus.Failed, outcome.Status);
        Assert.Null(outcome.Audio);
        Assert.NotNull(outcome.Error);
        Assert.Equal("tts.invalid_request", outcome.Error!.Code);
        Assert.Equal(400, outcome.Error.HttpStatusCode);
    }

    // #6 (structural) — invariant #3, §28 precedent: dev synthesis persists nothing, so the service has NO
    // SessionStore / SessionPersistenceWriter dependency — it structurally cannot write a session/secret/raw audio.
    [Fact]
    public void service_has_no_session_store_or_writer_dependency()
    {
        var ctorParamTypes = typeof(DevTtsSynthesisService)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(SessionStore), ctorParamTypes);
        Assert.DoesNotContain(typeof(SessionPersistenceWriter), ctorParamTypes);
    }

    // --- inline provider doubles ---

    // Yields distinct bytes OUT OF Seq order (2, 0, 1) to pin order-by-Seq in the service.
    private sealed class ReorderingTtsProvider : ITtsProvider
    {
        public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
            TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TtsStarted(DateTimeOffset.UtcNow);
            yield return new TtsFirstAudio("audio/wav", DateTimeOffset.UtcNow);
            yield return new TtsAudioChunk(new byte[] { 30 }, 2, DateTimeOffset.UtcNow);
            yield return new TtsAudioChunk(new byte[] { 10 }, 0, DateTimeOffset.UtcNow);
            yield return new TtsAudioChunk(new byte[] { 20 }, 1, DateTimeOffset.UtcNow);
            yield return new TtsComplete("audio/wav", DateTimeOffset.UtcNow);
        }
    }

    // Conformant no-chunk variant: Started -> Complete, no TtsFirstAudio, no chunks (§17).
    private sealed class NoChunkTtsProvider : ITtsProvider
    {
        public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
            TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TtsStarted(DateTimeOffset.UtcNow);
            yield return new TtsComplete("audio/wav", DateTimeOffset.UtcNow);
        }
    }

    // Records the request the service builds (so the voice/model/format shaping can be asserted).
    private sealed class CapturingTtsProvider : ITtsProvider
    {
        public TtsRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
            TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            LastRequest = request;
            await Task.CompletedTask;
            yield return new TtsFirstAudio("audio/wav", DateTimeOffset.UtcNow);
            yield return new TtsAudioChunk(new byte[] { 1 }, 0, DateTimeOffset.UtcNow);
            yield return new TtsComplete("audio/wav", DateTimeOffset.UtcNow);
        }
    }

    // Throws if reached — proves the over-cap pre-check short-circuits before any provider call.
    private sealed class ThrowingTtsProvider : ITtsProvider
    {
        public IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Provider must not be reached when the input exceeds the cap.");
    }
}
