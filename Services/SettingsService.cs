using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;

namespace M3U8Downloader.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            var path = Constants.SettingsFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }

            // Fix corrupted values from old ComboBox bindings
            var validPresets = new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
            if (!validPresets.Contains(Settings.FFmpegPreset))
                Settings.FFmpegPreset = "medium";

            if (Settings.Proxy.Type != ProxyType.Http &&
                Settings.Proxy.Type != ProxyType.Socks4 &&
                Settings.Proxy.Type != ProxyType.Socks5)
                Settings.Proxy.Type = ProxyType.Http;
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Constants.SettingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(Constants.SettingsFilePath, json);
        }
        catch
        {
            // Silently fail - settings save is not critical
        }
    }

    public string GetEffectiveFFmpegPath()
    {
        if (!string.IsNullOrEmpty(Settings.FFmpegPath) && File.Exists(Settings.FFmpegPath))
            return Settings.FFmpegPath;

        if (File.Exists(Constants.FFmpegDefaultPath))
            return Constants.FFmpegDefaultPath;

        return "";
    }

    public string GetEffectiveSaveDirectory()
    {
        if (!string.IsNullOrEmpty(Settings.SaveDirectory) && Directory.Exists(Settings.SaveDirectory))
            return Settings.SaveDirectory;

        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "M3U8Downloader");
        if (!Directory.Exists(defaultDir))
            Directory.CreateDirectory(defaultDir);

        return defaultDir;
    }
}
