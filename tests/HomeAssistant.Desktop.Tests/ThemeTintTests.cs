using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

public class ThemeTintTests
{
    [Theory]
    // What the injected watcher actually sends, having resolved the theme through the
    // browser's own computed style.
    [InlineData("rgb(28, 28, 28)", 28, 28, 28)]
    [InlineData("rgba(28, 28, 28, 0.9)", 28, 28, 28)]
    [InlineData("rgb(255 255 255)", 255, 255, 255)]
    [InlineData("rgb(0 0 0 / 50%)", 0, 0, 0)]
    // Hex, for a value that reached the host without that step.
    [InlineData("#1c1c1c", 28, 28, 28)]
    [InlineData("#FFF", 255, 255, 255)]
    [InlineData("#03a9f4ff", 3, 169, 244)]
    [InlineData("  #03a9f4  ", 3, 169, 244)]
    public void AColourTheThemeCanProduceIsUnderstood(string css, byte r, byte g, byte b)
    {
        Assert.True(ThemeTint.TryParse(css, out var colour));
        Assert.Equal(new Rgb(r, g, b), colour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("transparent")]
    [InlineData("var(--primary-background-color)")]
    [InlineData("#12345")]
    [InlineData("rgb(1, 2)")]
    // A fully transparent header means the theme has not been applied yet. Treating it
    // as a colour would paint the title bar black and then never correct itself.
    [InlineData("rgba(0, 0, 0, 0)")]
    public void AnythingUnusableIsRejectedRatherThanGuessedAt(string? css)
    {
        Assert.False(ThemeTint.TryParse(css, out _));
    }

    [Theory]
    [InlineData(28, 28, 28)]
    [InlineData(17, 17, 17)]
    public void ADarkHeaderFallsBackToWhiteText(byte r, byte g, byte b)
    {
        Assert.Equal(Rgb.White, ThemeTint.Contrasting(new Rgb(r, g, b)));
    }

    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(250, 250, 250)]
    // Home Assistant's own light blue. Black genuinely reads better on it, which is
    // why the theme's --app-header-text-color is preferred when it has one: Home
    // Assistant puts white here, and matching the dashboard matters more than winning
    // a contrast calculation.
    [InlineData(3, 169, 244)]
    public void ALighterHeaderFallsBackToBlackText(byte r, byte g, byte b)
    {
        Assert.Equal(Rgb.Black, ThemeTint.Contrasting(new Rgb(r, g, b)));
    }

    [Fact]
    public void HoverStatesMoveAwayFromTheHeaderRatherThanAlwaysGettingLighter()
    {
        var dark = new Rgb(28, 28, 28);
        var light = new Rgb(250, 250, 250);

        // On a dark header the hover tint brightens; on a light one it must darken, or
        // the caption buttons disappear exactly when the pointer is over them.
        Assert.True(ThemeTint.Blend(dark, ThemeTint.Contrasting(dark), 0.12).R > dark.R);
        Assert.True(ThemeTint.Blend(light, ThemeTint.Contrasting(light), 0.12).R < light.R);
    }

    [Fact]
    public void BlendingAllTheWayReachesTheOtherColour()
    {
        Assert.Equal(Rgb.White, ThemeTint.Blend(Rgb.Black, Rgb.White, 1));
        Assert.Equal(Rgb.Black, ThemeTint.Blend(Rgb.Black, Rgb.White, 0));
    }
}
