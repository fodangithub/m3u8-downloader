using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public partial class FFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly SettingsService _settingsService;

    public FFmpegService(ILogger<FFmpegService> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    public string? GetFFmpegPath()
    {
        var path = _settingsService.GetEffectiveFFmpegPath();
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    /// <summary>
    /// Detect available GPU encoders in FFmpeg. Returns the first one found, or null.
    /// Priority: h264_nvenc (NVIDIA) > h264_amf (AMD) > h264_qsv (Intel Quick Sync)
    /// </summary>
    public async Task<string?> DetectGPUEncoderAsync(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Check in priority order
            var gpuEncoders = new[] { "h264_nvenc", "h264_amf", "h264_qsv" };
            foreach (var encoder in gpuEncoders)
            {
                if (output.Contains(encoder))
                {
                    _logger.LogInformation("GPU encoder detected: {Encoder}", encoder);
                    return encoder;
                }
            }

            _logger.LogInformation("No GPU encoder detected, will use CPU (libx264)");
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ValidateFFmpegAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && output.Contains("ffmpeg version");
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetVersionAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return "unknown";

            var output = await process.StandardOutput.ReadLineAsync() ?? "unknown";
            await process.WaitForExitAsync();

            var match = VersionRegex().Match(output);
            return match.Success ? match.Groups[1].Value : output;
        }
        catch
        {
            return "unknown";
        }
    }

    public async Task MergeAndTranscodeAsync(
        string concatFile,
        string outputPath,
        IProgress<double> progress,
        double totalDurationSeconds,
        CancellationToken ct = default)
    {
        var ffmpegPath = GetFFmpegPath();
        if (ffmpegPath == null)
            throw new InvalidOperationException("FFmpeg not found");

        var settings = _settingsService.Settings;
        var outputDir = Path.GetDirectoryName(outputPath) ?? ".";
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Use a random temp filename to avoid FFmpeg parsing issues with special chars
        var tempFile = Path.Combine(outputDir, $"_m3u8_tmp_{Guid.NewGuid():N}.mp4");

        // Determine video encoder
        var videoEncoder = DetermineVideoEncoder(settings);

        // Build FFmpeg arguments - use short paths to avoid space/quote issues on Windows
        var args = new StringBuilder();
        args.Append("-y "); // Overwrite output
        args.Append("-f concat -safe 0 ");
        args.Append("-i ").Append(GetShortPath(concatFile)).Append(' ');

        if (videoEncoder == "libx264")
        {
            args.Append("-c:v libx264 ");
            args.Append("-preset ").Append(settings.FFmpegPreset).Append(' ');
            args.Append("-crf ").Append(settings.FFmpegCRF).Append(' ');
        }
        else
        {
            // GPU encoder - don't use preset/CRF, use -cq for quality
            args.Append("-c:v ").Append(videoEncoder).Append(' ');
            args.Append("-cq ").Append(settings.FFmpegCRF).Append(' ');
        }

        args.Append("-c:a aac -b:a 128k ");
        args.Append("-movflags +faststart ");
        args.Append(GetShortPath(tempFile));

        _logger.LogInformation("FFmpeg command: {Path} {Args}", ffmpegPath, args.ToString());

        var psi = new ProcessStartInfo(ffmpegPath, args.ToString())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>();

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try { tcs.TrySetResult(process.ExitCode); }
            catch { tcs.TrySetException(new InvalidOperationException("Process exited unexpectedly")); }
        };

        var stderrOutput = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrOutput.AppendLine(e.Data);
                if (totalDurationSeconds > 0)
                {
                    var pct = ParseFFmpegProgress(e.Data, totalDurationSeconds);
                    if (pct >= 0)
                        progress.Report(Math.Min(pct, 99));
                }
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start FFmpeg");

        process.BeginErrorReadLine();

        ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled();
        });

        var exitCode = await tcs.Task;

        if (exitCode != 0)
        {
            _logger.LogError("FFmpeg failed with exit code {Code}: {Output}",
                exitCode, stderrOutput.ToString());
            throw new InvalidOperationException($"FFmpeg failed: {stderrOutput.ToString().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))}");
        }

        // Rename temp file to final output name
        if (File.Exists(tempFile))
        {
            // Delete existing output file if any
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(tempFile, outputPath);
            _logger.LogInformation("Renamed temp file to: {Path}", outputPath);
        }

        progress.Report(100);
    }

    public async Task MergeConcatAsync(
        List<string> segmentFiles,
        string concatOutputPath,
        CancellationToken ct = default)
    {
        // Simple binary concatenation of TS segments
        _logger.LogInformation("Concatenating {Count} segments to {Path}",
            segmentFiles.Count, concatOutputPath);

        using var output = new FileStream(concatOutputPath, FileMode.Create, FileAccess.Write);
        foreach (var file in segmentFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                _logger.LogWarning("Segment file not found, skipping: {File}", file);
                continue;
            }
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read);
            await input.CopyToAsync(output, ct);
        }
    }

    public async Task MergeAndRemuxAsync(
        string videoFile,
        string audioFile,
        string outputPath,
        IProgress<double> progress,
        double totalDurationSeconds,
        CancellationToken ct = default)
    {
        var ffmpegPath = GetFFmpegPath();
        if (ffmpegPath == null)
            throw new InvalidOperationException("FFmpeg not found");

        var settings = _settingsService.Settings;
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var videoEncoder = DetermineVideoEncoder(settings);
        var encoderArgs = videoEncoder == "libx264"
            ? $"-c:v libx264 -preset {settings.FFmpegPreset} -crf {settings.FFmpegCRF}"
            : $"-c:v {videoEncoder} -cq {settings.FFmpegCRF}";

        var args = $"-y -i {GetShortPath(videoFile)} -i {GetShortPath(audioFile)} " +
                   $"{encoderArgs} " +
                   $"-c:a aac -b:a 128k -map 0:v -map 1:a " +
                   $"-movflags +faststart {GetShortPath(outputPath)}";

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && totalDurationSeconds > 0)
            {
                var pct = ParseFFmpegProgress(e.Data, totalDurationSeconds);
                if (pct >= 0) progress.Report(Math.Min(pct, 99));
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start FFmpeg");

        process.BeginErrorReadLine();

        ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled();
        });

        var exitCode = await tcs.Task;
        if (exitCode != 0)
            throw new InvalidOperationException("FFmpeg remux failed");

        progress.Report(100);
    }

    public static double ParseFFmpegProgress(string line, double totalDuration)
    {
        if (totalDuration <= 0) return -1;

        var match = TimeRegex().Match(line);
        if (!match.Success) return -1;

        var timeStr = match.Groups[1].Value;
        var parts = timeStr.Split(':');
        if (parts.Length != 3) return -1;

        if (double.TryParse(parts[0], out var hours) &&
            double.TryParse(parts[1], out var minutes) &&
            double.TryParse(parts[2], out var seconds))
        {
            var currentTime = hours * 3600 + minutes * 60 + seconds;
            return (currentTime / totalDuration) * 100;
        }

        return -1;
    }

    /// <summary>
    /// Returns the video encoder to use based on settings.
    /// If GPU mode is enabled and a GPU encoder is configured, returns it.
    /// Otherwise returns libx264.
    /// </summary>
    private string DetermineVideoEncoder(AppSettings settings)
    {
        if (settings.FFmpegUseGPU && !string.IsNullOrEmpty(settings.FFmpegGPUEncoder))
        {
            _logger.LogInformation("Using GPU encoder: {Encoder}", settings.FFmpegGPUEncoder);
            return settings.FFmpegGPUEncoder;
        }
        return "libx264";
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"time=(\d{2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();

    /// <summary>
    /// Converts a path to its short (8.3) form to avoid issues with spaces and special characters in FFmpeg arguments.
    /// For non-existent files, shortens the parent directory and appends the filename.
    /// </summary>
    private static string GetShortPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var sb = new System.Text.StringBuilder(260);
                if (GetShortPathName(path, sb, sb.Capacity) > 0)
                    return sb.ToString();
            }
            else if (Directory.Exists(path))
            {
                var sb = new System.Text.StringBuilder(260);
                if (GetShortPathName(path, sb, sb.Capacity) > 0)
                    return sb.ToString();
            }
            else
            {
                // For non-existent files, shorten the parent directory and append filename
                var dir = Path.GetDirectoryName(path);
                var fileName = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var sb = new System.Text.StringBuilder(260);
                    if (GetShortPathName(dir, sb, sb.Capacity) > 0)
                        return Path.Combine(sb.ToString(), fileName);
                }
            }
        }
        catch { }
        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int shortPathLength);
}
