using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Publishes MQTT messages by asking Home Assistant to do it.
///
/// Going through the <c>mqtt.publish</c> service rather than connecting to the broker
/// means there is no second credential to obtain, store or rotate: the access token the
/// app already holds is enough, and the app keeps working if the broker moves or its
/// password changes. Publishing, unlike subscribing, needs no administrator rights.
/// </summary>
public sealed class HaMqtt
{
    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly Func<string> _baseUrlProvider;

    public HaMqtt(
        Func<CancellationToken, Task<string?>> tokenProvider,
        Func<string> baseUrlProvider)
    {
        _tokenProvider = tokenProvider;
        _baseUrlProvider = baseUrlProvider;
    }

    public async Task<bool> PublishAsync(
        string topic, string payload, bool retain, CancellationToken token)
    {
        try
        {
            var baseUrl = _baseUrlProvider();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return false;
            }

            if (await _tokenProvider(token) is not { } accessToken)
            {
                return false;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var body = JsonSerializer.Serialize(new
            {
                topic,
                payload,
                retain,
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(
                new Uri(new Uri(baseUrl), "/api/services/mqtt/publish"), content, token);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("update", $"could not publish to {topic} ({(int)response.StatusCode})");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Log.Warn("update", $"could not publish to {topic}: {ex.Message}");
            return false;
        }
    }
}
