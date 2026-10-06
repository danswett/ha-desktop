using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// OAuth2 against Home Assistant, which implements IndieAuth.
///
/// The app holds a refresh token of its own rather than borrowing the dashboard's
/// session. That matters for setup, which has to be able to register this machine
/// before anyone has signed into a dashboard, and for longevity: a borrowed token
/// disappears the moment the user signs out of the page.
///
/// No socket is ever opened. Home Assistant accepts a redirect URI that shares a scheme
/// and host with the client id without fetching either (auth/indieauth.py,
/// verify_redirect_uri), and permits loopback hosts, so the redirect can point at a
/// localhost URL that nothing serves. The WebView cancels the navigation and reads the
/// code straight off the URL - see <see cref="SignInWindow"/>.
/// </summary>
public sealed class HaAuth
{
    /// <summary>
    /// Any http(s) URL is a valid IndieAuth client id. This one is never fetched,
    /// because the redirect below shares its scheme and host.
    /// </summary>
    public const string ClientId = "http://localhost/ha-desktop";

    public const string RedirectUri = "http://localhost/ha-desktop/callback";

    private const string RefreshTokenKey = "ha.refresh_token";

    private readonly ProtectedStore _secrets;
    private readonly Func<string> _baseUrlProvider;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public HaAuth(ProtectedStore secrets, Func<string> baseUrlProvider)
    {
        _secrets = secrets;
        _baseUrlProvider = baseUrlProvider;
    }

    /// <summary>Whether the app has its own credentials, as opposed to borrowing the page's.</summary>
    public bool IsSignedIn => !string.IsNullOrEmpty(_secrets.Get(RefreshTokenKey));

    /// <summary>
    /// Builds the URL to put in front of the user, and the PKCE verifier that must be
    /// presented when redeeming the resulting code.
    /// </summary>
    public static (Uri Authorize, string CodeVerifier, string State) BeginAuthorization(string baseUrl)
    {
        var verifier = CreateCodeVerifier();
        var state = CreateCodeVerifier();

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = ClientId;
        query["redirect_uri"] = RedirectUri;
        query["state"] = state;
        // PKCE is sent opportunistically. Home Assistant only demands the verifier when
        // a challenge was recorded during login (auth/__init__.py,
        // _async_handle_auth_code), and older versions do not support it at all - 2026.9.4
        // rejects code_challenge on /auth/login_flow outright, so its frontend never
        // forwards these. Verified against that version: /auth/token accepts an
        // unexpected code_verifier and answers about the code instead, so sending both
        // is safe whether or not the instance understands them.
        query["code_challenge"] = CreateCodeChallenge(verifier);
        query["code_challenge_method"] = "S256";

        var authorize = new Uri(new Uri(Normalize(baseUrl)), "/auth/authorize?" + query);
        return (authorize, verifier, state);
    }

    /// <summary>Redeems an authorization code and keeps the refresh token.</summary>
    public async Task SignInAsync(string baseUrl, string code, string codeVerifier, CancellationToken token)
    {
        var response = await PostTokenAsync(baseUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
        }, token);

        var refreshToken = response.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException("Home Assistant returned no refresh token.");

        _secrets.Set(RefreshTokenKey, refreshToken);
        CacheAccessToken(response);

        Log.Info("auth", "signed in; refresh token stored");
    }

    /// <summary>
    /// Returns a usable access token, refreshing when the cached one is close to
    /// expiry. Returns null when the app has no credentials of its own.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken token)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry)
        {
            return _accessToken;
        }

        var refreshToken = _secrets.Get(RefreshTokenKey);
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        await _refreshGate.WaitAsync(token);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry)
            {
                return _accessToken;
            }

            var response = await PostTokenAsync(_baseUrlProvider(), new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
            }, token);

            CacheAccessToken(response);
            return _accessToken;
        }
        catch (HaAuthException ex) when (ex.IsInvalidGrant)
        {
            // The refresh token was revoked, most likely from Home Assistant's own
            // security page. Forget it so the app asks for a new one rather than
            // retrying something that will never work again.
            Log.Warn("auth", "the refresh token was rejected; signing out");
            SignOutLocally();
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// The access token together with the seconds it has left. Home Assistant's
    /// external auth bridge asks for both, because the frontend schedules its own
    /// refresh from the lifetime rather than waiting to be rejected.
    /// </summary>
    /// <param name="force">
    /// Set when the frontend has already been told its token is invalid, in which
    /// case the cached one must not be handed back.
    /// </param>
    public async Task<(string Token, int ExpiresIn)?> GetAccessTokenWithLifetimeAsync(
        bool force, CancellationToken token)
    {
        if (force)
        {
            _accessToken = null;
            _accessTokenExpiry = DateTimeOffset.MinValue;
        }

        if (await GetAccessTokenAsync(token) is not { } value)
        {
            return null;
        }

        var remaining = (int)Math.Max(30, (_accessTokenExpiry - DateTimeOffset.UtcNow).TotalSeconds);
        return (value, remaining);
    }

    /// <summary>Revokes the refresh token at the server, then forgets it.</summary>
    public async Task SignOutAsync(CancellationToken token)
    {
        var refreshToken = _secrets.Get(RefreshTokenKey);
        SignOutLocally();

        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        try
        {
            var uri = new Uri(new Uri(Normalize(_baseUrlProvider())), "/auth/revoke");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken,
            });

            using var response = await _http.PostAsync(uri, content, token);
            Log.Info("auth", $"revoked the refresh token ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The local copy is already gone, which is the part that matters here.
            Log.Warn("auth", $"could not reach Home Assistant to revoke the token: {ex.Message}");
        }
    }

    private void SignOutLocally()
    {
        _secrets.Set(RefreshTokenKey, null);
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
    }

    private void CacheAccessToken(JsonElement response)
    {
        _accessToken = response.GetProperty("access_token").GetString();

        var lifetime = response.TryGetProperty("expires_in", out var expires)
            ? TimeSpan.FromSeconds(expires.GetDouble())
            : TimeSpan.FromMinutes(30);

        // Renew a minute early: a token that expires mid-request is a failure the
        // caller cannot distinguish from a real authentication problem.
        _accessTokenExpiry = DateTimeOffset.UtcNow + lifetime - TimeSpan.FromMinutes(1);
    }

    private async Task<JsonElement> PostTokenAsync(
        string baseUrl, Dictionary<string, string> form, CancellationToken token)
    {
        var uri = new Uri(new Uri(Normalize(baseUrl)), "/auth/token");

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(uri, content, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            string? description = null;
            try
            {
                var problem = JsonDocument.Parse(body).RootElement;
                error = problem.TryGetProperty("error", out var e) ? e.GetString() : null;
                description = problem.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            }
            catch (JsonException)
            {
                // Non-JSON error body; the status code is all there is to report.
            }

            throw new HaAuthException(
                $"Home Assistant rejected the token request ({(int)response.StatusCode}): {description ?? error ?? "no reason given"}",
                error);
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string Normalize(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8123/" : baseUrl.TrimEnd('/') + "/";

    private static string CreateCodeVerifier()
    {
        // RFC 7636: 43-128 unreserved characters. 32 bytes of base64url is 43.
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class HaAuthException(string message, string? error = null) : Exception(message)
{
    public string? Error { get; } = error;

    /// <summary>A credential that will never work again, as opposed to a transient failure.</summary>
    public bool IsInvalidGrant => Error is "invalid_grant";
}
