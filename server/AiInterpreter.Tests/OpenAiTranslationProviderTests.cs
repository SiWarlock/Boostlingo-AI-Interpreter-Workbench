using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// C.2 — OpenAiTranslationProvider deterministic-surface tests (ARCH-011 / ARCH-012 / ARCH-020).
//
// Unlike the Deepgram callback SDK (C.1), OpenAI is raw HttpClient — so the C.1 Q2 fork that was
// infeasible there IS feasible here: a mock HttpMessageHandler returns a canned SSE byte stream and we
// TDD the streaming parse END-TO-END (streaming-honesty, usage extraction, error statuses, request
// shape). This is higher fidelity than C.1's pure-parse-only fallback (lesson §18, extended).
//
// Scope guard (lesson §17): we do NOT re-test the ProviderErrorMapper truth table (B.1) or
// FakeTranslation per-variant behavior (B.2). The error tests pin only that the catch path routes
// through the mapper with the right "openai"/"translation" constants.
public class OpenAiTranslationProviderTests
{
    // === Group 1 — genuine streaming: ordered events + first-token + aggregation ===

    [Fact]
    public async Task streams_started_partials_final_in_order()
    {
        var events = await Collect(Provider(new StubHandler(SseWithUsage)).TranslateAsync(Req(), default));

        Assert.Collection(
            events,
            e => Assert.IsType<TranslationStarted>(e),
            e => Assert.Equal("Hola", Assert.IsType<TranslationPartial>(e).TextDelta),
            e => Assert.Equal(" mundo", Assert.IsType<TranslationPartial>(e).TextDelta),
            e => Assert.Equal("Hola mundo", Assert.IsType<TranslationFinal>(e).Text));
    }

    [Fact]
    public async Task first_delta_is_first_partial()
    {
        // The first output_text.delta -> the first TranslationPartial: the translation.first_token moment
        // the orchestrator stamps (ARCH-011/013). Forbidden #3 — real first arrival, never synthesized.
        var events = await Collect(Provider(new StubHandler(SseWithUsage)).TranslateAsync(Req(), default));

        Assert.IsType<TranslationStarted>(events[0]);
        var firstPartial = Assert.IsType<TranslationPartial>(events[1]);
        Assert.Equal("Hola", firstPartial.TextDelta);
    }

    [Fact]
    public async Task final_aggregates_deltas()
    {
        var events = await Collect(Provider(new StubHandler(SseWithUsage)).TranslateAsync(Req(), default));

        var final = Assert.IsType<TranslationFinal>(events[^1]);
        Assert.Equal("Hola mundo", final.Text); // concatenation of the streamed deltas, not a re-fetch
    }

    // === Group 2 — usage extraction off response.completed ===

    [Fact]
    public async Task usage_tokens_extracted_from_completed()
    {
        var events = await Collect(Provider(new StubHandler(SseWithUsage)).TranslateAsync(Req(), default));

        var final = events.OfType<TranslationFinal>().Single();
        Assert.Equal((int?)12, final.InputTokens);
        Assert.Equal((int?)6, final.OutputTokens);
    }

    [Fact]
    public async Task missing_usage_yields_null_tokens()
    {
        // response.completed without a usage block -> null tokens (B.5 cost estimator degrades; never fabricate).
        var events = await Collect(Provider(new StubHandler(SseNoUsage)).TranslateAsync(Req(), default));

        var final = events.OfType<TranslationFinal>().Single();
        Assert.Null(final.InputTokens);
        Assert.Null(final.OutputTokens);
    }

    // === Group 2b — usage-shape tolerance (057b): tolerate usage nested under `response` (the current
    // OpenAI Responses shape) AND at the event top level (shape variance / the §24 dual-shape precedent).
    // A genuinely-absent usage still degrades to null — honest, never fabricated. ParseEvent is public. ===

    [Fact]
    public void parse_event_reads_usage_nested_under_response()
    {
        var ev = OpenAiTranslationMapping.ParseEvent(
            "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":12,\"output_tokens\":6}}}");

        Assert.Equal(OpenAiTranslationMapping.SseKind.Completed, ev.Kind);
        Assert.Equal((int?)12, ev.InputTokens);
        Assert.Equal((int?)6, ev.OutputTokens);
    }

