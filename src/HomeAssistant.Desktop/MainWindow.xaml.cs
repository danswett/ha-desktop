using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
    private SystemStateWatcher? _systemWatcher;
    private ToastService? _toasts;
    private HaPushClient? _pushClient;
    private TaskbarBadge? _badge;
    private readonly UnseenNotifications _unseen = new();
    private readonly ProtectedStore _secrets = new();
    private HaAuth? _auth;
    private HaEndpoints? _endpoints;
    private ExternalAppBridge? _bridge;
    private bool _locked;
    private string? _pendingPath;
    private string? _pendingEntity;
    private int _nextQueryId;
    private readonly Dictionary<int, TaskCompletionSource<string?>> _pageQueries = [];
    private string? _credentialSource;
    private CoreWebView2Environment? _webViewEnvironment;
    private bool _signInInProgress;

    /// <summary>
    /// The Home Assistant address currently in use. Chosen rather than configured: a
    /// machine that moves between networks needs the local address at home and the
    /// external one everywhere else.
    /// </summary>
    private string BaseUrl => _endpoints?.Current ?? string.Empty;

    // Two independent reasons to stop painting. Rendering requires both.
    private bool _windowVisible = true;
    private bool _userPresent = true;
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
        _retryTimer.Tick += (_, _) => Navigate(BaseUrl);

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

        // Looking at the dashboard is reading everything on it, so the badge clears.
        Activated += OnWindowActivated;

        _badge = new TaskbarBadge(_hwnd);
        _unseen.CountChanged += OnUnseenCountChanged;
        if (_tray is { } tray)
        {
            tray.ShellRestarted += () => DispatcherQueue.TryEnqueue(() => _badge?.Reapply());
        }

        // Chromium cannot see that a composition-hosted WebView2 is covered, so the
        // host watches for it and stops the page painting pixels nobody can see.
        _visibilityWatcher = new WindowVisibilityWatcher(
            _hwnd,
            isEnabled: () => !_settings.RenderWhenCovered,
            interval: TimeSpan.FromSeconds(2));
        _visibilityWatcher.VisibilityChanged += OnWindowVisibilityChanged;

        // A locked session or a sleeping display is invisible to the watcher above:
        // the lock screen is a separate desktop, so our window is still "visible".
        try
        {
            _systemWatcher = new SystemStateWatcher();
            _systemWatcher.UserPresenceChanged += OnUserPresenceChanged;
            _systemWatcher.Resumed += OnSystemResumed;
        }
        catch (InvalidOperationException)
        {
            // Losing these notifications costs efficiency, never correctness.
        }

        _auth = new HaAuth(_secrets, () => BaseUrl);
        MigrateSecretsOutOfSettings();

        // Created before anything navigates, so the first page load already uses
        // whichever address is reachable from wherever this machine currently is.
        _endpoints = new HaEndpoints(() => (_settings.InternalUrl, _settings.ExternalUrl));
        _endpoints.Changed += OnEndpointChanged;
    }

    /// <summary>
    /// The machine moved, and Home Assistant now answers somewhere else. Everything
    /// holding the old address has to follow.
    /// </summary>
    private void OnEndpointChanged(string url) => DispatcherQueue.TryEnqueue(() =>
    {
        Log.Info("endpoint", $"reloading the dashboard against {url}");
        _retryAttempt = 0;
        Navigate(url);

        // The push client reads the address afresh on each attempt, so it only needs
        // waking out of whatever backoff the old address earned it.
        _pushClient?.Reconnect();
    });

    /// <summary>
    /// Moves the webhook id into the encrypted store. It addresses this device to Home
    /// Assistant, so it does not belong in a file the user is encouraged to open and
    /// edit alongside their window preferences.
    /// </summary>
    private void MigrateSecretsOutOfSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.PushWebhookId))
        {
            return;
        }

        _secrets.Set(WebhookIdKey, _settings.PushWebhookId);
        _settings.PushWebhookId = null;
        _settings.Save();
        Log.Info("secrets", "moved the webhook id out of settings.json");
    }

    private const string WebhookIdKey = "ha.webhook_id";

    private string? WebhookId => _secrets.Get(WebhookIdKey);

    /// <summary>
    /// Signs the app in to Home Assistant in its own right, so it no longer depends on
    /// the dashboard being signed in.
    /// </summary>
    private async Task SignInAsync()
    {
        if (_signInInProgress)
        {
            return;
        }

        if (_webViewEnvironment is not { } environment)
        {
            Log.Warn("auth", "cannot sign in before the browser engine is ready");
            return;
        }

        _signInInProgress = true;
        try
        {
            var (authorize, verifier, state) = HaAuth.BeginAuthorization(BaseUrl);
            var window = new SignInWindow(environment, BaseUrl, authorize, state);
            var result = await window.ShowAndWaitAsync();

            if (!result.Succeeded)
            {
                Log.Info("auth", $"sign-in did not complete: {result.Error ?? "cancelled"}");
                return;
            }

            await _auth!.SignInAsync(BaseUrl, result.Code!, verifier, CancellationToken.None);

            // The push client caches nothing about credentials, but it may be sitting
            // in a backoff after failing with the old ones.
            _pushClient?.Reconnect();
        }
        catch (Exception ex) when (ex is HaAuthException or HttpRequestException or TaskCanceledException)
        {
            Log.Error("auth", "sign-in failed", ex);
        }
        finally
        {
            _signInInProgress = false;
        }
    }

    private void OnWindowVisibilityChanged(bool visible)
    {
        _windowVisible = visible;
        UpdateRenderingState();

        // Belt and braces for the badge. Activated is the primary signal, but it does
        // not fire for every way a window can come back to the front, and a count that
        // will not clear is worse than one that clears early.
        if (visible && IsDashboardOnScreen())
        {
            _unseen.Clear();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        // Rendering is suspended while the window is covered, and the watcher only
        // notices it is back on its next poll. Waiting for that is what left the
        // dashboard blank for a moment after switching to it, so activation resumes
        // rendering directly.
        _visibilityWatcher?.MarkVisible();

        _unseen.Clear();
    }

    /// <summary>
    /// Whether the dashboard is actually on screen for the user to read.
    ///
    /// Asked live rather than tracked from Activated: the window is parked in the tray
    /// with AppWindow.Hide(), and WinUI raises no Activated event for that, so a cached
    /// flag stays stuck on "active" and the badge never counts anything.
    ///
    /// Deliberately not a focus test. A dashboard sitting uncovered on screen has been
    /// read whether or not it holds the keyboard, and the same predicate decides both
    /// whether to count a notification and when to clear the count, so the two can
    /// never disagree.
    /// </summary>
    private bool IsDashboardOnScreen() =>
        _windowVisible && IsWindowVisible(_hwnd) && !IsIconic(_hwnd);

    /// <summary>
    /// Raised off the push client's thread, so it has to be marshalled: the taskbar's
    /// COM object has thread affinity and the window belongs to the UI thread.
    /// </summary>
    private void OnUnseenCountChanged(int count) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            Log.Info("badge", $"unseen count is now {count}");
            _badge?.SetCount(count);
            _tray?.SetBadgeCount(count);
        });

    private void OnUserPresenceChanged(bool present)
    {
        _userPresent = present;
        UpdateRenderingState();
    }

    /// <summary>
    /// Collapsing the control suspends rendering but leaves the page running, so
    /// timers keep firing and the dashboard is current the moment it is seen again.
    /// </summary>
    private void UpdateRenderingState()
    {
        var shouldRender = _windowVisible && _userPresent;

        // Already on the UI thread when this comes from activation, and in that case
        // the user is watching: apply it now rather than a dispatcher turn later.
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyRenderingState(shouldRender);
            return;
        }

        DispatcherQueue.TryEnqueue(() => ApplyRenderingState(shouldRender));
    }

    private void ApplyRenderingState(bool shouldRender)
    {
        if (_webView is null)
        {
            return;
        }

        // Read the current state off the control rather than caching it. The WebView is
        // rebuilt wholesale after a browser crash, and a cached flag would then describe
        // a control that no longer exists - leaving rendering stuck on or off.
        var target = shouldRender ? Visibility.Visible : Visibility.Collapsed;
        if (_webView.Visibility == target)
        {
            return;
        }

        _webView.Visibility = target;
        Log.Info("render", shouldRender ? "resumed" : "suspended; the window cannot be seen");
    }

    /// <summary>
    /// After a sleep the network went away with the machine, so the page's websocket
    /// is stale however healthy it looks. Reload rather than trust it.
    ///
    /// A laptop also wakes somewhere else surprisingly often, so this is the most
    /// likely moment for the right address to have changed. Re-check before reloading,
    /// and let the endpoint change drive the reload if it finds a different one.
    /// </summary>
    private void OnSystemResumed()
    {
        _ = Task.Run(async () =>
        {
            var before = BaseUrl;
            var after = _endpoints is null ? before : await _endpoints.RefreshAsync();

            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                // OnEndpointChanged already navigated.
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                _retryAttempt = 0;
                ReloadNow();
            });
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

        UpdateSettingsButtonVisibility();
        PublishJumpList();

        // Raised before the window is shown, so the dashboard is never briefly visible
        // behind it. The browser still starts underneath: notifications and the unread
        // count are the reason the app is running, and holding those back would make a
        // locked app useless rather than private.
        if (_settings.RequireWindowsHello)
        {
            ShowLock("Verify with Windows Hello to show your dashboard.");
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

        if (_settings.RequireWindowsHello)
        {
            _ = UnlockAsync();
        }
    }

    /// <summary>
    /// Drops the gear from the title bar once Home Assistant itself can open these
    /// settings. Signing in attaches the external bridge, which puts a "Companion App"
    /// row in Home Assistant's own Settings and sidebar, and two buttons for one dialog
    /// is one too many on a window meant to be looked at rather than operated.
    ///
    /// Signed out there is no such row, and the status overlay - which carries its own
    /// Settings button - is not showing either, so the gear stays rather than leaving
    /// the tray icon, possibly hidden in the overflow, as the only way in.
    /// </summary>
    private void UpdateSettingsButtonVisibility()
    {
        SettingsButton.Visibility = _auth?.IsSignedIn == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowLock(string detail)
    {
        _locked = true;
        LockDetail.Text = detail;
        LockOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Asks for verification and reveals the dashboard once it succeeds. Left on the
    /// lock screen otherwise, so a refusal is recoverable without restarting.
    /// </summary>
    private async Task UnlockAsync()
    {
        if (!_locked)
        {
            return;
        }

        UnlockButton.IsEnabled = false;
        try
        {
            var outcome = await WindowsHello.VerifyAsync(_hwnd, "Unlock Home Assistant");

            switch (outcome)
            {
                case HelloOutcome.Verified:
                    _locked = false;
                    LockOverlay.Visibility = Visibility.Collapsed;
                    Log.Info("hello", "unlocked");
                    break;

                case HelloOutcome.Unavailable:
                    // The setting can only be turned on after a successful check, so
                    // getting here means Hello was removed afterwards. Refusing to open
                    // would strand the user with no way back in, so say so and open.
                    _locked = false;
                    LockOverlay.Visibility = Visibility.Collapsed;
                    Log.Warn("hello", "Windows Hello is no longer available; opening unlocked");
                    break;

                default:
                    LockDetail.Text = "Not verified. Try again, or exit.";
                    break;
            }
        }
        finally
        {
            UnlockButton.IsEnabled = true;
        }
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => _ = UnlockAsync();

    private void OnLockExitClick(object sender, RoutedEventArgs e) => Exit();

    /// <summary>
    /// Carries out what a launch asked for. Arrives either from this process starting
    /// or, because the app is single instance, from a second launch handing its
    /// arguments over - which is what a jump list entry does while the app is running.
    /// </summary>
    public void Handle(LaunchRequest request)
    {
        switch (request.Kind)
        {
            case LaunchRequestKind.OpenPage:
                NavigateToPath(request.Target);
                break;

            case LaunchRequestKind.ShowEntity:
                // There is no URL that opens an entity: /_my_redirect has no more_info
                // route, despite answering 200 like every other frontend path. The
                // frontend's own way in is a hass-more-info event, so raise it in the
                // page that is already loaded, or after loading one if there is not.
                if (_webView?.CoreWebView2 is not null && !string.IsNullOrWhiteSpace(BaseUrl))
                {
                    ShowAndFocus();
                    _ = ShowEntityDialogAsync(request.Target);
                }
                else
                {
                    _pendingEntity = request.Target;
                    NavigateToPath("/");
                }

                break;

            case LaunchRequestKind.PerformOnEntity:
                _ = PerformOnEntityAsync(request.Target);
                break;
        }
    }

    /// <summary>
    /// Toggles an entity without involving the page, so it works whether or not the
    /// dashboard is loaded - which it may not be, if the app was launched purely to
    /// carry out this one thing.
    /// </summary>
    private async Task PerformOnEntityAsync(string entityId)
    {
        try
        {
            if (await GetAccessTokenAsync(CancellationToken.None) is not { } token)
            {
                Log.Warn("jumplist", $"cannot act on {entityId}: no credentials");
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var body = JsonSerializer.Serialize(new { entity_id = entityId });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            // homeassistant.toggle covers every domain that has a notion of on and off,
            // so one call serves lights, switches, covers, locks and the rest.
            using var response = await http.PostAsync(
                new Uri(new Uri(BaseUrl), "/api/services/homeassistant/toggle"), content);

            Log.Info("jumplist", $"toggled {entityId} ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Log.Warn("jumplist", $"could not act on {entityId}: {ex.Message}");
        }
    }

    private void PublishJumpList() =>
        JumpList.Publish(_settings.JumpListSlots, Environment.ProcessPath ?? string.Empty);

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        }

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

            // Sign-in reuses this environment so it shares the browser profile, and
            // with it any Home Assistant session the dashboard already has.
            _webViewEnvironment = environment;
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

        // A fresh control is visible by default. If the window happens to be covered -
        // a crash recovery while parked, say - it would start painting pixels nobody
        // can see until the next visibility change happened to put it right.
        UpdateRenderingState();

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

        if (string.IsNullOrWhiteSpace(_settings.InternalUrl) && string.IsNullOrWhiteSpace(_settings.ExternalUrl))
        {
            ShowStatus("Home Assistant address not set",
                "Open Settings and enter the address of your Home Assistant.",
                showActions: true, busy: false);
            return;
        }

        // Decide which address to use before the first navigation, so a laptop that
        // starts up away from home does not have to fail once before being corrected.
        if (_endpoints is { } endpoints)
        {
            await endpoints.RefreshAsync();
        }

        // A launch that asked for a particular page should land there directly rather
        // than loading the default dashboard and then moving.
        var start = _pendingPath is { } pending ? BaseUrl + pending : BaseUrl;
        _pendingPath = null;

        Navigate(start);
        StartPushNotifications();
    }

    /// <summary>
    /// Opens the Home Assistant push channel once the dashboard exists to borrow a token
    /// from. Started after navigation rather than before, because the token only appears
    /// in the page's storage once it has signed in.
    /// </summary>
    private void StartPushNotifications()
    {
        Log.Info("push", "starting push notifications");

        if (_pushClient is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(WebhookId))
        {
            Log.Info("push", "this machine is not registered with Home Assistant yet");
            return;
        }

        _toasts = new ToastService(GetAccessTokenAsync, () => BaseUrl, NavigateToPath);
        if (!_toasts.TryInitialize())
        {
            _toasts = null;
            return;
        }

        // A notification that arrives while the user is already looking at the dashboard
        // has been seen by definition, so it never reaches the badge.
        _toasts.NotificationShown += tag =>
        {
            if (IsDashboardOnScreen())
            {
                Log.Info("badge", "notification arrived while the dashboard was on screen; not counted");
                return;
            }

            _unseen.Add(tag);
        };
        _toasts.NotificationDismissed += tag => _unseen.Remove(tag);

        _pushClient = new HaPushClient(GetAccessTokenAsync, () => BaseUrl, () => WebhookId);
        _pushClient.ConnectionChanged += (connected, error) =>
        {
            if (connected)
            {
                Log.Info("push", "push channel open");
            }
            else
            {
                Log.Warn("push", $"push channel closed: {error ?? "no reason given"}");
            }
        };
        _pushClient.NotificationReceived += payload =>
        {
            if (_toasts is { } toasts)
            {
                _ = toasts.HandleAsync(payload);
            }
        };
        _pushClient.Start();
    }

    /// <summary>
    /// Supplies an access token, preferring the app's own credentials.
    ///
    /// Falls back to borrowing the dashboard's token out of page storage, which is how
    /// this worked before the app could sign in for itself, and is what keeps an
    /// install that predates OAuth working until its owner next signs in.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_auth is { } auth)
        {
            try
            {
                if (await auth.GetAccessTokenAsync(cancellationToken) is { } token)
                {
                    NoteCredentialSource("the app's own sign-in");
                    return token;
                }
            }
            catch (Exception ex) when (ex is HaAuthException or HttpRequestException or TaskCanceledException)
            {
                // Fall through and try the page: a Home Assistant that is briefly
                // unreachable should not take the dashboard's working token with it.
                Log.Warn("auth", $"could not refresh the access token: {ex.Message}");
            }
        }

        return await BorrowDashboardTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Records which credential the app is running on. Both paths work, so without
    /// this there is no way to tell from the outside whether a sign-in is actually
    /// being used or whether the dashboard's token is quietly carrying everything.
    /// Logged only when it changes, so it does not repeat on every token refresh.
    /// </summary>
    private void NoteCredentialSource(string source)
    {
        if (_credentialSource == source)
        {
            return;
        }

        _credentialSource = source;
        Log.Info("auth", $"using {source}");
    }

    /// <summary>
    /// Borrows the dashboard's own access token out of the page's storage. Home
    /// Assistant rotates this token, so it is read fresh on every use rather than
    /// cached.
    /// </summary>
    private Task<string?> BorrowDashboardTokenAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(async void () =>
        {
            try
            {
                if (_webView?.CoreWebView2 is not { } core)
                {
                    completion.TrySetResult(null);
                    return;
                }

                var raw = await core.ExecuteScriptAsync("window.localStorage.getItem('hassTokens')");

                // ExecuteScriptAsync returns the result JSON-encoded, so a stored string
                // arrives as a JSON string whose content is itself JSON.
                var inner = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(inner))
                {
                    completion.TrySetResult(null);
                    return;
                }

                using var document = JsonDocument.Parse(inner);
                var borrowed = document.RootElement.TryGetProperty("access_token", out var value)
                    ? value.GetString()
                    : null;

                if (!string.IsNullOrEmpty(borrowed))
                {
                    NoteCredentialSource("the dashboard's own token");
                }

                completion.TrySetResult(borrowed);
            }
            catch (Exception)
            {
                completion.TrySetResult(null);
            }
        }))
        {
            completion.TrySetResult(null);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Opens one entity's dialog in the loaded dashboard.
    ///
    /// The event is one-shot: raised before the shell has attached its listener it is
    /// dropped silently, which is exactly the race a cold launch creates. Home
    /// Assistant's own end-to-end tests solve it by dispatching repeatedly until the
    /// dialog appears, because showing it again is harmless, so this does the same.
    /// </summary>
    private async Task ShowEntityDialogAsync(string entityId)
    {
        var script = $$"""
            (async () => {
              const id = {{JsonSerializer.Serialize(entityId)}};
              const deadline = Date.now() + 12000;
              const pause = ms => new Promise(r => setTimeout(r, ms));
              const isOpen = () => {
                const root = document.querySelector('home-assistant');
                return !!(root && root.shadowRoot &&
                  root.shadowRoot.querySelector('ha-more-info-dialog'));
              };
              while (Date.now() < deadline) {
                const root = document.querySelector('home-assistant');
                if (root) {
                  root.dispatchEvent(new CustomEvent('hass-more-info', {
                    detail: { entityId: id }, bubbles: true, composed: true }));
                  await pause(250);
                  if (isOpen()) { return true; }
                } else {
                  await pause(200);
                }
              }
              return isOpen();
            })()
            """;

        var answer = await QueryPageAsync(script, CancellationToken.None);
        var opened = answer is { ValueKind: JsonValueKind.True };
        Log.Info("jumplist", opened ? $"opened {entityId}" : $"could not open {entityId}");
    }

    private void NavigateToPath(string path)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowAndFocus();

            // A cold launch asking for a page arrives before the browser exists. Hold
            // the path and let the first navigation use it, rather than loading the
            // default dashboard and visibly moving off it.
            if (_webView?.CoreWebView2 is null)
            {
                _pendingPath = path.StartsWith('/') ? path : "/" + path;
                return;
            }

            if (Uri.TryCreate(new Uri(BaseUrl), path, out var target))
            {
                Navigate(target.ToString());
            }
        });
    }

    /// <summary>
    /// Runs a JavaScript expression in the dashboard and waits for its result.
    ///
    /// ExecuteScriptAsync cannot await a promise - it serialises whatever the
    /// expression evaluates to immediately, which for an async function is an empty
    /// object - so the result comes back over the same message channel the rest of the
    /// host integration uses, tagged with a correlation id.
    ///
    /// This borrows the page's own websocket rather than opening another, so it needs
    /// no credentials of its own and reflects exactly what the dashboard can see.
    /// </summary>
    private async Task<JsonElement?> QueryPageAsync(string expression, CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is not { } core)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _nextQueryId);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pageQueries)
        {
            _pageQueries[id] = completion;
        }

        var script = $$"""
            (function () {
              var id = {{id}};
              (async function () {
                try {
                  var result = await ({{expression}});
                  window.chrome.webview.postMessage(
                    { type: 'ha-query', id: id, ok: true, data: JSON.stringify(result) });
                } catch (e) {
                  window.chrome.webview.postMessage(
                    { type: 'ha-query', id: id, ok: false, data: String(e) });
                }
              })();
            })();
            """;

        try
        {
            await core.ExecuteScriptAsync(script);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var raw = await completion.Task.WaitAsync(timeout.Token);
            if (raw is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException)
        {
            Log.Warn("query", $"the dashboard did not answer: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_pageQueries)
            {
                _pageQueries.Remove(id);
            }
        }
    }

    private bool TryCompletePageQuery(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "ha-query")
        {
            return false;
        }

        if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id))
        {
            return true;
        }

        TaskCompletionSource<string?>? completion;
        lock (_pageQueries)
        {
            _pageQueries.Remove(id, out completion);
        }

        var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
        var data = root.TryGetProperty("data", out var dataValue) ? dataValue.GetString() : null;

        if (!ok)
        {
            Log.Warn("query", $"the dashboard reported: {data}");
        }

        completion?.TrySetResult(ok ? data : null);
        return true;
    }

    private void ConfigureCoreWebView(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.AreDefaultContextMenusEnabled = _settings.DevToolsEnabled;
        s.AreDevToolsEnabled = _settings.DevToolsEnabled;
        s.IsStatusBarEnabled = false;

        // Browser furniture that gives the game away. A native app does not offer to
        // remember your password, and it does not show Chromium's error page: this one
        // holds its own credentials and draws its own connection status.
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsZoomControlEnabled = true;

        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        core.WebMessageReceived += OnWebMessageReceived;

        InstallExternalAppBridge(core);
    }

    /// <summary>
    /// Attaches Home Assistant's external app bridge, but only when the app actually
    /// holds credentials of its own.
    ///
    /// The bridge is all-or-nothing from the page's point of view: once
    /// window.externalApp exists, the frontend stops using its own stored tokens
    /// entirely and asks the host for every one. Installing it without a sign-in to
    /// back it would therefore replace a dashboard that logs itself in with one that
    /// cannot log in at all.
    /// </summary>
    private void InstallExternalAppBridge(CoreWebView2 core)
    {
        if (_auth is not { IsSignedIn: true } auth)
        {
            Log.Info("bridge", "not signed in; leaving the dashboard to its own session");
            return;
        }

        _bridge = new ExternalAppBridge(
            tokenProvider: auth.GetAccessTokenWithLifetimeAsync,
            revoke: auth.SignOutAsync,
            showSettings: () => DispatcherQueue.TryEnqueue(() => _ = ShowSettingsDialogAsync()),
            runScript: async script =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(async void () =>
                {
                    try
                    {
                        if (_webView?.CoreWebView2 is { } live)
                        {
                            await live.ExecuteScriptAsync(script);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("bridge", $"could not run a bridge reply: {ex.Message}");
                    }
                    finally
                    {
                        completion.TrySetResult();
                    }
                }))
                {
                    completion.TrySetResult();
                }

                await completion.Task;
            });

        _ = ExternalAppBridge.InstallAsync(core);
        Log.Info("bridge", "external app bridge attached; the app is supplying the dashboard's tokens");
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

            if (Interlocked.Exchange(ref _pendingEntity, null) is { } entity)
            {
                _ = ShowEntityDialogAsync(entity);
            }

            return;
        }

        // A cancelled navigation is usually the page redirecting, not a failure.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        ScheduleRetry($"Could not reach {BaseUrl} ({Describe(e.WebErrorStatus)}).");
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
            if (TryCompletePageQuery(document.RootElement))
            {
                return;
            }

            if (_bridge?.TryHandle(document.RootElement) == true)
            {
                return;
            }

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

            // Coming back from the tray is the longest suspension of all, and
            // AppWindow.Show() does not reliably raise Activated, so resume rendering
            // here rather than relying on it.
            _visibilityWatcher?.MarkVisible();
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
        _systemWatcher?.Dispose();
        _systemWatcher = null;
        _endpoints?.Dispose();
        _endpoints = null;
        _ = _pushClient?.DisposeAsync().AsTask();
        _pushClient = null;
        _toasts?.Dispose();
        _toasts = null;
        _badge?.Dispose();
        _badge = null;
        DisposeWebView();
        _tray?.Dispose();
        _tray = null;
    }

    private void DisposeWebView()
    {
        _bridge = null;
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
            Navigate(BaseUrl);
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
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _retryAttempt = 0;
        Navigate(BaseUrl);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = ShowSettingsDialogAsync();

    /// <summary>
    /// Shows whether the app holds credentials of its own, and lets the user grant or
    /// revoke them. Until it does, it borrows the dashboard's token, which works but
    /// only for as long as the dashboard stays signed in.
    /// </summary>
    private StackPanel BuildAccountRow()
    {
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var button = new Button { Margin = new Thickness(0, 6, 0, 0) };

        void Refresh()
        {
            var signedIn = _auth?.IsSignedIn == true;
            status.Text = signedIn
                ? "This app has its own Home Assistant credentials."
                : "Using the dashboard's session. Sign in to give the app its own credentials, "
                  + "so notifications keep working when the dashboard is signed out.";
            button.Content = signedIn ? "Sign out" : "Sign in to Home Assistant";
        }

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                var wasSignedIn = _auth?.IsSignedIn == true;
                if (wasSignedIn)
                {
                    await _auth!.SignOutAsync(CancellationToken.None);
                }
                else
                {
                    await SignInAsync();
                }

                // The external app bridge is installed with the WebView, and once the
                // page can see it the frontend stops using its own session entirely.
                // Signing out would otherwise leave a dashboard asking this app for
                // tokens it no longer has, and signing in would not take effect until
                // the next launch. Rebuilding the browser puts the two back in step.
                if (wasSignedIn != (_auth?.IsSignedIn == true))
                {
                    UpdateSettingsButtonVisibility();
                    _bridge = null;
                    _ = RestartWebViewAsync();
                }
            }
            finally
            {
                Refresh();
                button.IsEnabled = true;
            }
        };

        Refresh();

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Account", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(status);
        panel.Children.Add(button);
        return panel;
    }

    private static TextBlock SectionHeader(string text, bool first = false) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, first ? 0 : 20, 0, 8),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private string DescribeJumpList()
    {
        var count = _settings.JumpListSlots.Count(s => s.IsUsable);
        return count == 0
            ? "Nothing configured. Add shortcuts to dashboards, views or single entities."
            : $"{count} entry{(count == 1 ? string.Empty : "ies")} on the taskbar menu.";
    }

    /// <summary>
    /// Asks the dashboard what this Home Assistant has, and hands it to the editor.
    /// Done when the section is first opened rather than when the dialog is built, so
    /// opening Settings does not wait on a websocket round trip for every user.
    /// </summary>
    private async Task LoadCatalogueAsync(JumpListEditor editor)
    {
        try
        {
            var catalogue = await QueryPageAsync(HaCatalogue.Script, CancellationToken.None) is { } answer
                ? HaCatalogue.From(answer)
                : new HaCatalogue();

            editor.SetCatalogue(catalogue);
            Log.Info("jumplist", $"catalogue: {catalogue.Pages.Count} page(s), {catalogue.Entities.Count} entities");
        }
        catch (Exception ex)
        {
            // An editor without suggestions still works by hand, and a failure here
            // must not escape into the dispatcher.
            editor.SetCatalogue(new HaCatalogue());
            Log.Warn("jumplist", $"could not read the catalogue: {ex.Message}");
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        if (_settingsDialogOpen)
        {
            return;
        }

        _settingsDialogOpen = true;
        try
        {
            var internalBox = new TextBox
            {
                Header = "On your own network",
                Text = _settings.InternalUrl,
                PlaceholderText = "http://homeassistant.local:8123",
            };

            var externalBox = new TextBox
            {
                Header = "From anywhere else (optional)",
                Text = _settings.ExternalUrl,
                PlaceholderText = "https://example.duckdns.org",
                Margin = new Thickness(0, 10, 0, 0),
            };

            var inUse = new TextBlock
            {
                Text = string.IsNullOrEmpty(BaseUrl) ? "Not connected." : $"Currently using {BaseUrl}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var detect = new Button
            {
                Content = "Fill in from Home Assistant",
                Margin = new Thickness(0, 10, 0, 0),
            };
            detect.Click += async (_, _) =>
            {
                detect.IsEnabled = false;
                try
                {
                    var discovered = await FetchConfiguredUrlsAsync(CancellationToken.None);
                    if (discovered is null)
                    {
                        inUse.Text = "Could not ask Home Assistant. Check the address above, or sign in first.";
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(discovered.Value.Internal))
                    {
                        internalBox.Text = discovered.Value.Internal;
                    }

                    if (!string.IsNullOrWhiteSpace(discovered.Value.External))
                    {
                        externalBox.Text = discovered.Value.External;
                    }

                    inUse.Text = "Filled in from Home Assistant's own configuration.";
                }
                finally
                {
                    detect.IsEnabled = true;
                }
            };

            var closeToTray = MakeToggleRow("Close button hides to the tray", _settings.CloseToTray);
            var minimizeToTray = MakeToggleRow("Minimise hides to the tray", _settings.MinimizeToTray);
            var startMinimized = MakeToggleRow("Start hidden in the tray", _settings.StartMinimized);
            var alwaysOnTop = MakeToggleRow("Always on top", _settings.AlwaysOnTop);
            var startWithWindows = MakeToggleRow("Start with Windows", StartupManager.IsEnabled());
            var devTools = MakeToggleRow("Developer tools and context menu", _settings.DevToolsEnabled);

            // Verified before it can be switched on, so that turning on a lock can
            // never be the thing that locks you out of your own dashboard.
            var requireHello = MakeToggleRow("Require Windows Hello to unlock", _settings.RequireWindowsHello);
            var helloHint = Hint(
                "Asked for once, at launch. This is a screen lock, not a vault: the app's "
                + "credentials are protected by Windows for your account either way.");
            requireHello.Toggle.Toggled += async (_, _) =>
            {
                if (!requireHello.Toggle.IsOn)
                {
                    return;
                }

                requireHello.Toggle.IsEnabled = false;
                try
                {
                    if (!await WindowsHello.IsAvailableAsync())
                    {
                        requireHello.Toggle.IsOn = false;
                        helloHint.Text = "Windows Hello is not set up on this machine. "
                            + "Add a PIN or a biometric sign-in in Windows Settings first.";
                        return;
                    }

                    if (await WindowsHello.VerifyAsync(_hwnd, "Confirm you can unlock Home Assistant")
                        != HelloOutcome.Verified)
                    {
                        requireHello.Toggle.IsOn = false;
                        helloHint.Text = "Not verified, so the lock has been left off.";
                        return;
                    }

                    helloHint.Text = "You will be asked for this the next time the app starts.";
                }
                finally
                {
                    requireHello.Toggle.IsEnabled = true;
                }
            };

            var openFolder = new HyperlinkButton
            {
                Content = "Open the app's data folder",
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(0),
            };
            openFolder.Click += (_, _) => OpenDataFolder();

            var jumpEditor = new JumpListEditor(_settings.JumpListSlots);
            var jumpSection = new Expander
            {
                Header = "Taskbar jump list",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = jumpEditor.Build(),
            };

            // Inline rather than a dialog of its own: WinUI allows only one ContentDialog
            // at a time, and opening a second from inside this one throws from an async
            // handler, which takes the whole process with it. Keeping it here also means
            // nothing typed above is lost on the way.
            var catalogueRequested = false;
            jumpSection.Expanding += (_, _) =>
            {
                if (catalogueRequested)
                {
                    return;
                }

                catalogueRequested = true;
                _ = LoadCatalogueAsync(jumpEditor);
            };

            // Wide enough for a jump list row to hold a name, a target and two choices
            // without wrapping.
            var panel = new StackPanel { Width = 600 };
            panel.Children.Add(SectionHeader("Home Assistant", first: true));
            panel.Children.Add(internalBox);
            panel.Children.Add(externalBox);
            panel.Children.Add(Hint(
                "The local address is used whenever it answers, and the external one only when it "
                + "does not. Reaching Home Assistant through a public tunnel makes it attribute every "
                + "client to a single WAN address, which is what trips its IP ban."));
            panel.Children.Add(detect);
            panel.Children.Add(inUse);

            panel.Children.Add(SectionHeader("Account"));
            panel.Children.Add(BuildAccountRow());

            panel.Children.Add(SectionHeader("Window"));
            panel.Children.Add(closeToTray.Row);
            panel.Children.Add(minimizeToTray.Row);
            panel.Children.Add(startMinimized.Row);
            panel.Children.Add(alwaysOnTop.Row);
            panel.Children.Add(startWithWindows.Row);

            panel.Children.Add(SectionHeader("Taskbar"));
            panel.Children.Add(jumpSection);

            panel.Children.Add(SectionHeader("Advanced"));
            panel.Children.Add(requireHello.Row);
            panel.Children.Add(helloHint);
            panel.Children.Add(devTools.Row);
            panel.Children.Add(openFolder);

            // Use the height the window actually has rather than a fixed guess, which
            // is what made this scroll as soon as a section was added.
            var available = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight - 220 : 560;

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Settings",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = Math.Clamp(available, 360, 900),
                    HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var previousInternal = _settings.InternalUrl;
            var previousExternal = _settings.ExternalUrl;
            var devToolsChanged = devTools.Toggle.IsOn != _settings.DevToolsEnabled;

            _settings.InternalUrl = internalBox.Text;
            _settings.ExternalUrl = externalBox.Text;
            _settings.CloseToTray = closeToTray.Toggle.IsOn;
            _settings.MinimizeToTray = minimizeToTray.Toggle.IsOn;
            _settings.StartMinimized = startMinimized.Toggle.IsOn;
            _settings.DevToolsEnabled = devTools.Toggle.IsOn;
            _settings.RequireWindowsHello = requireHello.Toggle.IsOn;
            _settings.JumpListSlots = jumpEditor.Result();
            _settings.Save();
            PublishJumpList();

            StartupManager.SetEnabled(startWithWindows.Toggle.IsOn);
            SetAlwaysOnTop(alwaysOnTop.Toggle.IsOn);

            if (devToolsChanged && _webView?.CoreWebView2 is { } core)
            {
                core.Settings.AreDevToolsEnabled = _settings.DevToolsEnabled;
                core.Settings.AreDefaultContextMenusEnabled = _settings.DevToolsEnabled;
            }

            var addressesChanged =
                !string.Equals(previousInternal, _settings.InternalUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousExternal, _settings.ExternalUrl, StringComparison.OrdinalIgnoreCase);

            if (addressesChanged && _endpoints is { } endpoints)
            {
                var before = BaseUrl;
                var after = await endpoints.RefreshAsync();

                // A change of address reloads through OnEndpointChanged; if the choice
                // happens to be the same one, reload anyway so an edited address takes
                // effect immediately.
                if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                {
                    _retryAttempt = 0;
                    Navigate(after);
                }
            }
        }
        finally
        {
            _settingsDialogOpen = false;
        }
    }

    /// <summary>
    /// Asks Home Assistant for the addresses it believes it has. Saves the user copying
    /// them by hand, and is the same source first-run setup will use.
    /// </summary>
    private async Task<(string Internal, string External)?> FetchConfiguredUrlsAsync(CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                return null;
            }

            if (await GetAccessTokenAsync(token) is not { } accessToken)
            {
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseUrl + "/"), "api/config"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;

            return (
                root.TryGetProperty("internal_url", out var i) ? i.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("external_url", out var e) ? e.GetString() ?? string.Empty : string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            Log.Warn("endpoint", $"could not read Home Assistant's configured addresses: {ex.Message}");
            return null;
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
