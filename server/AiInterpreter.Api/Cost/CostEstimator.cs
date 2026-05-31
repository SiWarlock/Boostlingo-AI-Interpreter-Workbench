using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cost;

/// <summary>
/// Estimates per-turn cost (ARCH-014) by branching on the configured pricing basis (audio-minute /
/// tokens / audio-output-tokens / characters), converting realtime audio-seconds → tokens before
/// applying token rates, and aggregating a cascade turn into one composite <see cref="CostEstimate"/>.
/// Computed in <c>decimal</c> with no rounding (the UI formats for display).
///
/// <para>Degrade-don't-crash (lesson §3, ARCH-018): missing/unavailable pricing — the loader failed,
/// or the model/basis/rate is absent from config — returns <c>Result.Failure</c> ("estimate
/// unavailable"), never a crash or a partial-garbage number. A <b>0.0</b> configured rate is
/// <i>present</i> and estimates to 0.0 (a known build-time-confirm), NOT "unavailable".</para>
///
/// Every estimate is clearly labelled an estimate (not billing-grade) via <c>Assumptions</c>.
/// </summary>
public sealed class CostEstimator(Result<PricingOptions> pricing)
{
    /// <summary>
    /// Realtime audio-second → token conversion factor. This is an <b>ESTIMATE</b> — pricing.json
    /// carries no explicit factor (its estimatorNote flags "CONFIRM at build"). On the build-time-
    /// confirm list (ARCH-027 / Decisions tabled). Tests pin the formula via this const, not a literal.
    /// </summary>
    public const decimal RealtimeTokensPerAudioSecond = 50m;

    private const decimal Million = 1_000_000m;
    private const string AssumptionBase = "Estimate from configured public pricing, not provider invoice data.";
    private const string RealtimeFactorAssumption =
        "Realtime audio seconds converted to tokens at an estimated 50 tokens/sec — confirm at build.";

    public Result<CostEstimate> EstimateStt(CostUsage usage, long audioDurationMs = 0)
    {
        if (!pricing.IsSuccess)
        {
            return Unavailable("pricing config not loaded");
        }

        var stt = pricing.Value.Providers?.Deepgram?.Stt;
        if (stt?.EffectiveUsdPerAudioMinute is not { } rate)
        {
            return Unavailable("Deepgram STT rate absent from pricing config");
        }

        if (usage.AudioMinutes is not { } minutes)
        {
            return Unavailable("STT audio minutes not supplied");
        }

        var usd = minutes * rate;
        var units = new Dictionary<string, decimal> { ["audioMinutes"] = minutes };
        return Build("deepgram", stt.Model ?? "nova-3", "usd_per_audio_minute", usd, audioDurationMs, units, [AssumptionBase]);
    }

    public Result<CostEstimate> EstimateTranslation(string model, CostUsage usage, long audioDurationMs = 0)
    {
        if (!pricing.IsSuccess)
        {
            return Unavailable("pricing config not loaded");
        }

        var rates = pricing.Value.Providers?.Openai?.Translation?.GetValueOrDefault(model);
        if (rates?.InputUsdPerMillionTokens is not { } inputRate || rates.OutputUsdPerMillionTokens is not { } outputRate)
        {
            return Unavailable($"translation rates absent for model '{model}'");
        }

        var inputTokens = usage.InputTokens ?? 0;
        var outputTokens = usage.OutputTokens ?? 0;
        var usd = inputTokens / Million * inputRate + outputTokens / Million * outputRate;
        var units = new Dictionary<string, decimal> { ["inputTokens"] = inputTokens, ["outputTokens"] = outputTokens };
        return Build("openai", model, "tokens", usd, audioDurationMs, units, [AssumptionBase]);
    }

    public Result<CostEstimate> EstimateTts(string model, CostUsage usage, long audioDurationMs = 0)
    {
        if (!pricing.IsSuccess)
        {
            return Unavailable("pricing config not loaded");
        }

        var rates = pricing.Value.Providers?.Openai?.Tts?.GetValueOrDefault(model);
        if (rates is null)
        {
            return Unavailable($"TTS rates absent for model '{model}'");
        }

        return rates.PricingBasis switch
        {
            "audio_output_tokens" => EstimateTtsAudioOutputTokens(model, rates, usage, audioDurationMs),
            "characters" => EstimateTtsCharacters(model, rates, usage, audioDurationMs),
            _ => Unavailable($"unsupported TTS pricing basis for model '{model}'"),
        };
    }

