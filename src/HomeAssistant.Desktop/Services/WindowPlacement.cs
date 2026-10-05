using System.Runtime.InteropServices;
using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Saves and restores the window rectangle across runs.
///
/// Uses GetWindowPlacement/SetWindowPlacement rather than raw screen coordinates because
/// placement stores the <em>restored</em> rectangle even while the window is maximised, so a
/// window closed maximised still remembers where to go when it is un-maximised.
///
/// The coordinate space those two APIs share is not physical pixels: on a 125% display a
/// window physically at 300,180 1500x980 reads back as 375,225 1875x1225. They do agree with
/// each other, so capture/re-apply is exact - but their values must never be compared against
/// GetWindowRect or GetMonitorInfo, which report physical pixels.
/// </summary>
public static class WindowPlacement
{
    private static readonly int PlacementSize = Marshal.SizeOf<WINDOWPLACEMENT>();
    private static readonly int MonitorInfoSize = Marshal.SizeOf<MONITORINFO>();

    public static WindowPlacementState? Capture(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)PlacementSize };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var rect = placement.rcNormalPosition;
        return new WindowPlacementState
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            // SW_SHOWMINIMIZED would otherwise persist "minimised" as the startup state.
            Maximized = placement.showCmd == SW_SHOWMAXIMIZED,
        };
    }

    /// <summary>
    /// Restores a saved rectangle. Returns false when there was nothing usable to restore,
    /// or when the result landed somewhere the user cannot reach; the caller should then
    /// fall back to a default placement.
    /// </summary>
    public static bool Apply(IntPtr hwnd, WindowPlacementState? state, bool startMinimized)
    {
        if (state is null || state.Width < 200 || state.Height < 150)
        {
            return false;
        }

        var placement = new WINDOWPLACEMENT
        {
            length = (uint)PlacementSize,
            rcNormalPosition = new RECT
            {
                Left = state.Left,
                Top = state.Top,
                Right = state.Left + state.Width,
                Bottom = state.Top + state.Height,
            },
            showCmd = (uint)(startMinimized
                ? SW_HIDE
                : state.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL),
        };

        if (!SetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        // Validated afterwards rather than predicted: GetWindowRect and GetMonitorInfo are
        // both physical pixels, so this comparison stays in one unit system.
        return IsReachable(hwnd);
    }

    /// <summary>
    /// True when a usable part of the window overlaps its nearest monitor's work area.
    /// Guards against a monitor that has been disconnected since the placement was saved.
    /// </summary>
    private static bool IsReachable(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)MonitorInfoSize };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var work = info.rcWork;
        var overlapWidth = Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left);
        var overlapHeight = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top);

        // Enough of the window to grab, not just a corner pixel.
        return overlapWidth >= 160 && overlapHeight >= 100;
    }
}
