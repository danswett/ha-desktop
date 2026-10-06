using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// User-visible configuration, persisted next to the isolated browser profile so that
/// deleting one folder resets the app completely.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The address on the local network. Preferred whenever it answers: going through
    /// the public tunnel makes Home Assistant attribute every client to one WAN
    /// address, which is what trips its IP ban.
    /// </summary>
    public string InternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// The address that works from anywhere. Optional, and only needed by a machine
    /// that leaves the network it was set up on.
    /// </summary>
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Superseded by <see cref="InternalUrl"/>. Retained so an older settings file
    /// still loads, and migrated away on first read.
    /// </summary>
    public string? HomeUrl { get; set; }

    /// <summary>
    /// A system-wide key combination that brings the window up from anywhere. Nothing
    /// is bound by default: taking a combination the user has not asked for would be
    /// taking it away from whatever they already use it for.
    /// </summary>
    public HotkeyBinding Hotkey { get; set; } = new();

    /// <summary>
    /// Offer new releases through Home Assistant''s update screen. On by default: the
    /// app is meant to run unattended, and a desktop app that never mentions its own
    /// updates simply does not get them.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Closing the window hides it to the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimising hides the window from the taskbar as well.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// Keep painting while the window is completely covered by another window.
    ///
    /// Timers keep running either way - that is what keeps the dashboard current -
    /// so this only controls whether it also rasterises and composites pixels that
    /// nobody can see.
    /// </summary>
    public bool RenderWhenCovered { get; set; }

    /// <summary>
    /// Ask the page for reduced motion. Home Assistant honours this and drops its
    /// continuous animations, which are the bulk of a dashboard's frame cost.
    /// </summary>
    public bool ReduceAnimations { get; set; }

    public bool DevToolsEnabled { get; set; }

    /// <summary>
    /// Ask for Windows Hello before showing the dashboard at launch.
    ///
    /// Not a security boundary - the refresh token is DPAPI-protected and therefore
    /// readable by anything already running as this user. This covers the case it
    /// says it does: an unattended, unlocked machine with the house on screen.
    /// </summary>
    public bool RequireWindowsHello { get; set; }

    /// <summary>
    /// Taskbar jump list entries, in the order they appear. Windows shows at most
    /// <see cref="JumpList.MaxSlots"/> of them however many are configured.
    /// </summary>
    public List<JumpListSlot> JumpListSlots { get; set; } = [];

    /// <summary>
    /// Buttons under the taskbar thumbnail. Same shape as a jump list entry, plus the
    /// icon: these show no text, so the icon and the tooltip are all there is.
    /// </summary>
    public List<JumpListSlot> ThumbButtons { get; set; } = [];

    /// <summary>
    /// Written by tools/Register-TickerTarget.ps1. Identifies this machine's mobile_app
    /// registration so the push channel can be opened. Not a credential on its own - the
    /// channel also requires an authenticated websocket.
    /// </summary>
    public string? PushWebhookId { get; set; }

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
        // An older file carried a single address, which was always the local one.
        if (!string.IsNullOrWhiteSpace(HomeUrl))
        {
            if (string.IsNullOrWhiteSpace(InternalUrl))
            {
                InternalUrl = HomeUrl;
            }

            HomeUrl = null;
        }

        InternalUrl = NormalizeUrl(InternalUrl);
        ExternalUrl = NormalizeUrl(ExternalUrl);
    }

    /// <summary>
    /// Tidies a typed-in address. Deliberately does not invent a default: a machine
    /// that has not been told where Home Assistant lives should say so rather than
    /// quietly failing to reach somebody else's.
    /// </summary>
    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        return url;
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