    private Result<CostEstimate> EstimateTtsAudioOutputTokens(string model, TtsModelRates rates, CostUsage usage, long audioDurationMs)
    {
        // Prefer real audio-output token counts; fall back to approxUsdPerAudioMinute × minutes when
        // the provider streamed audio without a token count (Q4) — disclosed in Assumptions.
        if (usage.AudioOutputTokens is { } audioOutputTokens)
        {
            if (rates.AudioOutputUsdPerMillionTokens is not { } audioRate)
            {
                return Unavailable($"TTS audio-output rate absent for model '{model}'");
            }

            var usd = audioOutputTokens / Million * audioRate;
            var units = new Dictionary<string, decimal> { ["audioOutputTokens"] = audioOutputTokens };
            if (usage.TextInputTokens is { } textInputTokens && rates.TextInputUsdPerMillionTokens is { } textRate)
            {
                usd += textInputTokens / Million * textRate;
                units["textInputTokens"] = textInputTokens;
            }

            return Build("openai", model, "audio_output_tokens", usd, audioDurationMs, units, [AssumptionBase]);
        }

        if (usage.AudioMinutes is { } minutes && rates.ApproxUsdPerAudioMinute is { } approx)
        {
            var usd = minutes * approx;
            var units = new Dictionary<string, decimal> { ["audioMinutes"] = minutes };
            string[] assumptions =
            [
                AssumptionBase,
                "TTS audio-output token count unavailable from the speech API; cost ESTIMATED from approxUsdPerAudioMinute × an estimated audio-output duration.",
            ];
            return Build("openai", model, "audio_output_tokens", usd, audioDurationMs, units, assumptions);
        }

        return Unavailable($"TTS audio-output tokens and approx-minute fallback both unavailable for model '{model}'");
    }

    private Result<CostEstimate> EstimateTtsCharacters(string model, TtsModelRates rates, CostUsage usage, long audioDurationMs)
    {
        if (rates.UsdPerMillionCharacters is not { } charRate)
        {
            return Unavailable($"TTS character rate absent for model '{model}'");
        }

        if (usage.Characters is not { } characters)
        {
            return Unavailable("TTS character count not supplied");
        }

        var usd = characters / Million * charRate;
        var units = new Dictionary<string, decimal> { ["characters"] = characters };
        return Build("openai", model, "characters", usd, audioDurationMs, units, [AssumptionBase]);
    }

    public Result<CostEstimate> EstimateRealtime(string model, CostUsage usage, long audioDurationMs = 0)
    {
        if (!pricing.IsSuccess)
        {
            return Unavailable("pricing config not loaded");
        }

        var realtime = pricing.Value.Providers?.Openai?.Realtime;
        var rates = model switch
        {
            "gpt-realtime" => realtime?.GptRealtime,
            "gpt-realtime-mini" => realtime?.GptRealtimeMini,
            _ => null,
        };

        if (rates?.AudioInputUsdPerMillionTokens is not { } inputRate || rates.AudioOutputUsdPerMillionTokens is not { } outputRate)
        {
            return Unavailable($"realtime rates absent for model '{model}'");
        }

        // 053-C2a — exact-count path: when the DC's audio-token counts are supplied, price directly from them
        // at the per-million audio rates (no seconds × 50 estimate). Absent → the legacy seconds estimate below.
        if (usage.AudioInputTokens is not null || usage.AudioOutputTokens is not null)
        {
            return EstimateRealtimeFromTokens(model, usage, rates, inputRate, outputRate, audioDurationMs);
        }

        var inputSeconds = usage.AudioInputSeconds ?? 0m;
        var outputSeconds = usage.AudioOutputSeconds ?? 0m;
        var cachedSeconds = usage.CachedAudioInputSeconds ?? 0m;

        var usd = inputSeconds * RealtimeTokensPerAudioSecond / Million * inputRate
            + outputSeconds * RealtimeTokensPerAudioSecond / Million * outputRate;

        var assumptions = new List<string> { AssumptionBase, RealtimeFactorAssumption };
        if (cachedSeconds > 0)
        {
            // Cached-input seconds billed at the cached rate when configured; else fall back to the
            // full input rate (gpt-realtime-mini has no cached rate) — disclosed, never dropped.
            if (rates.CachedAudioInputUsdPerMillionTokens is { } cachedRate)
            {
                usd += cachedSeconds * RealtimeTokensPerAudioSecond / Million * cachedRate;
            }
            else
            {
                usd += cachedSeconds * RealtimeTokensPerAudioSecond / Million * inputRate;
                assumptions.Add("No cached-input rate configured; cached seconds billed at the full input rate.");
            }
        }

        var units = new Dictionary<string, decimal>
        {
            ["audioInputSeconds"] = inputSeconds,
            ["audioOutputSeconds"] = outputSeconds,
            ["cachedAudioInputSeconds"] = cachedSeconds,
            ["tokensPerAudioSecond"] = RealtimeTokensPerAudioSecond,
        };
        return Build("openai", model, "tokens", usd, audioDurationMs, units, [.. assumptions]);
    }

