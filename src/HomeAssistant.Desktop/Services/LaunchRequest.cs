namespace HomeAssistant.Desktop.Services;

/// <summary>
/// What a launch was asked to do. Jump list entries re-run the executable with
/// arguments, and because the app is single instance those arguments arrive at the
/// copy that is already running rather than starting another.
/// </summary>
public sealed record LaunchRequest(LaunchRequestKind Kind, string Target)
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
}
