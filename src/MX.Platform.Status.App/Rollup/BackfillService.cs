using MX.Platform.Status.App.History;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Rollup;

public sealed class BackfillService
{
    private readonly HistoryReader _historyReader;
    private readonly DailyRollupService _dailyRollupService;
    private readonly int _backfillDays;

    public BackfillService(HistoryReader historyReader, DailyRollupService dailyRollupService)
    {
        _historyReader = historyReader;
        _dailyRollupService = dailyRollupService;
        _backfillDays = int.TryParse(Environment.GetEnvironmentVariable("ROLLUP_BACKFILL_DAYS_FIRST_RUN"), out var parsed) ? parsed : 30;
    }

    public async Task<HistoryDocument?> EnsureBackfilledAsync(SiteConfigurationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var existing = await _historyReader.ReadDailyHistoryAsync(snapshot.Site.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        return await _dailyRollupService.RollAsync(snapshot, _backfillDays, cancellationToken).ConfigureAwait(false);
    }
}