    // 053-C2a — exact-count realtime pricing from the DC's response.done.usage audio-token counts. Same basis
    // ("tokens" at audio rates), exact counting (no seconds × factor). Text tokens are disclosed-unpriced (no
    // text rates configured). Cached tokens use the cached rate when configured, else the full input rate
    // (over-estimates slightly — the honest direction, never under-counts).
    private Result<CostEstimate> EstimateRealtimeFromTokens(
        string model, CostUsage usage, RealtimeModelRates rates, decimal inputRate, decimal outputRate, long audioDurationMs)
    {
        var inputTokens = usage.AudioInputTokens ?? 0;
        var outputTokens = usage.AudioOutputTokens ?? 0;
        var cachedTokens = usage.CachedAudioInputTokens ?? 0;

        var usd = inputTokens / Million * inputRate + outputTokens / Million * outputRate;

        var assumptions = new List<string>
        {
            AssumptionBase,
            "Realtime priced from exact audio-token counts (response.done.usage); text tokens are not priced.",
        };
        if (cachedTokens > 0)
        {
            if (rates.CachedAudioInputUsdPerMillionTokens is { } cachedRate)
            {
                usd += cachedTokens / Million * cachedRate;
            }
            else
            {
                usd += cachedTokens / Million * inputRate;
                assumptions.Add("No cached-input rate configured; cached tokens billed at the full input rate.");
            }
        }

        var units = new Dictionary<string, decimal>
        {
            ["audioInputTokens"] = inputTokens,
            ["audioOutputTokens"] = outputTokens,
            ["cachedAudioInputTokens"] = cachedTokens,
        };
        return Build("openai", model, "tokens", usd, audioDurationMs, units, [.. assumptions]);
    }

    public Result<CostEstimate> EstimateCascadeTurn(
        string translationModel,
        string ttsModel,
        CostUsage sttUsage,
        CostUsage translationUsage,
        CostUsage ttsUsage,
        long audioDurationMs = 0)
    {
        var stt = EstimateStt(sttUsage, audioDurationMs);
        var translation = EstimateTranslation(translationModel, translationUsage, audioDurationMs);
        var tts = EstimateTts(ttsModel, ttsUsage, audioDurationMs);

        // A composite is meaningful only if every stage priced; otherwise degrade wholesale.
        if (!stt.IsSuccess || !translation.IsSuccess || !tts.IsSuccess)
        {
            return Unavailable("one or more cascade stages could not be priced");
        }

        var usd = stt.Value.EstimatedUsd + translation.Value.EstimatedUsd + tts.Value.EstimatedUsd;

        var units = new Dictionary<string, decimal>
        {
            ["sttUsd"] = stt.Value.EstimatedUsd,
            ["translationUsd"] = translation.Value.EstimatedUsd,
            ["ttsUsd"] = tts.Value.EstimatedUsd,
        };
        MergeUnits(units, "stt", stt.Value.Units);
        MergeUnits(units, "translation", translation.Value.Units);
        MergeUnits(units, "tts", tts.Value.Units);

        var assumptions = new List<string> { AssumptionBase };
        assumptions.Add($"Composite cascade cost: STT ({stt.Value.PricingBasis}) + translation ({translation.Value.PricingBasis}) + TTS ({tts.Value.PricingBasis}).");

        // 069 — carry each stage's METHOD disclosures into the composite (e.g. the TTS approx-minute ESTIMATE
        // when audio-output tokens aren't reported) so the cascade-ESTIMATED vs realtime-EXACT (059) asymmetry
        // is honest IN THE DATA (G.5). Accurate by construction: a stage adds its estimate disclosure only when
        // it actually estimated (an exact char-basis TTS adds none). Dedup the shared base line.
        foreach (var stage in new[] { stt.Value, translation.Value, tts.Value })
        {
            foreach (var assumption in stage.Assumptions)
            {
                if (assumption != AssumptionBase && !assumptions.Contains(assumption))
                {
                    assumptions.Add(assumption);
                }
            }
        }

        // Model = the translation model (the cascade comparison axis per ARCH-014) so B.7 can group
        // cascade cost by translation-model variant.
        return Build("cascade", translationModel, "composite", usd, audioDurationMs, units, [.. assumptions]);
    }

    private static void MergeUnits(Dictionary<string, decimal> target, string stagePrefix, Dictionary<string, decimal> stageUnits)
    {
        foreach (var (key, value) in stageUnits)
        {
            target[$"{stagePrefix}.{key}"] = value;
        }
    }

    private Result<CostEstimate> Build(
        string provider, string model, string basis, decimal usd, long audioDurationMs,
        Dictionary<string, decimal> units, string[] assumptions)
    {
        var version = pricing.IsSuccess ? pricing.Value.Version ?? "unknown" : "unknown";
        var perMinute = audioDurationMs > 0 ? usd / (audioDurationMs / 60000m) : (decimal?)null;
        return Result<CostEstimate>.Success(
            new CostEstimate(provider, model, basis, usd, perMinute, units, version, assumptions));
    }

    private static Result<CostEstimate> Unavailable(string why) =>
        Result<CostEstimate>.Failure($"Estimate unavailable: {why}");
}
