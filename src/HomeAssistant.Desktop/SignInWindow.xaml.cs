using System.Runtime.InteropServices;
using System.Web;
using HomeAssistant.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using static HomeAssistant.Desktop.Services.NativeMethods;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace HomeAssistant.Desktop;

/// <summary>Outcome of a sign-in attempt.</summary>
public sealed record SignInResult(string? Code, string? Error)
{
    public bool Succeeded => Code is not null;

    public static SignInResult Cancelled { get; } = new(null, null);
}

/// <summary>
/// Hosts Home Assistant's own login page and captures the authorization code.
///
/// The redirect never travels anywhere. Home Assistant is happy to redirect to a
/// localhost URL that nothing serves, and the navigation is cancelled the moment it
/// starts, so the code is read straight off the URL. That keeps the app's promise of
/// opening no listening socket - the same reason its push notifications use a websocket
/// channel rather than an HTTP webhook.
/// </summary>
public sealed partial class SignInWindow : Window
{
    private readonly TaskCompletionSource<SignInResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CoreWebView2Environment _environment;
    private readonly Uri _authorizeUri;
    private readonly string _expectedState;

    private WebView2? _webView;
    private bool _completed;

    public SignInWindow(CoreWebView2Environment environment, string baseUrl, Uri authorizeUri, string expectedState)
    {
        _environment = environment;
        _authorizeUri = authorizeUri;
        _expectedState = expectedState;

        InitializeComponent();

        Title = "Sign in to Home Assistant";
        AddressText.Text = baseUrl;

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        // Home Assistant's login page is a full web page, not a compact form: it can
        // carry a banner, a provider picker and a multi-factor step.
        //
        // AppWindow sizes in physical pixels, so a fixed size shrinks as display scaling
        // rises - the original 560x760 was only 448x608 at 125%, which is what squeezed
        // the login page into a scrollbar. Ask in logical pixels and scale to the display,
        // capped so the window still fits on a small or heavily scaled screen.
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = Math.Min((int)(620 * scale), (int)(work.Width * 0.9));
        var height = Math.Min((int)(880 * scale), (int)(work.Height * 0.9));

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Nothing here benefits from being maximised, but being able to drag it
            // larger is the escape hatch if a provider needs more room than this.
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ConfigureChrome(appWindow);

        Closed += OnClosed;
    }

    /// <summary>
    /// Gives this window the app's identity. Without it a second window falls back to
    /// the generic executable icon and the system's own title bar, which on a dark app
    /// shows up as a light bar above dark content.
    /// </summary>
    private void ConfigureChrome(AppWindow appWindow)
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        appWindow.SetIcon(iconPath);
        try
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(iconPath));
        }
        catch (Exception ex) when (ex is UriFormatException or FileNotFoundException)
        {
            // Decorative only.
        }
    }

    /// <summary>Shows the window and completes when the user signs in, cancels, or fails.</summary>
    public Task<SignInResult> ShowAndWaitAsync()
    {
        Activate();
        _ = StartAsync();
        return _completion.Task;
    }

    private async Task StartAsync()
    {
        try
        {
            var webView = new WebView2();
            WebViewHost.Children.Add(webView);
            _webView = webView;

            await webView.EnsureCoreWebView2Async(_environment);

            webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                LoadingRing.IsActive = false;

                // Only the first load is a wait worth announcing; leaving the text up
                // makes a loaded page look stuck.
                if (StatusText.Text.Length > 0 && !ErrorBar.IsOpen)
                {
                    StatusText.Text = string.Empty;
                }
            };
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // This window exists to take a password, which makes it exactly where the
            // browser would offer to remember one. It is not a browser.
            webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            StatusText.Text = "Waiting for Home Assistant\u2026";
            webView.CoreWebView2.Navigate(_authorizeUri.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("auth", "could not start the sign-in browser", ex);
            LoadingRing.IsActive = false;
            ShowError(ex.Message);
            CompleteAndClose(new SignInResult(null, ex.Message));
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith(HaAuth.RedirectUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Nothing is listening on the other end, and nothing needs to be.
        args.Cancel = true;

        var query = HttpUtility.ParseQueryString(new Uri(args.Uri).Query);
        var error = query["error"];
        var code = query["code"];
        var state = query["state"];

        SignInResult result;
        if (error is not null)
        {
            Log.Warn("auth", $"Home Assistant refused the authorization: {error}");
            ShowError(query["error_description"] ?? error);
            result = new SignInResult(null, error);
        }
        else if (state != _expectedState)
        {
            // A code arriving with the wrong state did not come from the request this
            // window made, so it is not ours to redeem.
            Log.Warn("auth", "the authorization response carried the wrong state; discarding it");
            ShowError("The sign-in response did not match this request. Try again.");
            result = new SignInResult(null, "state_mismatch");
        }
        else if (string.IsNullOrEmpty(code))
        {
            ShowError("Home Assistant returned no authorization code.");
            result = new SignInResult(null, "no_code");
        }
        else
        {
            result = new SignInResult(code, null);
        }

        // Closing from inside a WebView2 callback tears down the control that is
        // currently raising the event, so let the callback return first.
        Enqueue(() => CompleteAndClose(result));
    }

    private void ShowError(string message) => Enqueue(() =>
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        StatusText.Text = string.Empty;
    });

    private void OnCancelClick(object sender, RoutedEventArgs e) => CompleteAndClose(SignInResult.Cancelled);

    /// <summary>
    /// Settles the result. Safe to call repeatedly and from any of the several ways a
    /// sign-in can end.
    /// </summary>
    private void Complete(SignInResult result)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completion.TrySetResult(result);
    }

    private void CompleteAndClose(SignInResult result)
    {
        Complete(result);

        try
        {
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Already closing. The result is settled, which is the part that matters.
            Log.Warn("auth", $"could not close the sign-in window: {ex.Message}");
        }
    }

    /// <summary>
    /// The window can also be closed by the title bar, which must settle the waiting
    /// task exactly as the Cancel button does - and must not reach back into XAML that
    /// is already being torn down.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Complete(SignInResult.Cancelled);

        try
        {
            if (_webView is { } webView)
            {
                _webView = null;
                webView.Close();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
            Log.Warn("auth", $"could not dispose the sign-in browser: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs work on the UI thread, swallowing anything it throws.
    ///
    /// An exception escaping a DispatcherQueue callback does not reach the XAML
    /// unhandled handler; it becomes a stowed exception and takes the process with it.
    /// A failure while closing a sign-in window must never cost the user their
    /// dashboard.
    /// </summary>
    private void Enqueue(Action work) => DispatcherQueue.TryEnqueue(() =>
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Log.Error("auth", "sign-in window callback failed", ex);
        }
    });
}
