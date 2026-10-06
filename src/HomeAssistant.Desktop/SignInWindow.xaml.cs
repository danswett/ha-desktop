using System.Runtime.InteropServices;
using System.Web;
using HomeAssistant.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        appWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));

        Closed += OnClosed;
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
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            StatusText.Text = "Waiting for Home Assistant\u2026";
            webView.CoreWebView2.Navigate(_authorizeUri.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("auth", "could not start the sign-in browser", ex);
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
