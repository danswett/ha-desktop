using System.Net.Http;
using System.Net.NetworkInformation;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Chooses which Home Assistant address to use, and notices when that answer changes.
///
/// A desktop has one address and never moves. A laptop has two and moves constantly:
/// on the home network the direct LAN address works and is the right one to use, and
/// away from it only the external address resolves. Picking once at startup strands the
/// machine every time it changes network.
///
/// The internal address is always preferred when it answers. That is not only about
/// latency: reaching Home Assistant through a public tunnel makes it attribute every
/// client to a single WAN address, which is what trips its own IP ban.
/// </summary>
public sealed class HaEndpoints : IDisposable
{
    /// <summary>
    /// Long enough for a busy instance on a slow link, short enough that a laptop on
    /// the wrong network is not left staring at a blank window.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Network changes arrive in bursts - an adapter coming up, DHCP settling, a VPN
    /// attaching - and probing on each one would mean several useless probes per move.
    /// </summary>
    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(3);

    private readonly Func<(string Internal, string External)> _configured;
    private readonly HttpClient _http = new() { Timeout = ProbeTimeout };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private System.Threading.Timer? _settleTimer;
    private string _current = string.Empty;
    private bool _disposed;

    /// <summary>Raised when the address in use changes. Not raised on the UI thread.</summary>
    public event Action<string>? Changed;

    public HaEndpoints(Func<(string Internal, string External)> configured)
    {
        _configured = configured;

        var (internalUrl, externalUrl) = configured();
        _current = Pick(internalUrl, externalUrl);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    /// <summary>The address everything should currently be talking to.</summary>
    public string Current => _current;

    /// <summary>
    /// Re-evaluates which address to use. Safe to call often; only raises
    /// <see cref="Changed"/> when the answer actually differs.
    /// </summary>
    public async Task<string> RefreshAsync(CancellationToken token = default)
    {
        if (_disposed)
        {
            return _current;
        }

        await _gate.WaitAsync(token);
        try
        {
            var (internalUrl, externalUrl) = _configured();

            string chosen;
            if (string.IsNullOrWhiteSpace(externalUrl))
            {
                // Nothing to choose between, so do not spend a probe deciding.
                chosen = Pick(internalUrl, externalUrl);
            }
            else if (!string.IsNullOrWhiteSpace(internalUrl) && await IsHomeAssistantAsync(internalUrl, token))
            {
                chosen = internalUrl;
            }
            else
            {
                chosen = externalUrl;
            }

            chosen = Normalize(chosen);

            if (string.Equals(chosen, _current, StringComparison.OrdinalIgnoreCase))
            {
                return _current;
            }

            Log.Info("endpoint", $"switching from {Describe(_current)} to {Describe(chosen)}");
            _current = chosen;
            Changed?.Invoke(chosen);
            return chosen;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Confirms that something at this address really is Home Assistant.
    ///
    /// /auth/providers needs no credentials and answers with a recognisable shape, so a
    /// captive portal or a router's "page not found" cannot be mistaken for a working
    /// instance - which a bare connection test would happily do.
    /// </summary>
    private async Task<bool> IsHomeAssistantAsync(string baseUrl, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);

            var uri = new Uri(new Uri(Normalize(baseUrl) + "/"), "auth/providers");
            using var response = await _http.GetAsync(uri, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return body.Contains("\"providers\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or OperationCanceledException)
        {
            return false;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _settleTimer?.Dispose();
        _settleTimer = new System.Threading.Timer(
            _ => _ = RefreshAsync(), null, NetworkSettleDelay, Timeout.InfiniteTimeSpan);
    }

    private static string Pick(string internalUrl, string externalUrl) =>
        Normalize(!string.IsNullOrWhiteSpace(internalUrl) ? internalUrl : externalUrl);

    private static string Normalize(string url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private static string Describe(string url) => string.IsNullOrEmpty(url) ? "nothing" : url;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _settleTimer?.Dispose();
        _settleTimer = null;
        _http.Dispose();
        _gate.Dispose();
    }
}
