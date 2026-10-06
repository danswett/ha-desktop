using System.Runtime.InteropServices;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Lets the app see raw window messages, which WinUI otherwise keeps to itself.
///
/// More than one feature needs them - the thumbnail toolbar's clicks and the global
/// hotkey both arrive this way - and each subclassing the window separately would make
/// their lifetimes depend on each other: window procedures unhook correctly only in the
/// reverse of the order they were hooked, so disposing the first one to attach while a
/// later one is still in place would cut it out of the chain. One subclass, shared,
/// avoids the question.
/// </summary>
public sealed class WindowMessages : IDisposable
{
    /// <summary>Returns true to swallow the message; false to let it carry on.</summary>
    public delegate bool Handler(uint message, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;

    private readonly IntPtr _hwnd;
    private readonly WndProcDelegate _subclass;
    private readonly IntPtr _previous;
    private readonly List<Handler> _handlers = [];

    private bool _disposed;

    public WindowMessages(IntPtr hwnd)
    {
        _hwnd = hwnd;

        // Held as a field: a delegate marshalled to a function pointer is not kept alive
        // by the pointer, and collecting it leaves the window calling freed memory.
        _subclass = SubclassProc;
        _previous = SetWindowLongPtr(
            hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclass));
    }

    public void Add(Handler handler) => _handlers.Add(handler);

    public void Remove(Handler handler) => _handlers.Remove(handler);

    private IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Snapshotted, because a handler may well add or remove one.
        foreach (var handler in _handlers.ToArray())
        {
            try
            {
                if (handler(msg, wParam, lParam))
                {
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                // This runs inside the window procedure, where an escaping exception
                // takes the process down rather than surfacing anywhere useful.
                Log.Warn("window", $"a message handler failed: {ex.Message}");
            }
        }

        return CallWindowProc(_previous, hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handlers.Clear();

        if (_previous != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previous);
        }

        GC.KeepAlive(_subclass);
    }

    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
