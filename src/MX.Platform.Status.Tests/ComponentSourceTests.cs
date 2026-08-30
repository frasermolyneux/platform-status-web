using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Tests;

public sealed class ComponentSourceTests
{
    [Fact]
    public void EffectiveResources_PrefersResourcesListOverSingularResource()
    {
        var source = new ComponentSource { Resource = "default", Resources = ["portal", "geolocation"] };
        Assert.Equal(["portal", "geolocation"], source.EffectiveResources());
    }

    [Fact]
    public void EffectiveResources_FallsBackToSingularResourceWhenListEmpty()
    {
        var source = new ComponentSource { Resource = "default" };
        Assert.Equal(["default"], source.EffectiveResources());
    }

    [Fact]
    public void EffectiveResources_IsEmptyWhenNeitherConfigured()
    {
        var source = new ComponentSource();
        Assert.Empty(source.EffectiveResources());
    }

    [Fact]
    public void EffectiveResources_DeduplicatesResourcesList()
    {
        var source = new ComponentSource { Resources = ["default", "Default", "portal"] };
        Assert.Equal(2, source.EffectiveResources().Count);
    }
}
