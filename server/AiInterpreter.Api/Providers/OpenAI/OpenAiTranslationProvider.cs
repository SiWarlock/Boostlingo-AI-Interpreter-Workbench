using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Providers.OpenAI;

/// <summary>
/// Real OpenAI translation provider (C.2) behind the unchanged B.1 <see cref="ITranslationProvider"/> seam.
/// Streams the Responses API (<c>POST /v1/responses</c>, <c>stream=true</c>) over a raw <see cref="HttpClient"/>:
/// <c>response.created -> TranslationStarted</c>, each <c>response.output_text.delta -> TranslationPartial</c>
/// (the first delta is the translation.first_token moment), <c>response.completed -> TranslationFinal</c> with
/// the aggregated text + usage tokens. Genuinely streaming — events are yielded as the SSE lines arrive, never
/// buffered-then-emitted (forbidden #3/#4; ARCH-011 "the translation stage must stream").
///
/// All decision logic lives in the pure, unit-tested <see cref="OpenAiTranslationMapping"/>; this is the network
/// TRANSPORT SHELL (the real network call is manual-smoke, but the SSE parse + aggregation are TDD'd end-to-end
/// via a mock HttpMessageHandler). The injected <see cref="HttpClient"/>'s BaseAddress targets the API (C.4 wires
/// the typed client; tests supply a mock-handler-backed client).
/// </summary>
public sealed class OpenAiTranslationProvider : ITranslationProvider
{
    private const string ResponsesPath = "/v1/responses";

    private readonly HttpClient _http;
    private readonly OpenAiTranslationOptions _options;
    private readonly ILogger<OpenAiTranslationProvider> _logger;

    public OpenAiTranslationProvider(
        HttpClient http, IOptions<OpenAiTranslationOptions> options, ILogger<OpenAiTranslationProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<TranslationEvent> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Per-stage timeout (ARCH-012) over the ENTIRE operation — the send AND the SSE read loop — so a
        // stalled stream can't hang past TimeoutSeconds. Linked to the caller's ct: a resulting OCE is a
        // caller-cancel iff ct is set (then it propagates), else it's the timeout firing (-> a failure event).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpResponseMessage? response = null;
        TranslationFailed? sendFailure = null;
        try
        {
            using var message = BuildRequest(request);
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            response.EnsureSuccessStatusCode(); // non-2xx -> status-bearing HttpRequestException -> mapper
        }
        // INTENTIONAL polarity (do NOT invert to !ct.IsCancellationRequested): caller-cancel (ct set)
        // rethrows; a timeout OCE (ct NOT set) is deliberately left UNCAUGHT here so it falls to the generic
        // catch below -> ToFailed -> translation.timeout. Inverting + the generic catch would swallow caller-cancel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // HTTP/network error OR the linked-CTS timeout (OCE, ct not set) -> sanitized failure.
            response?.Dispose();
            sendFailure = OpenAiTranslationMapping.ToFailed(ex, DateTimeOffset.UtcNow);
        }

        if (sendFailure is not null)
        {
            yield return sendFailure;
            yield break;
        }

        var resp = response!; // non-null past the failure check above
        using (resp)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            var aggregate = new StringBuilder();

            while (true)
            {
                string? line;
                TranslationFailed? readFailure = null;
                try
                {
                    line = await reader.ReadLineAsync(linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // caller-cancel propagates; a read timeout (ct NOT set) falls to the catch below -> translation.timeout
                }
                catch (Exception ex)
                {
                    readFailure = OpenAiTranslationMapping.ToFailed(ex, DateTimeOffset.UtcNow);
                    line = null;
                }

                if (readFailure is not null)
                {
                    yield return readFailure;
                    yield break;
                }

                if (line is null)
                {
                    break; // stream end — the Responses API has no [DONE] sentinel
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue; // event: / blank / keep-alive line — ignore
                }

                var payload = line["data:".Length..].Trim();
                if (payload.Length == 0 || payload == "[DONE]")
                {
                    continue; // defensive: the Responses API doesn't send [DONE], but skip it if present
                }

                var parsed = OpenAiTranslationMapping.ParseEvent(payload);
                switch (parsed.Kind)
                {
                    case OpenAiTranslationMapping.SseKind.Created:
                        yield return new TranslationStarted(DateTimeOffset.UtcNow);
                        break;

                    case OpenAiTranslationMapping.SseKind.Delta:
                        var delta = parsed.Delta ?? string.Empty;
                        aggregate.Append(delta);
                        yield return new TranslationPartial(delta, DateTimeOffset.UtcNow);
                        break;

                    case OpenAiTranslationMapping.SseKind.Completed:
                        // 057b — sanitized raw-usage diagnostic at the terminal read so a live smoke reveals
                        // the real usage shape (the cost n/a root cause). Counts + matched-location only —
                        // never the translation text or the key (invariant #1). IsEnabled-guarded so the
                        // shape parse never runs when Debug is off (i.e. in prod) — not an eager arg.
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(
                                "Translation terminal usage shape: {Shape}",
                                OpenAiTranslationMapping.DescribeUsageShape(payload));
                        }

                        yield return new TranslationFinal(
                            aggregate.ToString(), parsed.InputTokens, parsed.OutputTokens, DateTimeOffset.UtcNow);
                        yield break;

                    case OpenAiTranslationMapping.SseKind.ApiError:
                        yield return OpenAiTranslationMapping.ToFailed(
                            new InvalidOperationException("openai stream error"), DateTimeOffset.UtcNow);
                        yield break;

                    default:
                        break; // ignored lifecycle event
                }
            }
        }
    }

    private HttpRequestMessage BuildRequest(TranslationRequest request)
    {
        var json = JsonSerializer.Serialize(
            OpenAiTranslationMapping.BuildRequestBody(request, _options), JsonDefaults.Options);
        var message = new HttpRequestMessage(HttpMethod.Post, ResponsesPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey ?? string.Empty);
        return message;
    }
}
