namespace M3U8Downloader.Models;

public enum TaskStatus
{
    Queued,
    Parsing,
    Downloading,
    Paused,
    Merging,
    Completed,
    Failed,
    Cancelled
}

public enum SegmentStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Retrying
}

public enum ProxyType
{
    Http,
    Socks4,
    Socks5
}

public enum DuplicateFileAction
{
    AutoRename,
    Overwrite,
    Skip
}
