using YamlDotNet.Serialization;

namespace MX.Platform.Status.App.Models;

public sealed record ComponentsDocument
{
    public int Version { get; init; } = 1;
    public List<Component> Components { get; init; } = [];
}

public sealed record Component
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Link { get; init; }
    public string Kind { get; init; } = "leaf";
    public bool Hidden { get; init; }
    public List<string> Tags { get; init; } = [];
    public SlaDefinition Sla { get; init; } = new();
    public ComponentSource Source { get; init; } = new();

    [YamlMember(Alias = "components")]
    public List<Component> Children { get; init; } = [];
}

public sealed record SlaDefinition
{
    public int WindowDays { get; init; } = 90;
    public double UptimeTarget { get; init; } = 0.999;
    public double DegradedBelow { get; init; } = 0.995;
    public double OutageBelow { get; init; } = 0.9;
    public int ExpectedIntervalSeconds { get; init; } = 60;
}

public sealed record ComponentSource
{
    public string Kind { get; init; } = "static";
    public string? Resource { get; init; }
    public string Table { get; init; } = "availabilityResults";
    public Dictionary<string, object?> Filter { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Status { get; init; }
}
