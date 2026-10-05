using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Decides whether the dashboard is actually in front of the user's eyes.
///
/// Chromium has its own occlusion detection and a switch to disable it, but neither
/// applies here: WinUI hosts WebView2 in composition mode, so the browser has no
/// top-level window of its own to test. Measured on a fully covered window, the page
/// kept rendering at 143.6 fps and burning 43.8% CPU with the switch on *and* off -
/// the flag is a no-op in this architecture, in both directions. The host therefore
/// has to make the call.
///
/// "Not visible" means any of: minimised, cloaked (another virtual desktop), or
/// entirely covered by a single window above it in the Z-order. The last is the
/// common case - a maximised window over the top - and is deliberately not a full
/// region-subtraction test, because the cost of being wrong is only that rendering
/// continues, which is today's behaviour anyway.
/// </summary>
public sealed class WindowVisibilityWatcher : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Func<bool> _isEnabled;
    private readonly System.Threading.Timer _timer;

    private bool _lastVisible = true;
    private bool _disposed;

    /// <summary>Raised when effective visibility changes. Not raised on the UI thread.</summary>
    public event Action<bool>? VisibilityChanged;

    public WindowVisibilityWatcher(IntPtr hwnd, Func<bool> isEnabled, TimeSpan interval)
    {
        _hwnd = hwnd;
        _isEnabled = isEnabled;
        _timer = new System.Threading.Timer(_ => Poll(), null, interval, interval);
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        bool visible;
        try
        {
            visible = !_isEnabled() || IsEffectivelyVisible();
        }
        catch (Exception)
        {
            // Never let a transient Win32 failure latch the dashboard off.
            visible = true;
        }

        if (visible == _lastVisible)
        {
            return;
        }

        _lastVisible = visible;
        VisibilityChanged?.Invoke(visible);
    }

    private bool IsEffectivelyVisible()
    {
        if (!IsWindowVisible(_hwnd) || IsIconic(_hwnd))
        {
            return false;
        }

        if (IsCloaked(_hwnd))
        {
            return false;
        }

        if (!GetWindowRect(_hwnd, out var self) || self.Right <= self.Left || self.Bottom <= self.Top)
        {
            return false;
        }

        // Safety net. If we are the foreground window the user is demonstrably looking
        // at us, so no geometry test is allowed to blank the dashboard. Without this, a
        // translucent or oddly-shaped window that happens to enclose our rectangle would
        // freeze the page while it is plainly on screen.
        if (GetForegroundWindow() == _hwnd)
        {
            return true;
        }

        // Walk upwards through the Z-order. GW_HWNDPREV is the window in front.
        var above = GetWindow(_hwnd, GW_HWNDPREV);
        var guard = 0;
        while (above != IntPtr.Zero && guard++ < 200)
        {
            if (CoversCompletely(above, self))
            {
                return false;
            }

            above = GetWindow(above, GW_HWNDPREV);
        }

        return true;
    }

    private static bool CoversCompletely(IntPtr candidate, RECT target)
    {
        if (!IsWindowVisible(candidate) || IsIconic(candidate) || IsCloaked(candidate))
        {
            return false;
        }

        var exStyle = (long)GetWindowLongPtr(candidate, GWL_EXSTYLE);

        // Click-through windows are overlays and do not hide anything.
        if ((exStyle & WS_EX_TRANSPARENT) != 0)
        {
            return false;
        }

        if (!GetWindowRect(candidate, out var rect))
        {
            return false;
        }

        return rect.Left <= target.Left
            && rect.Top <= target.Top
            && rect.Right >= target.Right
            && rect.Bottom >= target.Bottom;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}
