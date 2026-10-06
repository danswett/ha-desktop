using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Receives Home Assistant push notifications over the mobile_app websocket channel.
///
/// Registration asked for <c>push_websocket_channel</c> rather than <c>push_url</c>.
/// Home Assistant supports either (mobile_app/util.py, supports_push), but the websocket
/// form means notifications arrive on a connection this app opens outbound: no listening
/// port on the desktop, no inbound firewall rule, and nothing that breaks when DHCP moves
/// the machine.
///
/// The access token is not stored anywhere. It is read from the signed-in dashboard each
/// time a connection is made, so there is no separate credential to manage or leak, and
/// access dies with the Home Assistant session like any other login.
/// </summary>
public sealed class HaPushClient : IAsyncDisposable
{
    private static readonly int[] RetryDelaysSeconds = [2, 5, 10, 20, 30, 60];

    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly Func<string> _baseUrlProvider;
    private readonly Func<string?> _webhookIdProvider;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _retryWake;
    private Task? _loop;

    /// <summary>Raised with the notification payload Home Assistant pushed.</summary>
    public event Action<JsonElement>? NotificationReceived;

    /// <summary>Raised when the connection state changes, for surfacing in the UI.</summary>
    public event Action<bool, string?>? ConnectionChanged;

    /// <summary>
    /// An MQTT topic to watch alongside the push channel, or null for none.
    ///
    /// It shares this connection rather than opening its own because the subscription
    /// has to be re-made every time the socket comes back, and that reconnect handling
    /// already lives here. Read fresh on each connect so it survives being set later.
    /// </summary>
    public Func<string?>? MqttTopicProvider { get; set; }

    /// <summary>Raised with the payload of a message on <see cref="MqttTopicProvider"/>.</summary>
    public event Action<string>? MqttMessageReceived;

