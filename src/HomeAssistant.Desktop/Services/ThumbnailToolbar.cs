using System.Drawing;
using System.Runtime.InteropServices;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// The row of buttons under the taskbar thumbnail, shown when the app is hovered.
///
/// Three things about the shell's API shape this class.
///
/// The buttons can only be added once per window. After that they can be updated but
/// never added to, so the full complement is registered up front and the unused ones
/// are flagged hidden rather than left out.
///
/// They cannot be registered until the taskbar button exists, which the shell announces
/// with the TaskbarButtonCreated message - and announces again after Explorer restarts,
/// at which point the buttons have to be registered afresh.
///
/// Clicks arrive as WM_COMMAND on the window that owns the taskbar button, so this has
/// to see that window's messages, which it gets from the shared <see cref="WindowMessages"/>
/// hook - WinUI itself offers no way to observe arbitrary messages.
/// </summary>
public sealed class ThumbnailToolbar : IDisposable
{
    /// <summary>The shell will not show more than this many.</summary>
    public const int MaxButtons = 7;

    private const int WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;

    private const uint THBF_ENABLED = 0x0;
    private const uint THBF_HIDDEN = 0x8;

    /// <summary>Button ids start here so they cannot collide with a menu command.</summary>
    private const int FirstButtonId = 0x5100;

    private readonly IntPtr _hwnd;
    private readonly uint _buttonCreatedMessage;
    private readonly WindowMessages _messages;
    private readonly WindowMessages.Handler _handler;
    private readonly List<IntPtr> _icons = [];

    private ITaskbarList3? _taskbar;
    private IReadOnlyList<JumpListSlot> _buttons = [];
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised on the UI thread when one of the buttons is pressed.</summary>
    public event Action<JumpListSlot>? Invoked;

    public ThumbnailToolbar(IntPtr hwnd, WindowMessages messages)
    {
        _hwnd = hwnd;
        _messages = messages;
        _buttonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

        _handler = OnMessage;
        _messages.Add(_handler);
    }

    /// <summary>
    /// Sets what the buttons do. Safe to call before the taskbar button exists; the
    /// shell is told as soon as it does.
    /// </summary>
    public void SetButtons(IReadOnlyList<JumpListSlot> buttons)
    {
        _buttons = buttons.Where(b => b.IsUsable).Take(MaxButtons).ToList();

        if (_registered)
        {
            Apply(update: true);
        }
    }

    private void Apply(bool update)
    {
        if (_disposed)
        {
            return;
        }

        _taskbar ??= TaskbarList.Create("thumbbar");
        if (_taskbar is null)
        {
            return;
        }

        var size = Marshal.SizeOf<ThumbButton>();
        var block = Marshal.AllocHGlobal(size * MaxButtons);
        var fresh = new List<IntPtr>();

        try
        {
            for (var i = 0; i < MaxButtons; i++)
            {
                var slot = i < _buttons.Count ? _buttons[i] : null;
                var icon = slot is null ? IntPtr.Zero : GlyphIcon.Create(slot.Glyph);
                if (icon != IntPtr.Zero)
                {
                    fresh.Add(icon);
                }

                var button = new ThumbButton
                {
                    Mask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                    Id = (uint)(FirstButtonId + i),
                    Bitmap = 0,
                    Icon = icon,
                    // The shell shows no text, so the tooltip is the only label there is.
                    Tip = slot?.Title ?? string.Empty,
                    Flags = slot is null ? THBF_HIDDEN : THBF_ENABLED,
                };

                Marshal.StructureToPtr(button, block + (i * size), false);
            }

            var hr = update
                ? _taskbar.ThumbBarUpdateButtons(_hwnd, MaxButtons, block)
                : _taskbar.ThumbBarAddButtons(_hwnd, MaxButtons, block);

            if (hr != 0)
            {
                Log.Warn("thumbbar", $"{(update ? "update" : "add")} failed (0x{hr:X8})");
                DestroyAll(fresh);
                return;
            }

            _registered = true;

            // Only after the shell has taken the new ones: freeing an icon it is still
            // drawing leaves a torn button.
            DestroyAll(_icons);
            _icons.Clear();
            _icons.AddRange(fresh);

            Log.Info("thumbbar", $"{_buttons.Count} button(s) shown");
        }
        catch (COMException ex)
        {
            Log.Warn("thumbbar", $"could not set the buttons: {ex.Message}");
            DestroyAll(fresh);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _buttonCreatedMessage)
        {
            // Also arrives after an Explorer restart, which discards the old buttons.
            _registered = false;
            _taskbar = null;
            Apply(update: false);
            return false;
        }

        if (msg == WM_COMMAND)
        {
            var code = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            var id = (int)(wParam.ToInt64() & 0xFFFF);

            if (code == THBN_CLICKED)
            {
                var index = id - FirstButtonId;
                if (index >= 0 && index < _buttons.Count)
                {
                    Invoked?.Invoke(_buttons[index]);
                }

                return true;
            }
        }

        return false;
    }

    private static void DestroyAll(IEnumerable<IntPtr> icons)
    {
        foreach (var icon in icons)
        {
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.Remove(_handler);

        DestroyAll(_icons);
        _icons.Clear();

        if (_taskbar is not null)
        {
            Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Tip;

        public uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// Turns one of the icon font's glyphs into an HICON for a thumbnail button.
/// </summary>
public static class GlyphIcon
{
    private const string IconFont = "Segoe Fluent Icons";

    /// <summary>
    /// The glyphs offered for buttons, each checked against the font rather than taken
    /// on trust: a codepoint the font does not have still draws, as a blank or a box,
    /// so a palette assembled from documentation alone can ship tofu. U+EAFD, which
    /// looked like a door, is absent and was dropped for exactly that reason.
    /// </summary>
    public static IReadOnlyList<(string Name, string Glyph)> Palette { get; } =
    [
        ("Power", "\uE7E8"),
        ("Lightbulb", "\uE781"),
        ("Brightness", "\uE706"),
        ("Lock", "\uE72E"),
        ("Unlock", "\uE785"),
        ("Home", "\uE80F"),
        ("Play", "\uE768"),
        ("Pause", "\uE769"),
        ("Shield", "\uEA18"),
        ("Temperature", "\uE9CA"),
        ("Fan", "\uE81E"),
        ("Camera", "\uE722"),
        ("Speaker", "\uE767"),
        ("Garage", "\uE81C"),
        ("Scene", "\uE8B9"),
        ("Refresh", "\uE72C"),
        ("Settings", "\uE713"),
        ("Bell", "\uEA8F"),
        ("Leaf", "\uE8BE"),
    ];

    public static IntPtr Create(string? glyph)
    {
        var text = string.IsNullOrEmpty(glyph) ? Palette[0].Glyph : glyph;

        try
        {
            const int size = 32;
            using var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using var font = new Font(IconFont, 21, FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };

                graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), format);
            }

            return bitmap.GetHicon();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            Log.Warn("thumbbar", $"could not draw a button icon: {ex.Message}");
            return IntPtr.Zero;
        }
    }
}
