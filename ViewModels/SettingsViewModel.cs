using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;
using M3U8Downloader.Services;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly FFmpegService _ffmpegService;
    private readonly FFmpegDownloader _ffmpegDownloader;
    private readonly TaskManager _taskManager;
    private readonly ILogger<SettingsViewModel> _logger;

    // General
    [ObservableProperty] private string saveDirectory = "";
    [ObservableProperty] private bool autoStartDownload;
    [ObservableProperty] private bool autoDeleteTempFiles;
    [ObservableProperty] private bool minimizeToTray;
    [ObservableProperty] private bool playSoundOnComplete;
    [ObservableProperty] private DuplicateFileAction duplicateFileAction;

    // Download
    [ObservableProperty] private int maxConcurrentTasks;
    [ObservableProperty] private int maxConcurrentMerges;
    [ObservableProperty] private int maxConcurrentSegments;
    [ObservableProperty] private int retryIntervalSeconds;
    [ObservableProperty] private int maxRetries;
    [ObservableProperty] private int timeoutSeconds;
    [ObservableProperty] private long speedLimitKBps;

    // Proxy
    [ObservableProperty] private bool proxyEnabled;
    [ObservableProperty] private string proxyHost = "";
    [ObservableProperty] private int proxyPort;
    [ObservableProperty] private string proxyUsername = "";
    [ObservableProperty] private string proxyPassword = "";
    [ObservableProperty] private ProxyType proxyType;

    // FFmpeg
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _ffmpegVersion = "";
    [ObservableProperty] private bool _isFFmpegInstalled;
    [ObservableProperty] private string _ffmpegPreset = "medium";
    [ObservableProperty] private int _ffmpegCRF;
    [ObservableProperty] private bool _ffmpegUseGPU;
    [ObservableProperty] private string _ffmpegGPUEncoder = "";
    [ObservableProperty] private string _ffmpegDetectionLog = "";
    [ObservableProperty] private string _ffmpegSelectedVersion = "stable_7_1";
    [ObservableProperty] private string _ffmpegVersionDescription = "";
    [ObservableProperty] private bool _isDownloadingFFmpeg;
    [ObservableProperty] private double _ffmpegDownloadProgress;
    [ObservableProperty] private string _ffmpegDownloadStatusText = "";

    public IReadOnlyList<FFmpegVersionInfo> AvailableFFmpegVersions => Constants.FFmpegVersions;

    // HTTP headers
    [ObservableProperty] private string defaultUserAgent = "";
    [ObservableProperty] private string defaultReferer = "";
    [ObservableProperty] private string defaultCookies = "";

    public ICommand BrowseSaveDirCommand { get; }
    public ICommand BrowseFFmpegCommand { get; }
    public ICommand DownloadFFmpegCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CheckFFmpegCommand { get; }

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    public SettingsViewModel(
        SettingsService settingsService,
        FFmpegService ffmpegService,
        FFmpegDownloader ffmpegDownloader,
        TaskManager taskManager,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _ffmpegService = ffmpegService;
        _ffmpegDownloader = ffmpegDownloader;
        _taskManager = taskManager;
        _logger = logger;

        BrowseSaveDirCommand = new RelayCommand(BrowseSaveDir);
        BrowseFFmpegCommand = new RelayCommand(BrowseFFmpeg);
        DownloadFFmpegCommand = new AsyncRelayCommand(DownloadFFmpegAsync);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        CheckFFmpegCommand = new AsyncRelayCommand(CheckFFmpegAsync);

        LoadFromSettings();
        UpdateVersionDescription();
        _ = CheckFFmpegAsync();
    }

    partial void OnFfmpegSelectedVersionChanged(string value)
    {
        UpdateVersionDescription();
    }

    private void UpdateVersionDescription()
    {
        var version = Constants.GetFFmpegVersion(FfmpegSelectedVersion);
        FfmpegVersionDescription = version.Description;
    }

    private void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        SaveDirectory = s.SaveDirectory;
        AutoStartDownload = s.AutoStartDownload;
        AutoDeleteTempFiles = s.AutoDeleteTempFiles;
        MinimizeToTray = s.MinimizeToTray;
        PlaySoundOnComplete = s.PlaySoundOnComplete;
        DuplicateFileAction = s.DuplicateFileAction;
        MaxConcurrentTasks = s.MaxConcurrentTasks;
        MaxConcurrentMerges = s.MaxConcurrentMerges;
        MaxConcurrentSegments = s.MaxConcurrentSegments;
        RetryIntervalSeconds = s.RetryIntervalSeconds;
        MaxRetries = s.MaxRetries;
        TimeoutSeconds = s.TimeoutSeconds;
        SpeedLimitKBps = s.SpeedLimitKBps;
        ProxyEnabled = s.Proxy.Enabled;
        ProxyHost = s.Proxy.Host;
        ProxyPort = s.Proxy.Port;
        ProxyUsername = s.Proxy.Username;
        ProxyPassword = s.Proxy.Password;
        ProxyType = s.Proxy.Type;
        FfmpegPath = s.FFmpegPath;
        FfmpegSelectedVersion = string.IsNullOrEmpty(s.FFmpegVersion) ? "stable_7_1" : s.FFmpegVersion;
        FfmpegPreset = s.FFmpegPreset;
        FfmpegCRF = s.FFmpegCRF;
        FfmpegUseGPU = s.FFmpegUseGPU;
        FfmpegGPUEncoder = s.FFmpegGPUEncoder;
        DefaultUserAgent = s.DefaultUserAgent;
        DefaultReferer = s.DefaultReferer;
        DefaultCookies = s.DefaultCookies;
    }

    private void Save()
    {
        var s = _settingsService.Settings;
        s.SaveDirectory = SaveDirectory;
        s.AutoStartDownload = AutoStartDownload;
        s.AutoDeleteTempFiles = AutoDeleteTempFiles;
        s.MinimizeToTray = MinimizeToTray;
        s.PlaySoundOnComplete = PlaySoundOnComplete;
        s.DuplicateFileAction = DuplicateFileAction;
        s.MaxConcurrentTasks = Math.Max(1, MaxConcurrentTasks);
        s.MaxConcurrentMerges = Math.Max(1, MaxConcurrentMerges);
        s.MaxConcurrentSegments = Math.Max(1, MaxConcurrentSegments);
        s.RetryIntervalSeconds = Math.Max(1, RetryIntervalSeconds);
        s.MaxRetries = MaxRetries;
        s.TimeoutSeconds = Math.Max(5, TimeoutSeconds);
        s.SpeedLimitKBps = Math.Max(0, SpeedLimitKBps);
        s.Proxy.Enabled = ProxyEnabled;
        s.Proxy.Host = ProxyHost;
        s.Proxy.Port = ProxyPort;
        s.Proxy.Username = ProxyUsername;
        s.Proxy.Password = ProxyPassword;
        s.Proxy.Type = ProxyType;
        s.FFmpegVersion = FfmpegSelectedVersion;
        s.FFmpegDownloadUrl = Constants.GetFFmpegDownloadUrl(FfmpegSelectedVersion);
        s.FFmpegPath = FfmpegPath;
        s.FFmpegPreset = FfmpegPreset;
        s.FFmpegCRF = Math.Clamp(FfmpegCRF, 0, 51);
        s.FFmpegUseGPU = FfmpegUseGPU;
        s.FFmpegGPUEncoder = FfmpegGPUEncoder;
        s.DefaultUserAgent = DefaultUserAgent;
        s.DefaultReferer = DefaultReferer;
        s.DefaultCookies = DefaultCookies;

        _settingsService.Save();

        _taskManager.UpdateMaxConcurrentTasks(s.MaxConcurrentTasks);
        _taskManager.UpdateMaxConcurrentMerges(s.MaxConcurrentMerges);

        DialogResult = true;
        CloseAction?.Invoke();
    }

    private void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }

    private void BrowseSaveDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Save Directory"
        };
        if (dialog.ShowDialog() == true)
        {
            SaveDirectory = dialog.FolderName;
        }
    }

    private void BrowseFFmpeg()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select FFmpeg Executable",
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            FfmpegPath = dialog.FileName;
            _ = CheckFFmpegAsync();
        }
    }

    private async Task DownloadFFmpegAsync()
    {
        if (IsDownloadingFFmpeg) return;

        try
        {
            IsDownloadingFFmpeg = true;
            FfmpegDownloadStatusText = "Downloading FFmpeg...";

            // Update the download URL to match selected version before downloading
            _settingsService.Settings.FFmpegDownloadUrl = Constants.GetFFmpegDownloadUrl(FfmpegSelectedVersion);

            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                    FfmpegDownloadProgress = (double)p.downloaded / p.total * 100;
            });

            var path = await _ffmpegDownloader.DownloadFFmpegAsync(progress);
            FfmpegPath = path;

            var isValid = await _ffmpegDownloader.ValidateInstallationAsync();
            if (isValid)
            {
                FfmpegDownloadStatusText = "FFmpeg installed successfully!";
            }
            else
            {
                FfmpegDownloadStatusText = "Downloaded but validation failed";
            }

            _ = CheckFFmpegAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download FFmpeg");
            FfmpegDownloadStatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingFFmpeg = false;
        }
    }

    private async Task CheckFFmpegAsync()
    {
        var path = _settingsService.GetEffectiveFFmpegPath();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var version = await _ffmpegService.GetVersionAsync(path);
            FfmpegVersion = version;
            IsFFmpegInstalled = true;

            if (FfmpegUseGPU)
            {
                var encoder = await _ffmpegService.DetectGPUEncoderAsync(path);
                FfmpegDetectionLog = _ffmpegService.LastGPUDetectionLog;

                if (!string.IsNullOrEmpty(encoder))
                {
                    FfmpegGPUEncoder = encoder;
                    _settingsService.Settings.FFmpegGPUEncoder = encoder;
                }
                else
                {
                    FfmpegGPUEncoder = "(none detected)";
                }
            }
            else
            {
                FfmpegGPUEncoder = "";
                FfmpegDetectionLog = "";
            }
        }
        else
        {
            FfmpegVersion = "";
            IsFFmpegInstalled = false;
            FfmpegGPUEncoder = "";
            FfmpegDetectionLog = "";
        }
    }
}
