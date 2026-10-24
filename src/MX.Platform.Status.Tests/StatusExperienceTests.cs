using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Telemetry;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class StatusExperienceTests
{
    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-23T08:00:00Z");

    [Fact]
    public void ExplicitChecksReplaceLegacyProbesForTheirComponentWhileOtherLegacyProbesRemainAvailable()
    {
        var site = Catalogue().Get("mx") with
        {
            Checks = [new() { Id = "queue", Name = "Queue delay", Component = "mx.sitewatch.geo-location-web", Kind = "telemetry" }]
        };
        var queue = Assert.Single(site.EffectiveChecks, check => check.Component == "mx.sitewatch.geo-location-web");
        Assert.Equal("telemetry", queue.Kind);
        Assert.Equal("queue", queue.Id);
        Assert.Contains(site.EffectiveChecks, check => check.Component == "mx.sitewatch.molyneux-me" && check.Kind == "http");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(301, true)]
    [InlineData(-60, true)]
    public void HttpCheckSchedulingUsesDurableMatchingObservationsAndItsOwnInterval(int ageSeconds, bool due)
    {
        var now = DateTimeOffset.UtcNow;
        var check = new CheckDefinition
        {
            Id = "slow",
            Component = "component",
            Name = "Slow check",
            Target = "https://example.test/",
            ExpectedIntervalSeconds = 300
        };
        var observation = new ProbeObservation("mx", check.Component, check.Name, check.Target, "publisher",
            now.AddSeconds(-ageSeconds), true, 200, 10, null, check.Id);
        Assert.Equal(due, check.IsDue("mx", [observation], now));
        Assert.True(check.IsDue("mx", [], now));
        Assert.True(check.IsDue("xi", [observation], now));
        Assert.True(check.IsDue("mx", [observation with { CheckId = "different" }], now));
        Assert.True((check with { Target = "https://new.example.test/" }).IsDue("mx", [observation], now));
        Assert.True((check with { Method = "HEAD" }).IsDue("mx", [observation], now));
    }

    [Theory]
    [InlineData(true, "operational")]
    [InlineData(false, "outage")]
    public void PartialProbeBatchesRetainSlowCheckStateBeyondTheDefaultComponentFreshnessWindow(bool success, string expected)
    {
        const string component = "mx.sitewatch.geo-location-web";
        var site = Catalogue().Get("mx") with
        {
            Probes = [],
            Checks =
            [
                new() { Id = "fast", Component = component, Name = "Fast", Target = "https://fast.example.test/" },
                new() { Id = "slow", Component = component, Name = "Slow", Target = "https://slow.example.test/", ExpectedIntervalSeconds = 300 }
            ]
        };
        var now = DateTimeOffset.UtcNow;
        var previous = new ProbeObservation("mx", component, "Slow", "https://slow.example.test/", "publisher",
            now.AddSeconds(-250), success, success ? 200 : 503, 10, null, "slow");
        var fresh = previous with { CheckId = "fast", Target = "https://fast.example.test/", ObservedAt = now, Success = true, HttpStatus = 200 };
        var live = ComponentChecks.Build(site, [previous, fresh], []);
        var publication = new StatusMerger().Merge(site.Snapshot, live, null, [], []);
        var geo = Flatten(publication.Components).Single(item => item.Id == component);
        Assert.Equal(expected, geo.Status);
        Assert.Equal(expected, geo.Checks!.Single(check => check.Id == "mx/slow").Status);
        Assert.False(site.Checks.Single(check => check.Id == "slow").IsDue("mx", [previous], now));
        Assert.Equal("unknown", ComponentChecks.Build(site, [fresh], [])[component].Checks
            .Single(check => check.Id == "mx/slow").Status.ToApiString());
    }

    [Theory]
    [InlineData("operational")]
    [InlineData("unknown")]
    public void ConfiguredChecksOverrideStaticSourceFallbackAndBubbleUpToTheirService(string staticStatus)
    {
        var site = Catalogue().Get("mx") with
        {
            Probes = [],
            Checks = [new() { Id = "queue", Name = "Queue delay", Component = "component", Kind = "telemetry" }],
            Components = new()
            {
                Components = [new()
                {
                    Id = "service", Name = "Service", Kind = "group",
                    Children = [new()
                    {
                        Id = "component", Name = "Component", Kind = "leaf",
                        Source = new() { Kind = "static", Status = staticStatus }
                    }]
                }]
            }
        };
        var live = ComponentChecks.Build(site, [], [new("mx", "queue", ComponentStatus.Outage, DateTimeOffset.UtcNow)]);
        var publication = new StatusMerger().Merge(site.Snapshot, live, null, [], []);
        Assert.All(Flatten(publication.Components), component => Assert.Equal("outage", component.Status));
    }

    [Theory]
    [InlineData("xi.sitewatch.portal-web")]
    [InlineData("xi.service.portal")]
    public void DeclaredImpactOverridesChecksAndSurvivesResolutionInComponentAndServiceHistory(string target)
    {
        var catalog = Catalogue();
        var site = catalog.Get("xi");
        var started = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2).AddHours(10), TimeSpan.Zero);
        var command = new IncidentCommand("Portal disruption", [target], Severity.Degraded,
            IncidentState.Investigating, "Investigating", true);
        var record = command.Apply(41, "xi", null, started);
        record = (command with { Severity = Severity.Outage, Message = "Outage confirmed" })
            .Apply(41, "xi", record, started.AddDays(1));
        var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
            _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
        var open = new StatusMerger().Merge(site.Snapshot, live, null, [record.ToPublicIncident()], []);
        var service = Assert.Single(open.Components, component => component.Id == "xi.service.portal");
        Assert.Equal("outage", service.Status);
        Assert.Contains(41, service.OpenIncidentIds!);
        record = (command with { State = IncidentState.Resolved, Message = "Recovered" })
            .Apply(41, "xi", record, started.AddDays(1).AddHours(1));
        var history = new HistoryDocument();
        foreach (var component in StatusCatalog.Flatten(site.Components.Components).Where(component => component.Kind == "leaf"))
            history.ComponentsById[component.Id] = new ComponentHistoryRecord
            {
                Days = Enumerable.Range(0, 3).Select(day => new HistoryDay
                {
                    Date = DateOnly.FromDateTime(started.UtcDateTime).AddDays(day),
                    Status = ComponentStatus.Operational,
                    Total = 100,
                    Failed = 0,
                    Uptime = 1
                }).ToList()
            };
        var closed = new StatusMerger().Merge(site.Snapshot, live, history, [record.ToPublicIncident()], []);
        closed = IncidentImpact.Apply(PublicationWorker.CorrectTodayHistory(closed, history), [record.ToPublicIncident()]);
        foreach (var component in Flatten(closed.Components).Where(component => component.Id == target || component.Id == "xi.service.portal"))
        {
            Assert.Equal("operational", component.Status);
            Assert.Empty(component.OpenIncidentIds!);
            Assert.Equal("degraded", component.History.Single(day => day.Date == DateOnly.FromDateTime(started.UtcDateTime)).Status);
            Assert.Equal("outage", component.History.Single(day => day.Date == DateOnly.FromDateTime(started.UtcDateTime).AddDays(1)).Status);
            Assert.Equal("operational", component.History[^1].Status);
            Assert.Equal(1, component.UptimeRatio);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitHubResolutionBeforeMidnightDoesNotExtendImpactIntoThePollingDay(bool lateBaseline)
    {
        var site = Catalogue().Get("mx");
        var midnight = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var poll = midnight.AddMinutes(5);
        var target = "mx.sitewatch.geo-location-web";
        var command = new IncidentCommand("Resolved GitHub incident", [target], Severity.Outage,
            IncidentState.Investigating, "Approved update", true);
        var opened = command.Apply(73, "mx", null, midnight.AddMinutes(-10));
        if (lateBaseline)
        {
            opened = opened with { ImpactHistory = [] };
            opened = opened.ObserveImpact(opened, poll);
        }
        var resolvedAt = midnight.AddMinutes(-5);
        var closed = (opened with { State = IncidentState.Resolved, ResolvedAt = resolvedAt }).ObserveImpact(opened, poll);
        Assert.Equal(lateBaseline ? poll : resolvedAt, closed.ImpactHistory[^1].At);
        var history = new HistoryDocument();
        foreach (var component in StatusCatalog.Flatten(site.Components.Components).Where(component => component.Kind == "leaf"))
            history.ComponentsById[component.Id] = new ComponentHistoryRecord
            {
                Days = Enumerable.Range(-1, 2).Select(day => new HistoryDay
                {
                    Date = DateOnly.FromDateTime(midnight.UtcDateTime).AddDays(day),
                    Status = ComponentStatus.Operational,
                    Total = 100,
                    Failed = 0,
                    Uptime = 1
                }).ToList()
            };
        var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
            _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
        var publication = new StatusMerger().Merge(site.Snapshot, live, history, [closed.ToPublicIncident()], []);
        publication = IncidentImpact.Apply(PublicationWorker.CorrectTodayHistory(publication, history), [closed.ToPublicIncident()]);
        var geo = Flatten(publication.Components).Single(component => component.Id == target);
        Assert.Equal(lateBaseline ? "operational" : "outage",
            geo.History.Single(day => day.Date == DateOnly.FromDateTime(midnight.UtcDateTime).AddDays(-1)).Status);
        Assert.Equal("operational", geo.History.Single(day => day.Date == DateOnly.FromDateTime(midnight.UtcDateTime)).Status);
        var reopened = command.Apply(73, "mx", closed, poll.AddMinutes(1));
        var closedAgain = (reopened with { State = IncidentState.Resolved, ResolvedAt = poll.AddMinutes(2) })
            .ObserveImpact(reopened, poll.AddMinutes(3));
        Assert.Equal(poll.AddMinutes(2), closedAgain.ImpactHistory[^1].At);
        Assert.Equal(closedAgain.ImpactHistory.OrderBy(observation => observation.At), closedAgain.ImpactHistory);
    }

    [Theory]
    [InlineData(Severity.Outage)]
    [InlineData(Severity.Maintenance)]
    public void ResolvedLegacyImpactRemainsOnItsOriginalComponentsAfterSyncEditsAndReopening(Severity severity)
    {
        var site = Catalogue().Get("mx");
        var started = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(10), TimeSpan.Zero);
        var legacy = new IncidentRecord
        {
            Id = 74,
            SiteId = "mx",
            Published = true,
            Title = "Resolved legacy incident",
            Components = ["mx.sitewatch.geo-location-web"],
            Severity = severity,
            State = IncidentState.Resolved,
            StartedAt = started,
            ResolvedAt = started.AddHours(1),
            ScheduledStart = severity == Severity.Maintenance ? started : null,
            ScheduledEnd = severity == Severity.Maintenance ? started.AddHours(2) : null,
            PublicUpdates =
            [
                new() { At = started, State = IncidentState.Investigating, Severity = severity, Body = "Impact declared" },
                new() { At = started.AddHours(1), State = IncidentState.Resolved, Severity = severity, Body = "Recovered" }
            ]
        };
        var synchronized = legacy.ObserveImpact(legacy, DateTimeOffset.UtcNow);
        var edited = new IncidentCommand(legacy.Title, legacy.Components, Severity.Degraded,
            IncidentState.Resolved, "Resolution clarified", true)
            .Apply(legacy.Id, legacy.SiteId, synchronized, DateTimeOffset.UtcNow);
        var scopeChange = new IncidentCommand(legacy.Title, ["mx.sitewatch.molyneux-me"], Severity.Degraded,
            IncidentState.Resolved, "Scope corrected", true);
        var moved = scopeChange.Apply(legacy.Id, legacy.SiteId, legacy, DateTimeOffset.UtcNow);
        var reopened = (scopeChange with { State = IncidentState.Investigating, Message = "New disruption" })
            .Apply(legacy.Id, legacy.SiteId, legacy, DateTimeOffset.UtcNow);
        var unrecorded = legacy with { PublicUpdates = legacy.PublicUpdates.Select(update => update with { Severity = null }).ToArray() };
        if (severity != Severity.Maintenance)
            Assert.Empty(unrecorded.ObserveImpact(unrecorded, DateTimeOffset.UtcNow).ImpactHistory);
        var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
            _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
        foreach (var record in new[] { synchronized, edited, moved, reopened })
        {
            var publication = new StatusMerger().Merge(site.Snapshot, live, null, [record.ToPublicIncident()], []);
            foreach (var component in Flatten(publication.Components).Where(component =>
                component.Id is "mx.sitewatch.geo-location-web" or "mx.service.geo-location"))
            {
                Assert.Equal("operational", component.Status);
                var day = component.History.Single(day => day.Date == DateOnly.FromDateTime(started.UtcDateTime));
                Assert.Equal(severity.ToApiString(), day.Status);
                Assert.Contains(legacy.Id, day.IncidentIds!);
            }
            var unrelated = Flatten(publication.Components).Single(component => component.Id == "mx.sitewatch.molyneux-me")
                .History.Single(day => day.Date == DateOnly.FromDateTime(started.UtcDateTime));
            Assert.DoesNotContain(legacy.Id, unrelated.IncidentIds!);
        }
    }

    [Fact]
    public void ObservedLegacyImpactIsNotBackdatedAndRemainsAfterResolutionAndScopeChanges()
    {
        var catalog = Catalogue();
        var observed = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(10), TimeSpan.Zero);
        var old = new IncidentRecord
        {
            Id = 72,
            SiteId = "mx",
            Published = true,
            Title = "Legacy incident",
            Components = ["mx.sitewatch.geo-location-web"],
            Severity = Severity.Outage,
            State = IncidentState.Identified,
            StartedAt = observed.AddDays(-5),
            PublicUpdates = [new() { At = observed.AddDays(-5), State = IncidentState.Identified, Body = "Old approved message" }]
        };
        var known = old.ObserveImpact(old, observed);
        Assert.Equal(observed, Assert.Single(known.ImpactHistory).At);
        Assert.Single(known.PublicUpdates);
        var changed = new IncidentCommand("Another service", ["mx.sitewatch.molyneux-me"], Severity.Degraded,
            IncidentState.Monitoring, "Shared dependency has recovered", true).Apply(72, "mx", known, observed.AddHours(1));
        var projected = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [changed]));
        Assert.Equal(IncidentState.Resolved, projected.State);
        var site = catalog.Get("xi");
        var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
            _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
        var publication = new StatusMerger().Merge(site.Snapshot, live, null, [projected.ToPublicIncident()], []);
        var geo = Flatten(publication.Components).Single(component => component.Id == "xi.sitewatch.geo-location-web");
        Assert.Equal("operational", geo.Status);
        Assert.Equal("outage", geo.History.Single(day => day.Date == DateOnly.FromDateTime(observed.UtcDateTime)).Status);
        Assert.Equal("unknown", geo.History.Single(day => day.Date == DateOnly.FromDateTime(observed.UtcDateTime.AddDays(-1))).Status);
    }

    [Fact]
    public void SharedIssueProjectionRetainsEachBrandsIndependentlyScopedHistoricalImpact()
    {
        var catalog = Catalogue();
        var started = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2).AddHours(10), TimeSpan.Zero);
        var command = new IncidentCommand("Shared source", ["mx.sitewatch.geo-location-api"], Severity.Outage,
            IncidentState.Investigating, "Impact declared", true);
        var mx = command.Apply(62, "mx", null, started);
        mx = (command with { State = IncidentState.Resolved }).Apply(62, "mx", mx, started.AddHours(2));
        command = command with { Components = ["xi.sitewatch.geo-location-web"] };
        var xi = command.Apply(63, "xi", null, started.AddDays(1));
        xi = (command with { State = IncidentState.Resolved }).Apply(63, "xi", xi, started.AddDays(1).AddHours(2));
        const string url = "https://github.com/frasermolyneux/status-pages/issues/28";
        var site = catalog.Get("mx");
        var projected = Assert.Single(catalog.ProjectIncidents(site, [mx with { SourceUrl = url }, xi with { SourceUrl = url }]));
        var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
            _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
        var publication = new StatusMerger().Merge(site.Snapshot, live, null, [projected.ToPublicIncident()], []);
        var components = Flatten(publication.Components).ToDictionary(component => component.Id);
        var first = DateOnly.FromDateTime(started.UtcDateTime);
        Assert.Equal("outage", components["mx.sitewatch.geo-location-api"].History.Single(day => day.Date == first).Status);
        Assert.Equal("unknown", components["mx.sitewatch.geo-location-api"].History.Single(day => day.Date == first.AddDays(1)).Status);
        Assert.Equal("unknown", components["mx.sitewatch.geo-location-web"].History.Single(day => day.Date == first).Status);
        Assert.Equal("outage", components["mx.sitewatch.geo-location-web"].History.Single(day => day.Date == first.AddDays(1)).Status);
        foreach (var date in new[] { first, first.AddDays(1) })
            Assert.Equal("outage", components["mx.service.geo-location"].History.Single(day => day.Date == date).Status);
    }

    [Fact]
    public void SharedHistoricalScopesDoNotReactivateComponentsAfterBothBrandsMoveTheIncidentElsewhere()
    {
        var catalog = Catalogue();
        var started = DateTimeOffset.UtcNow.AddDays(-1);
        var command = new IncidentCommand("Shared incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Shared impact", true);
        var mx = command.Apply(70, "mx", null, started);
        mx = (command with { Components = ["mx.sitewatch.molyneux-me"] }).Apply(70, "mx", mx, started.AddHours(1));
        command = command with { Components = ["xi.sitewatch.geo-location-web"] };
        var xi = command.Apply(71, "xi", null, started);
        xi = (command with { Components = ["xi.sitewatch.server-events"] }).Apply(71, "xi", xi, started.AddHours(1));
        const string source = "https://github.com/frasermolyneux/status-pages/issues/45";
        foreach (var siteId in new[] { "mx", "xi" })
        {
            var site = catalog.Get(siteId);
            var geoId = $"{siteId}.sitewatch.geo-location-web";
            var currentId = siteId == "mx" ? "mx.sitewatch.molyneux-me" : "xi.sitewatch.server-events";
            var projected = Assert.Single(catalog.ProjectIncidents(site, [mx with { SourceUrl = source }, xi with { SourceUrl = source }]));
            Assert.Equal([currentId], projected.Components);
            var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key,
                _ => new ComponentLiveTelemetry(1, 0, DateTimeOffset.UtcNow, null));
            var publication = new StatusMerger().Merge(site.Snapshot, live, null, [projected.ToPublicIncident()], []);
            var components = Flatten(publication.Components).ToDictionary(component => component.Id);
            Assert.Equal("outage", components[currentId].Status);
            Assert.Equal("operational", components[geoId].Status);
            Assert.Empty(components[geoId].OpenIncidentIds!);
            Assert.Equal("outage", components[geoId].History.Single(day => day.Date == DateOnly.FromDateTime(started.UtcDateTime)).Status);
        }
    }

    [Fact]
    public void MultipleChecksAggregateWithoutHidingMissingSignalsAndShareGeoLocationAcrossBrandHierarchy()
    {
        var catalog = Catalogue();
        var mx = catalog.Get("mx") with
        {
            Probes = [],
            Checks =
            [
                new() { Id = "http", Name = "HTTP", Component = "mx.sitewatch.geo-location-web", Target = "https://example.com/" },
                new() { Id = "queue", Name = "Notification delay", Component = "mx.sitewatch.geo-location-web", Kind = "telemetry" }
            ]
        };
        var now = DateTimeOffset.UtcNow;
        var observation = new ProbeObservation("mx", "mx.sitewatch.geo-location-web", "GeoLocation Website",
            "https://example.com/", "publisher", now, true, 200, 10, null, "http");
        var missing = ComponentChecks.Build(mx, [observation], []);
        Assert.Contains(missing.Values.Single().Checks, check => check.Id == "mx/queue" && check.Status == ComponentStatus.Unknown);
        Assert.Equal(0, missing.Values.Single().Samples);
        var failing = ComponentChecks.Build(mx, [observation with { Success = false }], []);
        Assert.Equal(1, failing.Values.Single().Samples);
        Assert.Equal(1, failing.Values.Single().Failures);
        var signal = new CheckResult("mx", "queue", ComponentStatus.Degraded, now.AddSeconds(-15));
        var telemetry = ComponentChecks.Build(mx, [observation], [signal]);
        var localPublication = new StatusMerger().Merge(mx.Snapshot, telemetry, null, [], []);
        Assert.Equal(signal.ObservedAt, Flatten(localPublication.Components)
            .Single(component => component.Id == observation.Component).LastSampleAt);
        Assert.True(HealthDiagnostics.Build(mx, localPublication, [observation], [signal])
            .Single(component => component.Id == observation.Component).MatchesPublication);
        Assert.False(HealthDiagnostics.Build(mx, localPublication, [observation with { ObservedAt = now.AddSeconds(1) }], [signal])
            .Single(component => component.Id == observation.Component).MatchesPublication);
        Assert.False(HealthDiagnostics.Build(mx, localPublication, [observation], [signal with { ObservedAt = now }])
            .Single(component => component.Id == observation.Component).MatchesPublication);
        var all = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>
        {
            ["mx"] = telemetry,
            ["xi"] = []
        };
        var xi = catalog.Get("xi");
        var projected = catalog.ProjectTelemetry(xi, all);
        var publication = new StatusMerger().Merge(xi.Snapshot, projected, null, [], []);
        Assert.Equal("degraded", publication.Components.Single(component => component.Id == "xi.service.portal").Status);
        var geo = Flatten(publication.Components).Single(component => component.Id == "xi.sitewatch.geo-location-web");
        Assert.Equal(2, geo.Checks!.Count);
        Assert.Contains(geo.Checks, check => check.Id == "mx/queue" && check.Status == "degraded");
        var xiChecks = xi with
        {
            Checks = [new() { Id = "queue", Name = "Another queue", Component = "xi.sitewatch.geo-location-web", Kind = "telemetry" }]
        };
        all["xi"] = ComponentChecks.Build(xiChecks, [], [new("xi", "queue", ComponentStatus.Operational, now.AddSeconds(1))]);
        var independentChecks = catalog.ProjectTelemetry(xi, all)["xi.sitewatch.geo-location-web"].Checks;
        Assert.Contains(independentChecks, check => check.Id == "mx/queue" && check.Status == ComponentStatus.Degraded);
        Assert.Contains(independentChecks, check => check.Id == "xi/queue" && check.Status == ComponentStatus.Operational);
        var stale = ComponentChecks.Build(mx, [observation],
            [new("mx", "queue", ComponentStatus.Operational, now.AddMinutes(-10))]);
        Assert.Contains(stale.Values.Single().Checks, check => check.Id == "mx/queue" && check.Status == ComponentStatus.Unknown);
    }

    [Fact]
    public void SharedTelemetryDiagnosticsMatchPublicationWithoutSharingPrivateDetails()
    {
        var catalog = Catalogue();
        var now = DateTimeOffset.UtcNow;
        const string mxId = "mx.sitewatch.geo-location-web";
        const string xiId = "xi.sitewatch.geo-location-web";
        var mx = catalog.Get("mx");
        var xi = catalog.Get("xi");
        var http = mx.EffectiveChecks.Single(check => check.Id == mxId);
        var shared = new CheckDefinition
        {
            Id = "queue",
            SharedId = "geo-location.queue",
            Name = "Notification delay",
            Component = mxId,
            Kind = "telemetry"
        };
        mx = mx with { Checks = [http, shared] };
        xi = xi with
        {
            Checks = [xi.EffectiveChecks.Single(check => check.Id == xiId), shared with { Component = xiId }]
        };
        var probe = new ProbeObservation("mx", mxId, "GeoLocation Website", http.Target!, "publisher",
            now, true, 200, 10, null, http.Id);
        var ownProbe = probe with { Site = "xi", Component = xiId, CheckId = xiId };
        var signal = new CheckResult("mx", shared.Id, ComponentStatus.Degraded, now, "MX_PRIVATE_DETAIL");
        var all = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>
        {
            ["mx"] = ComponentChecks.Build(mx, [probe], [signal]),
            ["xi"] = ComponentChecks.Build(xi, [ownProbe], [])
        };
        var expected = catalog.ProjectTelemetry(xi, all);
        var publication = new StatusMerger().Merge(xi.Snapshot, expected, null, [], []);
        var diagnostics = HealthDiagnostics.Build(xi, publication, [ownProbe], [signal], expected);
        var geo = diagnostics.Single(component => component.Id == xiId);
        Assert.True(geo.MatchesPublication);
        Assert.Equal("degraded", geo.Checks!.Single(check => check.Id == shared.SharedId).Status);
        Assert.DoesNotContain("MX_PRIVATE_DETAIL", JsonSerializer.Serialize(diagnostics, StatusJson.Options));
        all["mx"] = ComponentChecks.Build(mx, [probe], [signal with { ObservedAt = now.AddSeconds(1) }]);
        Assert.False(HealthDiagnostics.Build(xi, publication, [ownProbe], [signal], catalog.ProjectTelemetry(xi, all))
            .Single(component => component.Id == xiId).MatchesPublication);
    }

    [Theory]
    [InlineData(Severity.Outage, "outage")]
    [InlineData(Severity.Degraded, "degraded")]
    [InlineData(Severity.Maintenance, "maintenance")]
    public void SharedIncidentsOverrideHealthyChecksOnBothBrandsWithoutSharingPrivateDrafts(Severity severity, string expected)
    {
        var catalog = Catalogue();
        var now = DateTimeOffset.UtcNow;
        var command = new IncidentCommand("GeoLocation disruption", ["mx.sitewatch.geo-location-web"], severity,
            IncidentState.Identified, "Public update", true,
            ScheduledStart: severity == Severity.Maintenance ? now.AddMinutes(-1) : null,
            ScheduledEnd: severity == Severity.Maintenance ? now.AddMinutes(10) : null);
        var source = command.Apply(27, "mx", null, now);
        foreach (var site in catalog.Sites)
        {
            var projected = Assert.Single(catalog.ProjectIncidents(site, [source]));
            Assert.Equal([$"{site.Site.Id}.sitewatch.geo-location-web"], projected.Components);
            var live = site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key, _ => new ComponentLiveTelemetry(1, 0, now, null));
            var result = new StatusMerger().Merge(site.Snapshot, live, null, [projected.ToPublicIncident()],
                severity == Severity.Maintenance ? [projected.ToMaintenance(now)] : []);
            var component = Assert.Single(Flatten(result.Components), component => component.Id == projected.Components[0]);
            Assert.Equal(expected, component.Status);
            Assert.Contains(27, component.OpenIncidentIds!);
            Assert.Contains("GeoLocation Website", result.Summary.Message);
        }
        Assert.Empty(catalog.ProjectIncidents(catalog.Get("xi"), [source with { Published = false }]));
        Assert.Empty(catalog.ProjectIncidents(catalog.Get("xi"), [source with { Components = ["mx.sitewatch.bishopsbees"] }]));
        var reverse = source with { SiteId = "xi", Components = ["xi.sitewatch.geo-location-web"] };
        Assert.Equal(["mx.sitewatch.geo-location-web"], Assert.Single(catalog.ProjectIncidents(catalog.Get("mx"), [reverse])).Components);
    }

    [Fact]
    public void SharedSourceIncidentsAreDeduplicatedAndDoNotExposePrivateUpdates()
    {
        var catalog = Catalogue();
        var first = new IncidentCommand("Shared incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Approved description", true).Apply(30, "mx", null, Created);
        first = first with
        {
            SourceUrl = "https://github.com/frasermolyneux/status-pages/issues/42",
            Updates = [new() { At = Created, Body = "PRIVATE_DRAFT" }],
            Evidence = new("source", "private", Created, "PRIVATE_CONTEXT")
        };
        var projected = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first]));
        Assert.Null(projected.Evidence);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(projected, StatusJson.Options));
        var local = first with { Id = 31, SiteId = "xi", Components = ["xi.sitewatch.geo-location-web"] };
        var preferred = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first, local]));
        Assert.Equal(31, preferred.Id);
        Assert.Contains(30, preferred.PublicAliases);
        var publication = new StatusMerger().Merge(catalog.Get("xi").Snapshot, new Dictionary<string, ComponentLiveTelemetry>(),
            null, [preferred.ToPublicIncident()], []);
        Assert.Contains(30, Assert.Single(publication.Incidents).Aliases!);
        Assert.True(Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first, local with { Published = false }])).Published);
        var apiOnly = first with { Components = ["mx.sitewatch.geo-location-api"] };
        var combined = Assert.Single(catalog.ProjectIncidents(catalog.Get("mx"), [apiOnly, local]));
        Assert.Contains("mx.sitewatch.geo-location-api", combined.Components);
        Assert.Contains("mx.sitewatch.geo-location-web", combined.Components);
    }

    [Fact]
    public void IncidentHistoryRetainsEachPublishedStateImpactAndDescription()
    {
        var command = new IncidentCommand("Incident", ["mx.sitewatch.geo-location-web"], Severity.Degraded,
            IncidentState.Investigating, "Investigating", true);
        var first = command.Apply(3, "mx", null, Created);
        var second = (command with { State = IncidentState.Identified, Severity = Severity.Outage, Message = "Impact confirmed" })
            .Apply(3, "mx", first, Created.AddMinutes(1));
        Assert.Equal([Severity.Degraded, Severity.Outage], second.PublicUpdates.Select(update => update.Severity));
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified], second.PublicUpdates.Select(update => update.State));
        Assert.Equal(["Investigating", "Impact confirmed"], second.PublicUpdates.Select(update => update.Body));
    }

    [Fact]
    public void UnknownGitHubSiteLabelsRequireReviewInsteadOfBlockingTheDeliveryWorker()
    {
        var command = new IncidentCommand("Incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Approved update", true);
        Assert.Throws<GitHubIncidentConflictException>(() =>
            GitHubIncidentDocument.Labels(Catalogue(), "mx", command, ["site:typo"]));
    }

    [Fact]
    public void MixedCaseGitHubPublicationLabelsDoNotAcknowledgeAnUndeliveredWithdrawal()
    {
        var command = new IncidentCommand("Withdrawal", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Private withdrawal", false, Delivery: "github", OperationId: Guid.NewGuid());
        var local = command.Apply(19, "mx", null, Created);
        using var api = new GitHubWriteFixture();
        var client = new GitHubIncidentClient(new(), Catalogue(), api);
        var snapshot = new GitHubIssueSnapshot(Issue(["Status:Incident", "Status:Published", "Site:MX", "Severity:Outage"]), []);
        Assert.False(client.Matches(new(command.OperationId!.Value, local, command, null, false, "pending"), snapshot));
    }

    [Theory]
    [InlineData("State: database unavailable")]
    [InlineData("State: resolved\nThis is descriptive text, not a state change")]
    [InlineData("### Public update\n## Keep the approved formatting")]
    public void GeneratedGitHubUpdatesPreserveApprovedTextWithoutLegacyHeaderParsing(string message)
    {
        var catalog = Catalogue();
        var command = new IncidentCommand("Generated incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Identified, message, true, Delivery: "github", OperationId: Guid.NewGuid());
        var local = command.Apply(19, "mx", null, Created);
        var body = GitHubIncidentDocument.Body(new(command.OperationId!.Value, local, command, null, false, "pending"));
        var imported = Assert.Single(GitHubIssueParser.Parse(Issue(body: body), [], catalog, new(), [local]));
        Assert.Equal(IncidentState.Identified, imported.State);
        Assert.Equal(local.PublicUpdates, imported.PublicUpdates);
    }

    [Fact]
    public void ResolvingThroughTheWorkspaceDoesNotPreventSubsequentGitHubReopening()
    {
        var catalog = Catalogue();
        var command = new IncidentCommand("Generated incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Resolved, "Service recovered", true, Delivery: "github", OperationId: Guid.NewGuid());
        var local = command.Apply(19, "mx", null, Created);
        var labels = GitHubIncidentDocument.Labels(catalog, "mx", command, ["state:monitoring"]);
        Assert.DoesNotContain(labels, label => label.StartsWith("state:", StringComparison.Ordinal));
        var body = GitHubIncidentDocument.Body(new(command.OperationId!.Value, local, command, null, false, "pending"));
        var reopened = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: body, updatedMinutes: 10), [], catalog, new(), [local]));
        Assert.Equal(IncidentState.Investigating, reopened.State);
        Assert.Null(reopened.ResolvedAt);
        Assert.Equal(IncidentState.Investigating, reopened.PublicUpdates[^1].State);
    }

    [Fact]
    public void GeneratedGitHubUpdatesRoundTripOnceAndRetainEditsAndExplicitRestoration()
    {
        var catalog = Catalogue();
        var command = new IncidentCommand("Generated incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Approved update", true, Delivery: "github", OperationId: Guid.NewGuid());
        var local = command.Apply(19, "mx", null, Created);
        var job = new GitHubJob(command.OperationId!.Value, local, command, null, false, "pending");
        var body = GitHubIncidentDocument.Body(job);
        var issue = Issue(body: body);
        var imported = Assert.Single(GitHubIssueParser.Parse(issue, [], catalog, new(), [local]));
        Assert.Single(imported.PublicUpdates);
        var editedBody = body.Replace("Approved update", "Edited in GitHub", StringComparison.Ordinal);
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(body: editedBody, updatedMinutes: 10),
            [], catalog, new(), [imported]));
        Assert.Equal("Edited in GitHub", edited.PublicUpdates[^1].Body);
        Assert.Equal(Created.AddMinutes(10), edited.PublicUpdates[^1].At);
        var repeated = Assert.Single(GitHubIssueParser.Parse(Issue(body: editedBody, updatedMinutes: 15),
            [], catalog, new(), [edited]));
        Assert.Equal(edited.PublicUpdates, repeated.PublicUpdates);
        var restoredComment = JsonSerializer.SerializeToElement(new
        {
            body,
            created_at = Created.AddMinutes(20),
            user = new { login = "frasermolyneux" }
        });
        var restored = Assert.Single(GitHubIssueParser.Parse(Issue(body: editedBody, updatedMinutes: 20),
            [restoredComment], catalog, new(), [repeated]));
        Assert.Equal("Approved update", restored.PublicUpdates[^1].Body);
        Assert.Equal(Created.AddMinutes(20), restored.PublicUpdates[^1].At);
        var stable = Assert.Single(GitHubIssueParser.Parse(Issue(body: editedBody, updatedMinutes: 25),
            [restoredComment], catalog, new(), [restored]));
        Assert.Equal(restored.PublicUpdates, stable.PublicUpdates);
        using var api = new GitHubWriteFixture();
        var client = new GitHubIncidentClient(new(), catalog, api);
        Assert.False(client.ContainsUpdate(new(Issue(body: editedBody), []), job));
        Assert.True(client.ContainsUpdate(new(issue, []), job));
        Assert.True(client.ContainsUpdate(new(Issue(body: editedBody), [restoredComment]), job));
        var editedComment = JsonSerializer.SerializeToElement(new
        {
            body = editedBody,
            created_at = Created.AddMinutes(25),
            user = new { login = "frasermolyneux" }
        });
        Assert.False(client.ContainsUpdate(new(issue, [editedComment]), job));
    }

    [Fact]
    public void GitHubDescriptionAndSeverityEditsRemainInHistoryWithoutRepeatingOnEverySync()
    {
        var catalog = Catalogue();
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(body: "Original description"), [], catalog, new()));
        Assert.Null(first.PublicUpdates[0].Severity);
        var labels = new[] { "status:incident", "status:published", "site:mx", "workload:geo-location", "severity:degraded" };
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Revised description", updatedMinutes: 10),
            [], catalog, new(), [first]));
        Assert.Contains(edited.PublicUpdates, update => update.Body == "Original description");
        Assert.Contains(edited.PublicUpdates, update => update.Body == "Revised description" && update.At == Created.AddMinutes(10));
        Assert.Contains(edited.PublicUpdates, update => update.Severity == Severity.Degraded);
        var repeated = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Revised description", updatedMinutes: 15, title: "Renamed"),
            [], catalog, new(), [edited]));
        Assert.Equal(edited.PublicUpdates, repeated.PublicUpdates);
        Assert.Equal(first.SourceUrl, repeated.ToPublicIncident().SourceUrl);
        var reverted = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Original description", updatedMinutes: 20),
            [], catalog, new(), [repeated]));
        Assert.Equal("Original description", reverted.PublicUpdates[^1].Body);
        Assert.Equal(Created.AddMinutes(20), reverted.PublicUpdates[^1].At);
        var identifiedLabels = labels.Append("state:identified").ToArray();
        var identified = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels, body: "Original description", updatedMinutes: 25),
            [], catalog, new(), [reverted]));
        var editedAfterIdentification = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels, body: "Identified cause", updatedMinutes: 30),
            [], catalog, new(), [identified]));
        Assert.Equal(IncidentState.Identified, editedAfterIdentification.PublicUpdates[^1].State);
        Assert.Equal("Identified cause", editedAfterIdentification.PublicUpdates[^1].Body);
    }

    [Fact]
    public void GitHubPublicHistoryNeverIncludesSupersededDraftsOrUnapprovedStateTransitions()
    {
        var catalog = Catalogue();
        string[] draftLabels = ["status:incident", "site:mx", "workload:geo-location", "severity:outage", "state:identified"];
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(draftLabels, body: "PRIVATE_FIRST"), [], catalog, new()));
        var draft = Assert.Single(GitHubIssueParser.Parse(Issue(draftLabels, body: "PRIVATE_REPLACEMENT", updatedMinutes: 10),
            [], catalog, new(), [first]));
        var approvedLabels = draftLabels.Select(label => label == "state:identified" ? "state:monitoring" : label)
            .Append("status:published").ToArray();
        var approved = Assert.Single(GitHubIssueParser.Parse(Issue(approvedLabels, body: "Approved update", updatedMinutes: 15),
            [], catalog, new(), [draft]));
        var withdrawn = Assert.Single(GitHubIssueParser.Parse(Issue(draftLabels, body: "PRIVATE_WITHDRAWN", updatedMinutes: 20),
            [], catalog, new(), [approved]));
        var republished = Assert.Single(GitHubIssueParser.Parse(Issue(approvedLabels, body: "Approved correction", updatedMinutes: 25),
            [], catalog, new(), [withdrawn]));
        Assert.Contains(republished.Updates, update => update.Body == "PRIVATE_FIRST");
        Assert.Contains(republished.PublicUpdates, update => update.Body == "Approved update");
        Assert.Contains(republished.PublicUpdates, update => update.Body == "Approved correction");
        Assert.DoesNotContain(republished.PublicUpdates, update => update.State == IncidentState.Identified);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(republished.ToPublicIncident(), StatusJson.Options));
        var shared = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [republished]));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(shared, StatusJson.Options));
    }

    [Fact]
    public void AddingAGitHubSiteRetainsTheAlreadySharedApprovedHistoryWithoutCopyingPrivateDrafts()
    {
        var catalog = Catalogue();
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(body: "Original update"), [], catalog, new()));
        string[] labels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:degraded", "state:identified"];
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Corrected update", updatedMinutes: 10),
            [], catalog, new(), [first]));
        edited = edited with { Updates = [.. edited.Updates, new() { At = Created.AddMinutes(11), Body = "PRIVATE_DRAFT" }] };
        var both = GitHubIssueParser.Parse(Issue([.. labels, "site:xi"], body: "Corrected update", updatedMinutes: 15),
            [], catalog, new(), [edited]);
        var xi = Assert.Single(both, record => record.SiteId == "xi");
        Assert.Equal(edited.PublicUpdates, xi.PublicUpdates);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(xi, StatusJson.Options));
        Assert.Equal(xi.PublicUpdates, Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), both)).PublicUpdates);
    }

    [Fact]
    public void RestoringAGitHubSiteKeepsIntermediateApprovedUpdatesAndPreviouslyPublishedLinks()
    {
        var catalog = Catalogue();
        string[] bothLabels = ["status:incident", "status:published", "site:mx", "site:xi", "workload:geo-location", "severity:outage"];
        var initial = GitHubIssueParser.Parse(Issue(bothLabels, body: "Original"), [], catalog, new());
        var mx = Assert.Single(initial, record => record.SiteId == "mx") with { Id = 30 };
        var withdrawn = Assert.Single(initial, record => record.SiteId == "xi") with { Id = 31, Published = false };
        var mxLabels = bothLabels.Where(label => label != "site:xi").ToArray();
        mx = Assert.Single(GitHubIssueParser.Parse(Issue(mxLabels, body: "Intermediate correction", updatedMinutes: 10),
            [], catalog, new(), [mx, withdrawn])) with
        { Id = 30 };
        mx = Assert.Single(GitHubIssueParser.Parse(Issue(mxLabels, body: "Latest correction", updatedMinutes: 15),
            [], catalog, new(), [mx, withdrawn])) with
        { Id = 30 };
        var restored = GitHubIssueParser.Parse(Issue(bothLabels, body: "Latest correction", updatedMinutes: 20),
            [], catalog, new(), [mx, withdrawn]);
        var xi = Assert.Single(restored, record => record.SiteId == "xi") with { Id = 31 };
        Assert.Equal(mx.PublicUpdates, xi.PublicUpdates);
        Assert.Contains(xi.PublicUpdates, update => update.Body == "Intermediate correction" && update.At == Created.AddMinutes(10));
        Assert.Contains(xi.PublicUpdates, update => update.Body == "Latest correction" && update.At == Created.AddMinutes(15));
        Assert.Contains(30, xi.PublicAliases);
        var projected = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [mx with { Published = false }, xi]));
        Assert.Contains(30, projected.ToPublicIncident().Aliases);
    }

    [Fact]
    public void GitHubUpdatesDiscoveredBeforeAnImpactChangeKeepTheirHistoricSeverityUnknown()
    {
        var catalog = Catalogue();
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(), [], catalog, new()));
        var comment = JsonSerializer.SerializeToElement(new
        {
            body = "An earlier update",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(6)
        });
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:degraded"], updatedMinutes: 10),
            [comment], catalog, new(), [first]));
        Assert.Null(Assert.Single(edited.PublicUpdates, update => update.Body == "An earlier update").Severity);
        Assert.Equal(Severity.Degraded, edited.PublicUpdates[^1].Severity);
        Assert.Equal(Created.AddMinutes(10), edited.PublicUpdates[^1].At);
    }

    [Fact]
    public void GitHubStateOnlyCommentEditsCreateOneObservedHistoryEntry()
    {
        var catalog = Catalogue();
        static JsonElement Comment(string state) => JsonSerializer.SerializeToElement(new
        {
            body = $"state: {state}\nSame description",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(1)
        });
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(), [Comment("investigating")], catalog, new()));
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(updatedMinutes: 10),
            [Comment("identified")], catalog, new(), [first]));
        Assert.Equal(IncidentState.Identified, edited.State);
        Assert.Equal(IncidentState.Identified, edited.PublicUpdates[^1].State);
        Assert.Equal(Created.AddMinutes(10), edited.PublicUpdates[^1].At);
        var repeated = Assert.Single(GitHubIssueParser.Parse(Issue(updatedMinutes: 15, title: "Renamed"),
            [Comment("identified")], catalog, new(), [edited]));
        Assert.Equal(edited.PublicUpdates, repeated.PublicUpdates);
    }

    [Fact]
    public void EditingAnEarlierStateHeaderDoesNotRewriteUnchangedLaterComments()
    {
        var catalog = Catalogue();
        static JsonElement Comment(string body, int minute) => JsonSerializer.SerializeToElement(new
        {
            body,
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(minute)
        });
        string[] labels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:monitoring"];
        var later = Comment("Unchanged later comment", 2);
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(labels),
            [Comment("state: investigating\nEarlier update", 1), later], catalog, new()));
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(labels, updatedMinutes: 10),
            [Comment("state: identified\nEarlier update", 1), later], catalog, new(), [first]));
        var unchanged = Assert.Single(edited.PublicUpdates, update => update.Body == "Unchanged later comment");
        Assert.Equal(Created.AddMinutes(2), unchanged.At);
        Assert.Equal(IncidentState.Investigating, unchanged.State);
        Assert.Equal(IncidentState.Monitoring, edited.PublicUpdates[^1].State);
        Assert.Equal(2, edited.PublicUpdates.Count(update => update.Body == "Earlier update"));
    }

    [Fact]
    public void PublicationUsesTheLatestObservationWithoutMakingPersistedEvidenceAppearFresh()
    {
        var site = Catalogue().Get("mx");
        const string id = "mx.sitewatch.geo-location-web";
        var first = new ProbeObservation("mx", id, "GeoLocation Website", site.EffectiveChecks.Single(check => check.Id == id).Target!, "publisher",
            Created, false, 503, 10, "HTTP 503");
        var latest = first with { ObservedAt = Created.AddMinutes(1), Success = true, HttpStatus = 200, Error = null };
        var result = PublicationWorker.LatestTelemetry(site, [latest, first,
            latest with { Site = "xi" }, latest with { Component = "removed-component" }]);
        var observation = Assert.Single(result).Value;
        Assert.Equal(latest.ObservedAt, observation.LastSeen);
        Assert.Equal(0, observation.Failures);
    }

    private static IEnumerable<ComponentDto> Flatten(IEnumerable<ComponentDto> components) =>
        components.SelectMany(component => new[] { component }.Concat(Flatten(component.Children ?? [])));

    [Fact]
    public void EvidencePromotionKeepsItsSourcePrivatelyAcrossEditsAndNeverAddsItToPublicUpdates()
    {
        var source = new IncidentEvidence("azure-alert", "PRIVATE_ALERT_ID", Created, "PRIVATE_MONITOR_DIAGNOSTICS");
        var command = new IncidentCommand("Website availability", ["mx.sitewatch.molyneux-me"], Severity.Degraded,
            IncidentState.Investigating, "We are investigating intermittent failures.", true, Evidence: source);
        Assert.Null(command.Validate(Catalogue().Get("mx")));
        var first = command.Apply(5, "mx", null, Created);
        var edited = (command with { Evidence = null, Revision = 1, State = IncidentState.Monitoring })
            .Apply(5, "mx", first, Created.AddMinutes(1));
        Assert.Equal(source, edited.Evidence);
        var publication = JsonSerializer.Serialize(edited.ToPublicIncident(), StatusJson.Options);
        Assert.DoesNotContain("PRIVATE_ALERT_ID", publication);
        Assert.DoesNotContain("PRIVATE_MONITOR_DIAGNOSTICS", publication);
        Assert.NotNull((command with { Evidence = source with { Source = "" } }).Validate(Catalogue().Get("mx")));
    }

    [Theory]
    [InlineData(200, true, "GET")]
    [InlineData(302, true, "GET")]
    [InlineData(503, false, "GET")]
    [InlineData(200, true, "HEAD")]
    [InlineData(302, true, "HEAD")]
    [InlineData(503, false, "HEAD")]
    public async Task IndependentChecksRetainTheHttpResultAndTargetForOperators(int status, bool success, string method)
    {
        using var client = new HttpClient(new ProbeHandler((request, _) =>
        {
            Assert.Equal(method, request.Method.Method);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        var result = await IndependentProbe.ObserveAsync(client, "xi", "xi.sitewatch.forums-web",
            "Community Forums", "https://www.xtremeidiots.com/", default, method);
        Assert.Equal(success, result.Success);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal("https://www.xtremeidiots.com/", result.Target);
        Assert.Equal("xi.sitewatch.forums-web", result.Component);
        Assert.Equal(method, result.Method);
        if (success) Assert.Null(result.Error);
        else Assert.Contains("HTTP 503", result.Error);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("head")]
    public void CatalogRejectsUnsupportedHttpMethods(string method)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Catalogue().Sites.Select(site => site with
            {
                Checks = site.Checks.Select(check => check with { Method = method }).ToList()
            }), StatusJson.Options));
            Assert.Throws<InvalidOperationException>(() => new StatusCatalog(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ProbeNetworkErrorsStayInspectableAndShutdownIsNotRecordedAsAnOutage()
    {
        using var client = new HttpClient(new ProbeHandler((_, _) =>
            throw new HttpRequestException("Name resolution failed.")));
        var result = await IndependentProbe.ObserveAsync(client, "mx", "web", "Website", "https://example.test/", default);
        Assert.False(result.Success);
        Assert.Null(result.HttpStatus);
        Assert.Equal("Name resolution failed.", result.Error);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndependentProbe.ObserveAsync(client, "mx", "web", "Website", "https://example.test/", canceled.Token));
    }

    [Fact]
    public void HealthDiagnosticsSeparateTheProbeFromIncidentOverridesAndKeepBrandsIsolated()
    {
        const string id = "xi.sitewatch.forums-web";
        var probe = new ProbeObservation("xi", id, "Forums", "https://example.test/", "publisher",
            Created, true, 200, 10, null);
        var publication = new StatusApiResponse
        {
            Components = [new ComponentDto { Id = id, Status = "degraded", LastSampleAt = Created, OpenIncidentIds = [7] }],
            Incidents = [new IncidentDto { Id = 7, Title = "Investigating intermittent failures", Severity = "degraded" }]
        };
        var checks = HealthDiagnostics.Build(Catalogue().Get("xi"), publication,
            [probe, probe with { Site = "mx", Component = "mx.sitewatch.molyneux-me" }]);
        var forums = Assert.Single(checks, check => check.Id == id);
        Assert.Equal("degraded", forums.Status);
        Assert.True(forums.Probe!.Success);
        Assert.True(forums.MatchesPublication);
        Assert.Equal(7, Assert.Single(forums.Incidents).Id);
        Assert.DoesNotContain(checks, check => check.Id.StartsWith("mx.", StringComparison.Ordinal));
        Assert.DoesNotContain(checks, check => check.Id == "xi.server-agent");
        var failure = probe with { Success = false, HttpStatus = 503, Error = "HTTP 503", ObservedAt = Created.AddMinutes(-1) };
        var nextCycle = HealthDiagnostics.Build(Catalogue().Get("xi"), publication,
            [failure, probe with { ObservedAt = Created.AddMinutes(1) }]);
        var recovered = Assert.Single(nextCycle, check => check.Id == id);
        Assert.False(recovered.MatchesPublication);
        Assert.True(recovered.Probe!.Success);
        Assert.Equal(failure, recovered.LastFailure);
    }

    [Fact]
    public void GeoLocationAndTheServerPipelineHaveApprovedBrandSpecificWorkloadMappings()
    {
        var both = GitHubIssueParser.Parse(Issue([
            "status:incident", "status:published", "site:mx", "site:xi", "workload:geo-location", "severity:outage"
        ]), [], Catalogue(), new());
        Assert.Equal(2, both.Count);
        Assert.Equal(["xi.sitewatch.geo-location-web"], Assert.Single(both, record => record.SiteId == "xi").Components);
        var xi = Catalogue().Get("xi");
        foreach (var workload in new[] { "portal-servers-integration", "portal-server-events" })
        {
            var record = Assert.Single(GitHubIssueParser.Parse(Issue([
                "status:incident", "status:published", "site:xi", $"workload:{workload}", "severity:degraded"
            ]), [], Catalogue(), new()));
            Assert.Equal(xi.Workloads[workload], record.Components);
            Assert.All(record.Components, component => Assert.StartsWith("xi.", component));
        }
    }

    [Theory]
    [InlineData("scheduled", "operational", 0)]
    [InlineData("in-progress", "maintenance", 1)]
    [InlineData("completed", "operational", 0)]
    public void MaintenanceIsExplainedOnlyWhileItActuallyOverridesAvailability(string phase, string status, int overrides)
    {
        const string id = "mx.sitewatch.molyneux-me";
        var publication = new StatusApiResponse
        {
            Components = [new ComponentDto { Id = id, Status = status, OpenIncidentIds = [99] }],
            Incidents = [new IncidentDto { Id = 99, Severity = "maintenance", Components = [id] }],
            Maintenance = [new MaintenanceDto { Id = 99, Components = [id], State = phase }]
        };
        var health = Assert.Single(HealthDiagnostics.Build(Catalogue().Get("mx"), publication, []), check => check.Id == id);
        Assert.Empty(health.Incidents);
        Assert.Equal(overrides, health.Maintenance.Count);
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }

    private static JsonElement Issue(string[]? labels = null, string author = "frasermolyneux", bool closed = false,
        string body = "We are investigating.", int updatedMinutes = 5, string title = "Availability incident") =>
        JsonSerializer.SerializeToElement(new
        {
            number = 42,
            title,
            body,
            user = new { login = author },
            created_at = Created,
            updated_at = Created.AddMinutes(updatedMinutes),
            closed_at = closed ? Created.AddMinutes(updatedMinutes) : (DateTimeOffset?)null,
            labels = (labels ?? ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage"])
                .Select(name => new { name })
        });

    [Fact]
    public void GitHubImportsRequirePublicationApprovalAndAttributeWorkloadsToPublicComponents()
    {
        var published = Assert.Single(GitHubIssueParser.Parse(Issue(body: "### Public update\n\nWe are investigating."), [], Catalogue(), new()));
        Assert.True(published.Published);
        Assert.Equal("We are investigating.", Assert.Single(published.PublicUpdates).Body);
        Assert.Equal(["mx.sitewatch.geo-location-web", "mx.sitewatch.geo-location-api"], published.Components);
        Assert.Equal("https://github.com/frasermolyneux/status-pages/issues/42", published.SourceUrl);
        var draft = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "site:mx", "workload:geo-location", "severity:outage"]), [], Catalogue(), new()));
        Assert.False(draft.Published);
        Assert.Empty(draft.PublicUpdates);
        Assert.NotEmpty(draft.Updates);
        Assert.Empty(GitHubIssueParser.Parse(Issue(["unrelated"], author: "stranger"), [], Catalogue(), new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedGitHubUpdatesRedactInternalNotesWithoutParsingLiteralStateHeaders(bool comment)
    {
        var command = new IncidentCommand("Generated update", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Monitoring, "state: resolved\nVisible <script>unsafe()</script>.", true,
            Delivery: "github", OperationId: Guid.NewGuid());
        var incident = command.Apply(75, "mx", null, Created.AddMinutes(2));
        var body = GitHubIncidentDocument.Body(new(command.OperationId!.Value, incident, command, null, false, "pending"))
            + "\n<!-- internal -->\nPRIVATE_NOTE\n<!-- /internal -->";
        var comments = comment ? new[]
        {
            JsonSerializer.SerializeToElement(new { body, user = new { login = "frasermolyneux" }, created_at = Created.AddMinutes(2) })
        } : [];
        var record = Assert.Single(GitHubIssueParser.Parse(Issue(body: comment ? "Initial public update" : body),
            comments, Catalogue(), new()));
        var update = Assert.Single(record.PublicUpdates, update => update.OperationId == command.OperationId);
        Assert.Equal(IncidentState.Monitoring, update.State);
        Assert.StartsWith("state: resolved<br>", update.Body);
        Assert.Contains(WebUtility.HtmlEncode("<script>"), update.Body);
        Assert.DoesNotContain("PRIVATE_NOTE", JsonSerializer.Serialize(record.ToPublicIncident(), StatusJson.Options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\t")]
    public void GitHubUpdatesExcludeUntrustedCommentsAndInternalNotesAndEscapeHtml(string indentation)
    {
        var comments = new[] {
            JsonSerializer.SerializeToElement(new { body = "UNTRUSTED_COMMENT", user = new { login = "stranger" }, created_at = Created.AddMinutes(1) }),
            JsonSerializer.SerializeToElement(new { body = "state: monitoring\nRecovered <script>unsafe()</script>.", user = new { login = "frasermolyneux" }, created_at = Created.AddMinutes(2) }),
        };
        var record = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage"],
            body: $"Public description.\n{indentation}<!-- internal -->\nPRIVATE_NOTE\n{indentation}<!-- /internal -->"), comments, Catalogue(), new()));
        Assert.Equal([IncidentState.Investigating, IncidentState.Monitoring], record.PublicUpdates.Select(update => update.State));
        Assert.Equal(IncidentState.Monitoring, record.State);
        var output = JsonSerializer.Serialize(record.ToPublicIncident(), StatusJson.Options);
        Assert.DoesNotContain("UNTRUSTED_COMMENT", output);
        Assert.DoesNotContain("PRIVATE_NOTE", output);
        Assert.DoesNotContain("<script>", record.PublicUpdates[1].Body);
        Assert.Contains(WebUtility.HtmlEncode("<script>"), record.PublicUpdates[1].Body);
    }

    [Fact]
    public void ClosingAndReopeningSourceIssuesUpdatesTheIncidentLifecycle()
    {
        var closed = Assert.Single(GitHubIssueParser.Parse(Issue(closed: true), [], Catalogue(), new()));
        Assert.Equal(IncidentState.Resolved, closed.State);
        Assert.Equal(Created.AddMinutes(5), closed.ResolvedAt);
        Assert.Equal(IncidentState.Resolved, closed.PublicUpdates.Last().State);
        var resolvedComment = JsonSerializer.SerializeToElement(new
        {
            body = "state: resolved\nPreviously recovered.",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(4)
        });
        var reopened = Assert.Single(GitHubIssueParser.Parse(Issue(), [resolvedComment], Catalogue(), new()));
        Assert.Equal(IncidentState.Investigating, reopened.State);
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public void ResolvedLabelsKeepTheirTransitionTimeAfterTitleEditsAndFollowupComments()
    {
        string[] labels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:resolved"];
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(labels), [], Catalogue(), new()));
        var edited = Assert.Single(GitHubIssueParser.Parse(
            Issue(labels, updatedMinutes: 10, title: "Corrected title"), [], Catalogue(), new(), [first]));
        Assert.Equal(first.ResolvedAt, edited.ResolvedAt);
        Assert.Equal(first.Updates.Select(update => update.At), edited.Updates.Select(update => update.At));
        var comment = JsonSerializer.SerializeToElement(new
        {
            body = "Follow-up details after recovery.",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(12)
        });
        var followed = Assert.Single(GitHubIssueParser.Parse(
            Issue(labels, updatedMinutes: 15, title: "Corrected title"), [comment], Catalogue(), new(), [edited]));
        Assert.Equal(first.ResolvedAt, followed.ResolvedAt);
        Assert.Equal(3, followed.PublicUpdates.Count);
        Assert.Equal(first.ResolvedAt, followed.PublicUpdates[1].At);
        Assert.Equal(IncidentState.Resolved, followed.PublicUpdates[^1].State);
        Assert.Equal(Created.AddMinutes(12), followed.PublicUpdates[^1].At);
    }

    [Fact]
    public void ActiveStateLabelsRetainTheirHistoryAcrossUnrelatedEditsUntrustedCommentsAndReopening()
    {
        string[] identifiedLabels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:identified"];
        var identified = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels), [], Catalogue(), new()));
        var monitoringLabels = identifiedLabels.Select(label => label == "state:identified" ? "state:monitoring" : label).ToArray();
        var monitoring = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 10), [], Catalogue(), new(), [identified]));
        var persisted = JsonSerializer.SerializeToNode(monitoring, StatusJson.Options)!.AsObject();
        foreach (var name in new[] { "publicSourceUpdates", "publicSourceStateUpdates", "publicSourceSeverity" })
            persisted.Remove(name);
        var legacy = persisted.Deserialize<IncidentRecord>(StatusJson.Options)!;
        var upgraded = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 15, title: "Corrected title"), [], Catalogue(), new(), [legacy]));
        Assert.Equal(monitoring.PublicUpdates, upgraded.PublicUpdates);
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified, IncidentState.Monitoring],
            monitoring.PublicUpdates.Select(update => update.State));
        var edited = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 15, title: "Corrected title"), [], Catalogue(), new(), [monitoring]));
        Assert.Equal(monitoring.PublicUpdates.Select(update => update.At), edited.PublicUpdates.Select(update => update.At));
        var comment = JsonSerializer.SerializeToElement(new
        {
            body = "state: resolved\nUntrusted update.",
            user = new { login = "stranger" },
            created_at = Created.AddMinutes(18)
        });
        var afterUntrusted = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 20, title: "Corrected title"), [comment], Catalogue(), new(), [edited]));
        Assert.Equal(JsonSerializer.Serialize(edited, StatusJson.Options), JsonSerializer.Serialize(afterUntrusted, StatusJson.Options));
        var closed = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, closed: true, updatedMinutes: 25), [], Catalogue(), new(), [afterUntrusted]));
        var reopened = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 30), [], Catalogue(), new(), [closed]));
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified, IncidentState.Monitoring, IncidentState.Resolved, IncidentState.Monitoring],
            reopened.PublicUpdates.Select(update => update.State));
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public async Task DisabledGitHubIngestionDoesNotReadTheDatabaseOrContactGitHub()
    {
        var options = new RuntimeOptions { Publisher = true, GitHubEnabled = false, DatabaseConnectionFile = "not-a-real-connection-file" };
        using var api = new GitHubApiFixture();
        await using var database = new PostgresStore(options, NullLogger<PostgresStore>.Instance);
        using var worker = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, api.Requests);
    }

    [Theory]
    [InlineData("site:unknown", "workload:geo-location", "severity:outage", "frasermolyneux")]
    [InlineData("site:xi", "workload:molyneux-me", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "workload:unknown", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "component:xi.sitewatch.forums-web", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "workload:geo-location", "severity:unknown", "frasermolyneux")]
    [InlineData("site:mx", "workload:geo-location", "severity:outage", "stranger")]
    public void InvalidAttributionOrUntrustedAuthorsCannotBecomePublicIncidents(string site, string workload, string severity, string author) =>
        Assert.Throws<InvalidDataException>(() => GitHubIssueParser.Parse(
            Issue(["status:incident", "status:published", site, workload, severity], author), [], Catalogue(), new()));

    [Fact]
    public void CrossBrandIssuesMapOnlyTheirOwnComponentsAndSupportExplicitTrustedMaintainers()
    {
        var records = GitHubIssueParser.Parse(Issue(
            ["status:incident", "site:mx", "site:xi", "workload:molyneux-me", "workload:xi-forums", "severity:degraded"],
            author: "maintainer"), [], Catalogue(), new() { GitHubTrustedAuthors = ["maintainer"] });
        Assert.Equal(2, records.Count);
        Assert.Equal(["mx.sitewatch.molyneux-me"], records.Single(record => record.SiteId == "mx").Components);
        Assert.Equal(["xi.sitewatch.forums-web"], records.Single(record => record.SiteId == "xi").Components);
    }

    [Theory]
    [InlineData("unknown", 0, 1)]
    [InlineData("info", 2, 1)]
    public void BannersRejectInvalidAppearanceOrTimeWindows(string tone, int start, int end)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(new BannerCommand("Notice", "Message", tone, true,
            StartsAt: now.AddHours(start), ExpiresAt: now.AddHours(end)).Validate());
        Assert.NotNull(new BannerCommand("Notice", "Message", "info", true, ExpiresAt: now.AddMinutes(-1)).Validate());
    }

    [Fact]
    public async Task BannerSchedulingSurvivesStaleDatabaseIndependentPublications()
    {
        var directory = Path.Combine(Path.GetTempPath(), "status-banner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new PublicationStore(Catalogue(), new() { DataDirectory = directory });
            await store.WriteAsync(new()
            {
                Site = new SiteInfo { Id = "mx" },
                GeneratedAt = now.AddHours(-1),
                Banners = [
                    new() { Id = 1, Title = "Current", StartsAt = now.AddMinutes(-1), ExpiresAt = now.AddHours(1) },
                    new() { Id = 2, Title = "Future", StartsAt = now.AddHours(1) },
                    new() { Id = 3, Title = "Expired", ExpiresAt = now.AddMinutes(-1) }
                ]
            });
            var read = await new PublicationStore(Catalogue(), new() { DataDirectory = directory }).ReadAsync("mx");
            Assert.True(read.DataFreshness!.Stale);
            Assert.Equal("Current", Assert.Single(read.Banners).Title);
            Assert.Empty((await store.ReadAsync("xi")).Banners);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncompleteOrRateLimitedGitHubSnapshotsFailExplicitlyBeforeChangingIncidents()
    {
        using var api = new GitHubApiFixture
        {
            Issues = Enumerable.Repeat(GitHubApiFixture.Incident(true, 0), 100).ToArray()
        };
        await using var database = new PostgresStore(new(), NullLogger<PostgresStore>.Instance);
        using var worker = new GitHubIssueWorker(Catalogue(), new(), database, api, NullLogger<GitHubIssueWorker>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ReadPagesAsync("issues?state=all", default));
        Assert.Equal(10, api.Requests);
        api.Status = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.ReadPagesAsync("issues?state=all", default));
    }
}
