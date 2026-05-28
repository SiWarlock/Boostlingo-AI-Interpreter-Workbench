namespace AiInterpreter.Api.Cost;

/// <summary>
/// Pricing configuration — MINIMAL binding target for A.2 (just <see cref="Version"/>). A.4 extends
/// this to the full ARCH-014 shape (per-provider/per-model bases) bound from <c>config/pricing.json</c>
/// via <c>PRICING_CONFIG_PATH</c>, with missing-config → "estimate unavailable" degradation (ARCH-018).
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    public string? Version { get; set; }
}
