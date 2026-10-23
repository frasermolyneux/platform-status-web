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

    [Theory]
    [InlineData("Visible\n    <!-- internal -->\nsecret\n-->\nMore visible", "Visible\nMore visible")]
    [InlineData("Visible <!--internal-->secret--> after", "Visible  after")]
    [InlineData("Visible\n<!-- INTERNAL -->\nsecret\n<!-- internal -->nested-->\nstill secret\n<!-- /internal -->\nMore visible", "Visible\nMore visible")]
    [InlineData("Visible\n<!-- internal -->\nunclosed secret", "Visible")]
    public void RedactsIndentedInlineNestedAndUnterminatedInternalNotes(string input, string expected) =>
        Assert.Equal(expected, IncidentRenderer.Render(input));

    [Fact]
    public void HandlesBodyWithNoInternalCommentsUnchanged()
    {
        var input = "All systems operational.";
        Assert.Equal(input, IncidentRenderer.Render(input));
    }
}
