using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Persists account profiles to %APPDATA%\AlpacaTools\accounts.json.
/// API keys are encrypted with Windows DPAPI (CurrentUser scope), so the file
/// is only readable by the same Windows user on the same machine.
/// </summary>
public static class AccountStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlpacaTools", "accounts.json");

    private static readonly byte[] Entropy = "AlpacaTools.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // DataPlan and BotPriority were added later; older files omit them and get the defaults.
    private sealed record StoredProfile(Guid Id, string Name, bool IsPaper, string KeyId, string SecretKey,
        MarketDataPlanSetting DataPlan = MarketDataPlanSetting.Auto, bool BotPriority = false);

    public static List<AccountProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];

        var stored = JsonSerializer.Deserialize<List<StoredProfile>>(File.ReadAllText(FilePath)) ?? [];
        return stored.Select(s => new AccountProfile
        {
            Id = s.Id,
            Name = s.Name,
            IsPaper = s.IsPaper,
            KeyId = Unprotect(s.KeyId),
            SecretKey = Unprotect(s.SecretKey),
            DataPlan = s.DataPlan,
            BotPriority = s.BotPriority,
        }).ToList();
    }

    public static void Save(IEnumerable<AccountProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stored = profiles
            .Select(p => new StoredProfile(p.Id, p.Name, p.IsPaper, Protect(p.KeyId), Protect(p.SecretKey), p.DataPlan, p.BotPriority))
            .ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, JsonOptions));
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string cipher) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
}
