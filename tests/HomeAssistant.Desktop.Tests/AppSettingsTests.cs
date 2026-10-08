using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

/// <summary>
/// Covers only <see cref="AppSettings.Normalize"/>, which is pure. Load and Save are
/// left alone deliberately: both read and write the real settings file under
/// LocalAppData, and a test has no business touching the installed app's state.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void MovesAnOldSingleAddressIntoTheInternalOne()
    {
        var settings = new AppSettings { HomeUrl = "http://192.168.1.188:8123" };

        settings.Normalize();

        Assert.Equal("http://192.168.1.188:8123", settings.InternalUrl);
        Assert.Null(settings.HomeUrl);
    }

    [Fact]
    public void DoesNotLetTheOldAddressOverwriteOneAlreadySet()
    {
        var settings = new AppSettings
        {
            HomeUrl = "http://stale.local:8123",
            InternalUrl = "http://192.168.1.188:8123",
        };

        settings.Normalize();

        Assert.Equal("http://192.168.1.188:8123", settings.InternalUrl);
        Assert.Null(settings.HomeUrl);
    }

    [Fact]
    public void RunningTwiceChangesNothingTheSecondTime()
    {
        var settings = new AppSettings { HomeUrl = "192.168.1.188:8123" };

        settings.Normalize();
        var once = settings.InternalUrl;
        settings.Normalize();

        Assert.Equal(once, settings.InternalUrl);
        Assert.Equal("http://192.168.1.188:8123", settings.InternalUrl);
    }

    [Theory]
    [InlineData("192.168.1.188:8123", "http://192.168.1.188:8123")]
    [InlineData("  ha.example.com  ", "http://ha.example.com")]
    [InlineData("http://ha.local:8123/", "http://ha.local:8123")]
    [InlineData("https://ha.example.com///", "https://ha.example.com")]
    public void TidiesATypedInAddress(string typed, string expected)
    {
        var settings = new AppSettings { InternalUrl = typed, ExternalUrl = typed };

        settings.Normalize();

        Assert.Equal(expected, settings.InternalUrl);
        Assert.Equal(expected, settings.ExternalUrl);
    }

    [Fact]
    public void LeavesAnHttpsAddressAlone()
    {
        var settings = new AppSettings { ExternalUrl = "https://ha.example.com" };

        settings.Normalize();

        Assert.Equal("https://ha.example.com", settings.ExternalUrl);
    }

    [Fact]
    public void InventsNoAddressWhenNoneWasGiven()
    {
        // Guessing here would send a machine that has not been set up at somebody
        // else's Home Assistant rather than saying it does not know where to look.
        var settings = new AppSettings();

        settings.Normalize();

        Assert.Equal(string.Empty, settings.InternalUrl);
        Assert.Equal(string.Empty, settings.ExternalUrl);
    }

    [Fact]
    public void TreatsAWhitespaceOnlyAddressAsNotSet()
    {
        var settings = new AppSettings { InternalUrl = "   ", ExternalUrl = "\t" };

        settings.Normalize();

        Assert.Equal(string.Empty, settings.InternalUrl);
        Assert.Equal(string.Empty, settings.ExternalUrl);
    }
}
