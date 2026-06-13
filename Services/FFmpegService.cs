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
    /// Detailed log from the last GPU detection attempt. Read by the Settings UI
    /// to show the user WHY an encoder was not detected (e.g. driver too old).
    /// </summary>
    public string LastGPUDetectionLog { get; private set; } = "";

    /// <summary>
    /// Detect available GPU encoders in FFmpeg. Returns the first working one, or null.
    /// Priority: h264_nvenc (NVIDIA) > h264_amf (AMD) > h264_qsv (Intel Quick Sync)
    /// Populates LastGPUDetectionLog with diagnostic details for the UI.
    /// </summary>
    public async Task<string?> DetectGPUEncoderAsync(string ffmpegPath)
    {
        var log = new StringBuilder();
        LastGPUDetectionLog = "";

        try
        {
            // Phase 1: List available encoders
            var listPsi = new ProcessStartInfo(ffmpegPath, "-encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var listProcess = Process.Start(listPsi);
            if (listProcess == null)
            {
                log.AppendLine("Failed to start ffmpeg -encoders");
                LastGPUDetectionLog = log.ToString();
                return null;
            }

            var stdout = await listProcess.StandardOutput.ReadToEndAsync();
            var stderr = await listProcess.StandardError.ReadToEndAsync();
            await listProcess.WaitForExitAsync();

            // Match encoder names at word boundaries to avoid false positives
            var gpuEncoders = new[] { "h264_nvenc", "h264_amf", "h264_qsv" };
            var listed = new List<string>();
            foreach (var enc in gpuEncoders)
            {
                if (Regex.IsMatch(stdout, $@"\b{enc}\b"))
                    listed.Add(enc);
            }

            if (listed.Count == 0)
            {
                log.AppendLine("No GPU encoders found in ffmpeg -encoders output");
                LastGPUDetectionLog = log.ToString();
                return null;
            }

            log.AppendLine($"Listed encoders: {string.Join(", ", listed)}");

            // Phase 2: Test each listed encoder with multiple parameter combos
            foreach (var encoder in listed)
            {
                log.AppendLine($"\n--- Testing {encoder} ---");
                var testArgSets = GetTestArgSets(encoder);

                for (var i = 0; i < testArgSets.Length; i++)
                {
                    var (args, label) = testArgSets[i];
                    log.AppendLine($"  Attempt {i + 1} ({label}): {args}");

                    var (success, errOutput) = await RunTestEncodeAsync(ffmpegPath, args);
                    if (success)
                    {
                        log.AppendLine($"  SUCCESS: {encoder} verified");
                        _logger.LogInformation("GPU encoder detected and verified: {Encoder}", encoder);
                        LastGPUDetectionLog = log.ToString();
                        return encoder;
                    }

                    // Extract the key error lines for diagnostics
                    var keyErrors = ExtractKeyErrors(errOutput);
                    log.AppendLine($"  FAILED: {keyErrors}");
                }
            }

            _logger.LogInformation("No working GPU encoder found. Detection log:\n{Log}", log);
            LastGPUDetectionLog = log.ToString();
            return null;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Exception: {ex.Message}");
            LastGPUDetectionLog = log.ToString();
            return null;
        }
    }

    /// <summary>
    /// Run a single test encode and return (success, stderr).
    /// Reads stdout and stderr concurrently to avoid deadlocks.
    /// </summary>
    private async Task<(bool success, string stderr)> RunTestEncodeAsync(string ffmpegPath, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return (false, "Failed to start process");

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(stderrTask, stdoutTask);
            await process.WaitForExitAsync();

            return (process.ExitCode == 0, stderrTask.Result);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Extract the most informative error lines from FFmpeg stderr output.
    /// Skips boilerplate lines and focuses on encoder-specific errors.
    /// </summary>
    private static string ExtractKeyErrors(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "(no error output)";

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Where(l => !l.StartsWith("Press [q]") && !l.StartsWith("Output #")
                     && !l.StartsWith("Stream mapping") && !l.StartsWith("frame=")
                     && !l.Contains("Press [?]"))
            .Take(3)
            .ToList();

        return lines.Count > 0 ? string.Join(" | ", lines) : "(no meaningful errors)";
    }

    /// <summary>
    /// Get multiple test argument sets for each encoder, ordered from most
    /// specific to most basic. If the first fails (e.g. unsupported preset),
    /// simpler fallbacks are tried.
    /// </summary>
    private static (string args, string label)[] GetTestArgSets(string encoder) => encoder switch
    {
        "h264_nvenc" =>
        [
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -preset fast -rc vbr -cq 23 -f null NUL",
             "preset=fast, rc=vbr, cq=23"),
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -rc constqp -qp 28 -f null NUL",
             "rc=constqp, qp=28"),
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -f null NUL",
             "defaults"),
        ],
        "h264_amf" =>
        [
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -quality balanced -f null NUL",
             "quality=balanced"),
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -usage transcoding -f null NUL",
             "usage=transcoding"),
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -f null NUL",
             "defaults"),
        ],
        "h264_qsv" =>
        [
            ($"-y -init_hw_device qsv=hw -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -global_quality 23 -f null NUL",
             "init_hw, global_quality=23"),
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -f null NUL",
             "defaults"),
        ],
        _ =>
        [
            ($"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v {encoder} -f null NUL", "defaults"),
        ],
    };

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

        // Try encoding; if GPU encoder fails, fall back to libx264
        var result = await TryEncodeAsync(
            ffmpegPath, concatFile, tempFile, videoEncoder, settings,
            progress, totalDurationSeconds, ct);

        if (!result.Success && videoEncoder != "libx264")
        {
            _logger.LogWarning("GPU encoder {Encoder} failed, falling back to libx264", videoEncoder);
            result = await TryEncodeAsync(
                ffmpegPath, concatFile, tempFile, "libx264", settings,
                progress, totalDurationSeconds, ct);

            if (result.Success)
            {
                // Update settings to avoid future GPU attempts
                settings.FFmpegUseGPU = false;
                _settingsService.Save();
            }
        }

        if (!result.Success)
        {
            _logger.LogError("FFmpeg failed with exit code {Code}: {Output}",
                result.ExitCode, result.Stderr);
            throw new InvalidOperationException($"FFmpeg failed: {result.Stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))}");
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

    /// <summary>
    /// Run a single FFmpeg encode attempt. Returns success status and stderr output.
    /// </summary>
    private async Task<EncodeResult> TryEncodeAsync(
        string ffmpegPath,
        string concatFile,
        string outputFile,
        string videoEncoder,
        AppSettings settings,
        IProgress<double> progress,
        double totalDurationSeconds,
        CancellationToken ct)
    {
        var args = BuildFFmpegArgs(concatFile, outputFile, videoEncoder, settings);
        _logger.LogInformation("FFmpeg command: {Path} {Args}", ffmpegPath, args);

        var psi = new ProcessStartInfo(ffmpegPath, args)
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
        return new EncodeResult(exitCode, stderrOutput.ToString());
    }

    private string BuildFFmpegArgs(string concatFile, string outputFile, string videoEncoder, AppSettings settings)
    {
        var encoderArgs = BuildEncoderArgs(videoEncoder, settings, includeInitDevice: true);
        return $"-y -f concat -safe 0 -i {GetShortPath(concatFile)} {encoderArgs} " +
               $"-c:a aac -b:a 128k -movflags +faststart {GetShortPath(outputFile)}";
    }

    /// <summary>
    /// Build encoder-specific FFmpeg arguments for video encoding.
    /// Shared between MergeAndTranscode and MergeAndRemux paths.
    /// </summary>
    private static string BuildEncoderArgs(string videoEncoder, AppSettings settings, bool includeInitDevice)
    {
        if (videoEncoder == "libx264")
        {
            var preset = settings.FFmpegPreset != "default" ? $"-preset {settings.FFmpegPreset} " : "";
            var crf = settings.FFmpegCRF > 0 ? settings.FFmpegCRF : 23;
            return $"-c:v libx264 {preset}-crf {crf}";
        }
        else if (videoEncoder == "h264_nvenc")
        {
            var preset = MapPresetToNVENCStatic(settings.FFmpegPreset);
            var cq = settings.FFmpegCRF > 0 ? settings.FFmpegCRF : 23;
            return $"-c:v h264_nvenc -preset {preset} -rc vbr -cq {cq}";
        }
        else if (videoEncoder == "h264_amf")
        {
            return "-c:v h264_amf -quality balanced";
        }
        else if (videoEncoder == "h264_qsv")
        {
            var preset = settings.FFmpegPreset != "default" ? $"-preset {settings.FFmpegPreset} " : "";
            var quality = settings.FFmpegCRF > 0 ? settings.FFmpegCRF : 23;
            var initDevice = includeInitDevice ? "-init_hw_device qsv=hw " : "";
            return $"{initDevice}-c:v h264_qsv {preset}-global_quality {quality}";
        }
        return $"-c:v {videoEncoder}";
    }

    private static string MapPresetToNVENCStatic(string preset) => preset.ToLowerInvariant() switch
    {
        "ultrafast" or "superfast" or "veryfast" or "faster" => "fast",
        "slow" => "slow",
        "slower" or "veryslow" => "slow",
        _ => "medium"
    };

    private record EncodeResult(int ExitCode, string Stderr)
    {
        public bool Success => ExitCode == 0;
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
        var encoderArgs = BuildEncoderArgs(videoEncoder, settings, includeInitDevice: true);

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
