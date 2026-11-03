using System.Text.Json;

namespace PS3Transfer.App.Services;

public sealed class AppSettings
{
    public bool VerifyCrc32 { get; set; }
    public bool HideExtraParts { get; set; } = true;
    public bool UseTitleNames { get; set; } = true;
    public string? LastDriveRoot { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Could not save settings. " + ex.Message);
        }
    }
}
