using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiInterpreter.Api.Common;

/// <summary>
/// The single shared System.Text.Json contract (ARCH-005 / ARCH-009 / ARCH-016): camelCase
/// property names + enums as camelCase strings + default ISO-8601 <see cref="DateTimeOffset"/>
/// (UTC instants serialize with an explicit "+00:00" offset — equivalent to the "Z" shorthand in
/// the doc examples; both round-trip losslessly). Reused by A.5 (ConfigureHttpJsonOptions) and B.7
/// (persistence) via <see cref="Apply"/> so API and persisted JSON cannot diverge.
///
/// Nulls are written EXPLICITLY ("summary": null per the ARCH-016 example) — deliberately NO
/// DefaultIgnoreCondition.WhenWritingNull.
/// </summary>
public static class JsonDefaults
{
    /// <summary>Pre-built options for direct serialization (persistence + tests).</summary>
    public static JsonSerializerOptions Options { get; } = Build();

    /// <summary>Applies the shared contract to an existing options instance (e.g. the ASP.NET HTTP options in A.5).</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        // Idempotent: A.5 may call Apply on framework-supplied options — don't double-register.
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
        {
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions();
        Apply(options);
        return options;
    }
}
