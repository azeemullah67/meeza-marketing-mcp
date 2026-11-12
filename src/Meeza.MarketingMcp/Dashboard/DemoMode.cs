using Meeza.Marketing.Shared.Security;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// True for <c>dashboard --demo</c>. A demo process runs on a throwaway database that holds one invented project and
/// resolves no settings at all, so it cannot show the user's real projects, read a credential or contact a provider.
/// Fixed for the life of the process.
/// </summary>
public sealed record DemoMode(bool Enabled)
{
    public const string Notice =
        "Demo mode shows invented data and does not use or save credentials. Start the dashboard without --demo to connect your accounts.";

    /// <summary>Throws when the process is a demo: for actions that only make sense with real accounts.</summary>
    public void RequireLive()
    {
        if (Enabled) throw new InvalidOperationException(Notice);
    }
}

/// <summary>Settings for demo mode: nothing is configured, whatever the environment or the secret store hold.</summary>
public sealed class NoSettings : ISettingsResolver
{
    public string? Get(string key) => null;
    public string? SourceOf(string key) => null;
}
