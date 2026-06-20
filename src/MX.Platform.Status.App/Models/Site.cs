namespace MX.Platform.Status.App.Models;

public sealed record Site
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Tagline { get; init; }
    public List<string> Domains { get; init; } = [];
    public List<SiteLink> Links { get; init; } = [];
    public Dictionary<string, AppInsightsResource> AppInsights { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IncidentDefaults IncidentDefaults { get; init; } = new();
    public FeedSettings Feed { get; init; } = new();
}

public sealed record SiteLink
{
    public string Label { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
}

public sealed record AppInsightsResource
{
    public string ResourceId { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed record IncidentDefaults
{
    public Severity Severity { get; init; } = Severity.Degraded;
    public bool AutoCloseOnAlertResolve { get; init; }
}

public sealed record FeedSettings
{
    public bool RssEnabled { get; init; } = true;
    public int MaxItems { get; init; } = 50;
}

public sealed record SiteConfigurationSnapshot(
    Site Site,
    ComponentsDocument Components,
    string SiteYaml,
    string ComponentsYaml,
    DateTimeOffset LoadedAtUtc);

public sealed record SiteConfigSnapshotContent(string SiteYaml, string ComponentsYaml);
