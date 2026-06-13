using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using M3U8Downloader.Helpers;
using M3U8Downloader.Models;
using M3U8Downloader.Services;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.ViewModels;

public partial class AddTaskDialogViewModel : ObservableObject
{
    private readonly TaskManager _taskManager;
    private readonly SettingsService _settingsService;
    private readonly ILogger<AddTaskDialogViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private string m3u8Url = "";

    [ObservableProperty]
    private string fileName = "";

    [ObservableProperty]
    private string saveDirectory = "";

    [ObservableProperty]
    private bool isParsing;

    [ObservableProperty]
    private string parseStatusText = "";

    [ObservableProperty]
    private int selectedVariantIndex;

    [ObservableProperty]
    private string customUserAgent = "";

    [ObservableProperty]
    private string customReferer = "";

    [ObservableProperty]
    private string customCookies = "";

    public ObservableCollection<VariantStream> Variants { get; } = new();
    public bool HasVariants => Variants.Count > 0;

    public int SegmentCount { get; private set; }
    public double TotalDuration { get; private set; }
    public string SegmentInfoText => SegmentCount > 0
        ? $"Segments: {SegmentCount} | Duration: {TimeSpan.FromSeconds(TotalDuration):hh\\:mm\\:ss}"
        : "";

    public AsyncRelayCommand ParseCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDirCommand { get; }
    public ICommand PasteUrlCommand { get; }

    public Action? CloseAction { get; set; }
    public bool? DialogResult { get; private set; }

    private M3U8Playlist? _parsedPlaylist;
    private M3U8Parser? _parser;

    public AddTaskDialogViewModel(
        TaskManager taskManager,
        SettingsService settingsService,
        ILogger<AddTaskDialogViewModel> logger)
    {
        _taskManager = taskManager;
        _settingsService = settingsService;
        _logger = logger;

        SaveDirectory = settingsService.GetEffectiveSaveDirectory();

        ParseCommand = new AsyncRelayCommand(ParseUrlAsync, () => !IsParsing && !string.IsNullOrWhiteSpace(M3u8Url));
        DownloadCommand = new AsyncRelayCommand(StartDownloadAsync, () => !string.IsNullOrWhiteSpace(M3u8Url));
        CancelCommand = new RelayCommand(Cancel);
        BrowseDirCommand = new RelayCommand(BrowseDir);
        PasteUrlCommand = new RelayCommand(PasteUrl);
    }

    private async Task ParseUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(M3u8Url))
            return;

        try
        {
            IsParsing = true;
            ParseStatusText = "Parsing...";
            Variants.Clear();

            _parser = new M3U8Parser(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<M3U8Parser>.Instance);

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                string.IsNullOrWhiteSpace(CustomUserAgent)
                    ? _settingsService.Settings.DefaultUserAgent
                    : CustomUserAgent);

            if (!string.IsNullOrWhiteSpace(CustomReferer))
                httpClient.DefaultRequestHeaders.Referrer = new Uri(CustomReferer);

            if (!string.IsNullOrWhiteSpace(CustomCookies))
                httpClient.DefaultRequestHeaders.Add("Cookie", CustomCookies);

            _parsedPlaylist = await _parser.ParseFromUrlAsync(M3u8Url, httpClient);

            if (_parsedPlaylist.IsMaster)
            {
                foreach (var variant in _parsedPlaylist.Variants)
                    Variants.Add(variant);

                SelectedVariantIndex = 0;
                ParseStatusText = $"Master playlist: {Variants.Count} quality options found";

                // Auto-select first variant for segment info
                await LoadVariantInfoAsync(httpClient, _parsedPlaylist.Variants[0]);
            }
            else
            {
                SegmentCount = _parsedPlaylist.Segments.Count;
                TotalDuration = _parsedPlaylist.TotalDuration;
                ParseStatusText = $"Media playlist: {SegmentCount} segments, duration {TimeSpan.FromSeconds(TotalDuration):hh\\:mm\\:ss}";

                // Suggest file name from URL
                if (string.IsNullOrWhiteSpace(FileName))
                    FileName = FileNameHelper.InferFileName(M3u8Url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse M3U8 URL");
            ParseStatusText = $"Parse failed: {ex.Message}";
        }
        finally
        {
            IsParsing = false;
        }
    }

    private async Task LoadVariantInfoAsync(HttpClient httpClient, VariantStream variant)
    {
        try
        {
            var mediaPlaylist = await _parser!.ResolveMasterPlaylistAsync(
                _parsedPlaylist!, variant, httpClient);
            SegmentCount = mediaPlaylist.Segments.Count;
            TotalDuration = mediaPlaylist.TotalDuration;

            if (string.IsNullOrWhiteSpace(FileName))
                FileName = FileNameHelper.InferFileName(M3u8Url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load variant info");
        }
    }

    private async Task StartDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(M3u8Url))
            return;

        try
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(CustomUserAgent))
                headers["User-Agent"] = CustomUserAgent;
            if (!string.IsNullOrWhiteSpace(CustomReferer))
                headers["Referer"] = CustomReferer;
            if (!string.IsNullOrWhiteSpace(CustomCookies))
                headers["Cookie"] = CustomCookies;

            var task = await _taskManager.AddTaskAsync(
                M3u8Url,
                string.IsNullOrWhiteSpace(FileName) ? null : FileName,
                string.IsNullOrWhiteSpace(SaveDirectory) ? null : SaveDirectory,
                headers.Count > 0 ? headers : null);

            DialogResult = true;
            CloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start download");
            ParseStatusText = $"Failed: {ex.Message}";
        }
    }

    private void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }

    private void BrowseDir()
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

    private void PasteUrl()
    {
        try
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Trim whitespace and try to extract a URL
                text = text.Trim();
                // If clipboard contains spaces/newlines, take the first non-empty line
                var lines = text.Split('\n');
                if (lines.Length > 1)
                {
                    text = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? text;
                }
                M3u8Url = text;
                _logger.LogInformation("Pasted URL from clipboard: {Url}", text.Length > 80 ? text[..80] + "..." : text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read clipboard");
        }
    }
}
