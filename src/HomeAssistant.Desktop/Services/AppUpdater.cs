using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Offers new releases through Home Assistant's own update screen.
///
/// The app publishes an MQTT <c>update</c> entity describing itself, which puts it in
/// Settings -> Updates next to Home Assistant's own, with a working Install button.
/// MQTT is not a stylistic choice: it is the only way an outside program holding just an
/// access token can create a real update entity. The mobile_app integration this app
/// already uses registers sensors and binary sensors only, and a state POSTed over the
/// REST API has no entity behind it, so its Install button calls a service that matches
/// nothing and silently does nothing.
///
/// Pressing Install publishes to the command topic, which is why the app subscribes over
/// its existing Home Assistant websocket.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    /// <summary>
    /// Long, because the only thing a check can discover is a release someone published
    /// by hand. Startup covers the case that matters - a machine that has been off.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>The whole payload travels through one MQTT message.</summary>
    private const int MaxNotesLength = 2000;

    private readonly HaMqtt _mqtt;
    private readonly ReleaseChecker _checker;
    private readonly UpdateInstaller _installer;
    private readonly Func<bool> _isEnabled;
    private readonly Action _requestExit;
    private readonly string _node;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ReleaseInfo? _latest;
    private bool _announced;
    private bool _installing;

    public AppUpdater(
        HaMqtt mqtt,
        ReleaseChecker checker,
        UpdateInstaller installer,
        Func<bool> isEnabled,
        Action requestExit)
    {
        _mqtt = mqtt;
        _checker = checker;
        _installer = installer;
        _isEnabled = isEnabled;
        _requestExit = requestExit;
        _node = Slug(Environment.MachineName);
    }

    /// <summary>
    /// What Home Assistant publishes to when Install is pressed. The push client
    /// subscribes to this, so it has to be readable before anything is published.
    /// </summary>
    public string CommandTopic => $"ha_desktop/{_node}/update/install";

    private string StateTopic => $"ha_desktop/{_node}/update/state";

    private string DiscoveryTopic => $"homeassistant/update/ha_desktop_{_node}/update/config";

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            // Let the dashboard finish signing in; the first publish needs a token.
            await Task.Delay(StartupDelay, token);

            while (!token.IsCancellationRequested)
            {
                await CheckAsync(token);
                await Task.Delay(CheckInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Checks now, whatever the schedule says.</summary>
    public async Task CheckAsync(CancellationToken token)
    {
        if (!_isEnabled())
        {
            return;
        }

        if (!UpdateInstaller.IsInstalledBuild())
        {
            // Said once, so a developer build does not repeat it every six hours.
            if (!_announced)
            {
                _announced = true;
                Log.Info("update", "running a build from outside the install folder; not offering updates");
            }

            return;
        }

        var release = await _checker.GetLatestAsync(token);
        if (release is null)
        {
            return;
        }

        _latest = release;
        var current = ReleaseChecker.Current;

        if (!_announced)
        {
            _announced = true;
            await PublishDiscoveryAsync(token);
        }

        await PublishStateAsync(current, release, inProgress: false, percent: null, token);

        Log.Info("update", release.Version > current
            ? $"{release.Tag} is available (running {current})"
            : $"up to date ({current})");
    }

    private async Task PublishDiscoveryAsync(CancellationToken token)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = "Update",
            ["unique_id"] = $"ha_desktop_{_node}_update",
            ["state_topic"] = StateTopic,
            ["command_topic"] = CommandTopic,

            // Required alongside command_topic. Home Assistant reads it directly when
            // Install is pressed, so leaving it out raises inside the integration while
            // still showing the button.
            ["payload_install"] = "install",
            ["icon"] = "mdi:microsoft-windows",
            ["device"] = new Dictionary<string, object?>
            {
                ["identifiers"] = new[] { $"ha_desktop_{_node}" },
                ["name"] = $"Home Assistant Desktop ({Environment.MachineName})",
                ["manufacturer"] = "Home Assistant Desktop",
                ["model"] = "Windows desktop app",
                ["sw_version"] = ReleaseChecker.Current.ToString(),
            },
        };

        if (await _mqtt.PublishAsync(
            DiscoveryTopic, JsonSerializer.Serialize(config), retain: true, token))
        {
            Log.Info("update", "offering updates through Home Assistant");
        }
    }

    private async Task PublishStateAsync(
        Version installed, ReleaseInfo release, bool inProgress, int? percent, CancellationToken token)
    {
        var notes = release.Notes;
        if (notes.Length > MaxNotesLength)
        {
            notes = notes[..(MaxNotesLength - 3)] + "...";
        }

        var state = new Dictionary<string, object?>
        {
            ["installed_version"] = installed.ToString(),
            ["latest_version"] = release.Version.ToString(),
            ["title"] = "Home Assistant Desktop",
            ["release_url"] = release.Url,
            ["release_summary"] = notes,

            // Always sent, so the spinner clears on its own rather than depending on
            // Home Assistant inferring anything from a missing key.
            ["in_progress"] = inProgress,
            ["update_percentage"] = percent,
        };

        await _mqtt.PublishAsync(StateTopic, JsonSerializer.Serialize(state), retain: true, token);
    }

    /// <summary>Called when something arrives on <see cref="CommandTopic"/>.</summary>
    public void HandleCommand(string payload)
    {
        if (!string.Equals(payload.Trim(), "install", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_installing)
        {
            Log.Info("update", "an install is already running");
            return;
        }

        _installing = true;
        _ = InstallAsync();
    }

    private async Task InstallAsync()
    {
        var token = CancellationToken.None;

        try
        {
            if (_latest is not { } release)
            {
                Log.Warn("update", "asked to install, but nothing has been checked yet");
                return;
            }

            var current = ReleaseChecker.Current;
            if (release.Version <= current)
            {
                Log.Info("update", $"asked to install {release.Tag}, which is not newer than {current}");
                return;
            }

            Log.Info("update", $"installing {release.Tag}");
            await PublishStateAsync(current, release, inProgress: true, percent: 0, token);

            var lastPublished = 0;
            var msi = await _installer.DownloadAsync(
                release,
                percent =>
                {
                    // Every tenth, rather than every byte: each one is a round trip
                    // through Home Assistant and a state write.
                    if (percent - lastPublished < 10 && percent != 100)
                    {
                        return;
                    }

                    lastPublished = percent;
                    _ = PublishStateAsync(current, release, inProgress: true, percent: percent, token);
                },
                token);

            if (msi is null)
            {
                await PublishStateAsync(current, release, inProgress: false, percent: null, token);
                return;
            }

            if (!UpdateInstaller.BeginInstall(msi))
            {
                await PublishStateAsync(current, release, inProgress: false, percent: null, token);
                return;
            }

            // The helper is waiting for this process to go.
            _requestExit();
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"the install failed: {ex.Message}");
        }
        finally
        {
            _installing = false;
        }
    }

    /// <summary>
    /// A machine name as a topic and entity-id fragment. Home Assistant builds the
    /// entity id from the device and entity names rather than from anything here, but
    /// the topic still has to be free of characters MQTT treats specially.
    /// </summary>
    private static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())
            .Trim('_');

        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug.Length > 0 ? slug : "desktop";
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _cts = null;
    }
}
