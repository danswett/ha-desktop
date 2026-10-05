using HomeAssistant.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace HomeAssistant.Desktop;

public partial class App : Application
{
    private readonly AppInstance _instance;
    private readonly string[] _args;
    private readonly DispatcherQueue _dispatcherQueue;

    private MainWindow? _window;

    public static AppSettings Settings { get; private set; } = null!;

    public App(AppInstance instance, string[] args)
    {
        _instance = instance;
        _args = args;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Settings = AppSettings.Load();

        InitializeComponent();

        UnhandledException += OnUnhandledException;
        _instance.Activated += OnInstanceActivated;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var startMinimized = Settings.StartMinimized
            || _args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow(Settings, startMinimized);
        _window.Start();
    }

    /// <summary>A second launch arrives here instead of starting another process.</summary>
    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        _dispatcherQueue.TryEnqueue(() => _window?.ShowAndFocus());
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(AppSettings.DataDirectory, "crash.log");
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:O}] {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
