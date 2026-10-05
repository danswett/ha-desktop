namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Append-only log for the parts of the app that run with no window attached.
///
/// Push delivery, toast registration and the watchers all fail silently by nature:
/// there is nothing on screen to show an error on, and swallowing the exception is
/// usually the right behaviour. That makes a written record the only way to tell
/// "working" apart from "quietly broken".
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string Path { get; } = System.IO.Path.Combine(AppSettings.DataDirectory, "app.log");

    public static void Info(string component, string message) => Write("INFO", component, message);

    public static void Warn(string component, string message) => Write("WARN", component, message);

    public static void Error(string component, string message, Exception? ex = null) =>
        Write("ERROR", component, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string component, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                Roll();
                File.AppendAllText(Path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {level,-5} {component,-12} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the thing that breaks the app.
        }
    }

    private static void Roll()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var previous = Path + ".1";
        File.Delete(previous);
        File.Move(Path, previous);
    }
}
