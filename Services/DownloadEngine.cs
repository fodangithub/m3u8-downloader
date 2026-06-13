using System.Diagnostics;
using System.Net.Http;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class DownloadEngine
{
    private readonly ILogger<DownloadEngine> _logger;
    private readonly SettingsService _settingsService;
    private readonly AesDecryptionService _decryptionService;
    private readonly IHttpClientFactory _httpClientFactory;

    public DownloadEngine(
        ILogger<DownloadEngine> logger,
        SettingsService settingsService,
        AesDecryptionService decryptionService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settingsService = settingsService;
        _decryptionService = decryptionService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task DownloadTaskAsync(
        DownloadTask task,
        IProgress<DownloadTask> progress,
        CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        var semaphore = new SemaphoreSlim(settings.MaxConcurrentSegments);
        var segmentTasks = new List<Task>();
        var speedTracker = new SpeedTracker();

        // Create temp directory
        Directory.CreateDirectory(task.TempDirectory);
        _logger.LogInformation("Temp directory created: {TempDir}", task.TempDirectory);

        task.Status = TaskStatus.Downloading;
        progress.Report(task);

        _logger.LogInformation("Starting download of {Count} segments with concurrency {Concurrency}",
            task.Segments.Count, settings.MaxConcurrentSegments);

        try
        {
            foreach (var segment in task.Segments)
            {
                ct.ThrowIfCancellationRequested();

                if (segment.Status == SegmentStatus.Completed)
                    continue;

                await semaphore.WaitAsync(ct);

                _logger.LogDebug("Starting download for segment #{Index}: {Url}",
                    segment.Index, segment.Url.Length > 100 ? segment.Url[..100] + "..." : segment.Url);

                var segmentTask = Task.Run(async () =>
                {
                    try
                    {
                        await DownloadSegmentAsync(task, segment, speedTracker, progress, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);

                segmentTasks.Add(segmentTask);
            }

            await Task.WhenAll(segmentTasks);

            var completed = task.Segments.Count(s => s.Status == SegmentStatus.Completed);
            var failed = task.Segments.Count(s => s.Status != SegmentStatus.Completed);
            _logger.LogInformation("Download phase complete for task {TaskId}: {Completed} succeeded, {Failed} failed",
                task.Id, completed, failed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download task cancelled: {TaskId}", task.Id);
            task.Status = TaskStatus.Cancelled;
            progress.Report(task);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download task failed: {TaskId}", task.Id);
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            progress.Report(task);
            throw;
        }
    }

    private async Task DownloadSegmentAsync(
        DownloadTask task,
        DownloadSegment segment,
        SpeedTracker speedTracker,
        IProgress<DownloadTask> progress,
        CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        var httpClient = _httpClientFactory.CreateClient("DownloadClient");
        var filePath = Path.Combine(task.TempDirectory, segment.FileName);
        var maxRetries = settings.MaxRetries; // 0 = infinite

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                segment.Status = SegmentStatus.Downloading;
                segment.BytesDownloaded = 0;
                UpdateTaskProgress(task);
                progress.Report(task);

                using var response = await httpClient.GetAsync(
                    segment.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                response.EnsureSuccessStatusCode();

                segment.TotalBytes = response.Content.Headers.ContentLength ?? 0;

                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                var buffer = new byte[8192];
                int bytesRead;
                var segmentBytes = 0L;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    segmentBytes += bytesRead;
                    segment.BytesDownloaded = segmentBytes;
                    speedTracker.AddBytes(bytesRead);

                    // Update task speed and progress
                    task.DownloadSpeed = speedTracker.GetSpeed();
                    task.TotalBytesDownloaded = task.Segments.Sum(s => s.BytesDownloaded);
                    progress.Report(task);
                }

                // Handle decryption if needed
                if (segment.Encryption?.IsEncrypted == true)
                {
                    var encryptedData = await File.ReadAllBytesAsync(filePath, ct);
                    var decryptedData = await _decryptionService.DecryptSegmentAsync(
                        encryptedData,
                        segment.Encryption,
                        segment.Index + (int)(task.Playlist?.MediaSequence ?? 0),
                        ct);
                    await File.WriteAllBytesAsync(filePath, decryptedData, ct);
                }

                segment.Status = SegmentStatus.Completed;
                segment.BytesDownloaded = segment.TotalBytes > 0
                    ? segment.TotalBytes
                    : new FileInfo(filePath).Length;
                segment.ErrorMessage = "";

                UpdateTaskProgress(task);
                progress.Report(task);
                return; // Success - exit retry loop
            }
            catch (OperationCanceledException)
            {
                segment.Status = SegmentStatus.Failed;
                throw;
            }
            catch (Exception ex)
            {
                segment.RetryCount++;
                segment.Status = SegmentStatus.Retrying;
                segment.ErrorMessage = ex.Message;

                _logger.LogWarning(ex,
                    "Segment {Index} download failed (attempt {Attempt}), retrying in {Interval}s",
                    segment.Index, segment.RetryCount, settings.RetryIntervalSeconds);

                task.FailedSegments = task.Segments.Count(s => s.Status == SegmentStatus.Failed || s.Status == SegmentStatus.Retrying);
                progress.Report(task);

                if (maxRetries > 0 && segment.RetryCount >= maxRetries)
                {
                    segment.Status = SegmentStatus.Failed;
                    _logger.LogError("Segment {Index} failed after {Attempts} attempts",
                        segment.Index, segment.RetryCount);
                    return;
                }

                // Wait before retry
                await Task.Delay(TimeSpan.FromSeconds(settings.RetryIntervalSeconds), ct);
            }
        }
    }

    private static void UpdateTaskProgress(DownloadTask task)
    {
        task.CompletedSegments = task.Segments.Count(s => s.Status == SegmentStatus.Completed);
        task.FailedSegments = task.Segments.Count(s =>
            s.Status == SegmentStatus.Failed || s.Status == SegmentStatus.Retrying);
        task.TotalBytesDownloaded = task.Segments.Sum(s => s.BytesDownloaded);
        task.TotalBytes = task.Segments.Sum(s => s.TotalBytes);
    }
}

internal class SpeedTracker
{
    private readonly Queue<(long bytes, DateTime time)> _samples = new();
    private readonly object _lock = new();
    private long _totalBytes;

    public void AddBytes(long bytes)
    {
        lock (_lock)
        {
            _samples.Enqueue((bytes, DateTime.UtcNow));
            _totalBytes += bytes;

            // Keep only last 3 seconds of samples
            var cutoff = DateTime.UtcNow.AddSeconds(-3);
            while (_samples.Count > 0 && _samples.Peek().time < cutoff)
                _samples.Dequeue();
        }
    }

    public double GetSpeed()
    {
        lock (_lock)
        {
            if (_samples.Count < 2) return 0;

            var oldest = _samples.First().time;
            var newest = _samples.Last().time;
            var elapsed = (newest - oldest).TotalSeconds;

            if (elapsed < 0.1) return 0;

            var totalBytes = _samples.Sum(s => s.bytes);
            return totalBytes / elapsed;
        }
    }
}
