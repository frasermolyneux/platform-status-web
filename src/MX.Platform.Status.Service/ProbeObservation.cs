namespace MX.Platform.Status.Service;

public sealed record ProbeObservation(
    string Site, string Component, string Name, string Target, string Observer,
    DateTimeOffset ObservedAt, bool Success, int? HttpStatus, long DurationMilliseconds, string? Error,
    string? CheckId = null, string Method = "GET");
