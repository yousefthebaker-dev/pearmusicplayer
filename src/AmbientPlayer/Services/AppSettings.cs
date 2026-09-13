using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AmbientPlayer.Services;

/// <summary>
/// The handful of bits this personal tool needs to remember across restarts.
/// Deliberately not a settings UI (out of scope for v1) - just a small file.
/// </summary>
public sealed class AppSettings
{
    public string? LastMonitorDeviceName { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmbientPlayer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] load failed, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] save failed: {ex.Message}");
        }
    }
}
