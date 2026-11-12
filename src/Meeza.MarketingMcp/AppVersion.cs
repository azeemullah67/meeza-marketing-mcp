using System.Reflection;

namespace Meeza.MarketingMcp;

/// <summary>
/// The product version. Its single source is &lt;Version&gt; in Directory.Build.props: the build stamps it into the
/// assembly and everything that reports a version (MCP server info, the HTTP User-Agent) reads it from here.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        // The SDK appends "+<commit>" to the informational version; report the plain version.
        var plain = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        return string.IsNullOrWhiteSpace(plain) ? assembly.GetName().Version?.ToString(3) ?? "0.0.0" : plain;
    }
}
