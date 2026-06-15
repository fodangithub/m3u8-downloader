using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;
using M3U8Downloader.Services;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly TaskManager _taskManager;
    private readonly SettingsService _settingsService;
    private readonly FFmpegDownloader _ffmpegDownloader;
    private readonly FFmpegService _ffmpegService;

    public ObservableCollection<DownloadTask> Tasks => _taskManager.Tasks;

    [ObservableProperty]
    private string totalSpeedText = "";

    [ObservableProperty]
    private int activeTaskCount;

    [ObservableProperty]
    private int completedTaskCount;

    [ObservableProperty]
    private bool isFFmpegInstalled;

    [ObservableProperty]
    private string ffmpegStatusText = "";

    [ObservableProperty]
    private double _ffmpegDownloadProgress;

    [ObservableProperty]
    private bool isDownloadingFFmpeg;

    [ObservableProperty]
    private bool showFFmpegPrompt;

    [ObservableProperty]
    private string _selectedFFmpegVersion = "stable_7_1";

    [ObservableProperty]
    private string _ffmpegVersionDescription = "";

    public IReadOnlyList<FFmpegVersionInfo> AvailableFFmpegVersions => Constants.FFmpegVersions;

    public ICommand AddTaskCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand DownloadFFmpegCommand { get; }
    public ICommand OpenTaskDetailCommand { get; }
    public ICommand PauseTaskCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RetryTaskCommand { get; }

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        TaskManager taskManager,
        SettingsService settingsService,
        FFmpegDownloader ffmpegDownloader,
        FFmpegService ffmpegService)
    {
        _logger = logger;
        _taskManager = taskManager;
        _settingsService = settingsService;
        _ffmpegDownloader = ffmpegDownloader;
        _ffmpegService = ffmpegService;

        AddTaskCommand = new AsyncRelayCommand(AddTaskAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        StartAllCommand = new RelayCommand(StartAll);
        PauseAllCommand = new RelayCommand(PauseAll);
        DownloadFFmpegCommand = new AsyncRelayCommand(DownloadFFmpegAsync);
        OpenTaskDetailCommand = new RelayCommand<DownloadTask>(task =>
        {
            if (task != null) OpenTaskDetail(task);
        });
        PauseTaskCommand = new RelayCommand<DownloadTask>(task =>
        {
            if (task != null)
            {
                if (task.Status == TaskStatus.Downloading)
                    _taskManager.PauseTask(task);
                else if (task.Status == TaskStatus.Paused)
                    _ = _taskManager.ResumeTaskAsync(task);
            }
        });
        OpenFolderCommand = new RelayCommand<DownloadTask>(task =>
        {
            if (task != null)
            {
                var dir = task.SaveDirectory;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show($"Output folder does not exist:\n{dir}",
                        "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        });
        RetryTaskCommand = new AsyncRelayCommand<DownloadTask>(async task =>
        {
            if (task == null) return;

            foreach (var seg in task.Segments.Where(s => s.Status == SegmentStatus.Failed))
            {
                seg.Status = SegmentStatus.Pending;
                seg.RetryCount = 0;
                seg.ErrorMessage = "";
            }

            task.MergeProgress = 0;
            task.ErrorMessage = "";

            await _taskManager.ResumeTaskAsync(task);
        });

        CheckFFmpegStatus();
        SelectedFFmpegVersion = string.IsNullOrEmpty(_settingsService.Settings.FFmpegVersion)
            ? "stable_7_1" : _settingsService.Settings.FFmpegVersion;
        UpdateFFmpegVersionDescription();

        // Update stats periodically
        _ = UpdateStatsAsync();
    }

    partial void OnSelectedFFmpegVersionChanged(string value)
    {
        UpdateFFmpegVersionDescription();
    }

    private void UpdateFFmpegVersionDescription()
    {
        var version = Constants.GetFFmpegVersion(SelectedFFmpegVersion);
        FfmpegVersionDescription = version.Description;
    }

    private void CheckFFmpegStatus()
    {
        IsFFmpegInstalled = _ffmpegDownloader.IsFFmpegInstalled();
        FfmpegStatusText = IsFFmpegInstalled ? "FFmpeg is ready" : "FFmpeg not found";
        ShowFFmpegPrompt = !IsFFmpegInstalled;
    }

    private async Task DownloadFFmpegAsync()
    {
        if (IsDownloadingFFmpeg) return;

        try
        {
            IsDownloadingFFmpeg = true;
            FfmpegStatusText = "Downloading FFmpeg...";

            // Set download URL from selected version
            _settingsService.Settings.FFmpegDownloadUrl = Constants.GetFFmpegDownloadUrl(SelectedFFmpegVersion);
            _settingsService.Settings.FFmpegVersion = SelectedFFmpegVersion;

            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                    FfmpegDownloadProgress = (double)p.downloaded / p.total * 100;
            });

            await _ffmpegDownloader.DownloadFFmpegAsync(progress);

            // Validate
            var isValid = await _ffmpegDownloader.ValidateInstallationAsync();
            if (isValid)
            {
                IsFFmpegInstalled = true;
                FfmpegStatusText = "FFmpeg installed successfully!";
                ShowFFmpegPrompt = false;
            }
            else
            {
                FfmpegStatusText = "FFmpeg download completed but validation failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download FFmpeg");
            FfmpegStatusText = $"FFmpeg download failed: {ex.Message}";
            MessageBox.Show($"Failed to download FFmpeg:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsDownloadingFFmpeg = false;
        }
    }

    private async Task AddTaskAsync()
    {
        try
        {
            var dialog = new Views.AddTaskDialog
            {
                DataContext = new AddTaskDialogViewModel(
                    _taskManager, _settingsService,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<AddTaskDialogViewModel>.Instance)
            };

            if (dialog.ShowDialog() == true)
            {
                _logger.LogInformation("New task added via dialog");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open AddTaskDialog");
            System.Windows.MessageBox.Show($"Failed to open dialog:\n{ex.Message}\n\n{ex.InnerException?.Message}",
                "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        var vm = new SettingsViewModel(
            _settingsService, _ffmpegService, _ffmpegDownloader, _taskManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsViewModel>.Instance);
        var dialog = new Views.SettingsWindow { DataContext = vm };
        dialog.ShowDialog();
    }

    private void StartAll()
    {
        foreach (var task in Tasks.Where(t => t.Status is TaskStatus.Paused or TaskStatus.Queued))
        {
            if (task.Status == TaskStatus.Paused)
                _ = _taskManager.ResumeTaskAsync(task);
            else
                _ = _taskManager.StartTaskAsync(task);
        }
    }

    private void PauseAll()
    {
        foreach (var task in Tasks.Where(t => t.Status == TaskStatus.Downloading))
        {
            _taskManager.PauseTask(task);
        }
    }

    private readonly Dictionary<string, Views.TaskDetailView> _taskDetailWindows = new();

    private async Task UpdateStatsAsync()
    {
        while (true)
        {
            await Task.Delay(1000);

            try
            {
                ActiveTaskCount = Tasks.Count(t => t.Status == TaskStatus.Downloading);
                CompletedTaskCount = Tasks.Count(t => t.Status == TaskStatus.Completed);

                var totalSpeed = Tasks
                    .Where(t => t.Status == TaskStatus.Downloading)
                    .Sum(t => t.DownloadSpeed);

                TotalSpeedText = FormatSpeed(totalSpeed);
            }
            catch { }
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond / 1024 / 1024:F2} MB/s";
    }

    public void OpenTaskDetail(DownloadTask task)
    {
        if (_taskDetailWindows.TryGetValue(task.Id, out var existing))
        {
            existing.Activate();
            return;
        }

        var vm = new TaskDetailViewModel(task, _taskManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskDetailViewModel>.Instance);
        var window = new Views.TaskDetailView { DataContext = vm };
        window.Closed += (s, e) => _taskDetailWindows.Remove(task.Id);
        _taskDetailWindows[task.Id] = window;
        window.Show();
    }
}
