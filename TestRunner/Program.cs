using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using M3U8Downloader.Models;
using M3U8Downloader.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var testUrl = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";
var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "M3U8Downloader_Test");
Directory.CreateDirectory(outputDir);

// Initialize logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("=== M3U8 Downloader Integration Test ===");
Log.Information("Test URL: {Url}", testUrl);

// Setup DI
var services = new ServiceCollection();
services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(); b.SetMinimumLevel(LogLevel.Debug); });
services.AddSingleton<SettingsService>();
services.AddHttpClient("DownloadClient", (sp, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
});
services.AddSingleton<M3U8Parser>();
services.AddSingleton<AesDecryptionService>();
services.AddSingleton<DownloadEngine>();
services.AddSingleton<FFmpegService>();
services.AddSingleton<FFmpegDownloader>();
services.AddSingleton<MergeService>();
services.AddSingleton<TaskManager>();

var sp = services.BuildServiceProvider();

// Test 1: Parse M3U8
Log.Information("--- Test 1: Parse M3U8 ---");
var parser = sp.GetRequiredService<M3U8Parser>();
var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadClient");

try
{
    var playlist = await parser.ParseFromUrlAsync(testUrl, httpClient);
    Log.Information("Master playlist: {IsMaster}, Variants: {Count}", playlist.IsMaster, playlist.Variants.Count);

    if (playlist.IsMaster && playlist.Variants.Count > 0)
    {
        var variant = playlist.Variants[0];
        Log.Information("Selecting variant: {Name}, {Resolution}, {Bandwidth}bps",
            variant.Name, variant.Resolution, variant.Bandwidth);

        var mediaPlaylist = await parser.ResolveMasterPlaylistAsync(playlist, variant, httpClient);
        Log.Information("Media playlist: {Segments} segments, {Duration:F0}s total duration",
            mediaPlaylist.Segments.Count, mediaPlaylist.TotalDuration);

        // Test 2: Download segments
        Log.Information("--- Test 2: Download Segments ---");
        var downloadEngine = sp.GetRequiredService<DownloadEngine>();

        var task = new DownloadTask
        {
            Id = "test_" + Guid.NewGuid().ToString("N")[..8],
            SourceUrl = testUrl,
            FileName = "test_output",
            SaveDirectory = outputDir,
            Playlist = mediaPlaylist,
            TotalSegments = mediaPlaylist.Segments.Count
        };

        foreach (var seg in mediaPlaylist.Segments)
        {
            task.Segments.Add(new DownloadSegment
            {
                Index = seg.Index,
                Url = seg.Url,
                Duration = seg.Duration,
                Status = SegmentStatus.Pending
            });
        }

        var progress = new Progress<DownloadTask>(t =>
        {
            Log.Debug("Progress: {Completed}/{Total} ({Pct:F3}%)",
                t.CompletedSegments, t.TotalSegments, t.Progress);
        });

        var startTime = DateTime.UtcNow;
        await downloadEngine.DownloadTaskAsync(task, progress, CancellationToken.None);
        var downloadTime = DateTime.UtcNow - startTime;

        var completed = task.Segments.Count(s => s.Status == SegmentStatus.Completed);
        var failed = task.Segments.Count(s => s.Status != SegmentStatus.Completed);
        Log.Information("Download complete: {Completed} succeeded, {Failed} failed, took {Time:F1}s",
            completed, failed, downloadTime.TotalSeconds);

        if (failed > 0)
        {
            Log.Error("Some segments failed, skipping FFmpeg test");
            return;
        }

        // Test 3: FFmpeg merge
        Log.Information("--- Test 3: FFmpeg Merge ---");
        var mergeService = sp.GetRequiredService<MergeService>();
        var ffmpegService = sp.GetRequiredService<FFmpegService>();

        var ffmpegPath = ffmpegService.GetFFmpegPath();
        if (ffmpegPath == null)
        {
            Log.Warning("FFmpeg not found, downloading...");
            var downloader = sp.GetRequiredService<FFmpegDownloader>();
            var dlProgress = new Progress<(long, long)>(p => Log.Debug("FFmpeg download: {Pct:F0}%", (double)p.Item1 / p.Item2 * 100));
            await downloader.DownloadFFmpegAsync(dlProgress);
        }

        ffmpegPath = ffmpegService.GetFFmpegPath();
        if (ffmpegPath != null)
        {
            var version = await ffmpegService.GetVersionAsync(ffmpegPath);
            Log.Information("FFmpeg version: {Version}", version);

            var mergeStart = DateTime.UtcNow;
            var mergeProgress = new Progress<double>(p => Log.Debug("Merge progress: {P0:F1}%", p));
            await mergeService.MergeAsync(task, mergeProgress);
            var mergeTime = DateTime.UtcNow - mergeStart;

            Log.Information("Merge complete in {Time:F1}s", mergeTime.TotalSeconds);

            var outputFile = task.OutputFilePath;
            if (File.Exists(outputFile))
            {
                var fi = new FileInfo(outputFile);
                Log.Information("Output file: {Path}", outputFile);
                Log.Information("Output size: {Size:F1} MB", fi.Length / (1024.0 * 1024.0));
                Log.Information("=== TEST PASSED ===");
            }
            else
            {
                Log.Error("Output file not found: {Path}", outputFile);
                Log.Error("=== TEST FAILED ===");
            }
        }
        else
        {
            Log.Error("FFmpeg still not available after download attempt");
            Log.Error("=== TEST FAILED ===");
        }
    }
}
catch (Exception ex)
{
    Log.Error(ex, "Test failed with exception");
    Log.Error("=== TEST FAILED ===");
}
