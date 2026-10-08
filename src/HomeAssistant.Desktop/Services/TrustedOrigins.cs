namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Decides which web origins this app will treat as Home Assistant.
///
/// The app hands out real Home Assistant access tokens - to the page through the
/// companion app bridge, and on the wire when it fetches a notification's picture - so
/// "is this still Home Assistant?" is a security question rather than a tidiness one.
/// Two things made it one worth asking:
///
/// A notification carries the address its picture comes from. That address was used as
/// given, with the bearer token attached, so a notification could name any host and be
/// sent a live token without the user touching anything.
///
/// A notification also carries the page to open when it is clicked, and that was
/// resolved with <c>Uri.TryCreate(base, target)</c>, which returns the target whenever
/// the target is absolute. A notification could therefore move the dashboard window to
/// any site, and the bridge - injected into every document regardless of origin - would
/// then answer that site's request for a token.
///
/// Origin here means what the web means by it: scheme, host and port, all three.
/// </summary>
public static class TrustedOrigins
{
    public static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    /// <summary>
    /// Whether an absolute address is one of the configured Home Assistant addresses.
    /// A machine that moves between networks has two, and both are equally itself.
    /// </summary>
    public static bool IsTrusted(string? candidate, IEnumerable<string?> trustedBases)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var trusted in trustedBases)
        {
            if (Uri.TryCreate(trusted, UriKind.Absolute, out var baseUri) && SameOrigin(uri, baseUri))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves somewhere a notification asked to open, and returns null if that turns
    /// out to be off Home Assistant.
    ///
    /// Note that this has to resolve before it judges. <c>/lovelace/0</c> is a path,
    /// <c>https://example.test/</c> replaces the base entirely, and <c>//example.test/</c>
    /// keeps the scheme and replaces the host - all three reach this as a string, and
    /// only the resolved address says which happened.
    /// </summary>
    public static Uri? ResolveWithin(string? target, string? baseUrl, IEnumerable<string?> trustedBases)
    {
        if (string.IsNullOrWhiteSpace(target)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, target, out var resolved))
        {
            return null;
        }

        return IsTrusted(resolved.ToString(), trustedBases) ? resolved : null;
    }

    /// <summary>
    /// Whether any usable address is configured at all. Nothing is trusted before the
    /// app knows where Home Assistant lives, and refusing every navigation at that point
    /// would stop it ever loading one.
    /// </summary>
    public static bool AnyConfigured(IEnumerable<string?> trustedBases) =>
        trustedBases.Any(b => Uri.TryCreate(b, UriKind.Absolute, out _));
}
