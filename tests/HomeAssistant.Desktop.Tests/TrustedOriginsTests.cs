using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

public class TrustedOriginsTests
{
    private static readonly string[] Configured =
        ["http://192.168.1.188:8123", "https://ha.example.test"];

    [Theory]
    [InlineData("http://192.168.1.188:8123/lovelace/0")]
    [InlineData("https://ha.example.test/config/dashboard")]
    // Case in the host is not a difference.
    [InlineData("https://HA.EXAMPLE.TEST/")]
    public void TheConfiguredAddressesAreTrusted(string candidate)
    {
        Assert.True(TrustedOrigins.IsTrusted(candidate, Configured));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    // Same host, different port, is a different origin on the web and here.
    [InlineData("http://192.168.1.188:9000/")]
    // Same host, different scheme, likewise.
    [InlineData("https://192.168.1.188:8123/")]
    // A host that merely ends with a trusted one.
    [InlineData("https://ha.example.test.evil.example/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNot(string? candidate)
    {
        Assert.False(TrustedOrigins.IsTrusted(candidate, Configured));
    }

    [Theory]
    [InlineData("/lovelace/0", "http://192.168.1.188:8123/lovelace/0")]
    [InlineData("lovelace/0", "http://192.168.1.188:8123/lovelace/0")]
    [InlineData("/config/dashboard?x=1", "http://192.168.1.188:8123/config/dashboard?x=1")]
    public void APathResolvesAgainstTheAddressInUse(string target, string expected)
    {
        var resolved = TrustedOrigins.ResolveWithin(target, "http://192.168.1.188:8123", Configured);
        Assert.Equal(expected, resolved?.ToString());
    }

    [Theory]
    // The bug this exists for: Uri.TryCreate(base, target) returns the target whenever
    // the target is absolute, so a notification could name any site it liked.
    [InlineData("https://evil.example/")]
    [InlineData("http://evil.example/steal")]
    // Scheme-relative keeps the scheme and replaces the host, which is just as bad and
    // much easier to miss.
    [InlineData("//evil.example/")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void SomewhereOffHomeAssistantResolvesToNothing(string target)
    {
        Assert.Null(TrustedOrigins.ResolveWithin(target, "http://192.168.1.188:8123", Configured));
    }

    [Fact]
    public void TheOtherConfiguredAddressIsStillHomeAssistant()
    {
        // A laptop that left the house is on the external address, and a notification
        // naming the internal one must not be treated as an attack.
        var resolved = TrustedOrigins.ResolveWithin(
            "http://192.168.1.188:8123/lovelace/0", "https://ha.example.test", Configured);

        Assert.Equal("http://192.168.1.188:8123/lovelace/0", resolved?.ToString());
    }

    [Fact]
    public void NothingResolvesBeforeAnAddressIsConfigured()
    {
        Assert.Null(TrustedOrigins.ResolveWithin("/lovelace/0", "", []));
        Assert.False(TrustedOrigins.AnyConfigured([null, "", "   "]));
        Assert.True(TrustedOrigins.AnyConfigured([null, "http://192.168.1.188:8123"]));
    }
}
