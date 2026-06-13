using System.Text;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class MergeService
{
    private readonly ILogger<MergeService> _logger;
    private readonly FFmpegService _ffmpegService;
    private readonly SettingsService _settingsService;

    public MergeService(
        ILogger<MergeService> logger,
        FFmpegService ffmpegService,
        SettingsService settingsService)
    {
        _logger = logger;
        _ffmpegService = ffmpegService;
        _settingsService = settingsService;
    }

    public async Task MergeAsync(
        DownloadTask task,
        IProgress<double> progress,
        CancellationToken ct = default)
    {
        var ffmpegPath = _ffmpegService.GetFFmpegPath();
        if (ffmpegPath == null)
            throw new InvalidOperationException("FFmpeg not found. Please install FFmpeg first.");

        task.Status = TaskStatus.Merging;
        task.StatusText = "Merging segments...";

        // Get all completed segment files in order
        var segmentFiles = task.Segments
            .Where(s => s.Status == SegmentStatus.Completed)
            .OrderBy(s => s.Index)
            .Select(s => Path.Combine(task.TempDirectory, s.FileName))
            .Where(File.Exists)
            .ToList();

        if (segmentFiles.Count == 0)
            throw new InvalidOperationException("No completed segments to merge");

        _logger.LogInformation("Merging {Count} segments for task {TaskId}",
            segmentFiles.Count, task.Id);

        // Create concat file list for FFmpeg
        var concatFile = Path.Combine(task.TempDirectory, "concat_list.txt");
        var concatContent = new StringBuilder();
        foreach (var file in segmentFiles)
        {
            concatContent.AppendLine($"file '{file.Replace("'", @"'\''")}'");
        }
        await File.WriteAllTextAsync(concatFile, concatContent.ToString(), ct);
        _logger.LogDebug("Concat file created: {ConcatFile}", concatFile);

        // Get output path
        var outputPath = task.OutputFilePath;
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Handle duplicate file names
        var settings = _settingsService.Settings;
        if (File.Exists(outputPath))
        {
            _logger.LogWarning("Output file already exists: {Path}", outputPath);
            outputPath = settings.DuplicateFileAction switch
            {
                DuplicateFileAction.AutoRename => EnsureUniqueFilePath(outputPath),
                DuplicateFileAction.Overwrite => outputPath,
                DuplicateFileAction.Skip => throw new OperationCanceledException("Output file already exists, skipping"),
                _ => outputPath
            };
            _logger.LogInformation("Output path resolved to: {Path}", outputPath);
        }

        task.StatusText = "Merging and transcoding...";
        var totalDuration = task.Playlist?.TotalDuration ?? segmentFiles.Count * 5;
        _logger.LogInformation("Starting FFmpeg transcode: {Count} segments, ~{Duration:F0}s duration, preset={Preset}, crf={CRF}",
            segmentFiles.Count, totalDuration, settings.FFmpegPreset, settings.FFmpegCRF);

        // Use FFmpeg to merge and transcode
        var mergeProgress = new Progress<double>(p =>
        {
            task.MergeProgress = p;
            task.StatusText = $"Transcoding: {p:F1}%";
            if (p % 10 < 1) // Log every ~10%
                _logger.LogDebug("Transcode progress: {P0:F1}%", p);
        });

        await _ffmpegService.MergeAndTranscodeAsync(
            concatFile, outputPath, mergeProgress, totalDuration, ct);

        // Cleanup temp files if configured
        if (settings.AutoDeleteTempFiles)
        {
            try
            {
                if (Directory.Exists(task.TempDirectory))
                    Directory.Delete(task.TempDirectory, true);
                _logger.LogInformation("Cleaned up temp directory: {Dir}", task.TempDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temp directory: {Dir}", task.TempDirectory);
            }
        }

        task.Status = TaskStatus.Completed;
        task.StatusText = "Completed";
        task.MergeProgress = 100;

        var outputInfo = new FileInfo(outputPath);
        _logger.LogInformation("Task {TaskId} completed successfully. Output: {Path} ({Size:F1} MB)",
            task.Id, outputPath, outputInfo.Length / (1024.0 * 1024.0));
    }

    private static string EnsureUniqueFilePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var counter = 1;

        var newPath = filePath;
        while (File.Exists(newPath))
        {
            newPath = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        }
        return newPath;
    }
}
