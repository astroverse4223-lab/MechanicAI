using MechanicAI.Application;
using MechanicAI.Application.Abstractions;
using MechanicAI.Desktop.Services;
using MechanicAI.Infrastructure;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Infrastructure.Platform;
using MechanicAI.Presentation;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Events;

namespace MechanicAI.Desktop;

/// <summary>
/// Application entry: builds the Generic Host (application + infrastructure + presentation +
/// WinUI services), configures Serilog file logging, installs global exception handlers,
/// initializes the local database, and shows the main window.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;
    private MainWindow? _window;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    public App()
    {
        ConfigureLogging();
        InstallExceptionHandlers();
        InitializeComponent();
    }

    /// <summary>The running host's services. Available after <see cref="OnLaunched"/> starts.</summary>
    public static IServiceProvider Services =>
        (Current as App)?._host?.Services ?? throw new InvalidOperationException("The application host is not running.");

    public static T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>The main window (used by pickers and dialogs that need a window handle or XamlRoot).</summary>
    public static MainWindow? MainWindow => (Current as App)?._window;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = BuildHost();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Mechanic AI {Version} starting", typeof(App).Assembly.GetName().Version);

            _window = new MainWindow();
            _host.Services.GetRequiredService<DispatcherService>().Attach(_window.DispatcherQueue);
            _window.Closed += OnWindowClosed;
            _window.Activate();

            await InitializeDataAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            var message = $"Mechanic AI could not start: {ex.Message}\n\nDetails were written to the log in {LogsDirectory}.";
            if (_window is not null)
            {
                _window.ShowStartupFailure(message);
            }
            else
            {
                // The host itself failed, so the main window (which needs it) cannot be shown.
                var fallback = new Window
                {
                    Title = "Mechanic AI",
                    Content = new Microsoft.UI.Xaml.Controls.TextBlock
                    {
                        Text = message,
                        Margin = new Thickness(24),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                };
                fallback.Activate();
            }
        }
    }

    private async Task InitializeDataAsync()
    {
        var shell = GetService<ShellViewModel>();
        try
        {
            await Task.Run(() => GetService<DatabaseInitializer>().InitializeAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Database initialization failed");
            shell.ReportStartupFailure(
                $"The local database could not be opened or upgraded: {ex.Message}\n\nData folder: {GetService<IAppPaths>().DataRoot}\nLog folder: {LogsDirectory}");
            return;
        }

        // Hosted services (indexing queue, connectivity monitor) start once the database exists.
        await _host!.StartAsync();
        GetService<ThemeService>().Start();
        await shell.InitializeAsync();
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environment.GetEnvironmentVariable("MECHANICAI_ENVIRONMENT") ?? Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: false);

        builder.Services.AddMechanicAiInfrastructure(o => o.Workstation = true);
        builder.Services.AddMechanicAiWorkstation();
        builder.Services.AddMechanicAiPresentation();

        // WinUI implementations of the presentation abstractions.
        builder.Services.AddSingleton<DispatcherService>();
        builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<DispatcherService>());
        builder.Services.AddSingleton<NavigationService>();
        builder.Services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IFilePicker, FilePickerService>();
        builder.Services.AddSingleton<IClipboard, ClipboardService>();
        builder.Services.AddSingleton<ILauncher, LauncherService>();
        builder.Services.AddSingleton<ThemeService>();

        return builder.Build();
    }

    private static string LogsDirectory { get; set; } = string.Empty;

    private static void ConfigureLogging()
    {
        // AppPaths honors MECHANICAI_DATA_DIR, so logs follow a portable data folder.
        LogsDirectory = new AppPaths().LogsDirectory;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogsDirectory, "mechanicai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void InstallExceptionHandlers()
    {
        // Exceptions thrown on the UI thread (event handlers, bindings). Mark handled so a single
        // failed action does not close the app; the technician sees a notice instead.
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
            ShowGlobalError("An unexpected error occurred. Details were written to the log.");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        ViewModelBase.UnhandledViewModelException += (sender, ex) =>
            Log.Error(ex, "Operation failed in {ViewModel}", sender?.GetType().Name);
    }

    private void ShowGlobalError(string message)
    {
        try
        {
            if (_host is null) return;
            var shell = _host.Services.GetService<ShellViewModel>();
            shell?.ShowNotice("Something went wrong", message, Presentation.Models.NoticeSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not display the error notice");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _logger?.LogInformation("Mechanic AI shutting down");
            var host = _host;
            if (host is not null)
            {
                // Stop background work (indexing queue, connectivity monitor, OBD stream) off the UI
                // thread, bounded so a stuck adapter can never keep the process alive.
                Task.Run(async () =>
                {
                    var live = host.Services.GetService<Application.Services.LiveDataService>();
                    if (live is not null) await live.DisposeAsync();
                    await host.StopAsync(TimeSpan.FromSeconds(5));
                    if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else host.Dispose();
                }).Wait(TimeSpan.FromSeconds(8));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
