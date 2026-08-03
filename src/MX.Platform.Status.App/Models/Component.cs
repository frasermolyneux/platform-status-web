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

    /// <summary>
    /// Optional explicit list of probe region identifiers (matching the <c>region</c>
    /// customDimension emitted by the producer) expected to report for this component. When set,
    /// a region in this list that has not reported at all (or only stale data) counts against
    /// the component's health instead of being silently ignored, so missing telemetry can never be
    /// presented as healthy. When unset, only the regions actually observed in the query window are
    /// considered.
    /// </summary>
    public List<string>? ExpectedRegions { get; init; }
}

public sealed record ComponentSource
{
    public string Kind { get; init; } = "static";
    public string? Resource { get; init; }

    /// <summary>
    /// Optional list of Application Insights resource aliases (keys into <see cref="Site.AppInsights"/>)
    /// to query for this component, for components whose telemetry spans more than one AI resource.
    /// When non-empty this takes precedence over the singular <see cref="Resource"/> field, which is
    /// retained for backward compatibility with existing single-resource content.
    /// </summary>
    public List<string> Resources { get; init; } = [];

    public string Table { get; init; } = "availabilityResults";
    public Dictionary<string, object?> Filter { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Status { get; init; }

    /// <summary>
    /// Returns the effective, de-duplicated list of Application Insights resource aliases to query
    /// for this component: <see cref="Resources"/> when populated, otherwise a single-item list built
    /// from <see cref="Resource"/>, or empty when neither is configured.
    /// </summary>
    public IReadOnlyList<string> EffectiveResources() =>
        Resources.Count > 0
            ? Resources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : string.IsNullOrWhiteSpace(Resource) ? [] : [Resource];
}
