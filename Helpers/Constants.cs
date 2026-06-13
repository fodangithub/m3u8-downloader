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

    // FFmpeg version definitions
    public static readonly FFmpegVersionInfo[] FFmpegVersions =
    [
        new(
            Id: "stable_7_1",
            DisplayName: "FFmpeg 7.1 (Stable - Recommended)",
            Description: "Best compatibility with most GPU drivers. Supports NVIDIA NVC API 12.x (driver 550+). Recommended for most users.",
            Url: "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-gpl-7.1.zip"),
        new(
            Id: "lts_6_1",
            DisplayName: "FFmpeg 6.1 (LTS - Legacy GPU)",
            Description: "For older GPUs with outdated drivers. Supports NVIDIA NVC API 11.x (driver 470+). Use if Stable fails.",
            Url: "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n6.1-latest-win64-gpl-6.1.zip"),
        new(
            Id: "latest_master",
            DisplayName: "Latest Master (Bleeding Edge)",
            Description: "Newest features but requires latest drivers. NVIDIA NVC API 13.x (driver 610+). Only for up-to-date systems.",
            Url: "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"),
    ];

    public static FFmpegVersionInfo GetFFmpegVersion(string id) =>
        FFmpegVersions.FirstOrDefault(v => v.Id == id) ?? FFmpegVersions[0];

    public static string GetFFmpegDownloadUrl(string versionId) =>
        GetFFmpegVersion(versionId).Url;
}

public record FFmpegVersionInfo(string Id, string DisplayName, string Description, string Url);
