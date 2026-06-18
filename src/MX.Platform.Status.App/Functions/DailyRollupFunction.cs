using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Platform.Status.App.Rollup;
using MX.Platform.Status.App.Sites;

namespace MX.Platform.Status.App.Functions;

public sealed class DailyRollupFunction
{
    private readonly SiteConfigLoader _siteConfigLoader;
    private readonly BackfillService _backfillService;
    private readonly DailyRollupService _dailyRollupService;
    private readonly YearlyAggregator _yearlyAggregator;
    private readonly ILogger<DailyRollupFunction> _logger;
    private readonly int _replayDays;

    public DailyRollupFunction(
        SiteConfigLoader siteConfigLoader,
        BackfillService backfillService,
        DailyRollupService dailyRollupService,
        YearlyAggregator yearlyAggregator,
        ILogger<DailyRollupFunction> logger)
    {
        _siteConfigLoader = siteConfigLoader;
        _backfillService = backfillService;
        _dailyRollupService = dailyRollupService;
        _yearlyAggregator = yearlyAggregator;
        _logger = logger;
        _replayDays = int.TryParse(Environment.GetEnvironmentVariable("ROLLUP_REPLAY_DAYS"), out var parsed) ? parsed : 3;
    }

    [Function("DailyRollup")]
    public async Task Run(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        foreach (var siteId in SiteConfigLoader.KnownSiteIds)
        {
            try
            {
                var snapshot = await _siteConfigLoader.LoadSiteAsync(siteId).ConfigureAwait(false);
                await _backfillService.EnsureBackfilledAsync(snapshot).ConfigureAwait(false);
                var history = await _dailyRollupService.RollAsync(snapshot, _replayDays).ConfigureAwait(false);
                await _yearlyAggregator.WriteAsync(history).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Daily rollup failed for site '{SiteId}'.", siteId);
            }
        }
    }
}
