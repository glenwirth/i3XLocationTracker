using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using I3XLocationTracker.Models;

namespace I3XLocationTracker.Services;

/// <summary>
/// Loads/saves the connection dialog's fields to %AppData%\I3XLocationTracker\settings.json.
/// The token/key is encrypted at rest with Windows DPAPI (current-user scope) — it is never written to disk in plain text.
/// </summary>
public static class SettingsService
{
    // Binds the encrypted blob to this app + purpose so it can't be swapped in from elsewhere; not a secret itself.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("I3XLocationTracker.token.v1");

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "I3XLocationTracker");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static (AppSettings Settings, string Token) Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return (new AppSettings(), "");
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return (settings, DecryptToken(settings.ProtectedToken));
        }
        catch
        {
            // Missing/corrupt settings file — start from defaults rather than fail app startup.
            return (new AppSettings(), "");
        }
    }

    public static void Save(AppSettings settings, string token)
    {
        try
        {
            settings.ProtectedToken = EncryptToken(token);
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            // Write-then-replace so a crash mid-write can never leave a truncated/corrupt settings file behind.
            var tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence — a save failure (e.g. locked file, no disk space) should never crash the app.
        }
    }

    private static string EncryptToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string DecryptToken(string protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Encrypted by a different user/machine (or the file was hand-edited) — treat as no saved token.
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }
}
