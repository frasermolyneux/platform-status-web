using System.Text.Json.Serialization;

namespace MX.Platform.Status.App.Contracts;

public sealed record StatusApiResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("site")]
    public SiteInfo Site { get; init; } = new();

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("dataFreshness")]
    public DataFreshness? DataFreshness { get; init; }

    [JsonPropertyName("summary")]
    public Summary Summary { get; init; } = new();

    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto> Components { get; init; } = [];

    [JsonPropertyName("incidents")]
    public IReadOnlyList<IncidentDto> Incidents { get; init; } = [];

    [JsonPropertyName("maintenance")]
    public IReadOnlyList<MaintenanceDto> Maintenance { get; init; } = [];
}

public sealed record SiteInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("tagline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tagline { get; init; }

    [JsonPropertyName("links")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LinkDto>? Links { get; init; }
}

public sealed record LinkDto
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("href")]
    public string Href { get; init; } = string.Empty;
}

public sealed record ComponentDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Link { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "leaf";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";

    [JsonPropertyName("lastSampleAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastSampleAt { get; init; }

    [JsonPropertyName("uptimeWindowDays")]
    public int UptimeWindowDays { get; init; }

    [JsonPropertyName("uptimeRatio")]
    public double? UptimeRatio { get; init; }

    [JsonPropertyName("history")]
    public IReadOnlyList<HistoryDayDto> History { get; init; } = [];

    [JsonPropertyName("openIncidentIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? OpenIncidentIds { get; init; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ComponentDto>? Children { get; init; }
}

public sealed record HistoryDayDto
{
    [JsonPropertyName("date")]
    public DateOnly Date { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";

    [JsonPropertyName("uptime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Uptime { get; init; }

    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }

    [JsonPropertyName("failed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Failed { get; init; }

    [JsonPropertyName("incidentIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? IncidentIds { get; init; }
}

public sealed record IncidentDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("components")]
    public IReadOnlyList<string> Components { get; init; } = [];

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "degraded";

    [JsonPropertyName("state")]
    public string State { get; init; } = "investigating";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("resolvedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ResolvedAt { get; init; }

    [JsonPropertyName("updates")]
    public IReadOnlyList<IncidentUpdateDto> Updates { get; init; } = [];
}

public sealed record IncidentUpdateDto
{
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "investigating";

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; init; }
}

public sealed record MaintenanceDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("components")]
    public IReadOnlyList<string> Components { get; init; } = [];

    [JsonPropertyName("scheduledStart")]
    public DateTimeOffset ScheduledStart { get; init; }

    [JsonPropertyName("scheduledEnd")]
    public DateTimeOffset ScheduledEnd { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "scheduled";

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }
}

public sealed record DataFreshness
{
    [JsonPropertyName("appInsightsAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AppInsightsAt { get; init; }

    [JsonPropertyName("historyAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HistoryAt { get; init; }

    [JsonPropertyName("stale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stale { get; init; }
}

public sealed record Summary
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
