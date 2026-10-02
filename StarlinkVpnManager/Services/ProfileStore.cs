using StarlinkVpnManager.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StarlinkVpnManager.Services;

internal sealed class ProfileStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StarlinkVpnManager.ProfileStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StarlinkVpnManager");
    private readonly string _metadataPath;

    public ProfileStore()
    {
        _metadataPath = Path.Combine(_directory, "profiles.json");
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<WireGuardProfile> Load()
    {
        if (!File.Exists(_metadataPath))
        {
            return [];
        }

        var profiles = JsonSerializer.Deserialize<List<WireGuardProfile>>(
            File.ReadAllText(_metadataPath),
            JsonOptions) ?? throw new InvalidDataException("فهرست پروفایل‌ها قابل خواندن نیست.");

        foreach (var profile in profiles)
        {
            if (profile is null
                || !Guid.TryParseExact(profile.Id, "N", out _)
                || !IsValidServiceName(profile.ServiceName)
                || string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new InvalidDataException("فهرست پروفایل‌ها شامل اطلاعات نامعتبر است.");
            }
        }

        return profiles;
    }

    public WireGuardProfile Import(string sourcePath)
    {
        var configuration = File.ReadAllText(sourcePath, Encoding.UTF8);
        WireGuardConfiguration.Validate(configuration);

        var id = Guid.NewGuid().ToString("N");
        var profile = new WireGuardProfile
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(sourcePath).Trim(),
            ServiceName = $"slvpn-{id[..8]}"
        };

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new FormatException("نام فایل برای ساخت پروفایل معتبر نیست.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(configuration);
        try
        {
            var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetConfigurationPath(profile), encryptedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        var profiles = Load().ToList();
        profiles.Add(profile);
        Save(profiles);
        return profile;
    }

    public byte[] ReadConfiguration(WireGuardProfile profile)
    {
        var encryptedBytes = File.ReadAllBytes(GetConfigurationPath(profile));
        return ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    public void Remove(WireGuardProfile profile)
    {
        var profiles = Load();
        Save(profiles.Where(existing => existing.Id != profile.Id));
        File.Delete(GetConfigurationPath(profile));
    }

    private void Save(IEnumerable<WireGuardProfile> profiles)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = _metadataPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profiles, JsonOptions), Encoding.UTF8);
        File.Move(temporaryPath, _metadataPath, overwrite: true);
    }

    private string GetConfigurationPath(WireGuardProfile profile) =>
        Path.Combine(_directory, $"{profile.Id}.conf.dpapi");

    private static bool IsValidServiceName(string? serviceName) =>
        serviceName is not null
        && serviceName.Length == 14
        && serviceName.StartsWith("slvpn-", StringComparison.Ordinal)
        && serviceName[6..].All(Uri.IsHexDigit);
}
