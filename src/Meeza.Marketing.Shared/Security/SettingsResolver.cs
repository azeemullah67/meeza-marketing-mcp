using Microsoft.Extensions.Configuration;

namespace Meeza.Marketing.Shared.Security;

public interface ISettingsResolver
{
    /// <summary>Returns the value for <paramref name="key"/> or null when not set.</summary>
    string? Get(string key);

    /// <summary>Where a value came from ("environment", "secret-store", "appsettings") or null when unset. Never returns the value.</summary>
    string? SourceOf(string key);
}

/// <summary>
/// Resolves settings in priority order: environment variable → encrypted local secret store →
/// appsettings.json ("Settings" section, non-secret keys only).
/// </summary>
public sealed class SettingsResolver(IConfiguration configuration, LocalSecretStore secretStore) : ISettingsResolver
{
    public string? Get(string key) => Resolve(key).Value;

    public string? SourceOf(string key) => Resolve(key).Source;

    private (string? Value, string? Source) Resolve(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), "environment");

        var stored = secretStore.Get(key);
        if (!string.IsNullOrWhiteSpace(stored)) return (stored.Trim(), "secret-store");

        if (!SettingKeys.IsSecret(key))
        {
            var cfg = configuration[$"Settings:{key}"];
            if (!string.IsNullOrWhiteSpace(cfg)) return (cfg.Trim(), "appsettings");
        }

        return (null, null);
    }

    /// <summary>Splits a comma/semicolon/whitespace separated list setting.</summary>
    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
