using System.Text.Json;

namespace MX.Platform.Status.Tests;

public sealed class HostTelemetryConfigurationTests
{
    private static readonly JsonElement Logging = LoadHostConfiguration()
        .GetProperty("logging");

    [Fact]
    public void HostLogging_DropsSuccessfulInvocationsButRetainsFailuresAndWarnings()
    {
        var logLevel = Logging.GetProperty("logLevel");

        Assert.Equal("Warning", logLevel.GetProperty("Function").GetString());
        Assert.Equal("Error", logLevel.GetProperty("Host.Results").GetString());
    }

    [Fact]
    public void HostLogging_DoesNotUseProbabilisticSampling()
    {
        var samplingSettings = Logging
            .GetProperty("applicationInsights")
            .GetProperty("samplingSettings");

        Assert.False(samplingSettings.GetProperty("isEnabled").GetBoolean());
    }

    private static JsonElement LoadHostConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "host.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.Clone();
    }
}
