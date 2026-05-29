using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// C.3 — OpenAiTtsProvider deterministic-surface tests (ARCH-011 / ARCH-012 / ARCH-020).
//
// Reuses the §20 raw-HttpClient harness (mock HttpMessageHandler + end-to-end TDD), but over a BINARY
// chunk stream (not SSE) — read raw byte buffers, no data:/event parsing. TtsFirstAudio on the genuine
// first chunk (forbidden #3 — never a relabeled completion); ContentType from the response header.
//
// Scope guard (lesson §17): we do NOT re-test the ProviderErrorMapper table (B.1) or FakeTts per-variant
// behavior (B.2). The whole-read-loop timeout + OCE handling are §20-established (no new test here).
public class OpenAiTtsProviderTests
{
    // === Group 1 — genuine chunk streaming: ordered events + first-audio-is-first-chunk ===

    [Fact]
    public async Task streams_started_firstaudio_chunks_complete_in_order()
    {
        // 6-byte body delivered 2 bytes/read -> 3 chunks (seq 0,1,2).
        var events = await Collect(
            Provider(new StubHandler(new byte[] { 1, 2, 3, 4, 5, 6 }, "audio/mpeg", chunkSize: 2)).SynthesizeAsync(Req(), default));

        Assert.Collection(
            events,
            e => Assert.IsType<TtsStarted>(e),
            e => Assert.Equal("audio/mpeg", Assert.IsType<TtsFirstAudio>(e).ContentType),
            e => AssertChunk(e, seq: 0, new byte[] { 1, 2 }),
            e => AssertChunk(e, seq: 1, new byte[] { 3, 4 }),
            e => AssertChunk(e, seq: 2, new byte[] { 5, 6 }),
            e => Assert.Equal("audio/mpeg", Assert.IsType<TtsComplete>(e).ContentType));
    }

    [Fact]
    public async Task first_audio_is_the_first_chunk()
    {
        // TtsFirstAudio precedes the first TtsAudioChunk and is NOT relabeled from completion (forbidden #3).
        var events = await Collect(
            Provider(new StubHandler(new byte[] { 1, 2, 3, 4 }, "audio/mpeg", chunkSize: 2)).SynthesizeAsync(Req(), default));

        Assert.IsType<TtsStarted>(events[0]);
        var firstAudioIdx = events.FindIndex(e => e is TtsFirstAudio);
        var firstChunkIdx = events.FindIndex(e => e is TtsAudioChunk);
        Assert.True(firstAudioIdx >= 0);
        Assert.True(firstAudioIdx < firstChunkIdx);
        Assert.IsType<TtsComplete>(events[^1]); // a real Complete still terminates — FirstAudio wasn't the completion
    }

    // === Group 2 — ContentType: response header first, response_format fallback ===

    [Fact]
    public async Task content_type_from_response_header()
    {
        // A sentinel header value (distinct from the mp3 fallback) proves ContentType is read from the header.
        var events = await Collect(
            Provider(new StubHandler(new byte[] { 1, 2 }, "audio/x-sentinel", chunkSize: 0)).SynthesizeAsync(Req(), default));

        Assert.Equal("audio/x-sentinel", events.OfType<TtsFirstAudio>().Single().ContentType);
        Assert.Equal("audio/x-sentinel", events.OfType<TtsComplete>().Single().ContentType);
    }

    [Fact]
    public async Task content_type_falls_back_to_format_when_header_absent()
    {
        // No Content-Type header -> derive from response_format (mp3 -> audio/mpeg). Defensive fallback (Q2).
        var events = await Collect(
            Provider(new StubHandler(new byte[] { 1, 2 }, contentType: null, chunkSize: 0)).SynthesizeAsync(Req(), default));

        Assert.Equal("audio/mpeg", events.OfType<TtsFirstAudio>().Single().ContentType);
        Assert.Equal("audio/mpeg", events.OfType<TtsComplete>().Single().ContentType); // Complete shares the resolved type
    }

    // === Group 3 — pre-call 4096-char input cap (fail fast, no HTTP request) ===

