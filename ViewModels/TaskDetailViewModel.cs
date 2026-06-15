using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using M3U8Downloader.Models;
using M3U8Downloader.Services;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.ViewModels;

public partial class TaskDetailViewModel : ObservableObject
{
    private readonly DownloadTask _task;
    private readonly TaskManager _taskManager;
    private readonly ILogger<TaskDetailViewModel> _logger;

    public string TaskId => _task.Id;
    public string SourceUrl => _task.SourceUrl;
    public string FileName => _task.FileName;
    public string SaveDirectory => _task.SaveDirectory;
    public string OutputFilePath => _task.OutputFilePath;

    [ObservableProperty]
    private TaskStatus status;

    [ObservableProperty]
    private int totalSegments;

    [ObservableProperty]
    private int completedSegments;

    [ObservableProperty]
    private int failedSegments;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string downloadSpeedText = "";

    [ObservableProperty]
    private string sizeText = "";

    [ObservableProperty]
    private string remainingTimeText = "";

    [ObservableProperty]
    private double mergeProgress;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private string errorMessage = "";

    public ObservableCollection<DownloadSegment> Segments => _task.Segments;

    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RetryAllCommand { get; }
    public ICommand RetrySegmentCommand { get; }

    public TaskDetailViewModel(
        DownloadTask task,
        TaskManager taskManager,
        ILogger<TaskDetailViewModel> logger)
    {
        _task = task;
        _taskManager = taskManager;
        _logger = logger;

        PauseResumeCommand = new RelayCommand(TogglePauseResume);
        CancelCommand = new RelayCommand(CancelTask);
        RetryCommand = new AsyncRelayCommand(RetryFailedAsync);
        RetryAllCommand = new AsyncRelayCommand(RetryAllFailedAsync);
        RetrySegmentCommand = new RelayCommand<object>(RetrySegment);

        // Subscribe to task changes
        _task.PropertyChanged += (_, e) => UpdateFromTask();
        UpdateFromTask();

        // Start update loop
        _ = UpdateLoopAsync();
    }

    private void UpdateFromTask()
    {
        Status = _task.Status;
        TotalSegments = _task.TotalSegments;
        CompletedSegments = _task.CompletedSegments;
        FailedSegments = _task.FailedSegments;
        Progress = _task.Progress;
        DownloadSpeedText = _task.DownloadSpeedText;
        SizeText = _task.SizeText;
        RemainingTimeText = _task.RemainingTimeText;
        MergeProgress = _task.MergeProgress;
        StatusText = _task.StatusText;
        ErrorMessage = _task.ErrorMessage;
        OnPropertyChanged(nameof(PauseResumeButtonText));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }

    private void TogglePauseResume()
    {
        if (_task.Status == TaskStatus.Downloading)
            _taskManager.PauseTask(_task);
        else if (_task.Status == TaskStatus.Paused)
            _ = _taskManager.ResumeTaskAsync(_task);
    }

    private void CancelTask()
    {
        _taskManager.CancelTask(_task);
    }

    private async Task RetryFailedAsync()
    {
        foreach (var segment in _task.Segments.Where(s => s.Status == SegmentStatus.Failed))
        {
            segment.Status = SegmentStatus.Pending;
            segment.RetryCount = 0;
            segment.ErrorMessage = "";
        }

        await _taskManager.ResumeTaskAsync(_task);
    }

    private async Task RetryAllFailedAsync()
    {
        foreach (var segment in _task.Segments.Where(s => s.Status == SegmentStatus.Failed))
        {
            segment.Status = SegmentStatus.Pending;
            segment.RetryCount = 0;
            segment.ErrorMessage = "";
        }
        _task.MergeProgress = 0;
        _task.ErrorMessage = "";

        await _taskManager.ResumeTaskAsync(_task);
    }

    private void RetrySegment(object? parameter)
    {
        if (parameter == null) return;
        int segmentIndex;
        try { segmentIndex = Convert.ToInt32(parameter); }
        catch { return; }

        var segment = _task.Segments.FirstOrDefault(s => s.Index == segmentIndex);
        if (segment == null || segment.Status is not SegmentStatus.Failed) return;

        segment.Status = SegmentStatus.Pending;
        segment.RetryCount = 0;
        segment.ErrorMessage = "";
        _task.ErrorMessage = "";

        _ = _taskManager.ResumeTaskAsync(_task);
    }

    private async Task UpdateLoopAsync()
    {
        while (true)
        {
            await Task.Delay(500);
            UpdateFromTask();
        }
    }

    public string PauseResumeButtonText =>
        Status == TaskStatus.Downloading ? "Pause" : "Resume";

    public bool CanPauseResume =>
        Status is TaskStatus.Downloading or TaskStatus.Paused;

    public bool CanCancel =>
        Status is TaskStatus.Downloading or TaskStatus.Paused or TaskStatus.Queued;

    public bool CanRetry =>
        Status == TaskStatus.Failed && FailedSegments > 0;
}
