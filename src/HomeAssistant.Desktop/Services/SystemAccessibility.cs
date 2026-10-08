using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Whether Windows is in a high contrast theme.
///
/// This matters because the app paints its own title bar in Home Assistant's colours.
/// That is the right thing to do normally and the wrong thing to do here: high contrast
/// exists so that someone who needs a specific, guaranteed pairing of colours gets it
/// everywhere, and an app that overrides it with a colour of its own - however
/// tastefully chosen - takes that away.
///
/// Read through <c>SystemParametersInfo</c> rather than
/// <c>Windows.UI.ViewManagement.AccessibilitySettings</c>, which wants a CoreWindow that
/// an unpackaged desktop app does not have.
/// </summary>
public static class SystemAccessibility
{
    public static bool IsHighContrast()
    {
        try
        {
            var info = new HIGHCONTRAST
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HIGHCONTRAST>(),
            };

            return SystemParametersInfo(SPI_GETHIGHCONTRAST, info.cbSize, ref info, 0)
                && (info.dwFlags & HCF_HIGHCONTRASTON) != 0;
        }
        catch (Exception ex)
        {
            // Guessing "on" would strip the app of its colours for everybody; guessing
            // "off" is the status quo, and is what a failure here should fall back to.
            Log.Warn("a11y", $"could not read the high contrast setting: {ex.Message}");
            return false;
        }
    }
}
