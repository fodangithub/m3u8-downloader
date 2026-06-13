using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace M3U8Downloader.Models;

public partial class DownloadTask : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceUrl { get; set; } = "";

    [ObservableProperty]
    private string fileName = "";

    [ObservableProperty]
    private string saveDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMerging))]
    [NotifyPropertyChangedFor(nameof(IsFailedOrCancelled))]
    [NotifyPropertyChangedFor(nameof(IsActiveOrPaused))]
    private TaskStatus status = TaskStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
    private int totalSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int completedSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private int failedSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
    private long totalBytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
    private long totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedText))]
    [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
    private double downloadSpeed; // bytes per second

    // Track when download actually started (set by TaskManager when Phase 2 begins)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
    [NotifyPropertyChangedFor(nameof(ElapsedTimeText))]
    private DateTime? downloadStartTime;

    private string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}s";
        if (seconds < 3600) return $"{seconds / 60:F0}m {seconds % 60:F0}s";
        return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m";
    }

    public string ElapsedTimeText
    {
        get
        {
            if (!DownloadStartTime.HasValue) return "";
            var elapsed = (DateTime.UtcNow - DownloadStartTime.Value).TotalSeconds;
            return FormatDuration(elapsed);
        }
    }

    [ObservableProperty]
    private string errorMessage = "";

    [ObservableProperty]
    private double mergeProgress;

    [ObservableProperty]
    private string statusText = "";

    public bool IsMerging => Status == TaskStatus.Merging;
    public bool IsFailedOrCancelled => Status is TaskStatus.Failed or TaskStatus.Cancelled;
    public bool IsActiveOrPaused => Status is TaskStatus.Downloading or TaskStatus.Paused or TaskStatus.Parsing or TaskStatus.Queued;

    // Custom headers for this task (override defaults)
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    // Parsed playlist
    public M3U8Playlist? Playlist { get; set; }

    // Segments
    public ObservableCollection<DownloadSegment> Segments { get; set; } = new();

    // Temp directory for this task
    public string TempDirectory => Path.Combine(Path.GetTempPath(), "M3U8Downloader", Id);

    // Output file path
    public string OutputFilePath
    {
        get
        {
            var dir = string.IsNullOrEmpty(SaveDirectory) ? "" : SaveDirectory;
            var name = string.IsNullOrEmpty(FileName) ? "download" : FileName;
            if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                name += ".mp4";
            return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
        }
    }

    public double Progress => TotalSegments > 0 ? (double)CompletedSegments / TotalSegments * 100 : 0;

    public string DownloadSpeedText
    {
        get
        {
            if (DownloadSpeed <= 0) return "";
            if (DownloadSpeed < 1024) return $"{DownloadSpeed:F0} B/s";
            if (DownloadSpeed < 1024 * 1024) return $"{DownloadSpeed / 1024:F1} KB/s";
            return $"{DownloadSpeed / 1024 / 1024:F2} MB/s";
        }
    }

    public string SizeText
    {
        get
        {
            if (TotalBytesDownloaded <= 0) return "";
            if (TotalBytesDownloaded < 1024) return $"{TotalBytesDownloaded} B";
            if (TotalBytesDownloaded < 1024 * 1024) return $"{TotalBytesDownloaded / 1024:F1} KB";
            if (TotalBytesDownloaded < 1024L * 1024 * 1024) return $"{TotalBytesDownloaded / 1024 / 1024:F1} MB";
            return $"{TotalBytesDownloaded / 1024.0 / 1024 / 1024:F2} GB";
        }
    }

    public string RemainingTimeText
    {
        get
        {
            if (DownloadSpeed <= 0 || CompletedSegments == 0 || TotalSegments == 0)
                return "No Estimation";

            var avgSegmentSize = (double)TotalBytesDownloaded / CompletedSegments;
            var estimatedTotal = avgSegmentSize * TotalSegments;
            var remaining = estimatedTotal - TotalBytesDownloaded;
            if (remaining <= 0) return "";

            var elapsed = DownloadStartTime.HasValue ? (DateTime.UtcNow - DownloadStartTime.Value).TotalSeconds : 0;
            var seconds = remaining / DownloadSpeed;

            return $"{FormatDuration(elapsed)} - {FormatDuration(seconds)}";
        }
    }
}
