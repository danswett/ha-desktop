using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Puts the window menu - Restore, Move, Size, Minimize, Maximize, Close - back on a
/// custom title bar.
///
/// Windows raises it for free on a standard caption. Extending content into the title
/// bar replaces the non-client area with ordinary XAML, and WinUI does not reimplement
/// that part: right-clicking the title bar and pressing Alt+Space both do nothing
/// unless the app shows the menu itself.
///
/// The menu is the real one. <c>GetSystemMenu</c> hands back the window's own, so the
/// items, their order, their accelerators and their localisation are all the system's
/// rather than an imitation that would drift from it.
/// </summary>
public static class SystemMenu
{
    /// <summary>
    /// Shows the menu at a point in screen coordinates and carries out what was picked.
    /// </summary>
    public static void Show(IntPtr hwnd, int screenX, int screenY, bool maximized)
    {
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        // Windows normally fixes these up as it opens the menu, which it is not doing
        // here. Without this a maximized window offers to maximize again, and a
        // restored one offers a Restore that does nothing.
        Enable(menu, SC_RESTORE, maximized);
        Enable(menu, SC_MOVE, !maximized);
        Enable(menu, SC_SIZE, !maximized);
        Enable(menu, SC_MAXIMIZE, !maximized);
        Enable(menu, SC_MINIMIZE, true);
        Enable(menu, SC_CLOSE, true);

        // Without this the menu does not dismiss when the user clicks away from it.
        SetForegroundWindow(hwnd);

        var command = TrackPopupMenuEx(
            menu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_TOPALIGN,
            screenX,
            screenY,
            hwnd,
            IntPtr.Zero);

        if (command != 0)
        {
            // Posted rather than sent: the menu is still unwinding, and Move and Size
            // start their own modal loop.
            PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)command, IntPtr.Zero);
        }
    }

    private static void Enable(IntPtr menu, uint item, bool enabled) =>
        EnableMenuItem(menu, item, MF_BYCOMMAND | (enabled ? MF_ENABLED : MF_GRAYED));
}
