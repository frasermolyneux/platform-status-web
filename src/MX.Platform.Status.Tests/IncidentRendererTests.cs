using MX.Platform.Status.App.Incidents;

namespace MX.Platform.Status.Tests;

public sealed class IncidentRendererTests
{
    [Fact]
    public void StripsSingleLineInternalComments()
    {
        var result = IncidentRenderer.Render("Visible\n<!-- internal -->secret-->\nMore visible");
        Assert.Equal("Visible\nMore visible", result);
    }

    [Fact]
    public void StripsMultiLineInternalBlocks()
    {
        var result = IncidentRenderer.Render("Visible\n<!-- internal -->\nsecret\nmore secret\n-->\nMore visible");
        Assert.Equal("Visible\nMore visible", result);
    }

    [Fact]
    public void PreservesNonInternalContent()
    {
        var result = IncidentRenderer.Render("Visible content");
        Assert.Equal("Visible content", result);
    }

    [Fact]
    public void HandlesBodyWithNoInternalCommentsUnchanged()
    {
        var input = "All systems operational.";
        Assert.Equal(input, IncidentRenderer.Render(input));
    }
}
