using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meeza.Marketing.Shared.Security;

/// <summary>
/// Per-user encrypted secret store. On Windows each value is encrypted with DPAPI
/// (CurrentUser scope), so the file is unreadable by other Windows accounts and other machines.
/// On other operating systems persistence is refused rather than writing plaintext; use
/// environment variables (or your OS keychain feeding environment variables) instead.
/// </summary>
public sealed class LocalSecretStore
{
    private static readonly byte[] Entropy = "Meeza.MarketingMcp.v1"u8.ToArray();
    private readonly string _path;
    private readonly object _gate = new();

    public LocalSecretStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeezaMarketingMcp", "secrets.json");

    public string FilePath => _path;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public string? Get(string key)
    {
        if (!IsSupported) return null;
        var all = Load();
        return all.TryGetValue(key, out var cipher) ? Decrypt(cipher) : null;
    }

    public IReadOnlyCollection<string> ListKeys() => IsSupported ? Load().Keys.ToArray() : [];

    public void Set(string key, string value)
    {
        EnsureSupported();
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret value must not be empty.");
        lock (_gate)
        {
            var all = Load();
            all[key] = Encrypt(value);
            Save(all);
        }
    }

    public bool Remove(string key)
    {
        EnsureSupported();
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(key)) return false;
            Save(all);
            return true;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        var json = File.ReadAllText(_path);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "The encrypted local secret store currently requires Windows (DPAPI). " +
                "On macOS/Linux, provide secrets through environment variables instead.");
    }

    private static string Encrypt(string plain)
    {
        if (!OperatingSystem.IsWindows()) { EnsureSupported(); return ""; }
        return Convert.ToBase64String(ProtectWindows(Encoding.UTF8.GetBytes(plain)));
    }

    private static string? Decrypt(string cipher)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(UnprotectWindows(Convert.FromBase64String(cipher)));
        }
        catch (CryptographicException)
        {
            // Encrypted by another user or machine: treat as missing rather than crash.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
