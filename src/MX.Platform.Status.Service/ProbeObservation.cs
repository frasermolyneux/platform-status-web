using System.Diagnostics;

namespace MX.Platform.Status.Service;

public sealed record ProbeObservation(
    string Site, string Component, string Name, string Target, string Observer,
    DateTimeOffset ObservedAt, bool Success, int? HttpStatus, long DurationMilliseconds, string? Error);

public static class IndependentProbe
{
    public static async Task<ProbeObservation> ObserveAsync(
        HttpClient client, string site, string component, string name, string target, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        int? status = null;
        string? error = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            if (status is < 200 or >= 400) error = $"HTTP {status} {response.ReasonPhrase}";
        }
        catch (HttpRequestException exception)
        {
            error = exception.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "The HTTP check timed out after 5 seconds.";
        }
        ct.ThrowIfCancellationRequested();
        return new(site, component, name, target, Environment.MachineName, DateTimeOffset.UtcNow,
            status is >= 200 and < 400, status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
    }
}
