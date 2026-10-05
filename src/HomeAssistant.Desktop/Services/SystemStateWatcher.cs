using System.Runtime.InteropServices;
using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Watches the things Windows knows about that the window itself cannot see:
/// the session being locked, the display powering down, and the machine sleeping.
///
/// The occlusion watcher cannot cover these. A locked workstation runs on a separate
/// desktop, so our window is neither covered nor hidden by anything it can enumerate -
/// it stays "visible" and would happily keep compositing at the display refresh rate
/// to an audience of nobody.
///
/// Sleep is a correctness problem rather than a cost one: the network goes away with
/// the machine, so on resume the page needs reloading instead of being trusted.
/// </summary>
public sealed class SystemStateWatcher : IDisposable
{
    private const string WindowClassName = "HomeAssistantDesktop.SystemStateWindow";

    private readonly WndProc _wndProcDelegate;

    private IntPtr _hwnd;
    private IntPtr _displayNotification;
    private bool _registeredSession;
    private bool _disposed;

    /// <summary>True when the user could actually see the screen.</summary>
    public bool IsUserPresent { get; private set; } = true;

    /// <summary>Raised when <see cref="IsUserPresent"/> changes. Fires on the thread that created this.</summary>
    public event Action<bool>? UserPresenceChanged;

    /// <summary>Raised after the machine resumes from sleep, when the page should be reloaded.</summary>
    public event Action? Resumed;

    public SystemStateWatcher()
    {
        _wndProcDelegate = StateWndProc;
        CreateMessageWindow();

        _registeredSession = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);

        var displayGuid = GUID_CONSOLE_DISPLAY_STATE;
        _displayNotification = RegisterPowerSettingNotification(_hwnd, ref displayGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
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

        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, WindowClassName, "HomeAssistantDesktopSystemState", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create the system state window (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr StateWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_WTSSESSION_CHANGE:
                switch (wParam.ToInt32())
                {
                    case WTS_SESSION_LOCK:
                        SetPresence(false);
                        break;
                    case WTS_SESSION_UNLOCK:
                        SetPresence(true);
                        break;
                }

                return IntPtr.Zero;

            case WM_POWERBROADCAST:
                HandlePowerBroadcast(wParam.ToInt32(), lParam);
                return new IntPtr(1);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandlePowerBroadcast(int evt, IntPtr lParam)
    {
        switch (evt)
        {
            case PBT_APMSUSPEND:
                SetPresence(false);
                break;

            case PBT_APMRESUMESUSPEND:
            case PBT_APMRESUMEAUTOMATIC:
                SetPresence(true);
                Resumed?.Invoke();
                break;

            case PBT_POWERSETTINGCHANGE:
                if (lParam == IntPtr.Zero)
                {
                    break;
                }

                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    // Data is 0 off, 1 on, 2 dimmed. Dimmed still counts as watchable.
                    SetPresence(setting.Data != 0);
                }

                break;
        }
    }

    private void SetPresence(bool present)
    {
        if (present == IsUserPresent)
        {
            return;
        }

        IsUserPresent = present;
        UserPresenceChanged?.Invoke(present);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_displayNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = IntPtr.Zero;
        }

        if (_registeredSession)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            _registeredSession = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