    public HaPushClient(
        Func<CancellationToken, Task<string?>> tokenProvider,
        Func<string> baseUrlProvider,
        Func<string?> webhookIdProvider)
    {
        _tokenProvider = tokenProvider;
        _baseUrlProvider = baseUrlProvider;
        _webhookIdProvider = webhookIdProvider;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Cuts short whatever backoff the client is in. Called when the credentials
    /// change, since the reason for the last failure may have just been fixed and
    /// waiting out a minute of backoff would be waiting for nothing.
    /// </summary>
    public void Reconnect()
    {
        try
        {
            _retryWake?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop moved on between the read and the cancel, which means it is
            // already doing what the wake would have asked for.
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(token);
                attempt = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false, ex.Message);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var delay = RetryDelaysSeconds[Math.Min(attempt, RetryDelaysSeconds.Length - 1)];
            attempt++;

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            _retryWake = wake;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), wake.Token);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Woken on purpose: the credentials changed, so the reason for the last
                // failure may be gone. Start the backoff over rather than carrying on
                // from a long delay earned under the old ones.
                attempt = 0;
            }
            finally
            {
                _retryWake = null;
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken token)
    {
        var webhookId = _webhookIdProvider();
        if (string.IsNullOrWhiteSpace(webhookId))
        {
            throw new InvalidOperationException(
                "This machine is not registered with Home Assistant. Run tools/Register-TickerTarget.ps1.");
        }

        // Read fresh every time: the dashboard's access token is short-lived and the
        // frontend rotates it, so a cached copy would work until it silently did not.
        var accessToken = await _tokenProvider(token)
            ?? throw new InvalidOperationException("The dashboard is not signed in yet.");

        var baseUrl = _baseUrlProvider();
        var wsUri = new Uri(new Uri(baseUrl), "/api/websocket");
        wsUri = new UriBuilder(wsUri) { Scheme = wsUri.Scheme == "https" ? "wss" : "ws" }.Uri;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(wsUri, token);

        var auth = await ReceiveJsonAsync(socket, token);
        if (auth.GetProperty("type").GetString() != "auth_required")
        {
            throw new InvalidOperationException("Unexpected greeting from Home Assistant.");
        }

        await SendJsonAsync(socket, new { type = "auth", access_token = accessToken }, token);

        var authResult = await ReceiveJsonAsync(socket, token);
        if (authResult.GetProperty("type").GetString() != "auth_ok")
        {
            throw new InvalidOperationException("Home Assistant rejected the dashboard's token.");
        }

        // Home Assistant requires every message id on a connection to be strictly
        // greater than the last, and silently drops any that is not. Reusing an id here
        // is not a harmless protocol foul: an unconfirmed notification makes Home
        // Assistant assume the device is unreachable, tear the channel down after
        // PUSH_CONFIRM_TIMEOUT, and fail every later send.
        var nextMessageId = 1;

        var subscriptionId = nextMessageId++;
        await SendJsonAsync(socket, new
        {
            id = subscriptionId,
            type = "mobile_app/push_notification_channel",
            webhook_id = webhookId,
            support_confirm = true,
        }, token);

        var subscribed = await ReceiveJsonAsync(socket, token);
        if (!subscribed.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var message = subscribed.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var text)
                    ? text.GetString()
                    : "unknown error";
            throw new InvalidOperationException($"Could not open the push channel: {message}");
        }

        ConnectionChanged?.Invoke(true, null);

        // Sent now, but its reply is handled in the loop below: push events can arrive
        // between the request and the answer, so this cannot assume the next message is
        // the one it asked for.
        var mqttSubscriptionId = 0;
        if (MqttTopicProvider?.Invoke() is { Length: > 0 } mqttTopic)
        {
            mqttSubscriptionId = nextMessageId++;
            await SendJsonAsync(socket, new
            {
                id = mqttSubscriptionId,
                type = "mqtt/subscribe",
                topic = mqttTopic,
            }, token);
        }

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveJsonAsync(socket, token);

            var messageId = message.TryGetProperty("id", out var idValue)
                && idValue.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;

            var messageType = message.TryGetProperty("type", out var type)
                ? type.GetString()
                : null;

            if (mqttSubscriptionId != 0 && messageId == mqttSubscriptionId)
            {
                if (messageType == "result")
                {
                    var ok = message.TryGetProperty("success", out var mqttOk) && mqttOk.GetBoolean();
                    if (ok)
                    {
                        Log.Info("update", "listening for the Install button");
                    }
                    else
                    {
                        // Home Assistant allows only administrators to subscribe. The
                        // update entity still works without this; its Install button is
                        // what stops doing anything, so say so rather than failing the
                        // whole connection over it.
                        Log.Warn("update",
                            "cannot watch for the Install button; the signed-in account is probably not an administrator");
                        mqttSubscriptionId = 0;
                    }

                    continue;
                }

                if (messageType == "event"
                    && message.TryGetProperty("event", out var mqttEvent)
                    && mqttEvent.TryGetProperty("payload", out var mqttPayload))
                {
                    MqttMessageReceived?.Invoke(mqttPayload.GetString() ?? string.Empty);
                    continue;
                }
            }

            if (messageType == "event"
                && messageId == subscriptionId
                && message.TryGetProperty("event", out var payload))
            {
                NotificationReceived?.Invoke(payload.Clone());
                Log.Info("push", "notification received");

                // Confirming tells Home Assistant the notification was delivered. It is
                // not optional bookkeeping: without it the channel is torn down.
                if (payload.TryGetProperty("hass_confirm_id", out var confirmId))
                {
                    await SendJsonAsync(socket, new
                    {
                        id = nextMessageId++,
                        type = "mobile_app/push_notification_confirm",
                        webhook_id = webhookId,
                        confirm_id = confirmId.GetString(),
                    }, token);
                }
            }
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        var builder = new StringBuilder();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Home Assistant closed the push channel.");
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(builder.ToString());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
