using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Meeza.Marketing.Shared.Security;

/// <summary>
/// Keeps the per-user data directory (secret store, dashboard key, SQLite reporting database) readable by the
/// current user only. On Unix that is mode 0700 for the directory and 0600 for files; on Windows an explicit ACL
/// (current user + SYSTEM, inheritance disabled). Everything is best effort: a failure to tighten permissions never
/// stops the application, because the alternative is to not store the data at all.
/// </summary>
public static class PrivateStorage
{
    /// <summary>The default per-user data directory (%APPDATA%\MeezaMarketingMcp or ~/.config/MeezaMarketingMcp).</summary>
    public static string DefaultDirectory =>
        DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeezaMarketingMcp");

    /// <summary>
    /// Test seam: lets the test suite keep the dashboard key, instance records and default store paths in a throwaway
    /// folder, so tests can rotate and delete them without touching the user's real profile. Never set in production.
    /// </summary>
    internal static string? DirectoryOverride { get; set; }

    /// <summary>Creates <paramref name="directory"/> if needed and restricts it to the current user.</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            if (OperatingSystem.IsWindows()) RestrictWindowsDirectory(directory);
            else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Best effort; the directory may live on a file system without permission support.
        }
    }

    /// <summary>Restricts an existing file to the current user (0600 / explicit ACL). Missing files are ignored.</summary>
    public static void MakeFilePrivate(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows()) RestrictWindowsFile(path);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Restricts a SQLite database and its WAL / SHM side files.</summary>
    public static void MakeDatabasePrivate(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) MakeFilePrivate(dbPath + suffix);
    }

    /// <summary>True when <paramref name="path"/> is inside the default per-user data directory.</summary>
    public static bool IsInDefaultDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in Principals())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Principals())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Principals()
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null) yield return user;
        // SYSTEM keeps backup agents and the OS itself working; no other account (Administrators included) inherits access.
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    }
}
