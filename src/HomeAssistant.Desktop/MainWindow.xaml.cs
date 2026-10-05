using System.Diagnostics;
using System.Text.Json;
using HomeAssistant.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;
using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Chromium is tuned for a foreground browser tab, which is the wrong trade-off for
    /// a dashboard that spends its life behind other windows. Without the throttling
    /// flags the renderer's timers are clamped and the page quietly falls behind.
    /// </summary>
    private string[] BuildBrowserArguments()
    {
        var args = new List<string>
        {
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            // So camera cards start streaming without needing a click.
            "--autoplay-policy=no-user-gesture-required",
        };

        // Off by default: when the window is fully covered there is nothing to see, and
        // compositing a 4K surface at the display refresh rate is the single largest
        // cost this app has. Timer fidelity does not depend on it.
        if (_settings.RenderWhenCovered)
        {
            args.Add("--disable-backgrounding-occluded-windows");
        }

        if (_settings.ReduceAnimations)
        {
            args.Add("--force-prefers-reduced-motion");
        }

        return [.. args];
    }

    private const string HostKeyScript = """
        (function () {
          if (window.__haDesktopKeys) { return; }
          window.__haDesktopKeys = true;
          window.addEventListener('keydown', function (e) {
            if (e.key === 'F11') {
              e.preventDefault();
              window.chrome.webview.postMessage({ type: 'toggle-fullscreen' });
            } else if (e.key === 'Escape') {
              window.chrome.webview.postMessage({ type: 'escape' });
            }
          }, true);
        })();
        """;

    private static readonly int[] RetryDelaysSeconds = [2, 5, 10, 20, 30, 60];

    private readonly AppSettings _settings;
    private readonly bool _startMinimized;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _retryTimer;
    private readonly DispatcherQueueTimer _placementSaveTimer;
    private readonly string _iconPath;

    private WebView2? _webView;
    private TrayIcon? _tray;
    private WindowVisibilityWatcher? _visibilityWatcher;
    private int _retryAttempt;
    private bool _exiting;
    private bool _suppressMinimizeToTray;
    private bool _settingsDialogOpen;
    private string _documentTitle = "Home Assistant";

    public MainWindow(AppSettings settings, bool startMinimized)
    {
        _settings = settings;
        _startMinimized = startMinimized;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        _retryTimer = DispatcherQueue.CreateTimer();
        _retryTimer.IsRepeating = false;
        _retryTimer.Tick += (_, _) => Navigate(_settings.HomeUrl);

        // Debounced: AppWindow.Changed fires on every step of a drag or resize.
        _placementSaveTimer = DispatcherQueue.CreateTimer();
        _placementSaveTimer.IsRepeating = false;
        _placementSaveTimer.Interval = TimeSpan.FromSeconds(2);
        _placementSaveTimer.Tick += (_, _) => SavePlacement();

        Title = "Home Assistant";

        ConfigureTitleBar();
        ConfigureWindow();
        SetUpTrayIcon();

        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;

        // Chromium cannot see that a composition-hosted WebView2 is covered, so the
        // host watches for it and stops the page painting pixels nobody can see.
        _visibilityWatcher = new WindowVisibilityWatcher(
            _hwnd,
            isEnabled: () => !_settings.RenderWhenCovered,
            interval: TimeSpan.FromSeconds(2));
        _visibilityWatcher.VisibilityChanged += OnEffectiveVisibilityChanged;
    }

    /// <summary>
    /// Collapsing the control suspends rendering but leaves the page running, so
    /// timers keep firing and the dashboard is current the moment it is uncovered.
    /// </summary>
    private void OnEffectiveVisibilityChanged(bool visible)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_webView is not null)
            {
                _webView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        });
    }

    // ---- start-up ----------------------------------------------------------

    /// <summary>
    /// Sizes the window on first run, before anything has been saved.
    ///
    /// Deliberately expressed as fractions of the work area and applied through the
    /// AppWindow APIs. Mixing units here is easy to get wrong: GetMonitorInfo reports
    /// physical pixels while AppWindow and WINDOWPLACEMENT share a scaled space, so a
    /// work area measured one way and applied the other comes out wrong by the display
    /// scale factor.
    /// </summary>
    private void ApplyDefaultPlacement()
    {
        var work = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        var height = (int)(work.Height * 0.82);
        var width = Math.Min((int)(work.Width * 0.72), (int)(height * 1.9));

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    public void Start()
    {
        if (!WindowPlacement.Apply(_hwnd, _settings.Placement, _startMinimized))
        {
            ApplyDefaultPlacement();
        }

        if (_startMinimized)
        {
            // Activate first so XAML completes layout, then drop straight to the tray.
            Activate();
            _appWindow.Hide();
        }
        else
        {
            Activate();
        }

        _ = InitializeWebViewAsync();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        }

        UpdatePinIcon();

        if (File.Exists(_iconPath))
        {
            _appWindow.SetIcon(_iconPath);
            try
            {
                TitleBarIcon.Source = new BitmapImage(new Uri(_iconPath));
            }
            catch (Exception ex) when (ex is UriFormatException or FileNotFoundException)
            {
                // Decorative only.
            }
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        // RightInset is in physical pixels; the XAML tree is in effective pixels.
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var inset = (_appWindow.TitleBar.RightInset / scale) + 8;

        // AppWindow.Changed fires continuously while dragging, so only touch layout
        // when the inset has actually moved.
        if (Math.Abs(inset - TitleBarCommands.Padding.Right) < 0.5)
        {
            return;
        }

        TitleBarCommands.Padding = new Thickness(0, 0, inset, 0);
    }

    private void SetUpTrayIcon()
    {
        try
        {
            _tray = new TrayIcon("Home Assistant", _iconPath);
        }
        catch (InvalidOperationException)
        {
            // Without a tray icon the app still works; it just cannot hide itself.
            _settings.CloseToTray = false;
            _settings.MinimizeToTray = false;
            return;
        }

        _tray.ShowHideCaption = () => IsWindowShowing() ? "Hide dashboard" : "Show dashboard";
        _tray.IsChecked = command => command switch
        {
            TrayIcon.MenuCommand.AlwaysOnTop => _settings.AlwaysOnTop,
            TrayIcon.MenuCommand.StartWithWindows => StartupManager.IsEnabled(),
            _ => false,
        };
        _tray.CommandInvoked += OnTrayCommand;
    }

    // ---- WebView2 ----------------------------------------------------------

    private async Task InitializeWebViewAsync()
    {
        ShowStatus("Starting\u2026", "Preparing the isolated browser profile.", showActions: false, busy: true);

        CoreWebView2Environment environment;
        try
        {
            Directory.CreateDirectory(AppSettings.WebViewUserDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Join(' ', BuildBrowserArguments()),
            };

            // A dedicated user data folder is what makes this a wholly separate browser:
            // its own browser process, GPU process, network service and renderers.
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: string.Empty,
                userDataFolder: AppSettings.WebViewUserDataFolder,
                options: options);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not start the browser engine",
                $"{ex.Message}\n\nThe Microsoft Edge WebView2 Runtime may be missing or the profile folder may be locked.",
                showActions: true, busy: false);
            return;
        }

        var webView = new WebView2();
        WebViewHost.Children.Clear();
        WebViewHost.Children.Add(webView);
        _webView = webView;

        try
        {
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not start the browser engine", ex.Message, showActions: true, busy: false);
            return;
        }

        var core = webView.CoreWebView2;
        ConfigureCoreWebView(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HostKeyScript);

        Navigate(_settings.HomeUrl);
    }

    private void ConfigureCoreWebView(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.AreDefaultContextMenusEnabled = _settings.DevToolsEnabled;
        s.AreDevToolsEnabled = _settings.DevToolsEnabled;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = true;
        s.IsGeneralAutofillEnabled = true;
        s.IsSwipeNavigationEnabled = false;
        s.IsZoomControlEnabled = true;

        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        core.WebMessageReceived += OnWebMessageReceived;
        core.HistoryChanged += OnHistoryChanged;
    }

    private void Navigate(string url)
    {
        _retryTimer.Stop();

        if (_webView?.CoreWebView2 is not { } core)
        {
            return;
        }

        ShowStatus("Connecting\u2026", url, showActions: false, busy: true);

        try
        {
            core.Navigate(url);
        }
        catch (ArgumentException)
        {
            ShowStatus("That address is not valid", url, showActions: true, busy: false);
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _retryAttempt = 0;
            HideStatus();
            return;
        }

        // A cancelled navigation is usually the page redirecting, not a failure.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        ScheduleRetry($"Could not reach {_settings.HomeUrl} ({Describe(e.WebErrorStatus)}).");
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The environment died with it, so the control has to be rebuilt.
                ShowStatus("The browser engine stopped", "Restarting\u2026", showActions: false, busy: true);
                _ = RestartWebViewAsync();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ScheduleRetry("The dashboard page stopped responding.");
                break;
        }
    }

    private async Task RestartWebViewAsync()
    {
        DisposeWebView();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await InitializeWebViewAsync();
    }

    private void ScheduleRetry(string reason)
    {
        var delay = RetryDelaysSeconds[Math.Min(_retryAttempt, RetryDelaysSeconds.Length - 1)];
        _retryAttempt++;

        ShowStatus("Not connected", $"{reason}\n\nRetrying in {delay}s.", showActions: true, busy: false);

        _retryTimer.Stop();
        _retryTimer.Interval = TimeSpan.FromSeconds(delay);
        _retryTimer.Start();
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "host name not resolved",
        CoreWebView2WebErrorStatus.ServerUnreachable => "server unreachable",
        CoreWebView2WebErrorStatus.Timeout => "timed out",
        CoreWebView2WebErrorStatus.ConnectionAborted => "connection aborted",
        CoreWebView2WebErrorStatus.ConnectionReset => "connection reset",
        CoreWebView2WebErrorStatus.Disconnected => "disconnected",
        CoreWebView2WebErrorStatus.CannotConnect => "cannot connect",
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "invalid server response",
        _ => status.ToString(),
    };

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Keep the app on the dashboard; send anything that wants its own window to the
        // user's real browser.
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser registered; nothing sensible to fall back to.
        }
    }

    private void OnDocumentTitleChanged(CoreWebView2 sender, object args)
    {
        _documentTitle = string.IsNullOrWhiteSpace(sender.DocumentTitle) ? "Home Assistant" : sender.DocumentTitle;
        TitleBarText.Text = _documentTitle;

        // Also the OS window title, so Alt+Tab and the taskbar show the current view.
        Title = _documentTitle;

        _tray?.UpdateTooltip(_documentTitle);
    }

    private void OnHistoryChanged(CoreWebView2 sender, object args)
    {
        BackButton.IsEnabled = sender.CanGoBack;
    }

    private void OnContainsFullScreenElementChanged(CoreWebView2 sender, object args)
    {
        // A camera card going full screen should take the whole window with it.
        SetFullScreen(sender.ContainsFullScreenElement);
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            type = document.RootElement.TryGetProperty("type", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "toggle-fullscreen":
                SetFullScreen(_appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
                break;
            case "escape" when _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen:
                SetFullScreen(false);
                break;
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        var isFullScreen = _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        if (fullScreen == isFullScreen)
        {
            return;
        }

        if (fullScreen)
        {
            SavePlacement();
            TitleBarRow.Height = new GridLength(0);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Default);
            TitleBarRow.Height = GridLength.Auto;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
            }

            ConfigureTitleBar();
        }
    }

    // ---- window state ------------------------------------------------------

    private bool IsWindowShowing() =>
        _appWindow.IsVisible
        && _appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    public void ShowAndFocus()
    {
        _suppressMinimizeToTray = true;
        try
        {
            _appWindow.Show();

            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            Activate();
            SetForegroundWindow(_hwnd);
        }
        finally
        {
            // Let the restore settle before the minimise watcher is armed again.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _suppressMinimizeToTray = false);
        }
    }

    private void HideToTray()
    {
        SavePlacement();
        _appWindow.Hide();
    }

    private void ToggleVisibility()
    {
        if (IsWindowShowing())
        {
            HideToTray();
        }
        else
        {
            ShowAndFocus();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            UpdateTitleBarInsets();

            // An always-running app is as likely to be ended by a reboot as by its own
            // Exit, so don't rely on shutdown to be the moment placement is written.
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        }

        if (_exiting || _suppressMinimizeToTray || !_settings.MinimizeToTray || _tray is null)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } && sender.IsVisible)
        {
            sender.Hide();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SavePlacement();

        if (_exiting || !_settings.CloseToTray || _tray is null)
        {
            return;
        }

        // The whole point of the app is to stay running, so the close button parks it.
        args.Cancel = true;
        HideToTray();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _retryTimer.Stop();
        _placementSaveTimer.Stop();
        _visibilityWatcher?.Dispose();
        _visibilityWatcher = null;
        DisposeWebView();
        _tray?.Dispose();
        _tray = null;
    }

    private void DisposeWebView()
    {
        if (_webView is null)
        {
            return;
        }

        var webView = _webView;
        _webView = null;

        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                core.NewWindowRequested -= OnNewWindowRequested;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.ProcessFailed -= OnProcessFailed;
                core.DocumentTitleChanged -= OnDocumentTitleChanged;
                core.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.HistoryChanged -= OnHistoryChanged;
            }

            WebViewHost.Children.Clear();
            webView.Close();
        }
        catch (Exception)
        {
            // After a browser process crash the control's COM objects are already dead,
            // and tearing it down is best effort.
            WebViewHost.Children.Clear();
        }
    }

    private void SavePlacement()
    {
        // Presenter.Kind reads Overlapped for a normal window - never Default - so this
        // must exclude full screen explicitly rather than require Default.
        if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen || !_appWindow.IsVisible)
        {
            return;
        }

        var placement = WindowPlacement.Capture(_hwnd);
        if (placement is null)
        {
            return;
        }

        _settings.Placement = placement;
        _settings.Save();
    }

    private void Exit()
    {
        _exiting = true;
        SavePlacement();
        Close();
    }

    // ---- status overlay ----------------------------------------------------

    private void ShowStatus(string title, string detail, bool showActions, bool busy)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusProgress.IsActive = busy;
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusActions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusProgress.IsActive = false;
        StatusOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- commands ----------------------------------------------------------

    private void OnTrayCommand(TrayIcon.MenuCommand command)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (command)
            {
                case TrayIcon.MenuCommand.ShowHide:
                    ToggleVisibility();
                    break;
                case TrayIcon.MenuCommand.Reload:
                    ReloadNow();
                    break;
                case TrayIcon.MenuCommand.AlwaysOnTop:
                    SetAlwaysOnTop(!_settings.AlwaysOnTop);
                    break;
                case TrayIcon.MenuCommand.StartWithWindows:
                    StartupManager.SetEnabled(!StartupManager.IsEnabled());
                    break;
                case TrayIcon.MenuCommand.Settings:
                    ShowAndFocus();
                    _ = ShowSettingsDialogAsync();
                    break;
                case TrayIcon.MenuCommand.Exit:
                    Exit();
                    break;
            }
        });
    }

    private void ReloadNow()
    {
        _retryAttempt = 0;
        _retryTimer.Stop();

        if (_webView?.CoreWebView2 is { } core && core.Source is { Length: > 0 } and not "about:blank")
        {
            core.Reload();
        }
        else
        {
            Navigate(_settings.HomeUrl);
        }
    }

    private void SetAlwaysOnTop(bool value)
    {
        _settings.AlwaysOnTop = value;
        _settings.Save();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = value;
        }

        UpdatePinIcon();
    }

    private void UpdatePinIcon()
    {
        // E718 is the outline pin, E77A the filled one.
        PinIcon.Glyph = _settings.AlwaysOnTop ? "\uE77A" : "\uE718";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_webView?.CoreWebView2 is { CanGoBack: true } core)
        {
            core.GoBack();
        }
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => Navigate(_settings.HomeUrl);

    private void OnReloadClick(object sender, RoutedEventArgs e) => ReloadNow();

    private void OnPinClick(object sender, RoutedEventArgs e) => SetAlwaysOnTop(!_settings.AlwaysOnTop);

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _retryAttempt = 0;
        Navigate(_settings.HomeUrl);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = ShowSettingsDialogAsync();

    private async Task ShowSettingsDialogAsync()
    {
        if (_settingsDialogOpen)
        {
            return;
        }

        _settingsDialogOpen = true;
        try
        {
            var urlBox = new TextBox
            {
                Header = "Dashboard address",
                Text = _settings.HomeUrl,
                PlaceholderText = "http://192.168.1.188:8123",
            };

            var closeToTray = MakeToggleRow("Close button hides to the tray", _settings.CloseToTray);
            var minimizeToTray = MakeToggleRow("Minimise hides to the tray", _settings.MinimizeToTray);
            var startMinimized = MakeToggleRow("Start hidden in the tray", _settings.StartMinimized);
            var alwaysOnTop = MakeToggleRow("Always on top", _settings.AlwaysOnTop);
            var startWithWindows = MakeToggleRow("Start with Windows", StartupManager.IsEnabled());
            var devTools = MakeToggleRow("Developer tools and context menu", _settings.DevToolsEnabled);

            var openFolder = new HyperlinkButton
            {
                Content = "Open the app's data folder",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(0),
            };
            openFolder.Click += (_, _) => OpenDataFolder();

            var panel = new StackPanel { Spacing = 0, Width = 420 };
            panel.Children.Add(urlBox);
            panel.Children.Add(new TextBlock
            {
                Text = "On the LAN, use the direct address. Going through the public tunnel makes "
                     + "Home Assistant attribute every client to one WAN address, which is what trips its IP ban.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 6, 0, 10),
                FontSize = 12,
            });
            panel.Children.Add(closeToTray.Row);
            panel.Children.Add(minimizeToTray.Row);
            panel.Children.Add(startMinimized.Row);
            panel.Children.Add(alwaysOnTop.Row);
            panel.Children.Add(startWithWindows.Row);
            panel.Children.Add(devTools.Row);
            panel.Children.Add(openFolder);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Settings",
                Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var previousUrl = _settings.HomeUrl;
            var devToolsChanged = devTools.Toggle.IsOn != _settings.DevToolsEnabled;

            _settings.HomeUrl = urlBox.Text;
            _settings.CloseToTray = closeToTray.Toggle.IsOn;
            _settings.MinimizeToTray = minimizeToTray.Toggle.IsOn;
            _settings.StartMinimized = startMinimized.Toggle.IsOn;
            _settings.DevToolsEnabled = devTools.Toggle.IsOn;
            _settings.Save();

            StartupManager.SetEnabled(startWithWindows.Toggle.IsOn);
            SetAlwaysOnTop(alwaysOnTop.Toggle.IsOn);

            if (devToolsChanged && _webView?.CoreWebView2 is { } core)
            {
                core.Settings.AreDevToolsEnabled = _settings.DevToolsEnabled;
                core.Settings.AreDefaultContextMenusEnabled = _settings.DevToolsEnabled;
            }

            if (!string.Equals(previousUrl, _settings.HomeUrl, StringComparison.OrdinalIgnoreCase))
            {
                _retryAttempt = 0;
                Navigate(_settings.HomeUrl);
            }
        }
        finally
        {
            _settingsDialogOpen = false;
        }
    }

    /// <summary>
    /// A label and a switch on one line. ToggleSwitch's own Header plus its On/Off caption
    /// makes each option three lines tall, which overflows the dialog.
    /// </summary>
    private static (Grid Row, ToggleSwitch Toggle) MakeToggleRow(string label, bool isOn)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(toggle, label);

        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(text);
        row.Children.Add(toggle);

        return (row, toggle);
    }

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            Process.Start(new ProcessStartInfo(AppSettings.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing useful to report from a convenience button.
        }
    }
}
