namespace HomeAssistant.Desktop.Services;

/// <summary>What a jump list entry points at.</summary>
public enum JumpTargetKind
{
    /// <summary>A path within Home Assistant, such as a dashboard or a view.</summary>
    Page,

    /// <summary>A single entity.</summary>
    Entity,
}

/// <summary>What happens when the entry is chosen.</summary>
public enum JumpTargetAction
{
    /// <summary>Bring the window up showing the target.</summary>
    Open,

    /// <summary>Act on the entity without disturbing the window.</summary>
    Perform,
}

/// <summary>
/// One configured jump list or thumbnail toolbar entry.
///
/// Kept apart from <see cref="JumpList"/> itself because this is settings data - it is
/// serialised into settings.json and edited in the settings dialog - while that is COM
/// interop against the Windows shell. Separating them lets the data model be used, and
/// tested, without dragging the shell in behind it.
/// </summary>
public sealed class JumpListSlot
{
    public string Title { get; set; } = string.Empty;

    public JumpTargetKind Kind { get; set; } = JumpTargetKind.Page;

    /// <summary>A path like <c>/lovelace/kitchen</c>, or an entity id.</summary>
    public string Target { get; set; } = string.Empty;

    public JumpTargetAction Action { get; set; } = JumpTargetAction.Open;

    /// <summary>
    /// Which icon to draw, for the thumbnail toolbar. Ignored by the jump list, which
    /// shows text and takes its icon from the executable.
    /// </summary>
    public string Glyph { get; set; } = string.Empty;

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Target);

    /// <summary>The command line this slot launches the app with.</summary>
    public string ToArguments() => Kind switch
    {
        JumpTargetKind.Entity when Action == JumpTargetAction.Perform =>
            $"--perform \"{Target}\"",
        JumpTargetKind.Entity => $"--entity \"{Target}\"",
        _ => $"--open \"{Target}\"",
    };
}
