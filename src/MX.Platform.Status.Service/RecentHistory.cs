using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

internal sealed record MonitoringBucket(string Component, DateTimeOffset At, int Total, int Failed, string Status);

internal static class RecentHistory
{
    internal static StatusApiResponse Apply(StatusApiResponse publication, IReadOnlyList<MonitoringBucket> buckets,
        IReadOnlyList<Incident> incidents)
    {
        var now = publication.GeneratedAt;
        var quarter = Floor(now, 15);
        var hour = Floor(now, 60);
        var indexed = buckets.ToLookup(bucket => bucket.Component);
        return publication with { Components = publication.Components.Select(component => Build(component, [])).ToArray() };

        ComponentDto Build(ComponentDto component, string[] ancestors)
        {
            var children = component.Children?.Select(child => Build(child, [.. ancestors, component.Id])).ToArray();
            var scope = Descendants(component).Concat(ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var leaves = Leaves(component).ToArray();
            var samples = leaves.SelectMany(leaf => indexed[leaf]).ToArray();
            return component with
            {
                Children = children,
                RecentHistory = new(
                    Window(quarter.AddMinutes(-95 * 15), 96, 15),
                    Window(hour.AddHours(-71), 72, 60))
            };

            HistoryBucketDto[] Window(DateTimeOffset start, int count, int minutes) =>
                Enumerable.Range(0, count).Select(index =>
                {
                    var from = start.AddMinutes(index * minutes);
                    var to = from.AddMinutes(minutes);
                    var end = to < now ? to : now;
                    var observed = samples.Where(bucket => bucket.At >= from && bucket.At < to).ToArray();
                    var impacts = incidents.Select(incident => (incident.Id,
                        Statuses: IncidentImpact.ForRange(incident, scope, from, end).ToArray()))
                        .Where(impact => impact.Statuses.Length > 0).ToArray();
                    var statuses = observed.Select(bucket => bucket.Status).Concat(impacts.SelectMany(impact => impact.Statuses));
                    if (leaves.Any(leaf => !observed.Any(bucket => bucket.Component == leaf)))
                        statuses = statuses.Append("unknown");
                    return new HistoryBucketDto(from, to, IncidentImpact.Worst(statuses),
                        observed.Sum(bucket => bucket.Total), observed.Sum(bucket => bucket.Failed),
                        impacts.Select(impact => impact.Id).Distinct().Order().ToArray());
                }).ToArray();
        }
    }

    private static DateTimeOffset Floor(DateTimeOffset at, int minutes) =>
        new(at.UtcDateTime.Ticks / TimeSpan.FromMinutes(minutes).Ticks * TimeSpan.FromMinutes(minutes).Ticks, TimeSpan.Zero);

    private static IEnumerable<string> Descendants(ComponentDto component) =>
        new[] { component.Id }.Concat((component.Children ?? []).SelectMany(Descendants));

    private static IEnumerable<string> Leaves(ComponentDto component) => component.Children is { Count: > 0 }
        ? component.Children.SelectMany(Leaves) : [component.Id];
}