    [Fact]
    public async Task input_over_4096_chars_fails_without_call()
    {
        var handler = new StubHandler(new byte[] { 1 }, "audio/mpeg", chunkSize: 0);
        var longText = new string('a', 4097);

        var events = await Collect(Provider(handler).SynthesizeAsync(Req(text: longText), default));

        Assert.Single(events);                                // ONLY the failure — no TtsStarted before the cap guard
        var failed = Assert.IsType<TtsFailed>(events[0]);
        Assert.Equal("tts.invalid_request", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal(0, handler.CallCount);                   // pre-call guard — no HTTP request sent
    }

    // === Group 4 — HTTP error -> TtsFailed via the mapper (status-bearing; no C.1 Q5 gap) ===

    [Fact]
    public async Task rate_limited_yields_failed_retryable()
    {
        var events = await Collect(Provider(new StubHandler(HttpStatusCode.TooManyRequests)).SynthesizeAsync(Req(), default));

        Assert.Single(events);                                // a pre-stream HTTP error -> ONLY TtsFailed (TtsStarted is emitted only after a 200)
        var failed = Assert.IsType<TtsFailed>(events[0]);
        Assert.Equal("tts.rate_limited", failed.Error.Code);
        Assert.True(failed.Error.Retryable);
        Assert.Equal("tts", failed.Error.Stage);
        Assert.Equal("openai", failed.Error.Provider);
    }

    [Fact]
    public async Task auth_error_yields_failed_nonretryable()
    {
        var events = await Collect(Provider(new StubHandler(HttpStatusCode.Forbidden)).SynthesizeAsync(Req(), default));

        var failed = Assert.IsType<TtsFailed>(events[^1]);
        Assert.Equal("tts.auth", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("openai", failed.Error.Provider);
    }

    // === Group 5 — request body shape (voice/format/input + the load-bearing stream_format) ===

    [Fact]
    public async Task request_body_sets_voice_format_input_streamformat()
    {
        var handler = new StubHandler(new byte[] { 1, 2 }, "audio/mpeg", chunkSize: 0);

        await Collect(Provider(handler).SynthesizeAsync(Req(text: "hola mundo"), default));

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("gpt-4o-mini-tts", root.GetProperty("model").GetString());
        Assert.Equal("alloy", root.GetProperty("voice").GetString());
        Assert.Equal("hola mundo", root.GetProperty("input").GetString());
        Assert.Equal("mp3", root.GetProperty("response_format").GetString());
        // Load-bearing: gpt-4o-mini-tts defaults stream_format to "sse" (base64 in SSE); "audio" forces raw chunks.
        Assert.Equal("audio", root.GetProperty("stream_format").GetString());
    }

    // === helpers ===

    private static void AssertChunk(TtsEvent e, int seq, byte[] bytes)
    {
        var chunk = Assert.IsType<TtsAudioChunk>(e);
        Assert.Equal(seq, chunk.Seq);
        Assert.Equal(bytes, chunk.Bytes);
    }

    private static OpenAiTtsProvider Provider(HttpMessageHandler handler, OpenAiTtsOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new OpenAiTtsProvider(http, Options.Create(options ?? new OpenAiTtsOptions()));
    }

    private static TtsRequest Req(string text = "hola mundo", string voice = "alloy", string model = "gpt-4o-mini-tts", string format = "mp3") =>
        new(text, LanguageCode.Es, voice, model, format, null, "session_1", "turn_1");

    private static async Task<List<TtsEvent>> Collect(IAsyncEnumerable<TtsEvent> source)
    {
        var events = new List<TtsEvent>();
        await foreach (var e in source)
        {
            events.Add(e);
        }

        return events;
    }

    // Mock transport: returns a canned BINARY body (optionally chunked to stress chunk emission) with a
    // Content-Type header, or a non-2xx status with a JSON error body; tracks call count + captures the body.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private readonly string? _contentType;
        private readonly int _chunkSize;
        private readonly bool _success;

        public int CallCount { get; private set; }

        public string? CapturedBody { get; private set; }

        public StubHandler(byte[] body, string? contentType, int chunkSize)
        {
            _status = HttpStatusCode.OK;
            _body = body;
            _contentType = contentType;
            _chunkSize = chunkSize;
            _success = true;
        }

        public StubHandler(HttpStatusCode status)
        {
            _status = status;
            _body = Array.Empty<byte>();
            _contentType = null;
            _chunkSize = 0;
            _success = false;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (!_success)
            {
                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"upstream error\",\"type\":\"x\"}}", Encoding.UTF8, "application/json"),
                };
            }

            Stream content = _chunkSize > 0 ? new ChunkStream(_body, _chunkSize) : new MemoryStream(_body);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
            if (_contentType is not null)
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            }

            return response;
        }
    }

    // A read-only stream that releases at most _chunk bytes per read — drives deterministic chunk emission.
    private sealed class ChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;

        public ChunkStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length)
            {
                return 0;
            }

            var n = Math.Min(Math.Min(count, _chunk), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= _data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var n = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
