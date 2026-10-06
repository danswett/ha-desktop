using System.Runtime.InteropServices;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Draws a count on the app's taskbar button.
///
/// Uses the shell's overlay icon rather than Windows App SDK's BadgeNotificationManager,
/// which expects package identity this app deliberately does not have. An overlay icon
/// is a plain Win32 facility: it works unpackaged, it sits on the taskbar button the
/// user already has pinned, and its description is what a screen reader announces.
///
/// All calls must be made on the UI thread. The shell's COM object has thread affinity,
/// and the taskbar button belongs to that thread's window regardless.
/// </summary>
public sealed partial class TaskbarBadge : IDisposable
{
    private readonly IntPtr _hwnd;

    private ITaskbarList3? _taskbar;
    private bool _initialised;
    private IntPtr _currentIcon;
    private int _count;
    private bool _disposed;

    public TaskbarBadge(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Shows <paramref name="count"/> on the taskbar button, or clears it when zero.
    /// </summary>
    public void SetCount(int count)
    {
        if (_disposed)
        {
            return;
        }

        count = Math.Max(0, count);
        if (count == _count && _initialised)
        {
            return;
        }

        _count = count;
        Apply();
    }

    /// <summary>
    /// Re-applies the badge after the shell has restarted. Explorer rebuilds the taskbar
    /// from scratch and the overlay goes with it, in the same way tray icons do.
    /// </summary>
    public void Reapply()
    {
        if (_disposed)
        {
            return;
        }

        // The old COM object belonged to the shell process that just died.
        Release();
        _initialised = false;
        Apply();
    }

    private void Apply()
    {
        try
        {
            if (!EnsureInitialised())
            {
                return;
            }

            var previous = _currentIcon;

            if (_count == 0)
            {
                _currentIcon = IntPtr.Zero;
                _taskbar!.SetOverlayIcon(_hwnd, IntPtr.Zero, null);
                Log.Info("badge", "taskbar overlay cleared");
            }
            else
            {
                _currentIcon = BadgeIcon.CreateOverlay(_count, Math.Max(16, GetSystemMetrics(SM_CXSMICON)));
                var description = _count == 1
                    ? "1 new notification"
                    : $"{_count} new notifications";
                _taskbar!.SetOverlayIcon(_hwnd, _currentIcon, description);
                Log.Info("badge", $"taskbar overlay set to {_count}");
            }

            // Only after the shell has taken the new one: destroying an icon the taskbar
            // is still drawing leaves a torn overlay.
            if (previous != IntPtr.Zero)
            {
                DestroyIcon(previous);
            }
        }
        catch (COMException ex)
        {
            Log.Error("badge", "could not update the taskbar badge", ex);
        }
    }

    private bool EnsureInitialised()
    {
        if (_initialised)
        {
            return true;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(TaskbarListClsid)
                ?? throw new NotSupportedException("The shell did not register CLSID_TaskbarList.");

            _taskbar = (ITaskbarList3)Activator.CreateInstance(type)!;
            _taskbar.HrInit();
            _initialised = true;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            Log.Error("badge", "the shell would not provide ITaskbarList3", ex);
            _taskbar = null;
            return false;
        }
    }

    private void Release()
    {
        if (_currentIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentIcon);
            _currentIcon = IntPtr.Zero;
        }

        if (_taskbar is not null)
        {
            Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_initialised && _taskbar is not null)
            {
                _taskbar.SetOverlayIcon(_hwnd, IntPtr.Zero, null);
            }
        }
        catch (COMException)
        {
            // Shutting down regardless.
        }

        Release();
    }

    private const int SM_CXSMICON = 49;

    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Only <c>SetOverlayIcon</c> is used, but every method that precedes it has to be
    /// declared, in order, for the vtable to line up.
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);

        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

        void SetProgressState(IntPtr hwnd, int flags);

        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);

        void UnregisterTab(IntPtr hwndTab);

        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);

        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);

        void ThumbBarAddButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

        void ThumbBarUpdateButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);

        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
