using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Turns Home Assistant push payloads into Windows notifications, and reports button
/// presses back as the <c>mobile_app_notification_action</c> event.
///
/// That event is the same one the phones raise, which is what lets Ticker's existing
/// action sets work here unchanged - its listener does not care which device answered.
/// </summary>
public sealed class ToastService : IDisposable
{
    private const string ActionArgument = "haAction";
    private const string TagArgument = "haTag";

    /// <summary>
    /// Home Assistant's convention for dismissing an already-delivered notification:
    /// a normal push whose message is this sentinel, carrying the tag to remove.
    /// </summary>
    private const string ClearMessage = "clear_notification";

    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly Func<string> _baseUrlProvider;
    private readonly Action<string> _onNavigate;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private bool _registered;

    public ToastService(
        Func<CancellationToken, Task<string?>> tokenProvider,
        Func<string> baseUrlProvider,
        Action<string> onNavigate)
    {
        _tokenProvider = tokenProvider;
        _baseUrlProvider = baseUrlProvider;
        _onNavigate = onNavigate;
    }

    public bool TryInitialize()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _registered = true;
            Log.Info("toasts", "registered with the Windows notification platform");
            return true;
        }
        catch (Exception ex)
        {
            // Without this the app still works; it just cannot raise toasts.
            Log.Error("toasts", "could not register with the Windows notification platform", ex);
            return false;
        }
    }

    public async Task HandleAsync(JsonElement payload)
    {
        var message = GetString(payload, "message") ?? string.Empty;
        var data = payload.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : default;

        var tag = GetString(data, "tag");

        if (string.Equals(message, ClearMessage, StringComparison.OrdinalIgnoreCase))
        {
            await ClearAsync(tag);
            return;
        }

        var builder = new AppNotificationBuilder()
            .AddText(GetString(payload, "title") ?? "Home Assistant")
            .AddText(message);

        if (!string.IsNullOrEmpty(tag))
        {
            builder.SetTag(tag);
        }

        var imagePath = await TryDownloadImageAsync(GetString(data, "image"));
        if (imagePath is not null)
        {
            builder.SetInlineImage(new Uri(imagePath));
        }

        // Clicking the body rather than a button navigates, matching the phones.
        var navigate = GetString(data, "navigate_to") ?? GetString(data, "clickAction");
        if (!string.IsNullOrEmpty(navigate))
        {
            builder.AddArgument("navigate", navigate);
        }

        AddActionButtons(builder, data, tag);

        var notification = builder.BuildNotification();
        AppNotificationManager.Default.Show(notification);

        // Show() reports nothing when it fails. A non-zero Id is the only confirmation
        // that the platform accepted the toast rather than silently discarding it.
        if (notification.Id == 0)
        {
            Log.Error("toasts", $"the notification platform rejected this toast: {notification.Payload}");
        }
        else
        {
            Log.Info("toasts", $"toast shown (id {notification.Id}, tag: {(string.IsNullOrEmpty(tag) ? "none" : tag)})");
        }
    }

    private static void AddActionButtons(AppNotificationBuilder builder, JsonElement data, string? tag)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Windows renders at most five buttons; Home Assistant allows more.
        foreach (var action in actions.EnumerateArray().Take(5))
        {
            var id = GetString(action, "action");
            var title = GetString(action, "title") ?? id;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            var button = new AppNotificationButton(title)
                .AddArgument(ActionArgument, id);

            if (!string.IsNullOrEmpty(tag))
            {
                button.AddArgument(TagArgument, tag);
            }

            builder.AddButton(button);
        }
    }

    private async Task ClearAsync(string? tag)
    {
        try
        {
            if (string.IsNullOrEmpty(tag))
            {
                await AppNotificationManager.Default.RemoveAllAsync();
            }
            else
            {
                await AppNotificationManager.Default.RemoveByTagAsync(tag);
            }
        }
        catch (Exception)
        {
            // A notification the user already dismissed is not an error.
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;

        if (arguments.TryGetValue("navigate", out var path) && !string.IsNullOrEmpty(path))
        {
            _onNavigate(path);
        }

        if (arguments.TryGetValue(ActionArgument, out var action) && !string.IsNullOrEmpty(action))
        {
            arguments.TryGetValue(TagArgument, out var tag);
            _ = FireActionEventAsync(action, tag);
        }
    }

    /// <summary>
    /// Reports the press as the event the mobile apps raise, so integrations listening
    /// for it - Ticker's action listener among them - handle it without knowing or
    /// caring that the answer came from a desktop.
    /// </summary>
    private async Task FireActionEventAsync(string action, string? tag)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = await _tokenProvider(cts.Token);
            if (string.IsNullOrEmpty(token))
            {
                return;
            }

            var payload = new Dictionary<string, string> { ["action"] = action };
            if (!string.IsNullOrEmpty(tag))
            {
                payload["tag"] = tag;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(_baseUrlProvider()), "/api/events/mobile_app_notification_action"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await _http.SendAsync(request, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // The action is lost, but a failed callback must not take the app down.
        }
    }

    private async Task<string?> TryDownloadImageAsync(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        try
        {
            // Toast images in an unpackaged app have to be local files, and a camera
            // proxy URL needs the bearer token anyway, so fetch it here either way.
            var uri = Uri.TryCreate(image, UriKind.Absolute, out var absolute)
                ? absolute
                : new Uri(new Uri(_baseUrlProvider()), image);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            var token = await _tokenProvider(cts.Token);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var directory = Path.Combine(AppSettings.DataDirectory, "ToastImages");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{Guid.NewGuid():N}{GuessExtension(uri)}");
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(cts.Token), cts.Token);

            PruneImages(directory);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            // A toast without its picture is still worth showing.
            return null;
        }
    }

    private static string GuessExtension(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrEmpty(extension) ? ".jpg" : extension;
    }

    private static void PruneImages(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(40);

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_registered)
        {
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception)
            {
                // Shutting down regardless.
            }

            _registered = false;
        }

        _http.Dispose();
    }
}
