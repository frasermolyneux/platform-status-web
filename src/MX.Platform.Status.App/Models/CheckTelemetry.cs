namespace MX.Platform.Status.App.Models;

public sealed record CheckTelemetry(
    string Id, string Name, string Kind, ComponentStatus Status, DateTimeOffset? ObservedAt);
