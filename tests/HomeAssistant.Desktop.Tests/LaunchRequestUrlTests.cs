using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

/// <summary>
/// The homeassistant:// spellings are the official companion apps', not ones invented
/// here, and several of them are easy to get subtly wrong - so each is pinned against
/// the example given in Home Assistant's own documentation or source.
/// </summary>
public class LaunchRequestUrlTests
{
    [Theory]
    // The documented example, which has no leading slash after the host.
    [InlineData("homeassistant://navigate/dashboard-mobile/my-subview", "/dashboard-mobile/my-subview")]
    [InlineData("homeassistant://navigate/lovelace/0", "/lovelace/0")]
    [InlineData("homeassistant://navigate/history", "/history")]
    // The scheme is case-insensitive, as every URL scheme is.
    [InlineData("HomeAssistant://Navigate/energy", "/energy")]
    public void NavigateOpensThePage(string url, string expected)
    {
        var request = LaunchRequest.ParseUrl(url);

        Assert.Equal(LaunchRequestKind.OpenPage, request.Kind);
        Assert.Equal(expected, request.Target);
    }

    [Theory]
    // There is no more-info host: an entity is a query parameter on a root navigate,
    // spelled with hyphens. Both the slashed and unslashed forms occur in the wild.
    [InlineData("homeassistant://navigate/?more-info-entity-id=light.kitchen")]
    [InlineData("homeassistant://navigate?more-info-entity-id=light.kitchen")]
    public void RootNavigateWithAnEntityShowsIt(string url)
    {
        var request = LaunchRequest.ParseUrl(url);

        Assert.Equal(LaunchRequestKind.ShowEntity, request.Kind);
        Assert.Equal("light.kitchen", request.Target);
    }

    [Fact]
    public void AnEntityOnANonRootNavigateIsIgnored()
    {
        // The companion apps honour it only at the root, so a path wins here too.
        var request = LaunchRequest.ParseUrl(
            "homeassistant://navigate/history?more-info-entity-id=light.kitchen");

        Assert.Equal(LaunchRequestKind.OpenPage, request.Kind);
        Assert.Equal("/history", request.Target);
    }

    [Fact]
    public void CallServiceTakesOneDottedSegmentAndTheQuery()
    {
        // The documented example.
        var request = LaunchRequest.ParseUrl(
            "homeassistant://call_service/device_tracker.see?entity_id=device_tracker.entity");

        Assert.Equal(LaunchRequestKind.CallService, request.Kind);
        Assert.Equal("device_tracker.see", request.Target);
        Assert.Equal("entity_id=device_tracker.entity", request.Data);
    }

    [Fact]
    public void CallServiceKeepsEveryQueryItem()
    {
        var request = LaunchRequest.ParseUrl(
            "homeassistant://call_service/light.turn_on?entity_id=light.kitchen&brightness=200");

        Assert.Equal("light.turn_on", request.Target);
        Assert.Equal("entity_id=light.kitchen&brightness=200", request.Data);
    }

    [Theory]
    // Reserved by Home Assistant as the companion apps' OAuth redirect: a link arriving
    // here carries someone else's authorisation code and must not be acted on.
    [InlineData("homeassistant://auth-callback?code=secret")]
    // Pairing and onboarding, which this app has no part in.
    [InlineData("homeassistant://invite/#url=https%3A%2F%2Fexample")]
    // Real hosts, but iOS-only and not implemented here; guessing is worse than ignoring.
    [InlineData("homeassistant://fire_event/custom_event")]
    [InlineData("homeassistant://send_location/")]
    // A service name needs exactly two parts.
    [InlineData("homeassistant://call_service/nodots?x=1")]
    [InlineData("homeassistant://call_service/too.many.parts")]
    [InlineData("homeassistant://call_service/")]
    // A bare navigate says nothing about where to go.
    [InlineData("homeassistant://navigate/")]
    // Not ours, and not a URL at all.
    [InlineData("https://example.com/navigate/thing")]
    [InlineData("not a url")]
    [InlineData("")]
    public void AnythingElseIsIgnored(string url)
    {
        Assert.Equal(LaunchRequestKind.None, LaunchRequest.ParseUrl(url).Kind);
    }

    [Fact]
    public void ALinkIsFoundInAnActivationCommandLine()
    {
        // Activation hands over the whole line, executable included, as one string.
        var arguments = LaunchRequest.Split(
            "\"C:\\Program Files\\Home Assistant\\HomeAssistant.Desktop.exe\" \"homeassistant://navigate/energy\"");

        var request = LaunchRequest.Parse(arguments);

        Assert.Equal(LaunchRequestKind.OpenPage, request.Kind);
        Assert.Equal("/energy", request.Target);
    }

    [Theory]
    [InlineData("--open", LaunchRequestKind.OpenPage, "/lovelace")]
    [InlineData("--entity", LaunchRequestKind.ShowEntity, "light.kitchen")]
    [InlineData("--perform", LaunchRequestKind.PerformOnEntity, "light.kitchen")]
    public void TheSwitchesStillWork(string flag, LaunchRequestKind kind, string target)
    {
        var request = LaunchRequest.Parse(["app.exe", flag, target]);

        Assert.Equal(kind, request.Kind);
        Assert.Equal(target, request.Target);
    }
}
