using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>What a published release says about itself.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string Url,
    string? InstallerUrl,
    long InstallerSize);

/// <summary>
/// Asks GitHub what the newest release is.
///
/// The token is optional. The repository is public, so checks work with no credential
/// at all; supplying one only raises the rate limit and would let the same code serve a
/// private repository, which is what it was built against.
/// </summary>
public sealed class ReleaseChecker
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly Func<string?> _tokenProvider;

    public ReleaseChecker(string owner, string repo, Func<string?> tokenProvider)
    {
        _owner = owner;
        _repo = repo;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// The running build's version, as three parts. The file version carries a fourth
    /// that is always zero, and comparing it against a three-part tag would make every
    /// release look older than itself.
    /// </summary>
    public static Version Current
    {
        get
        {
            var raw = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);
            return new Version(raw.Major, raw.Minor, raw.Build);
        }
    }

    /// <summary>
    /// Reads a version out of a release tag. Tags here are <c>v1.2.0</c>, but the
    /// leading v is a convention rather than a guarantee, so it is optional.
    /// </summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // GitHub answers 403 to a request with no user agent.
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HomeAssistantDesktop", Current.ToString()));
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (_tokenProvider() is { Length: > 0 } credential)
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", credential);
            }

            // /releases/latest skips drafts and pre-releases, which is what makes it
            // safe to offer whatever this returns without further filtering.
            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest", token);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("update", response.StatusCode == HttpStatusCode.NotFound
                    ? "no published release yet"
                    : $"the release check failed ({(int)response.StatusCode})");
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            return Parse(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn("update", $"could not reach GitHub: {ex.Message}");
            return null;
        }
    }

    /// <summary>Separate from the fetch so it can be tested without a network.</summary>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTag(tag, out var version))
        {
            return null;
        }

        string? installerUrl = null;
        long installerSize = 0;

        if (release.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                installerUrl = asset.TryGetProperty("browser_download_url", out var u)
                    ? u.GetString() : null;
                installerSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        }

        var title = release.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        var notes = release.TryGetProperty("body", out var b) ? b.GetString() : null;
        var url = release.TryGetProperty("html_url", out var h) ? h.GetString() : null;

        return new ReleaseInfo(
            version,
            tag!,
            string.IsNullOrWhiteSpace(title) ? tag! : title!,
            notes ?? string.Empty,
            url ?? string.Empty,
            installerUrl,
            installerSize);
    }
}
