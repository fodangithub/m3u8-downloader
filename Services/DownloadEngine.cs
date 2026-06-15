using System.Collections.Concurrent;
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

    private ConcurrentStack<DownloadSegment>? _segmentStack;
    private readonly HashSet<DownloadSegment> _queued = new();
    private readonly object _queueLock = new();
    private SemaphoreSlim? _workAvailable;

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
        var maxConcurrent = Math.Max(1, settings.MaxConcurrentSegments);
        var segmentSemaphore = new SemaphoreSlim(maxConcurrent);
        var speedTracker = new SpeedTracker();

        Directory.CreateDirectory(task.TempDirectory);
        _logger.LogInformation("Temp directory created: {TempDir}", task.TempDirectory);

        task.Status = TaskStatus.Downloading;
        progress.Report(task);

        // Initialize stack — push in reverse so segment 0 is on top (processed first)
        lock (_queueLock)
        {
            _segmentStack = new ConcurrentStack<DownloadSegment>();
            _queued.Clear();
            _workAvailable = new SemaphoreSlim(0);

            for (int i = task.Segments.Count - 1; i >= 0; i--)
            {
                var seg = task.Segments[i];
                if (seg.Status != SegmentStatus.Completed)
                {
                    _segmentStack.Push(seg);
                    _queued.Add(seg);
                    _workAvailable.Release();
                }
            }
        }

        _logger.LogInformation("Starting download of {Count} segments with concurrency {Concurrency}",
            task.Segments.Count(s => s.Status != SegmentStatus.Completed), maxConcurrent);

        try
        {
            var workers = new Task[maxConcurrent];
            for (int i = 0; i < maxConcurrent; i++)
            {
                workers[i] = Task.Run(() => WorkerLoopAsync(
                    task, segmentSemaphore, speedTracker, progress, ct), ct);
            }

            await Task.WhenAll(workers);

            var completed = task.Segments.Count(s => s.Status == SegmentStatus.Completed);
            var failed = task.Segments.Count(s => s.Status != SegmentStatus.Completed);
            _logger.LogInformation("Download phase complete for task {TaskId}: {Completed} succeeded, {Failed} failed",
                task.Id, completed, failed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download task cancelled: {TaskId}", task.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download task failed: {TaskId}", task.Id);
            task.ErrorMessage = ex.Message;
            throw;
        }
    }

    /// <summary>
    /// Push segments onto the stack for retry while a download is in progress.
    /// Workers will pick them up with priority (LIFO) as soon as slots are available.
    /// </summary>
    public void EnqueueRetrySegments(IEnumerable<DownloadSegment> segments)
    {
        lock (_queueLock)
        {
            if (_segmentStack == null || _workAvailable == null)
                return;

            foreach (var seg in segments)
            {
                if (_queued.Add(seg))
                {
                    _segmentStack.Push(seg);
                    _workAvailable.Release();
                }
            }
        }
    }

    private async Task WorkerLoopAsync(
        DownloadTask task,
        SemaphoreSlim segmentSemaphore,
        SpeedTracker speedTracker,
        IProgress<DownloadTask> progress,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _workAvailable!.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DownloadSegment? segment;
            lock (_queueLock)
            {
                if (!_segmentStack!.TryPop(out segment))
                    continue;
                _queued.Remove(segment);
            }

            await segmentSemaphore.WaitAsync(ct);
            try
            {
                await ProcessSegmentAsync(task, segment, speedTracker, progress, ct);
            }
            finally
            {
                segmentSemaphore.Release();
            }
        }
    }

    private async Task ProcessSegmentAsync(
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

        segment.Status = SegmentStatus.Downloading;
        segment.BytesDownloaded = 0;
        UpdateTaskProgress(task);
        progress.Report(task);

        _logger.LogDebug("Starting download for segment #{Index}: {Url}",
            segment.Index, segment.Url.Length > 100 ? segment.Url[..100] + "..." : segment.Url);

        while (!ct.IsCancellationRequested)
        {
            try
            {
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
                return;
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

                task.FailedSegments = task.Segments.Count(s =>
                    s.Status == SegmentStatus.Failed || s.Status == SegmentStatus.Retrying);
                progress.Report(task);

                if (maxRetries > 0 && segment.RetryCount >= maxRetries)
                {
                    segment.Status = SegmentStatus.Failed;
                    _logger.LogError("Segment {Index} failed after {Attempts} attempts",
                        segment.Index, segment.RetryCount);
                    UpdateTaskProgress(task);
                    progress.Report(task);
                    return;
                }

                // Wait before retry, then re-enqueue for worker pickup
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.RetryIntervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    segment.Status = SegmentStatus.Failed;
                    throw;
                }

                // Re-enqueue so any worker (including this one) can pick it up
                lock (_queueLock)
                {
                    if (_segmentStack != null && _queued.Add(segment))
                    {
                        _segmentStack.Push(segment);
                        _workAvailable?.Release();
                    }
                }
                return;
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

    public void AddBytes(long bytes)
    {
        lock (_lock)
        {
            _samples.Enqueue((bytes, DateTime.UtcNow));

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
