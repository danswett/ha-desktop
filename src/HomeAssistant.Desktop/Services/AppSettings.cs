using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// User-visible configuration, persisted next to the isolated browser profile so that
/// deleting one folder resets the app completely.
/// </summary>
public sealed class AppSettings
{
    public string HomeUrl { get; set; } = "http://192.168.1.188:8123";

    /// <summary>Closing the window hides it to the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimising hides the window from the taskbar as well.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool AlwaysOnTop { get; set; }

    public bool DevToolsEnabled { get; set; }

    public WindowPlacementState? Placement { get; set; }

    // ---- persistence -------------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string? _path;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeAssistantDesktop");

    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// The WebView2 profile. Living under our own LocalAppData folder is what keeps this
    /// browser instance - processes, cache, cookies, storage - separate from every other.
    /// </summary>
    public static string WebViewUserDataFolder { get; } = Path.Combine(DataDirectory, "WebView2");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDirectory);

        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), SerializerOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not stop the app from starting.
            settings = new AppSettings();
        }

        settings._path = SettingsPath;
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        var path = _path ?? SettingsPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a settings write is not worth taking the app down for.
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(HomeUrl))
        {
            HomeUrl = "http://192.168.1.188:8123";
        }

        HomeUrl = HomeUrl.Trim();
        if (!HomeUrl.Contains("://", StringComparison.Ordinal))
        {
            HomeUrl = "http://" + HomeUrl;
        }
    }
}

public sealed class WindowPlacementState
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
