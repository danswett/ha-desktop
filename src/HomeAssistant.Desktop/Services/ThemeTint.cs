namespace HomeAssistant.Desktop.Services;

/// <summary>An opaque colour, kept free of WinUI so the arithmetic below can be tested.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb White { get; } = new(255, 255, 255);

    public static Rgb Black { get; } = new(0, 0, 0);
}

/// <summary>
/// Works out what to paint the title bar so that it reads as part of the dashboard
/// rather than as a strip of Windows sitting above it.
///
/// The colour comes from the page. Home Assistant resolves <c>--app-theme-color</c> -
/// which is its app header's background - and writes it into
/// <c>&lt;meta name="theme-color"&gt;</c> every time the theme is applied
/// (frontend, src/state/themes-mixin.ts). That meta tag exists for exactly this
/// purpose, so it is the right thing to follow rather than guessing at a theme
/// variable.
///
/// Parsing is deliberately narrow. The injected watcher resolves whatever the theme
/// holds through the browser's own computed style first, so what arrives here is
/// already normalised to an <c>rgb()</c> or <c>rgba()</c> triple. Hex is accepted too,
/// because it costs four lines and means a value that skipped that step still works.
/// </summary>
public static class ThemeTint
{
    public static bool TryParse(string? css, out Rgb colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(css))
        {
            return false;
        }

        var text = css.Trim();

        return text.StartsWith('#')
            ? TryParseHex(text, out colour)
            : TryParseFunctional(text, out colour);
    }

    private static bool TryParseHex(string text, out Rgb colour)
    {
        colour = default;
        var digits = text[1..];

        // #rgb and #rgba are shorthand for doubled digits; the alpha pair is ignored
        // either way, since a title bar cannot be partly transparent.
        if (digits.Length is 3 or 4)
        {
            digits = string.Concat(digits[..3].Select(c => new string(c, 2)));
        }
        else if (digits.Length is 8)
        {
            digits = digits[..6];
        }
        else if (digits.Length is not 6)
        {
            return false;
        }

        if (!byte.TryParse(digits[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(digits[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(digits[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        colour = new Rgb(r, g, b);
        return true;
    }

    private static bool TryParseFunctional(string text, out Rgb colour)
    {
        colour = default;

        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        var name = text[..open].Trim();
        if (!name.Equals("rgb", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("rgba", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Both the legacy comma form and the modern space-separated one, which is what
        // getComputedStyle returns when a theme carries an alpha channel.
        var parts = text[(open + 1)..close]
            .Replace('/', ' ')
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            return false;
        }

        // A fully transparent header is not a colour to copy; it means the theme has
        // not been applied yet, and painting the title bar black would be wrong.
        if (parts.Length > 3
            && double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var alpha)
            && alpha <= 0)
        {
            return false;
        }

        if (!TryParseChannel(parts[0], out var r)
            || !TryParseChannel(parts[1], out var g)
            || !TryParseChannel(parts[2], out var b))
        {
            return false;
        }

        colour = new Rgb(r, g, b);
        return true;
    }

    private static bool TryParseChannel(string text, out byte value)
    {
        value = 0;

        var percent = text.EndsWith('%');
        if (!double.TryParse(
                percent ? text[..^1] : text,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }

        if (percent)
        {
            number = number * 255 / 100;
        }

        value = (byte)Math.Clamp(Math.Round(number), 0, 255);
        return true;
    }

    /// <summary>
    /// Black or white, whichever the eye can actually read against this background.
    ///
    /// Chosen by contrast ratio rather than a luminance threshold, because the two
    /// disagree around mid greys - exactly where Home Assistant's own header colours
    /// tend to sit.
    /// </summary>
    public static Rgb Contrasting(Rgb background)
    {
        var luminance = RelativeLuminance(background);
        var againstWhite = 1.05 / (luminance + 0.05);
        var againstBlack = (luminance + 0.05) / 0.05;

        return againstWhite >= againstBlack ? Rgb.White : Rgb.Black;
    }

    /// <summary>
    /// Mixes <paramref name="over"/> into <paramref name="background"/>, for the hover
    /// and pressed states of the caption buttons. Deriving them from the background
    /// rather than hardcoding a grey is what keeps them visible on a light theme.
    /// </summary>
    public static Rgb Blend(Rgb background, Rgb over, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        static byte Mix(byte from, byte to, double amount) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * amount)), 0, 255);

        return new Rgb(
            Mix(background.R, over.R, amount),
            Mix(background.G, over.G, amount),
            Mix(background.B, over.B, amount));
    }

    /// <summary>WCAG relative luminance, which expects linearised sRGB channels.</summary>
    public static double RelativeLuminance(Rgb colour)
    {
        static double Linear(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(colour.R))
            + (0.7152 * Linear(colour.G))
            + (0.0722 * Linear(colour.B));
    }
}
