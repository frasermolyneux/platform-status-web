namespace MX.Platform.Status.App.Telemetry;

/// <summary>
/// Configures the recent-window lookback used for live status queries (see
/// <see cref="AvailabilityQueryBuilder.BuildLiveRegionalQuery"/>), replacing the previous
/// <c>startofday(now())</c> query which grew increasingly stale/expensive across a UTC day and could
/// mask a fresh outage behind hours of earlier healthy samples. Configured via the
/// <c>LIVE_WINDOW_MINUTES</c> app setting.
/// </summary>
public sealed record LiveWindowOptions(int Minutes)
{
    public const int DefaultMinutes = 15;
}
