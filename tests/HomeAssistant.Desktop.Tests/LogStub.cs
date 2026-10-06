namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Stands in for the real logger, which writes beside the app's settings and so would
/// drag the whole settings model - and with it the jump list's COM interop - into a
/// test project that deliberately targets plain .NET.
///
/// Nothing under test asserts on logging; this exists so the code under test compiles.
/// </summary>
internal static class Log
{
    public static void Info(string category, string message)
    {
    }

    public static void Warn(string category, string message)
    {
    }
}