    [Fact]
    public void parse_event_reads_top_level_usage()
    {
        // Tolerant fallback: usage at the event TOP LEVEL (not nested under `response`).
        var ev = OpenAiTranslationMapping.ParseEvent(
            "{\"type\":\"response.completed\",\"usage\":{\"input_tokens\":7,\"output_tokens\":3}}");

        Assert.Equal((int?)7, ev.InputTokens);
        Assert.Equal((int?)3, ev.OutputTokens);
    }

    [Fact]
    public void parse_event_absent_usage_yields_null_tokens()
    {
        var ev = OpenAiTranslationMapping.ParseEvent("{\"type\":\"response.completed\",\"response\":{}}");

        Assert.Equal(OpenAiTranslationMapping.SseKind.Completed, ev.Kind);
        Assert.Null(ev.InputTokens);
        Assert.Null(ev.OutputTokens);
    }

    [Fact]
    public async Task usage_tokens_extracted_from_top_level_usage_shape()
    {
        var events = await Collect(Provider(new StubHandler(SseTopLevelUsage)).TranslateAsync(Req(), default));

        var final = events.OfType<TranslationFinal>().Single();
        Assert.Equal((int?)7, final.InputTokens);
        Assert.Equal((int?)3, final.OutputTokens);
    }

    [Fact]
    public void describe_usage_shape_is_sanitized_and_single_lined()
    {
        // Usage present -> the usage sub-object (token COUNTS) + matched location, nothing else.
        Assert.Equal(
            "response.usage={\"input_tokens\":5,\"output_tokens\":2}",
            OpenAiTranslationMapping.DescribeUsageShape(
                "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}"));

        // Usage absent -> TOP-LEVEL key NAMES only: the nested translation text is NOT a top-level key so it
        // never appears, and a newline in a crafted key name is single-lined (lesson §13 log-forge guard).
        var absent = OpenAiTranslationMapping.DescribeUsageShape(
            "{\"type\":\"response.completed\",\"response\":{\"output\":\"secret translation\"},\"in\\njected\":1}");
        Assert.StartsWith("usage absent; top-level keys=[", absent);
        Assert.DoesNotContain("secret translation", absent); // nested value never logged (invariant #1)
        Assert.DoesNotContain("\n", absent);                  // single-lined (no log forging)
    }

    // === Group 3 — failures: HTTP error (status-bearing) + mid-stream in-band SSE error ===

    [Fact]
    public async Task rate_limited_yields_failed_retryable()
    {
        var events = await Collect(Provider(new StubHandler(HttpStatusCode.TooManyRequests)).TranslateAsync(Req(), default));

        var failed = Assert.IsType<TranslationFailed>(events[^1]);
        Assert.Equal("translation.rate_limited", failed.Error.Code);
        Assert.True(failed.Error.Retryable);
        Assert.Equal("translation", failed.Error.Stage);
        Assert.Equal("openai", failed.Error.Provider);
        Assert.DoesNotContain(events, e => e is TranslationFinal);
    }

