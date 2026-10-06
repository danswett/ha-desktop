using System.Runtime.InteropServices;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// The shell's taskbar button interface, used for the unread overlay and the thumbnail
/// toolbar alike.
///
/// Only a few of these methods are called, but every one that precedes them has to be
/// declared, in order, for the vtable to line up.
/// </summary>
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
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

    [PreserveSig]
    int ThumbBarAddButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

    [PreserveSig]
    int ThumbBarUpdateButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

    void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

    void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

    void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);

    void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
}

internal static class TaskbarList
{
    internal static readonly Guid Clsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    /// <summary>Creates and initialises the shell's taskbar object, or null if it will not.</summary>
    internal static ITaskbarList3? Create(string component)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(Clsid)
                ?? throw new NotSupportedException("The shell did not register CLSID_TaskbarList.");

            var list = (ITaskbarList3)Activator.CreateInstance(type)!;
            list.HrInit();
            return list;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            Log.Error(component, "the shell would not provide ITaskbarList3", ex);
            return null;
        }
    }
}
