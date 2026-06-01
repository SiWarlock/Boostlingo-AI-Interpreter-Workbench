using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Realtime;

/// <summary>
/// The ephemeral-credential broker (E.1, SAFETY): mints a short-lived OpenAI Realtime client secret
/// (<c>ek_…</c>) by calling the GA <c>POST https://api.openai.com/v1/realtime/client_secrets</c> with the
/// standard key server-side, returning ONLY the ephemeral secret + expiry + model to the caller.
///
/// <para>Invariants: the standard key (<see cref="RealtimeOptions.ApiKey"/>) is Bearer-only and NEVER
/// crosses the response boundary (#1); the minted <c>ek_…</c> is response-only — this service holds NO
/// <c>SessionStore</c>/<c>SessionPersistenceWriter</c> dependency (so it structurally cannot persist) and
/// does not log the secret (#2).</para>
///
/// <para>The thin transport shell (lesson §18/§20 applied to a non-streaming upstream call): all decision
/// logic lives in the pure <see cref="RealtimeClientSecretMapping"/>. The model is resolved + allow-listed
/// and a missing key short-circuits — both fail closed BEFORE any upstream call. One bounded 429 retry
/// honors <c>Retry-After</c>; the delay is injectable (<paramref name="delay"/>) so the suite never sleeps
/// on the wall clock (lesson §6). A linked-CTS timeout covers send + read (lesson §20).</para>
/// </summary>
public sealed class RealtimeClientSecretService
{
    private const string ClientSecretsPath = "/v1/realtime/client_secrets";
    private const string SafetyIdentifier = "ai-interpreter-workbench";
    private const string Provider = "openai";
    private const string Stage = "realtime";

    private readonly HttpClient _http;
    private readonly RealtimeOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RealtimeClientSecretService(
        HttpClient http,
        IOptions<RealtimeOptions> options,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _options = options.Value;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Mints an ephemeral credential or returns a normalized, already-safe failure outcome.</summary>
    public async Task<RealtimeMintOutcome> MintAsync(RealtimeTokenRequest request, CancellationToken ct)
    {
        // Resolve + allow-list the model — fail closed BEFORE any upstream call (Q4).
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model!;
        if (!RealtimeModelCatalog.Models.Contains(model))
        {
            return RealtimeMintOutcome.Fail(ProviderErrorMapper.MapStatus(400, Provider, Stage));
        }

        // Missing standard key → fail closed, no doomed upstream call (Q5; capability-from-key-presence §15).
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return RealtimeMintOutcome.Fail(ProviderErrorMapper.MapStatus(401, Provider, Stage));
        }

        // Per-stage timeout (ARCH-012) over the send AND the body read (lesson §20). Linked to the caller's
        // ct: a resulting OCE is a caller-cancel iff ct is set (rethrow), else the timeout firing (→ failure).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.TokenTimeoutSeconds));

        try
        {
            using var response = await SendWithRetryAsync(request.Direction, model, request.Bidirectional, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                return RealtimeMintOutcome.Fail(ProviderErrorMapper.MapStatus((int)response.StatusCode, Provider, Stage));
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token);
            var secret = RealtimeClientSecretMapping.ParseResponse(body);
            if (secret is null)
            {
                return RealtimeMintOutcome.Fail(ProviderErrorMapper.Unknown(Provider, Stage));
            }

            var dto = new RealtimeTokenResponse(
                secret.Value.Value,
                RealtimeClientSecretMapping.ToIso8601(secret.Value.ExpiresAtEpoch),
                model);
            return RealtimeMintOutcome.Ok(dto);
        }
        // INTENTIONAL polarity (lesson §20): caller-cancel (ct set) rethrows; a timeout OCE (ct NOT set) is
        // left uncaught here so it falls to the generic catch → ToFailed → realtime.timeout. Inverting +
        // the trailing generic catch would swallow caller-cancel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // HTTP/network error OR the linked-CTS timeout (OCE, ct not set) → sanitized failure. The
            // exception NEVER reaches the caller; ToFailed produces a fixed-message ProviderError (#1).
            return RealtimeMintOutcome.Fail(RealtimeClientSecretMapping.ToFailed(ex));
        }
    }

    // Sends the mint request; on a 429 performs exactly ONE bounded retry honoring Retry-After (Q3). A fresh
    // HttpRequestMessage per send (a message can't be reused). 5xx/network/other statuses are NOT retried —
    // they fall through to the status mapper / generic catch.
    private async Task<HttpResponseMessage> SendWithRetryAsync(LanguageDirection direction, string model, bool bidirectional, CancellationToken linkedCt)
    {
        // The caller (MintAsync) owns disposal of the RETURNED response via its `using`; on the 429 branch we
        // dispose the first response here before issuing the single retry. `linkedCt` is the LINKED token
        // (caller-ct + timeout), not the raw caller ct — don't inspect it to split cancel-vs-timeout here.
        var response = await _http.SendAsync(BuildMessage(direction, model, bidirectional), linkedCt);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var wait = ResolveRetryDelay(response);
        response.Dispose();
        await _delay(wait, linkedCt);
        return await _http.SendAsync(BuildMessage(direction, model, bidirectional), linkedCt);
    }

    // Retry-After as integer seconds (the form OpenAI sends), capped at the token timeout so a hostile/huge
    // value can't park the request. Absent/unparseable → a small fixed fallback backoff (also capped).
    private TimeSpan ResolveRetryDelay(HttpResponseMessage response)
    {
        var cap = TimeSpan.FromSeconds(_options.TokenTimeoutSeconds);
        if (response.Headers.TryGetValues("Retry-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            var requested = TimeSpan.FromSeconds(seconds);
            return requested < cap ? requested : cap;
        }

        var fallback = TimeSpan.FromMilliseconds(500);
        return fallback < cap ? fallback : cap;
    }

    private HttpRequestMessage BuildMessage(LanguageDirection direction, string model, bool bidirectional)
    {
        var json = JsonSerializer.Serialize(
            RealtimeClientSecretMapping.BuildRequestBody(direction, model, _options, bidirectional), JsonDefaults.Options);
        var message = new HttpRequestMessage(HttpMethod.Post, ClientSecretsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // The standard key is Bearer-only (invariant #1) — never serialized into a body or a response.
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        // Abuse-tracking id (not a secret) — a fixed server-side constant (Q6).
        message.Headers.Add("OpenAI-Safety-Identifier", SafetyIdentifier);
        return message;
    }
}
