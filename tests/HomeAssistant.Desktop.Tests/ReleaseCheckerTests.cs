using System.Text.Json;
using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

/// <summary>
/// Release tags and the GitHub payload are the two places an update check can quietly
/// go wrong: a tag that fails to parse makes every release invisible, and picking the
/// wrong asset makes Install download something that is not an installer.
/// </summary>
public class ReleaseCheckerTests
{
    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("V2.0.1", 2, 0, 1)]
    // The file version carries a fourth part that is always zero; it must not make the
    // release look different from the build that produced it.
    [InlineData("v1.2.0.0", 1, 2, 0)]
    [InlineData("v1.2", 1, 2, 0)]
    public void TagsParse(string tag, int major, int minor, int build)
    {
        Assert.True(ReleaseChecker.TryParseTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-two")]
    public void NonsenseTagsAreRejected(string? tag)
    {
        Assert.False(ReleaseChecker.TryParseTag(tag, out _));
    }

    [Fact]
    public void TheInstallerAssetIsPicked()
    {
        var release = Parse("""
            {
              "tag_name": "v1.3.0",
              "name": "v1.3.0",
              "body": "Some notes",
              "html_url": "https://github.com/o/r/releases/tag/v1.3.0",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://x/notes.txt", "size": 10 },
                { "name": "HomeAssistantDesktop-1.3.0-x64.msi", "browser_download_url": "https://x/app.msi", "size": 78643200 }
              ]
            }
            """);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 3, 0), release!.Version);
        Assert.Equal("https://x/app.msi", release.InstallerUrl);
        Assert.Equal(78643200, release.InstallerSize);
        Assert.Equal("Some notes", release.Notes);
    }

    [Fact]
    public void AReleaseWithNoInstallerIsStillReported()
    {
        // Worth surfacing rather than hiding: the version is real, and the entity
        // saying so is more useful than silence. Install is what has to cope.
        var release = Parse("""
            { "tag_name": "v1.4.0", "name": "v1.4.0", "assets": [] }
            """);

        Assert.NotNull(release);
        Assert.Null(release!.InstallerUrl);
    }

    [Fact]
    public void AReleaseWithNoUsableTagIsIgnored()
    {
        Assert.Null(Parse("""{ "tag_name": "nightly", "assets": [] }"""));
    }

    [Fact]
    public void TheTitleFallsBackToTheTag()
    {
        var release = Parse("""{ "tag_name": "v1.5.0", "assets": [] }""");

        Assert.Equal("v1.5.0", release!.Title);
    }

    [Fact]
    public void TheRunningVersionHasThreeParts()
    {
        // Compared against a three-part tag, so a fourth would never match.
        var current = ReleaseChecker.Current;

        Assert.Equal(-1, current.Revision);
        Assert.True(current.Major >= 0);
    }

    private static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReleaseChecker.Parse(document.RootElement);
    }
}
