namespace HomeAssistant.Desktop.Services;

/// <summary>One entry of a browser context menu, as far as the policy cares.</summary>
public readonly record struct ContextMenuEntry(string Name, bool IsSeparator);

/// <summary>What to do with the context menu Chromium offered.</summary>
public enum ContextMenuOutcome
{
    /// <summary>Show no menu at all, the way a native app does on ordinary content.</summary>
    Suppress,

    /// <summary>Show the entries the policy kept.</summary>
    ShowFiltered,

    /// <summary>
    /// Show Chromium's menu untouched. Used when the filter would leave an editable
    /// field with no editing commands at all, which can only mean the names Chromium
    /// uses have moved on from the ones listed here.
    /// </summary>
    ShowEverything,
}

/// <summary>
/// Decides which of Chromium's context menu entries a native app should show.
///
/// The app cannot simply switch context menus off: doing that also takes away Cut,
/// Copy and Paste in Home Assistant's text fields, which every Windows app is expected
/// to offer. So the browser menu stays on and is filtered down to the commands that
/// belong in a desktop app, dropping the ones that only make sense in a browser -
/// "Open link in new tab", "Save page as", "Translate", and the rest.
/// </summary>
public static class ContextMenuPolicy
{
    // Chromium's own identifiers, which are unlocalized and lower camel case. Anything
    // not named here is browser furniture and is dropped.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "undo",
        "redo",
        "cut",
        "copy",
        "paste",
        "pasteAsPlainText",
        "delete",
        "selectAll",

        // Home Assistant dashboards are full of camera snapshots and graphs, so these
        // two earn their place; "Copy image link" and "Open image in new tab" do not.
        "copyImage",
        "saveImageAs",
    };

    public static bool IsAllowed(string name) => Allowed.Contains(name);

    /// <summary>
    /// What to show, and - for <see cref="ContextMenuOutcome.ShowFiltered"/> - the
    /// indexes of the entries worth showing, in order.
    ///
    /// An editable field that ends up with nothing is treated as this policy being out
    /// of date rather than as a menu worth suppressing: Chromium has renamed these
    /// items before, and a text box with no Cut, Copy or Paste is a worse outcome than
    /// a text box carrying a few browser commands.
    /// </summary>
    public static (ContextMenuOutcome Outcome, IReadOnlyList<int> Keep) Decide(
        IReadOnlyList<ContextMenuEntry> items, bool isEditable)
    {
        var keep = Keep(items);

        if (keep.Count > 0)
        {
            return (ContextMenuOutcome.ShowFiltered, keep);
        }

        return isEditable && items.Count > 0
            ? (ContextMenuOutcome.ShowEverything, keep)
            : (ContextMenuOutcome.Suppress, keep);
    }

    /// <summary>
    /// The indexes of the entries worth showing, in order. Separators that would be
    /// left stranded by the filtering - leading, trailing, or doubled up - are dropped
    /// too, so the result never has a stray dividing line in it.
    /// </summary>
    public static IReadOnlyList<int> Keep(IReadOnlyList<ContextMenuEntry> items)
    {
        var kept = new List<int>();
        var pendingSeparator = -1;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            if (item.IsSeparator)
            {
                // Only worth keeping if a real command follows it, which is not known
                // yet - so hold on to it and decide when one turns up.
                if (kept.Count > 0)
                {
                    pendingSeparator = i;
                }

                continue;
            }

            if (!IsAllowed(item.Name))
            {
                continue;
            }

            if (pendingSeparator >= 0)
            {
                kept.Add(pendingSeparator);
                pendingSeparator = -1;
            }

            kept.Add(i);
        }

        return kept;
    }
}
