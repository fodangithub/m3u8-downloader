namespace M3U8Downloader.Helpers;

public static class Constants
{
    public const string AppName = "M3U8 Downloader";
    public const string AppVersion = "1.0.0";

    // Default paths
    public static string AppDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "M3U8Downloader");

    public static string SettingsFilePath => Path.Combine(AppDataPath, "settings.json");
    public static string ToolsPath => Path.Combine(AppDataPath, "tools");
    public static string FFmpegDefaultPath => Path.Combine(ToolsPath, "ffmpeg.exe");

    // Download defaults
    public const int DefaultMaxConcurrentTasks = 1;
    public const int DefaultMaxConcurrentSegments = 8;
    public const int DefaultRetryIntervalSeconds = 15;
    public const int DefaultTimeoutSeconds = 30;

    // UI
    public const int ProgressBarHeight = 20;
    public const int SegmentGridCellSize = 4;
}
