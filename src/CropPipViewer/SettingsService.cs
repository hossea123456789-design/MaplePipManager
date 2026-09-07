using System.IO;
using System.Text.Json;

namespace CropPipViewer;

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CropPipViewer");
    public static string SettingsPath => Path.Combine(AppDir, "settings.json");
    public static string LogPath => Path.Combine(AppDir, "latest.log");

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Keep viewer non-blocking.
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000)
            {
                var lines = File.ReadLines(LogPath).TakeLast(2000).ToArray();
                File.WriteAllLines(LogPath, lines);
            }
        }
        catch
        {
        }
    }
}
