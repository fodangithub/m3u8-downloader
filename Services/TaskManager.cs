using System.Collections.ObjectModel;
using System.Net.Http;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class TaskManager
{
    private readonly ILogger<TaskManager> _logger;
    private readonly SettingsService _settingsService;
    private readonly M3U8Parser _m3u8Parser;
    private readonly DownloadEngine _downloadEngine;
    private readonly MergeService _mergeService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly SemaphoreSlim _mergeSemaphore;
    private readonly Dictionary<string, CancellationTokenSource> _taskCancellations = new();
    private readonly object _lock = new();

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    public TaskManager(
        ILogger<TaskManager> logger,
        SettingsService settingsService,
        M3U8Parser m3u8Parser,
        DownloadEngine downloadEngine,
        MergeService mergeService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settingsService = settingsService;
        _m3u8Parser = m3u8Parser;
        _downloadEngine = downloadEngine;
        _mergeService = mergeService;
        _httpClientFactory = httpClientFactory;
        _downloadSemaphore = new SemaphoreSlim(settingsService.Settings.MaxConcurrentTasks);
        _mergeSemaphore = new SemaphoreSlim(settingsService.Settings.MaxConcurrentMerges);
    }

    public async Task<DownloadTask> AddTaskAsync(
        string url,
        string? fileName = null,
        string? saveDirectory = null,
        Dictionary<string, string>? customHeaders = null)
    {
        var task = new DownloadTask
        {
            SourceUrl = url,
            FileName = string.IsNullOrEmpty(fileName)
                ? FileNameHelper.InferFileName(url)
                : FileNameHelper.SanitizeFileName(fileName),
            SaveDirectory = saveDirectory ?? _settingsService.GetEffectiveSaveDirectory(),
            CustomHeaders = customHeaders ?? new Dictionary<string, string>()
        };

        Tasks.Add(task);
        _logger.LogInformation("Task added: {TaskId} - {Url}", task.Id, url);

        if (_settingsService.Settings.AutoStartDownload)
            _ = StartTaskAsync(task);

        return task;
    }

    public async Task StartTaskAsync(DownloadTask task)
    {
        if (task.Status is TaskStatus.Downloading or TaskStatus.Merging)
            return;

        await _downloadSemaphore.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _taskCancellations[task.Id] = cts;
            }

            _ = RunTaskAsync(task, cts.Token);
        }
        catch
        {
            _downloadSemaphore.Release();
            throw;
        }
    }

    private async Task RunTaskAsync(DownloadTask task, CancellationToken ct)
    {
        bool downloadSlotHeld = true;
        bool mergeSlotHeld = false;

        try
        {
            var proxy = _settingsService.Settings.Proxy;
            _logger.LogInformation("Starting task {TaskId} - {FileName} from {Url}",
                task.Id, task.FileName, task.SourceUrl);
            _logger.LogInformation("Proxy config: Enabled={Enabled}, {Type}://{Host}:{Port}",
                proxy.Enabled, proxy.Type, proxy.Host, proxy.Port);

            // Phase 1: Parse M3U8
            task.Status = TaskStatus.Parsing;
            task.StatusText = "Parsing M3U8...";
            _logger.LogDebug("Phase 1: Parsing M3U8 playlist for task {TaskId}", task.Id);

            var httpClient = CreateHttpClient(task);
            var playlist = await _m3u8Parser.ParseFromUrlAsync(task.SourceUrl, httpClient, ct);

            _logger.LogInformation("M3U8 parsed: {IsMaster} playlist, {Segments} segments, {Duration:F1}s duration",
                playlist.IsMaster ? "Master" : "Media", playlist.Segments.Count, playlist.TotalDuration);

            // Handle Master Playlist - select best quality
            if (playlist.IsMaster && playlist.Variants.Count > 0)
            {
                var bestVariant = playlist.Variants[0]; // Already sorted by bandwidth desc
                _logger.LogInformation("Master playlist detected, selecting best quality: {Variant} (Bandwidth: {Bandwidth})",
                    bestVariant.DisplayName, bestVariant.Bandwidth);

                playlist = await _m3u8Parser.ResolveMasterPlaylistAsync(
                    playlist, bestVariant, httpClient, ct);

                _logger.LogInformation("Resolved media playlist: {Segments} segments", playlist.Segments.Count);
            }

            task.Playlist = playlist;
            task.TotalSegments = playlist.Segments.Count;
            task.TotalBytes = playlist.Segments.Sum(s => 0L);

            // Create download segments
            task.Segments.Clear();
            foreach (var seg in playlist.Segments)
            {
                task.Segments.Add(new DownloadSegment
                {
                    Index = seg.Index,
                    Url = seg.Url,
                    Duration = seg.Duration,
                    Encryption = seg.Encryption,
                    Status = SegmentStatus.Pending
                });
            }

            _logger.LogInformation("Created {Count} download segments for task {TaskId}",
                task.TotalSegments, task.Id);

            // Phase 2+3: Download segments and merge (shared with resume path)
            await ContinueDownloadAndMergeAsync(task, ct);

            _logger.LogInformation("Task completed: {TaskId} - {FileName}",
                task.Id, task.FileName);
        }
        catch (OperationCanceledException)
        {
            // Preserve Paused status if PauseTask already set it
            if (task.Status != TaskStatus.Paused)
            {
                task.Status = TaskStatus.Cancelled;
                task.StatusText = "Cancelled";
            }
            _logger.LogInformation("Task {Status}: {TaskId}", task.Status, task.Id);
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.StatusText = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Task failed: {TaskId}", task.Id);
        }
        finally
        {
            if (downloadSlotHeld)
                _downloadSemaphore.Release();
            if (mergeSlotHeld)
                _mergeSemaphore.Release();
            lock (_lock)
            {
                _taskCancellations.Remove(task.Id);
            }
        }
    }

    /// <summary>
    /// Shared download and merge phase, used by both fresh start and resume paths.
    /// Assumes the download semaphore slot is already held.
    /// </summary>
    private async Task ContinueDownloadAndMergeAsync(DownloadTask task, CancellationToken ct)
    {
        bool downloadSlotHeld = true;
        bool mergeSlotHeld = false;

        try
        {
            // Phase 2: Download segments
            task.Status = TaskStatus.Downloading;
            task.StatusText = "Downloading...";
            if (!task.DownloadStartTime.HasValue)
                task.DownloadStartTime = DateTime.UtcNow;
            _logger.LogDebug("Starting segment downloads for task {TaskId}, concurrency: {Concurrency}",
                task.Id, _settingsService.Settings.MaxConcurrentSegments);

            var progress = new Progress<DownloadTask>(t => _logger.LogDebug("Task {TaskId} progress: {Completed}/{Total}",
                t.Id, t.CompletedSegments, t.TotalSegments));
            await _downloadEngine.DownloadTaskAsync(task, progress, ct);

            // Check if all segments completed
            var failedSegments = task.Segments.Count(s => s.Status != SegmentStatus.Completed);
            if (failedSegments > 0)
            {
                _logger.LogError("Task {TaskId}: {Failed} segments failed to download",
                    task.Id, failedSegments);
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = $"{failedSegments} segment(s) failed to download";
                task.StatusText = $"{failedSegments} segment(s) failed - retry or delete task";
                return;
            }

            // Verify all segment files actually exist on disk before merging
            var missingFiles = new List<string>();
            var corruptedFiles = new List<string>();
            foreach (var seg in task.Segments.Where(s => s.Status == SegmentStatus.Completed))
            {
                var filePath = Path.Combine(task.TempDirectory, seg.FileName);
                if (!File.Exists(filePath))
                {
                    missingFiles.Add(seg.Index.ToString());
                    _logger.LogError("Segment file missing on disk: {Index} - {File}", seg.Index, filePath);
                }
                else if (new FileInfo(filePath).Length == 0)
                {
                    corruptedFiles.Add(seg.Index.ToString());
                    _logger.LogError("Segment file is empty (corrupted): {Index} - {File}", seg.Index, filePath);
                }
            }

            if (missingFiles.Count > 0 || corruptedFiles.Count > 0)
            {
                var details = new List<string>();
                if (missingFiles.Count > 0)
                    details.Add($"Missing: [{string.Join(", ", missingFiles)}]");
                if (corruptedFiles.Count > 0)
                    details.Add($"Empty/corrupted: [{string.Join(", ", corruptedFiles)}]");

                var totalBad = missingFiles.Count + corruptedFiles.Count;
                _logger.LogError("Task {TaskId}: {Count} segment file(s) invalid before merge. {Details}",
                    task.Id, totalBad, string.Join("; ", details));
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = $"{totalBad} segment file(s) invalid. {string.Join("; ", details)}";
                task.StatusText = "Segment files missing/corrupted - retry or delete task";

                foreach (var seg in task.Segments)
                {
                    var idx = seg.Index.ToString();
                    if (missingFiles.Contains(idx) || corruptedFiles.Contains(idx))
                    {
                        seg.Status = SegmentStatus.Failed;
                        seg.ErrorMessage = missingFiles.Contains(idx) ? "File missing on disk" : "File is empty/corrupted";
                    }
                }
                return;
            }

            _logger.LogInformation("All segments downloaded and verified for task {TaskId}, proceeding to merge", task.Id);

            // Release download slot so other tasks can start downloading while this one merges
            _downloadSemaphore.Release();
            downloadSlotHeld = false;

            // Acquire merge slot
            await _mergeSemaphore.WaitAsync(ct);
            mergeSlotHeld = true;

            // Phase 3: Merge and transcode
            task.Status = TaskStatus.Merging;
            task.StatusText = "Merging and transcoding...";
            _logger.LogDebug("Phase 3: Merging and transcoding task {TaskId}", task.Id);

            var mergeProgress = new Progress<double>(p => task.MergeProgress = p);
            await _mergeService.MergeAsync(task, mergeProgress, ct);
        }
        finally
        {
            if (downloadSlotHeld)
                _downloadSemaphore.Release();
            if (mergeSlotHeld)
                _mergeSemaphore.Release();
        }
    }

    /// <summary>
    /// Resume a paused task. Skips M3U8 re-parsing and continues downloading
    /// from where it left off, preserving already-completed segments.
    /// </summary>
    public async Task ResumeTaskAsync(DownloadTask task)
    {
        if (task.Status is not (TaskStatus.Paused or TaskStatus.Failed))
            return;

        // Immediately set to Downloading to prevent double-click race
        task.Status = TaskStatus.Downloading;
        task.StatusText = "Resuming...";
        task.ErrorMessage = "";

        // Reset in-flight/failed segments back to Pending
        foreach (var seg in task.Segments)
        {
            if (seg.Status is SegmentStatus.Failed or SegmentStatus.Retrying or SegmentStatus.Pending)
            {
                seg.Status = SegmentStatus.Pending;
                seg.RetryCount = 0;
                seg.ErrorMessage = "";
            }
        }

        await _downloadSemaphore.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _taskCancellations[task.Id] = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ContinueDownloadAndMergeAsync(task, cts.Token);
                    _logger.LogInformation("Task resumed and completed: {TaskId}", task.Id);
                }
                catch (OperationCanceledException)
                {
                    if (task.Status != TaskStatus.Paused)
                    {
                        task.Status = TaskStatus.Cancelled;
                        task.StatusText = "Cancelled";
                    }
                    _logger.LogInformation("Task {Status}: {TaskId}", task.Status, task.Id);
                }
                catch (Exception ex)
                {
                    task.Status = TaskStatus.Failed;
                    task.ErrorMessage = ex.Message;
                    task.StatusText = $"Failed: {ex.Message}";
                    _logger.LogError(ex, "Task failed: {TaskId}", task.Id);
                }
                finally
                {
                    _downloadSemaphore.Release();
                    lock (_lock)
                    {
                        _taskCancellations.Remove(task.Id);
                    }
                }
            });
        }
        catch
        {
            _downloadSemaphore.Release();
            throw;
        }
    }

    public void PauseTask(DownloadTask task)
    {
        if (task.Status != TaskStatus.Downloading)
            return;

        // Reset in-flight segments back to Pending so they re-download on resume
        foreach (var seg in task.Segments)
        {
            if (seg.Status is SegmentStatus.Downloading or SegmentStatus.Retrying)
            {
                seg.Status = SegmentStatus.Pending;
                seg.RetryCount = 0;
                seg.ErrorMessage = "";
            }
        }

        lock (_lock)
        {
            if (_taskCancellations.TryGetValue(task.Id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _taskCancellations.Remove(task.Id);
            }
        }

        task.Status = TaskStatus.Paused;
        task.StatusText = "Paused";
        task.DownloadSpeed = 0;
        _logger.LogInformation("Task paused: {TaskId}", task.Id);
    }

    public void CancelTask(DownloadTask task)
    {
        lock (_lock)
        {
            if (_taskCancellations.TryGetValue(task.Id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _taskCancellations.Remove(task.Id);
            }
        }

        task.Status = TaskStatus.Cancelled;
        task.StatusText = "Cancelled";

        try
        {
            if (Directory.Exists(task.TempDirectory))
                Directory.Delete(task.TempDirectory, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temp files for task: {TaskId}", task.Id);
        }

        _logger.LogInformation("Task cancelled: {TaskId}", task.Id);
    }

    public void RemoveTask(DownloadTask task)
    {
        CancelTask(task);
        Tasks.Remove(task);
    }

    private HttpClient CreateHttpClient(DownloadTask task)
    {
        var httpClient = _httpClientFactory.CreateClient("DownloadClient");
        foreach (var header in task.CustomHeaders)
        {
            httpClient.DefaultRequestHeaders.Remove(header.Key);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
        return httpClient;
    }

    public void UpdateMaxConcurrentTasks(int maxTasks)
    {
        _logger.LogInformation("Max concurrent download tasks updated to: {Max}", maxTasks);
    }

    public void UpdateMaxConcurrentMerges(int maxMerges)
    {
        _logger.LogInformation("Max concurrent merge tasks updated to: {Max}", maxMerges);
    }
}
