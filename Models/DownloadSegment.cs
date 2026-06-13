using CommunityToolkit.Mvvm.ComponentModel;

namespace M3U8Downloader.Models;

public partial class DownloadSegment : ObservableObject
{
    public int Index { get; set; }
    public string Url { get; set; } = "";
    public double Duration { get; set; }

    [ObservableProperty]
    private SegmentStatus status = SegmentStatus.Pending;

    [ObservableProperty]
    private long bytesDownloaded;

    [ObservableProperty]
    private long totalBytes;

    [ObservableProperty]
    private int retryCount;

    [ObservableProperty]
    private string errorMessage = "";

    public string FileName => $"segment_{Index:D6}.ts";
    public EncryptionInfo? Encryption { get; set; }

    public double Progress => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
}
