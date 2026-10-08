using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

public class ContextMenuPolicyTests
{
    private static ContextMenuEntry Item(string name) => new(name, false);

    private static ContextMenuEntry Separator() => new(string.Empty, true);

    private static string[] Kept(params ContextMenuEntry[] entries) =>
        ContextMenuPolicy.Keep(entries)
            .Select(i => entries[i].IsSeparator ? "---" : entries[i].Name)
            .ToArray();

    [Fact]
    public void KeepsTheEditingCommandsOnATextField()
    {
        var kept = Kept(
            Item("undo"), Item("redo"), Separator(),
            Item("cut"), Item("copy"), Item("paste"), Item("pasteAsPlainText"),
            Separator(), Item("selectAll"));

        Assert.Equal(
            ["undo", "redo", "---", "cut", "copy", "paste", "pasteAsPlainText", "---", "selectAll"],
            kept);
    }

    [Fact]
    public void DropsTheBrowserOnlyCommands()
    {
        var kept = Kept(
            Item("back"), Item("forward"), Item("reload"), Separator(),
            Item("saveAs"), Item("print"), Item("translate"),
            Item("viewSource"), Item("inspect"),
            Item("openLinkInNewTab"), Item("copyLinkAddress"),
            Item("share"), Item("webCapture"), Item("emoji"));

        Assert.Empty(kept);
    }

    [Fact]
    public void KeepsDeleteOnATextField()
    {
        // Windows' own edit menu is Undo | Cut Copy Paste Delete | Select All.
        var kept = Kept(Item("cut"), Item("copy"), Item("paste"), Item("delete"));

        Assert.Equal(["cut", "copy", "paste", "delete"], kept);
    }

    [Fact]
    public void ShowsTheWholeBrowserMenuRatherThanStripATextFieldBare()
    {
        // What a Chromium rename looks like from here. Showing a text box with no Cut,
        // Copy or Paste at all would be worse than showing a few browser commands.
        var entries = new[]
        {
            Item("snip"), Item("duplicate"), Item("chooseAll"),
        };

        var (outcome, _) = ContextMenuPolicy.Decide(entries, isEditable: true);

        Assert.Equal(ContextMenuOutcome.ShowEverything, outcome);
    }

    [Fact]
    public void StillSuppressesOrdinaryContentThatMatchesNothing()
    {
        var entries = new[] { Item("back"), Item("reload"), Item("translate") };

        var (outcome, _) = ContextMenuPolicy.Decide(entries, isEditable: false);

        Assert.Equal(ContextMenuOutcome.Suppress, outcome);
    }

    [Fact]
    public void FiltersWhenTheTextFieldCommandsAreRecognised()
    {
        var entries = new[]
        {
            Item("cut"), Item("copy"), Item("paste"), Separator(), Item("translate"),
        };

        var (outcome, keep) = ContextMenuPolicy.Decide(entries, isEditable: true);

        Assert.Equal(ContextMenuOutcome.ShowFiltered, outcome);
        Assert.Equal([0, 1, 2], keep);
    }

    [Fact]
    public void SuppressesAnEmptyMenuEvenOnAnEditableField()
    {
        var (outcome, _) = ContextMenuPolicy.Decide([], isEditable: true);

        Assert.Equal(ContextMenuOutcome.Suppress, outcome);
    }

    [Fact]
    public void KeepsCopyWhenOnlyTextIsSelected()
    {
        var kept = Kept(Item("copy"), Separator(), Item("searchForText"), Item("translate"));

        Assert.Equal(["copy"], kept);
    }

    [Fact]
    public void KeepsTheUsefulImageCommandsButNotTheRest()
    {
        var kept = Kept(
            Item("copyImage"), Item("copyImageLink"),
            Item("saveImageAs"), Item("openImageInNewTab"));

        Assert.Equal(["copyImage", "saveImageAs"], kept);
    }

    [Fact]
    public void NeverLeavesALeadingSeparator()
    {
        var kept = Kept(Item("back"), Item("forward"), Separator(), Item("copy"));

        Assert.Equal(["copy"], kept);
    }

    [Fact]
    public void NeverLeavesATrailingSeparator()
    {
        var kept = Kept(Item("copy"), Separator(), Item("print"), Item("translate"));

        Assert.Equal(["copy"], kept);
    }

    [Fact]
    public void CollapsesSeparatorsLeftAdjacentByTheFiltering()
    {
        // "print" and "share" are dropped, which would otherwise leave the two
        // separators around them stacked up against each other.
        var kept = Kept(
            Item("copy"), Separator(), Item("print"), Separator(), Item("share"),
            Separator(), Item("paste"));

        Assert.Equal(["copy", "---", "paste"], kept);
    }

    [Fact]
    public void MatchesNamesWithoutRegardToCase()
    {
        // Chromium has changed the casing of these identifiers before now.
        Assert.True(ContextMenuPolicy.IsAllowed("SelectAll"));
        Assert.True(ContextMenuPolicy.IsAllowed("pasteasplaintext"));
        Assert.False(ContextMenuPolicy.IsAllowed("Inspect"));
    }

    [Fact]
    public void ShowsNothingAtAllForAnEmptyMenu()
    {
        Assert.Empty(ContextMenuPolicy.Keep([]));
    }
}
