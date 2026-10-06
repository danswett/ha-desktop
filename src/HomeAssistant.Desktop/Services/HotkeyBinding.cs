using System.Text.Json.Serialization;

namespace HomeAssistant.Desktop.Services;

/// <summary>A key combination, as stored in settings and shown in the picker.</summary>
public sealed class HotkeyBinding
{
    public bool Control { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    /// <summary>The Windows key. Allowed, but most combinations are taken by the shell.</summary>
    public bool Win { get; set; }

    /// <summary>A virtual-key code. Zero means nothing is bound.</summary>
    public uint Key { get; set; }

    /// <summary>
    /// Windows refuses a hotkey with no modifier for anything but the function keys,
    /// and a bare letter would be unusable anyway.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => Key != 0 && (Control || Alt || Shift || Win);

    public HotkeyBinding Clone() => new()
    {
        Control = Control,
        Alt = Alt,
        Shift = Shift,
        Win = Win,
        Key = Key,
    };

    public override string ToString()
    {
        if (Key == 0)
        {
            return "None";
        }

        var parts = new List<string>();
        if (Win)
        {
            parts.Add("Win");
        }

        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// A readable name for a virtual-key code. The printable keys are named from the
    /// current layout so the picker shows the key the user actually pressed.
    /// </summary>
    public static string KeyName(uint key) => key switch
    {
        0 => "None",
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x09 => "Tab",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0xC0 => "`",
        0xBD => "-",
        0xBB => "=",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        _ => "Key " + key,
    };
}
