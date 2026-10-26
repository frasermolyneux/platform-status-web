using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class ProcessMonitoringTests
{
    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
    private static CheckDefinition Check => Catalogue().Get("xi").Checks.Single(check => check.Id == "xi.process.server-agent.fleet");

    [Fact]
    public void ProcessSourcesAreExplicitApprovedAndCannotCrossBrandRoleOrResourceBoundaries()
    {
        var source = Check.Process!;
        Assert.True(ProcessMonitoring.IsApproved(source));
        Assert.False(ProcessMonitoring.IsApproved(source with { SiteId = "mx" }));
        Assert.False(ProcessMonitoring.IsApproved(source with { Role = "Another workload" }));
        Assert.False(ProcessMonitoring.IsApproved(source with { Resource = "platform" }));
        var query = ProcessMonitoring.BuildQuery([source]);
        Assert.Contains("siteId == \"xi\"", query);
        Assert.Contains("cloud_RoleName == \"Portal Server Agent\"", query);
        Assert.Contains("arg_max(timestamp", query);
        Assert.DoesNotContain("availabilityResults", query);
        Assert.Throws<InvalidOperationException>(() => ProcessMonitoring.BuildQuery([source with { Role = "other" }]));
        Assert.Throws<InvalidDataException>(() => ProcessMonitoring.Validate(new(source, DateTimeOffset.UtcNow,
            ComponentStatus.Operational, 2, 1, 1, 0), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RepollsRestartsAndMissingSourcesRetainOriginalAssessmentAndBecomeUnknownWithoutNewSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var check = Check;
        var observation = new ProcessObservation(check.Process!, now, ComponentStatus.Degraded, 2, 1, 1, 0);
        var first = ProcessMonitoring.Project("xi", check, [observation], now)!;
        var repolled = ProcessMonitoring.Project("xi", check, [observation], now.AddMinutes(1), first)!;
        Assert.Equal(first.ObservationKey, repolled.ObservationKey);
        Assert.Equal(now, repolled.ObservedAt);
        Assert.Same(first, ProcessMonitoring.Project("xi", check, [], now.AddMinutes(16), first));
        var site = Catalogue().Get("xi") with { Checks = [check] };
        Assert.Equal(ComponentStatus.Unknown, ComponentChecks.Build(site, [], [first], now.AddMinutes(16))[check.Component].Checks.Single().Status);
        Assert.Null(ProcessMonitoring.Project("xi", check, [], now, first with
        {
            Process = check.Process! with { Role = "wrong" }
        }));
        Assert.NotEqual(first.ObservationKey, ProcessMonitoring.Project("xi", check,
            [observation with { ObservedAt = now.AddMinutes(5) }], now.AddMinutes(5))!.ObservationKey);
    }

    [Theory]
    [InlineData("outage", "degraded")]
    [InlineData("degraded", "degraded")]
    public void SupportingImpactIsCappedAcrossLiveDailyAndGranularHistoryWithoutChangingCounts(string measured, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var document = Definition();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var snapshot = Catalogue().Get("xi").Snapshot with { Components = document };
        var definition = document.Components.Single().Children.Single(child => child.Id == "worker");
        snapshot = snapshot with
        {
            Components = document with
            {
                Components = [document.Components.Single() with { Children =
            [
                document.Components.Single().Children[0],
                definition with { Source = new() { Kind = "static", Status = measured } }
            ] }]
            }
        };
        var publication = new StatusMerger().Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry>(), null, [], []);
        Assert.Equal(expected, publication.Components.Single().Status);
        var propagation = new ImpactPropagation(document);
        var dto = new ComponentDto
        {
            Id = "portal",
            History = [new() { Date = day }],
            Children =
            [
                new() { Id = "web", History = [new() { Date = day, Status = "operational", Total = 1 }] },
                new() { Id = "worker", History = [new() { Date = day, Status = measured, Total = 1, Failed = 1 }] }
            ]
        };
        var at = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0, TimeSpan.Zero);
        var recent = RecentHistory.Apply(publication with { Components = [dto], GeneratedAt = now },
            [new("web", at, 1, 0, "operational"),
             new("worker", at, 1, 1, measured)], [], propagation);
        var bucket = recent.Components.Single().RecentHistory!.Last24Hours[^1];
        Assert.Equal(expected, bucket.Status);
        Assert.Equal(2, bucket.Total);
        Assert.Equal(1, bucket.Failed);
        var daily = IncidentImpact.Apply(publication with { Components = [dto] }, [], propagation).Components.Single();
        Assert.Equal(expected, daily.History.Single().Status);
        Assert.Equal(1, daily.Children!.Single(child => child.Id == "worker").History.Single().Failed);
    }

    [Theory]
    [InlineData("worker", "degraded")]
    [InlineData("portal", "outage")]
    public void IncidentContributionCapsDoNotSuppressExplicitParentOutages(string affected, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var document = Definition();
        var snapshot = Catalogue().Get("xi").Snapshot with { Components = document };
        var incident = new Incident
        {
            Id = 15,
            Title = "Incident",
            Components = [affected],
            CreatedAt = now.AddMinutes(-5),
            Severity = Severity.Outage,
            State = IncidentState.Identified,
            ImpactHistory = [new(now.AddMinutes(-5), IncidentState.Identified, Severity.Outage, [affected])]
        };
        var publication = new StatusMerger().Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry>(), null, [incident], []);
        Assert.Equal(expected, publication.Components.Single().Status);
        Assert.Equal(expected, publication.Components.Single().History[^1].Status);
        var recent = RecentHistory.Apply(publication, [], [incident], new(document));
        Assert.Equal(expected, recent.Components.Single().RecentHistory!.Last24Hours[^1].Status);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("maintenance")]
    public void CapsDoNotReclassifyUncertaintyOrMaintenance(string status) =>
        Assert.Equal(status, new ImpactPropagation(Definition()).Contribute("portal", "worker", status));

    private static ComponentsDocument Definition() => new()
    {
        Components = [new()
        {
            Id = "portal", Kind = "group", Children =
            [
                new() { Id = "web", Source = new() { Kind = "static", Status = "operational" } },
                new() { Id = "worker", MaximumParentImpact = "degraded", HistoryClassification = "impact",
                    Source = new() { Kind = "static", Status = "operational" } }
            ]
        }]
    };
}