    [Fact]
    public async Task auth_error_yields_failed_nonretryable()
    {
        var events = await Collect(Provider(new StubHandler(HttpStatusCode.Forbidden)).TranslateAsync(Req(), default));

        var failed = Assert.IsType<TranslationFailed>(events[^1]);
        Assert.Equal("translation.auth", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("openai", failed.Error.Provider);
    }

    [Fact]
    public async Task mid_stream_error_event_yields_failed_no_final()
    {
        // After a 200 + some deltas, an in-band SSE `error` event is a distinct failure mode from an HTTP
        // status (the stream already started). It terminates as TranslationFailed (translation.unknown — we
        // do NOT mine the non-contractual error payload) with no TranslationFinal.
        var events = await Collect(Provider(new StubHandler(SseMidStreamError)).TranslateAsync(Req(), default));

        var failed = Assert.IsType<TranslationFailed>(events[^1]);
        Assert.Equal("translation.unknown", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("translation", failed.Error.Stage);
        Assert.Equal("openai", failed.Error.Provider);
        Assert.DoesNotContain(events, e => e is TranslationFinal);
    }

    // === Group 4 — request body shape (latency params + interpreter prompt + streaming) ===

    [Fact]
    public async Task request_body_sets_streaming_and_interpreter_prompt()
    {
        var handler = new StubHandler(SseWithUsage);

        await Collect(Provider(handler).TranslateAsync(Req("hello world"), default));

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        // Responses API nests these (NOT top-level reasoning_effort, which is Chat-Completions only).
        Assert.Equal("minimal", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", root.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("gpt-5-nano", root.GetProperty("model").GetString());
        Assert.Equal("hello world", root.GetProperty("input").GetString());
        var instructions = root.GetProperty("instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("ONLY", instructions, StringComparison.OrdinalIgnoreCase); // faithful-interpreter: output only the translation
        Assert.Contains("English", instructions);
        Assert.Contains("Spanish", instructions);
    }

    // === Group 5 — SSE framing robustness: a line split across reads reassembles (Q3) ===

    [Fact]
    public async Task delta_split_across_reads_reassembles()
    {
        // chunkSize:1 forces byte-by-byte reads -> proves StreamReader line reassembly (no hand-rolled
        // buffering that would corrupt a delta split across reads). Streaming-honesty robustness.
        var events = await Collect(Provider(new StubHandler(SseWithUsage, chunkSize: 1)).TranslateAsync(Req(), default));

        Assert.Collection(
            events,
            e => Assert.IsType<TranslationStarted>(e),
            e => Assert.Equal("Hola", Assert.IsType<TranslationPartial>(e).TextDelta),
            e => Assert.Equal(" mundo", Assert.IsType<TranslationPartial>(e).TextDelta),
            e => Assert.Equal("Hola mundo", Assert.IsType<TranslationFinal>(e).Text));
    }

    // === fixtures + helpers ===

    private static string SseEvent(string type, string dataJson) => $"event: {type}\ndata: {dataJson}\n\n";

    private static readonly string SseWithUsage =
        SseEvent("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\"Hola\"}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\" mundo\"}") +
        SseEvent("response.completed", "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":12,\"output_tokens\":6,\"total_tokens\":18}}}");

    private static readonly string SseNoUsage =
        SseEvent("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\"Hola\"}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\" mundo\"}") +
        SseEvent("response.completed", "{\"type\":\"response.completed\",\"response\":{}}");

    private static readonly string SseTopLevelUsage =
        SseEvent("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\"Hola\"}") +
        SseEvent("response.completed", "{\"type\":\"response.completed\",\"usage\":{\"input_tokens\":7,\"output_tokens\":3}}");

    private static readonly string SseMidStreamError =
        SseEvent("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}") +
        SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\"Hola\"}") +
        SseEvent("error", "{\"type\":\"error\",\"code\":\"server_error\",\"message\":\"upstream blew up\"}");

    private static OpenAiTranslationProvider Provider(HttpMessageHandler handler, OpenAiTranslationOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new OpenAiTranslationProvider(
            http, Options.Create(options ?? new OpenAiTranslationOptions()),
            NullLogger<OpenAiTranslationProvider>.Instance);
    }

    private static TranslationRequest Req(string text = "hello world", string model = "gpt-5-nano") =>
        new(text, LanguageCode.En, LanguageCode.Es, model, "session_1", "turn_1");

    private static async Task<List<TranslationEvent>> Collect(IAsyncEnumerable<TranslationEvent> source)
    {
        var events = new List<TranslationEvent>();
        await foreach (var e in source)
        {
            events.Add(e);
        }

        return events;
    }

    // Mock transport: returns a canned SSE byte stream (optionally chunked to stress line reassembly),
    // or a non-2xx status with a JSON error body; captures the outgoing request body for assertion.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly bool _sse;
        private readonly int _chunkSize;

        public string? CapturedBody { get; private set; }

        public StubHandler(string sseBody, int chunkSize = 0)
        {
            _status = HttpStatusCode.OK;
            _body = sseBody;
            _sse = true;
            _chunkSize = chunkSize;
        }

        public StubHandler(HttpStatusCode status)
        {
            _status = status;
            _body = "{\"error\":{\"message\":\"upstream error\",\"type\":\"x\"}}";
            _sse = false;
            _chunkSize = 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (!_sse)
            {
                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                };
            }

            var bytes = Encoding.UTF8.GetBytes(_body);
            Stream content = _chunkSize > 0 ? new ChunkStream(bytes, _chunkSize) : new MemoryStream(bytes);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
    }

    // A read-only stream that releases at most _chunk bytes per read — simulates a delta split across reads.
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
