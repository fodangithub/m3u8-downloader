namespace M3U8Downloader.Models;

public class AppSettings
{
    // General
    public string SaveDirectory { get; set; } = "";
    public bool AutoStartDownload { get; set; } = true;
    public bool AutoDeleteTempFiles { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool PlaySoundOnComplete { get; set; } = true;
    public DuplicateFileAction DuplicateFileAction { get; set; } = DuplicateFileAction.AutoRename;

    // Download
    public int MaxConcurrentTasks { get; set; } = 1;
    public int MaxConcurrentMerges { get; set; } = 1;
    public int MaxConcurrentSegments { get; set; } = 8;
    public int RetryIntervalSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 0; // 0 = infinite
    public int TimeoutSeconds { get; set; } = 30;
    public long SpeedLimitKBps { get; set; } = 0; // 0 = unlimited

    // Proxy
    public ProxyConfig Proxy { get; set; } = new();

    // FFmpeg
    public string FFmpegVersion { get; set; } = "stable_7_1";
    public string FFmpegPath { get; set; } = "";
    public string FFmpegDownloadUrl { get; set; } = "";
    public string FFmpegPreset { get; set; } = "default";
    public int FFmpegCRF { get; set; } = 0;
    public bool FFmpegUseGPU { get; set; } = true;
    public string FFmpegGPUEncoder { get; set; } = ""; // e.g. "h264_nvenc", "h264_amf", "h264_qsv"

    // Default HTTP headers
    public string DefaultUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    public string DefaultReferer { get; set; } = "";
    public string DefaultCookies { get; set; } = "";
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}

public class ProxyConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public ProxyType Type { get; set; } = ProxyType.Http;
}
