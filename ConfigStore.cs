using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace DeepSharp;

public sealed class ConfigStore
{
    private const string EncryptedPrefix = "DeepSharp-DPAPI-v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeepSharp config v1");
    public string Path { get; } = GetConfigPath();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static string GetConfigPath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("APPDATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return System.IO.Path.Combine(root, OperatingSystem.IsWindows() ? "DeepSharp" : "deepwrap", "config.json");
    }
    public AppConfig Load()
    {
        if (File.Exists(Path))
        {
            var config = ReadConfigFile(Path);
            if (OperatingSystem.IsWindows())
            {
                var legacyPath = GetLegacyConfigPath();
                if (File.Exists(legacyPath)) _ = ReadConfigFile(legacyPath);
            }
            return config;
        }
        if (OperatingSystem.IsWindows())
        {
            var oldPath = GetLegacyConfigPath();
            if (File.Exists(oldPath))
            {
                var oldConfig = ReadConfigFile(oldPath);
                Save(oldConfig);
                return oldConfig;
            }
        }
        return new();
    }
    private static string GetLegacyConfigPath()
    {
        var root = Environment.GetEnvironmentVariable("APPDATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        return System.IO.Path.Combine(root, "DeepWrap", "config.json");
    }
    public void Save(AppConfig config)
    {
        WriteConfigFile(Path, config);
    }
    public AppConfig UpdateToken(string token) { var config = Load() with { Token = BearerToken.Normalize(token) }; Save(config); return config; }

    private static AppConfig ReadConfigFile(string path)
    {
        var contents = File.ReadAllText(path, Encoding.UTF8);
        if (contents.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This config file is encrypted with Windows DPAPI and can only be read on Windows by the same user account.");
            byte[] encrypted;
            try { encrypted = Convert.FromBase64String(contents[EncryptedPrefix.Length..].Trim()); }
            catch (FormatException e) { throw new InvalidDataException("The encrypted DeepSharp config file is malformed.", e); }
            byte[] plaintext;
            try { plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException e) { throw new InvalidOperationException("Could not decrypt the DeepSharp config. Run it as the same Windows account that encrypted the file.", e); }
            return JsonSerializer.Deserialize<AppConfig>(plaintext, JsonOptions) ?? new();
        }

        var config = JsonSerializer.Deserialize<AppConfig>(contents, JsonOptions) ?? new();
        if (OperatingSystem.IsWindows()) WriteConfigFile(path, config);
        return config;
    }

    private static void WriteConfigFile(string path, AppConfig config)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var bytes = OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetBytes(EncryptedPrefix + Convert.ToBase64String(ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)))
            : plaintext;
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, true);
    }
}

public static class BearerToken
{
    public static string Normalize(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        return string.IsNullOrWhiteSpace(token) ? throw new ArgumentException("API key cannot be empty.") : token;
    }
}
