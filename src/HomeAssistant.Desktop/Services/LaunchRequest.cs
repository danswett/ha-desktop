namespace HomeAssistant.Desktop.Services;

/// <summary>
/// What a launch was asked to do. Jump list entries re-run the executable with
/// arguments, and because the app is single instance those arguments arrive at the
/// copy that is already running rather than starting another.
/// </summary>
public sealed record LaunchRequest(LaunchRequestKind Kind, string Target, string? Data = null)
{
    public static LaunchRequest None { get; } = new(LaunchRequestKind.None, string.Empty);

    /// <summary>
    /// Reads a request out of a command line.
    ///
    /// Activation hands over the whole line including the executable, which may itself
    /// be quoted and contain spaces, so this scans for the switches rather than
    /// assuming a position.
    /// </summary>
    public static LaunchRequest Parse(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var bare = argument.Trim('"');
            if (bare.StartsWith("homeassistant:", StringComparison.OrdinalIgnoreCase))
            {
                return ParseUrl(bare);
            }
        }

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            var kind = arguments[i].ToLowerInvariant() switch
            {
                "--open" => LaunchRequestKind.OpenPage,
                "--entity" => LaunchRequestKind.ShowEntity,
                "--perform" => LaunchRequestKind.PerformOnEntity,
                _ => LaunchRequestKind.None,
            };

            if (kind != LaunchRequestKind.None)
            {
                var target = arguments[i + 1].Trim('"');
                if (!string.IsNullOrWhiteSpace(target))
                {
                    return new LaunchRequest(kind, target);
                }
            }
        }

        return None;
    }

    /// <summary>
    /// Reads a homeassistant:// link, using the spellings the official companion apps
    /// use so that a link written for a phone works here unchanged.
    ///
    /// Three of them are understood. <c>navigate/&lt;path&gt;</c> opens a page, with no
    /// leading slash after the host, which is how both apps build them. A root navigate
    /// carrying <c>?more-info-entity-id=</c> opens that entity's dialog - there is no
    /// more-info host, despite how often one is assumed. And
    /// <c>call_service/&lt;domain&gt;.&lt;service&gt;</c> runs a service, taking its data
    /// from the query string, with the domain and service joined by a dot in a single
    /// segment rather than split across two.
    ///
    /// Everything else is ignored rather than guessed at, which matters most for
    /// <c>auth-callback</c>: Home Assistant's server reserves it as the companion apps'
    /// OAuth redirect, so a link arriving here would be carrying someone else's
    /// authorisation code.
    /// </summary>
    public static LaunchRequest ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("homeassistant", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        // Authority rather than Host: the latter lower-cases and, for a host that is not
        // a valid name, can come back empty.
        var host = uri.Authority.Length > 0 ? uri.Authority : uri.Segments.FirstOrDefault()?.Trim('/') ?? string.Empty;
        var path = uri.AbsolutePath.Trim('/');

        switch (host.ToLowerInvariant())
        {
            case "navigate":
                if (query["more-info-entity-id"] is { Length: > 0 } entity && path.Length == 0)
                {
                    return new LaunchRequest(LaunchRequestKind.ShowEntity, entity);
                }

                return path.Length > 0
                    ? new LaunchRequest(LaunchRequestKind.OpenPage, "/" + path)
                    : None;

            case "call_service":
                // Exactly two non-empty parts, as the companion apps require; anything
                // else is not a service and is better refused than half-run.
                var parts = path.Split('.');
                return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
                    ? new LaunchRequest(LaunchRequestKind.CallService, path, uri.Query.TrimStart('?'))
                    : None;

            default:
                return None;
        }
    }

    /// <summary>
    /// Splits a raw command line the way the shell would. Activation arguments arrive
    /// as one string, not the argv the process was started with.
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in commandLine)
        {
            switch (c)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ' ' when !quoted:
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}

public enum LaunchRequestKind
{
    None,

    /// <summary>A path within Home Assistant.</summary>
    OpenPage,

    /// <summary>One entity, shown in its own dialog.</summary>
    ShowEntity,

    /// <summary>One entity, acted on without showing anything.</summary>
    PerformOnEntity,

    /// <summary>A service, named domain.service, with its data in the query string.</summary>
    CallService,
}
