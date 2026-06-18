using System.Text.Json.Serialization;

namespace MX.Platform.Status.App.Models;

public sealed record HistoryDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string Site { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public DateOnly? RolledUpThrough { get; init; }
    public Dictionary<string, ComponentHistoryRecord> ComponentsById { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ComponentHistoryRecord
{
    public string DisplayName { get; init; } = string.Empty;
    public string Kind { get; init; } = "leaf";
    public string? ParentId { get; init; }
    public List<HistoryDay> Days { get; init; } = [];
}

public sealed record HistoryDay
{
    [JsonPropertyName("d")]
    public DateOnly Date { get; init; }

    [JsonPropertyName("s")]
    public ComponentStatus Status { get; init; }

    [JsonPropertyName("u")]
    public double? Uptime { get; init; }

    [JsonPropertyName("t")]
    public int? Total { get; init; }

    [JsonPropertyName("f")]
    public int? Failed { get; init; }

    [JsonPropertyName("p95")]
    public double? P95 { get; init; }

    [JsonPropertyName("o")]
    public bool Overridden { get; init; }
}

public sealed record YearlyHistoryDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string Site { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public Dictionary<string, YearlyComponentHistory> ComponentsById { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record YearlyComponentHistory
{
    public string DisplayName { get; init; } = string.Empty;
    public List<YearSummary> Years { get; init; } = [];
}

public sealed record YearSummary
{
    public int Year { get; init; }
    public double? Uptime { get; init; }
    public int TotalDays { get; init; }
    public int OperationalDays { get; init; }
    public int DegradedDays { get; init; }
    public int OutageDays { get; init; }
    public int UnknownDays { get; init; }
    public int MaintenanceDays { get; init; }
    public int IncidentCount { get; init; }
    public List<QuarterSummary> Quarters { get; init; } = [];
}

public sealed record QuarterSummary
{
    public int Q { get; init; }
    public double? Uptime { get; init; }
}

public sealed record OverridesDocument
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<HistoryOverride> Overrides { get; init; } = [];
}

public sealed record HistoryOverride
{
    public string Component { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public ComponentStatus Status { get; init; }
    public string? Reason { get; init; }
}
