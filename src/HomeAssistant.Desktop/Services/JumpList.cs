using System.Runtime.InteropServices;
using System.Text;

namespace HomeAssistant.Desktop.Services;

/// <summary>What a jump list entry points at.</summary>
public enum JumpTargetKind
{
    /// <summary>A path within Home Assistant, such as a dashboard or a view.</summary>
    Page,

    /// <summary>A single entity.</summary>
    Entity,
}

/// <summary>What happens when the entry is chosen.</summary>
public enum JumpTargetAction
{
    /// <summary>Bring the window up showing the target.</summary>
    Open,

    /// <summary>Act on the entity without disturbing the window.</summary>
    Perform,
}

public sealed class JumpListSlot
{
    public string Title { get; set; } = string.Empty;

    public JumpTargetKind Kind { get; set; } = JumpTargetKind.Page;

    /// <summary>A path like <c>/lovelace/kitchen</c>, or an entity id.</summary>
    public string Target { get; set; } = string.Empty;

    public JumpTargetAction Action { get; set; } = JumpTargetAction.Open;

    /// <summary>
    /// Which icon to draw, for the thumbnail toolbar. Ignored by the jump list, which
    /// shows text and takes its icon from the executable.
    /// </summary>
    public string Glyph { get; set; } = string.Empty;

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Target);

    /// <summary>The command line this slot launches the app with.</summary>
    public string ToArguments() => Kind switch
    {
        JumpTargetKind.Entity when Action == JumpTargetAction.Perform =>
            $"--perform \"{Target}\"",
        JumpTargetKind.Entity => $"--entity \"{Target}\"",
        _ => $"--open \"{Target}\"",
    };
}

/// <summary>
/// Publishes the taskbar jump list.
///
/// The modern Windows.UI.StartScreen.JumpList needs package identity, which this app
/// does not have, so this uses the Win32 ICustomDestinationList instead. Verified
/// working unpackaged: BeginList reports ten available slots and CommitList persists a
/// .customDestinations-ms file under the app's AppUserModelID.
///
/// That ID is the one the Windows App SDK already registered for notifications, which
/// it derives from the executable's path. Taking it from the process rather than
/// inventing one keeps the jump list attached to the same taskbar button the app
/// already owns.
/// </summary>
public static class JumpList
{
    /// <summary>Windows itself will not show more than this, whatever is published.</summary>
    public const int MaxSlots = 10;

    public static void Publish(IReadOnlyList<JumpListSlot> slots, string executablePath)
    {
        var usable = slots.Where(s => s.IsUsable).Take(MaxSlots).ToList();

        try
        {
            var list = (ICustomDestinationList)new CDestinationList();

            if (GetAppId() is { } appId)
            {
                list.SetAppID(appId);
            }

            var iid = ObjectArrayIid;
            var hr = list.BeginList(out _, ref iid, out _);
            if (hr != 0)
            {
                Log.Warn("jumplist", $"BeginList failed (0x{hr:X8})");
                return;
            }

            if (usable.Count == 0)
            {
                // An empty committed list is how the entry is removed; deleting it
                // outright would also discard anything the user had pinned.
                list.CommitList();
                Log.Info("jumplist", "cleared");
                return;
            }

            var collection = (IObjectCollection)new CObjectCollection();
            foreach (var slot in usable)
            {
                collection.AddObject(CreateLink(executablePath, slot));
            }

            var added = list.AddUserTasks((IObjectArray)collection);
            if (added != 0)
            {
                list.AbortList();
                Log.Warn("jumplist", $"AddUserTasks failed (0x{added:X8})");
                return;
            }

            list.CommitList();
            Log.Info("jumplist", $"published {usable.Count} item(s)");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            // A taskbar without a jump list is a missing convenience, not a failure
            // worth taking the app down for.
            Log.Warn("jumplist", $"could not publish: {ex.Message}");
        }
    }

    private static object CreateLink(string executablePath, JumpListSlot slot)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(executablePath);
        link.SetArguments(slot.ToArguments());
        link.SetIconLocation(executablePath, 0);
        link.SetDescription(Describe(slot));

        // The taskbar renders System.Title, not the link's description, so the visible
        // text has to go through the link's property store.
        var store = (IPropertyStore)link;
        var key = new PropertyKey(TitleFormatId, 2);
        var value = AllocStringPropVariant(slot.Title);
        try
        {
            store.SetValue(ref key, value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(value);
            Marshal.FreeCoTaskMem(value);
        }

        return link;
    }

    private static string Describe(JumpListSlot slot) => slot.Kind switch
    {
        JumpTargetKind.Entity when slot.Action == JumpTargetAction.Perform =>
            $"Act on {slot.Target}",
        JumpTargetKind.Entity => $"Show {slot.Target}",
        _ => $"Open {slot.Target}",
    };

    /// <summary>
    /// The identity the shell associates with this process. Returns null when nothing
    /// has set one, in which case the shell falls back to its own derivation and the
    /// list still attaches correctly.
    /// </summary>
    private static string? GetAppId()
    {
        try
        {
            var hr = GetCurrentProcessExplicitAppUserModelID(out var id);
            return hr == 0 ? id : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    // ---- interop -----------------------------------------------------------

    private const ushort VT_LPWSTR = 31;

    private static readonly Guid TitleFormatId = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid ObjectArrayIid = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");

    /// <summary>
    /// Builds a VT_LPWSTR PROPVARIANT by hand. The InitPropVariantFrom* helpers look
    /// like exports but are inline in propvarutil.h, so there is nothing to call.
    /// Layout: the type occupies the first two bytes and the value union starts at
    /// offset eight.
    /// </summary>
    private static IntPtr AllocStringPropVariant(string value)
    {
        var pv = Marshal.AllocCoTaskMem(24);
        for (var i = 0; i < 24; i++)
        {
            Marshal.WriteByte(pv, i, 0);
        }

        Marshal.WriteInt16(pv, 0, unchecked((short)VT_LPWSTR));
        Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value));
        return pv;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pvar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] out string appId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);

        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray's methods, repeated because COM inheritance is vtable order.
        void GetCount(out uint count);

        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void AddObject([MarshalAs(UnmanagedType.Interface)] object pvObject);

        void AddFromArray(IObjectArray source);

        void RemoveObjectAt(uint index);

        void Clear();
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [PreserveSig]
        int BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);

        void AppendKnownCategory(int category);

        [PreserveSig]
        int AddUserTasks(IObjectArray items);

        void CommitList();

        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);

        void AbortList();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint props);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, IntPtr pv);

        void SetValue(ref PropertyKey key, IntPtr pv);

        void Commit();
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink
    {
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CDestinationList
    {
    }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CObjectCollection
    {
    }
}
