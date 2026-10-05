using System.Runtime.InteropServices;
using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Notification-area icon backed by a message-only window.
///
/// Hand-rolled rather than taken from a package because an always-running app has to
/// survive an Explorer restart: when the shell comes back it broadcasts TaskbarCreated
/// and every icon must re-add itself or it is gone until the app restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public enum MenuCommand
    {
        None = 0,
        ShowHide = 1,
        Reload = 2,
        AlwaysOnTop = 3,
        StartWithWindows = 4,
        Settings = 5,
        Exit = 6,
    }

    private const string WindowClassName = "HomeAssistantDesktop.TrayWindow";

    private readonly WndProc _wndProcDelegate;
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _iconId = 1;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private NOTIFYICONDATA _data;
    private bool _added;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a tray menu item or click is actioned.</summary>
    public event Action<MenuCommand>? CommandInvoked;

    /// <summary>Queried when the context menu is built, so check marks reflect live state.</summary>
    public Func<MenuCommand, bool>? IsChecked { get; set; }

    /// <summary>Queried for the show/hide item's caption.</summary>
    public Func<string>? ShowHideCaption { get; set; }

    public TrayIcon(string tooltip, string iconPath)
    {
        _wndProcDelegate = TrayWndProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        CreateMessageWindow();
        _hIcon = LoadTrayIcon(iconPath);

        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _iconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = Truncate(tooltip, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            uVersion = NOTIFYICON_VERSION_4,
        };

        AddIcon();
    }

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };

        // A duplicate class registration is benign; the window creation below is what matters.
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, WindowClassName, "HomeAssistantDesktopTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create the tray message window (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static IntPtr LoadTrayIcon(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return IntPtr.Zero;
        }

        var cx = GetSystemMetrics(SM_CXSMICON);
        var cy = GetSystemMetrics(SM_CYSMICON);
        return LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
    }

    private void AddIcon()
    {
        if (Shell_NotifyIcon(NIM_ADD, ref _data))
        {
            _added = true;
            Shell_NotifyIcon(NIM_SETVERSION, ref _data);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed || !_added)
        {
            return;
        }

        _data.szTip = Truncate(tooltip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    private IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted and dropped every tray icon. Re-add ours.
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_TRAYICON:
                // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
                var evt = (uint)(lParam.ToInt64() & 0xFFFF);
                switch (evt)
                {
                    case WM_LBUTTONUP:
                    case WM_LBUTTONDBLCLK:
                        CommandInvoked?.Invoke(MenuCommand.ShowHide);
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;
                }

                return IntPtr.Zero;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var showHide = ShowHideCaption?.Invoke() ?? "Show dashboard";
            AppendMenu(menu, MF_STRING, (UIntPtr)MenuCommand.ShowHide, showHide);
            AppendMenu(menu, MF_STRING, (UIntPtr)MenuCommand.Reload, "Reload");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | Check(MenuCommand.AlwaysOnTop), (UIntPtr)MenuCommand.AlwaysOnTop, "Always on top");
            AppendMenu(menu, MF_STRING | Check(MenuCommand.StartWithWindows), (UIntPtr)MenuCommand.StartWithWindows, "Start with Windows");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)MenuCommand.Settings, "Settings\u2026");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)MenuCommand.Exit, "Exit");

            GetCursorPos(out var pt);

            // Required so the menu dismisses when the user clicks away from it.
            SetForegroundWindow(_hwnd);

            var selected = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            if (selected != 0)
            {
                CommandInvoked?.Invoke((MenuCommand)selected);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private uint Check(MenuCommand command) =>
        IsChecked?.Invoke(command) == true ? MF_CHECKED : MF_UNCHECKED;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
