using System.Security;
using System.Text.Json;
using AiInterpreter.Api.Common;

namespace AiInterpreter.Api.Cost;

/// <summary>
/// Loads pricing.json (from PRICING_CONFIG_PATH) and deserializes it via the shared
/// <c>JsonDefaults.Options</c>. Per ARCH-018 it DEGRADES — never throws — on missing, unreadable,
/// or invalid config: it returns <c>Result&lt;PricingOptions&gt;.Failure</c> so the caller (B.5)
/// renders "estimate unavailable". DI wiring of the path is A.5.
/// </summary>
public static class PricingLoader
{
    public static Result<PricingOptions> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Result<PricingOptions>.Failure($"pricing config not found: {path}");
        }

        try
        {
            // Guard against a misconfigured path to a huge artifact — read a tiny config, never
            // risk an OutOfMemoryException (which must not be swallowed) in ReadAllText.
            const long maxBytes = 1024 * 1024; // 1 MB; pricing.json is ~1 KB.
            var length = new FileInfo(path).Length;
            if (length > maxBytes)
            {
                return Result<PricingOptions>.Failure(
                    $"pricing config too large ({length} bytes, max {maxBytes}): {path}");
            }

            var json = File.ReadAllText(path);
            var options = JsonSerializer.Deserialize<PricingOptions>(json, JsonDefaults.Options);

            if (options is null || options.Version is null || options.Providers is null)
            {
                return Result<PricingOptions>.Failure($"pricing config invalid or empty: {path}");
            }

            return Result<PricingOptions>.Success(options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            return Result<PricingOptions>.Failure($"pricing config unreadable/invalid ({path}): {ex.Message}");
        }
    }
}
