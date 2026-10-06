using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// A system-wide key combination that summons the window, registered with the shell so
/// it works whatever has focus.
///
/// Windows hands the combination to exactly one window, first come first served, so
/// registration genuinely fails when something else already holds it. That is reported
/// rather than swallowed: a hotkey that silently does nothing is worse than none.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4841;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>Without this, holding the keys down repeats the message endlessly.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly IntPtr _hwnd;
    private readonly WindowMessages _messages;
    private readonly WindowMessages.Handler _handler;

    private bool _registered;
    private bool _disposed;

    /// <summary>Raised on the window's thread when the combination is pressed.</summary>
    public event Action? Pressed;

    public GlobalHotkey(IntPtr hwnd, WindowMessages messages)
    {
        _hwnd = hwnd;
        _messages = messages;
        _handler = OnMessage;
        _messages.Add(_handler);
    }

    /// <summary>
    /// Binds the combination, replacing whatever was bound before. Returns false only
    /// when a usable combination was refused, which means another program holds it.
    /// </summary>
    public bool Set(HotkeyBinding? binding)
    {
        Unregister();

        if (binding is null || !binding.IsUsable)
        {
            Log.Info("hotkey", "no global hotkey is set");
            return true;
        }

        var modifiers = MOD_NOREPEAT
            | (binding.Control ? MOD_CONTROL : 0)
            | (binding.Alt ? MOD_ALT : 0)
            | (binding.Shift ? MOD_SHIFT : 0)
            | (binding.Win ? MOD_WIN : 0);

        if (RegisterHotKey(_hwnd, HotkeyId, modifiers, binding.Key))
        {
            _registered = true;
            Log.Info("hotkey", $"{binding} will summon the window");
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
        {
            Log.Warn("hotkey", $"{binding} is already taken by another program");
            return false;
        }

        Log.Warn("hotkey", $"{binding} was refused: {new Win32Exception(error).Message}");
        return false;
    }

    private bool OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != WM_HOTKEY || wParam.ToInt64() != HotkeyId)
        {
            return false;
        }

        Pressed?.Invoke();
        return true;
    }

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.Remove(_handler);
        Unregister();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
