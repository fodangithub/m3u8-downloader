using System.IO.Compression;
using System.Net.Http;
using M3U8Downloader.Helpers;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class FFmpegDownloader
{
    private readonly ILogger<FFmpegDownloader> _logger;
    private readonly SettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;

    public FFmpegDownloader(
        ILogger<FFmpegDownloader> logger,
        SettingsService settingsService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    public bool IsFFmpegInstalled()
    {
        var path = _settingsService.GetEffectiveFFmpegPath();
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    public async Task<string> DownloadFFmpegAsync(
        IProgress<(long downloaded, long total)> progress,
        CancellationToken ct = default)
    {
        var downloadUrl = _settingsService.Settings.FFmpegDownloadUrl;
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("FFmpeg download URL not configured");

        _logger.LogInformation("Downloading FFmpeg from: {Url}", downloadUrl);

        // Ensure tools directory exists
        if (!Directory.Exists(Constants.ToolsPath))
            Directory.CreateDirectory(Constants.ToolsPath);

        var httpClient = _httpClientFactory.CreateClient("DownloadClient");
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        _logger.LogInformation("Downloading FFmpeg from: {Url}", downloadUrl);

        // Download zip
        var zipPath = Path.Combine(Constants.ToolsPath, "ffmpeg.zip");
        using (var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            _logger.LogInformation("FFmpeg download started, total size: {Size:F1} MB", totalBytes / (1024.0 * 1024.0));

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[8192];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                progress.Report((downloaded, totalBytes));
            }
        }

        _logger.LogInformation("FFmpeg downloaded, extracting...");

        // Extract zip
        var extractDir = Path.Combine(Constants.ToolsPath, "ffmpeg_extract");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);

        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // Find ffmpeg.exe in extracted files
        var ffmpegExe = Directory.GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (ffmpegExe == null)
            throw new InvalidOperationException("ffmpeg.exe not found in downloaded archive");

        // Move to tools directory
        var targetPath = Constants.FFmpegDefaultPath;
        if (File.Exists(targetPath))
            File.Delete(targetPath);

        File.Move(ffmpegExe, targetPath);

        // Cleanup
        try
        {
            File.Delete(zipPath);
            Directory.Delete(extractDir, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup FFmpeg download artifacts");
        }

        _logger.LogInformation("FFmpeg installed to: {Path}", targetPath);
        _settingsService.Settings.FFmpegPath = targetPath;
        _settingsService.Save();

        return targetPath;
    }

    public async Task<bool> ValidateInstallationAsync()
    {
        var path = _settingsService.GetEffectiveFFmpegPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            var ffmpegService = new FFmpegService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FFmpegService>.Instance,
                _settingsService);
            return await ffmpegService.ValidateFFmpegAsync(path);
        }
        catch
        {
            return false;
        }
    }
}
