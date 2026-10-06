using System.Runtime.InteropServices;
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

        var request = LaunchRequest.Parse(_args);

        // A launch that only acts on Home Assistant should not drag the window up, so
        // one carrying such a request starts hidden even if it would otherwise not.
        if (IsSilent(request))
        {
            startMinimized = true;
        }

        _window = new MainWindow(Settings, startMinimized);
        _window.Start();
        _window.Handle(request);
    }

    /// <summary>A second launch arrives here instead of starting another process.</summary>
    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        var request = ReadRequest(e);

        _dispatcherQueue.TryEnqueue(() =>
        {
            // Acting on Home Assistant is not a reason to interrupt what is on screen.
            if (!IsSilent(request))
            {
                _window?.ShowAndFocus();
            }

            _window?.Handle(request);
        });
    }

    /// <summary>Requests that change something without needing to show anything.</summary>
    private static bool IsSilent(LaunchRequest request) =>
        request.Kind is LaunchRequestKind.PerformOnEntity or LaunchRequestKind.CallService;

    private static LaunchRequest ReadRequest(AppActivationArguments e)
    {
        try
        {
            // Activation reports the command line as a single string, executable and
            // all, rather than the argv this process was started with.
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
            {
                return LaunchRequest.Parse(LaunchRequest.Split(launch.Arguments ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            // An activation this app does not understand is not worth failing over.
        }

        return LaunchRequest.None;
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

