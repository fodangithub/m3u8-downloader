using System.Net;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using M3U8Downloader.Services;
using M3U8Downloader.ViewModels;
using M3U8Downloader.Helpers;
using Serilog;
using Serilog.Events;

namespace M3U8Downloader;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static readonly string LogDirectory = Path.Combine(Constants.AppDataPath, "logs");
    public static readonly string LogFilePath = Path.Combine(LogDirectory, "m3u8-downloader-.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize Serilog early so any startup issues are logged
        ConfigureSerilog();

        base.OnStartup(e);

        // Catch unhandled exceptions so they get logged instead of silent crash
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");
            Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled domain exception");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Log.Information("Application started - Version {Version}", Constants.AppVersion);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureSerilog()
    {
        if (!Directory.Exists(LogDirectory))
            Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: LogEventLevel.Debug)
            .WriteTo.File(
                path: LogFilePath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 25L * 1024 * 1024, // 25 MB per file
                retainedFileCountLimit: 10,             // Keep last 10 files (up to ~250 MB total)
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging - use Serilog as the logging provider
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Settings
        services.AddSingleton<SettingsService>();

        // HTTP Client with proxy support
        services.AddHttpClient("DownloadClient", (sp, client) =>
        {
            var settings = sp.GetRequiredService<SettingsService>().Settings;
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);

            if (!string.IsNullOrWhiteSpace(settings.DefaultUserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.DefaultUserAgent);

            if (!string.IsNullOrWhiteSpace(settings.DefaultReferer))
            {
                if (Uri.TryCreate(settings.DefaultReferer, UriKind.Absolute, out var refUri))
                    client.DefaultRequestHeaders.Referrer = refUri;
            }

            if (!string.IsNullOrWhiteSpace(settings.DefaultCookies))
                client.DefaultRequestHeaders.Add("Cookie", settings.DefaultCookies);

            foreach (var header in settings.CustomHeaders)
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>().Settings;
            var handler = new HttpClientHandler();

            if (settings.Proxy.Enabled && !string.IsNullOrWhiteSpace(settings.Proxy.Host))
            {
                var proxyUri = new Uri($"{settings.Proxy.Type.ToString().ToLower()}://{settings.Proxy.Host}:{settings.Proxy.Port}");
                var proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = false
                };

                if (!string.IsNullOrWhiteSpace(settings.Proxy.Username))
                {
                    proxy.Credentials = new NetworkCredential(
                        settings.Proxy.Username, settings.Proxy.Password);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
                handler.PreAuthenticate = true;
            }

            return handler;
        });

        // Services
        services.AddSingleton<M3U8Parser>();
        services.AddSingleton<AesDecryptionService>();
        services.AddSingleton<DownloadEngine>();
        services.AddSingleton<FFmpegService>();
        services.AddSingleton<FFmpegDownloader>();
        services.AddSingleton<MergeService>();
        services.AddSingleton<TaskManager>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
    }
}
