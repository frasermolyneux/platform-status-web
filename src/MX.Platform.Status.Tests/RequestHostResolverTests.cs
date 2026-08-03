using MX.Platform.Status.App.Sites;

namespace MX.Platform.Status.Tests;

public sealed class RequestHostResolverTests
{
    [Fact]
    public void FallsBackToHostWhenForwardedHostAbsent()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Host"] = ["status.xtremeidiots.com"],
        };

        Assert.Equal("status.xtremeidiots.com", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void PrefersForwardedHostOverHostForCustomDomain()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Host"] = ["internal-swa-linked-app.azurewebsites.net"],
            ["X-Forwarded-Host"] = ["status.xtremeidiots.com"],
        };

        Assert.Equal("status.xtremeidiots.com", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void PreservesPortFromForwardedHost()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Host"] = ["internal.azurewebsites.net"],
            ["X-Forwarded-Host"] = ["dev.mxstatus.io:8443"],
        };

        Assert.Equal("dev.mxstatus.io:8443", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void PreservesCasingFromForwardedHost()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["X-Forwarded-Host"] = ["MXStatus.IO"],
        };

        Assert.Equal("MXStatus.IO", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void PreservesTrailingDotFromForwardedHost()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["X-Forwarded-Host"] = ["mxstatus.io."],
        };

        Assert.Equal("mxstatus.io.", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void UsesFirstHopOfForwardedHostChain()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Host"] = ["internal.azurewebsites.net"],
            ["X-Forwarded-Host"] = ["status.xtremeidiots.com, edge-hop-2.internal, edge-hop-3.internal"],
        };

        Assert.Equal("status.xtremeidiots.com", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void UsesOnlyFirstForwardedHostHeaderInstance()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["X-Forwarded-Host"] = ["status.xtremeidiots.com", "attacker-controlled.example"],
        };

        Assert.Equal("status.xtremeidiots.com", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("host\twith\ttabs")]
    [InlineData("host\r\nwith\r\ninjected\r\nheader")]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToHostWhenForwardedHostIsMalformed(string malformedForwardedHost)
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Host"] = ["status.xtremeidiots.com"],
            ["X-Forwarded-Host"] = [malformedForwardedHost],
        };

        Assert.Equal("status.xtremeidiots.com", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void ReturnsNullWhenNeitherHeaderIsPresent()
    {
        var headers = new Dictionary<string, IEnumerable<string>>();

        Assert.Null(RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    [Fact]
    public void AcceptsBracketedIPv6ForwardedHost()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["X-Forwarded-Host"] = ["[::1]:8080"],
        };

        Assert.Equal("[::1]:8080", RequestHostResolver.Resolve(name => Lookup(headers, name)));
    }

    private static IEnumerable<string>? Lookup(Dictionary<string, IEnumerable<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) ? values : null;
}
