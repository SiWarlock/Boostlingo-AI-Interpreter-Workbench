using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Providers.OpenAI;

/// <summary>
/// Real OpenAI TTS provider (C.3) behind the unchanged B.1 <see cref="ITtsProvider"/> seam. Chunk-transfer
/// streams `POST /v1/audio/speech` (<c>stream_format="audio"</c>) over a raw <see cref="HttpClient"/>:
/// <c>TtsStarted</c> (after 200) → <c>TtsFirstAudio</c> (on the genuine first audio chunk — never a relabeled
/// completion) → <c>TtsAudioChunk(bytes, seq)*</c> → <c>TtsComplete</c>. Reads raw byte buffers (no SSE parse);
/// <c>ContentType</c> comes from the response header. A 4096-char input over-cap fails fast WITHOUT an HTTP call.
///
/// Decision logic lives in the pure, unit-TDD'd <see cref="OpenAiTtsMapping"/>; this is the transport shell
/// (real network = manual-smoke, but the chunk loop + ordering are TDD'd end-to-end via a mock handler). Reuses
/// the §20 raw-HttpClient pattern: a per-stage timeout covers the whole send + read loop; the OCE filter is
/// <c>when (ct.IsCancellationRequested) { throw; }</c> + generic-arm-maps-timeout (NOT inverted).
/// </summary>
public sealed class OpenAiTtsProvider : ITtsProvider
{
    private const string SpeechPath = "/v1/audio/speech";
    private const int ReadBufferSize = 8192;

    private readonly HttpClient _http;
    private readonly OpenAiTtsOptions _options;

    public OpenAiTtsProvider(HttpClient http, IOptions<OpenAiTtsOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Pre-call 4096-char input cap (ARCH-011) — fail fast, no HTTP request.
        if (request.Text.Length > OpenAiTtsMapping.MaxInputChars)
        {
            yield return OpenAiTtsMapping.CapExceeded(DateTimeOffset.UtcNow);
            yield break;
        }

        // Per-stage timeout (ARCH-012) over the whole send + chunk-read loop (§20).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpResponseMessage? response = null;
        TtsFailed? sendFailure = null;
        try
        {
            using var message = BuildRequest(request);
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            response.EnsureSuccessStatusCode(); // non-2xx -> status-bearing HttpRequestException -> mapper
        }
        // INTENTIONAL polarity (§20; do NOT invert): caller-cancel (ct set) rethrows; a timeout OCE (ct NOT set)
        // is left uncaught here so it falls to the generic catch -> ToFailed -> tts.timeout. Inverting + the
        // generic catch would swallow caller-cancel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            sendFailure = OpenAiTtsMapping.ToFailed(ex, DateTimeOffset.UtcNow);
        }

        if (sendFailure is not null)
        {
            yield return sendFailure;
            yield break;
        }

        var resp = response!; // non-null past the failure check above
        using (resp)
        {
            var contentType = OpenAiTtsMapping.ResolveContentType(
                resp.Content.Headers.ContentType?.MediaType, request.ResponseFormat);

            yield return new TtsStarted(DateTimeOffset.UtcNow);

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[ReadBufferSize];
            var seq = 0;
            var firstEmitted = false;

            while (true)
            {
                int read;
                TtsFailed? readFailure = null;
                try
                {
                    read = await stream.ReadAsync(buffer, linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // caller-cancel propagates; a read timeout (ct NOT set) falls to the catch below -> tts.timeout
                }
                catch (Exception ex)
                {
                    readFailure = OpenAiTtsMapping.ToFailed(ex, DateTimeOffset.UtcNow);
                    read = 0;
                }

                if (readFailure is not null)
                {
                    yield return readFailure;
                    yield break;
                }

                if (read == 0)
                {
                    break; // EOF — end of the audio stream
                }

                if (!firstEmitted)
                {
                    yield return new TtsFirstAudio(contentType, DateTimeOffset.UtcNow); // the genuine first chunk
                    firstEmitted = true;
                }

                yield return new TtsAudioChunk(buffer[..read], seq++, DateTimeOffset.UtcNow);
            }

            yield return new TtsComplete(contentType, DateTimeOffset.UtcNow);
        }
    }

    private HttpRequestMessage BuildRequest(TtsRequest request)
    {
        var json = JsonSerializer.Serialize(OpenAiTtsMapping.BuildRequestBody(request, _options), JsonDefaults.Options);
        var message = new HttpRequestMessage(HttpMethod.Post, SpeechPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey ?? string.Empty);
        return message;
    }
}
