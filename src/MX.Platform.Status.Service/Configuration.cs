using System.Text.Json;
using System.Text.RegularExpressions;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

public sealed record RuntimeOptions
{
    public string DataDirectory { get; init; } = "data";
    public string CatalogPath { get; init; } = "catalog.json";
    public bool Publisher { get; init; }
    public int RefreshSeconds { get; init; } = 60;
    public int StaleAfterSeconds { get; init; } = 180;
    public string? DatabaseConnectionFile { get; init; }
    public string? WriterConnectionFile { get; init; }
    public string? DatabaseHost { get; init; }
    public int DatabasePort { get; init; } = 5432;
    public string? WriterHost { get; init; }
    public int WriterPort { get; init; } = 15432;
    public string DatabasePasswordFile { get; init; } = "/run/secrets/app-password";
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? AzureCertificatePath { get; init; }
    public string? AzureCollectorClientId { get; init; }
    public Dictionary<string, string> AzureMonitoringResources { get; init; } = [];
    public Uri AzureStatusFeed { get; init; } = new("https://azure.status.microsoft/status/feed/");
    public string[] WebhookApplicationIds { get; init; } = [];
    public bool GitHubEnabled { get; init; } = true;
    public string GitHubRepository { get; init; } = "frasermolyneux/status-pages";
    public string[] GitHubTrustedAuthors { get; init; } = [];
    public string? GitHubTokenFile { get; init; }
    public int? GitHubAppId { get; init; }
    public long? GitHubInstallationId { get; init; }
    public string? GitHubPrivateKeyFile { get; init; }
    public bool GitHubWriteConfigured => GitHubTokenFile is not null || GitHubPrivateKeyFile is not null;
    public string? TrafficConfigurationFile { get; init; }
    public string? TrafficTokenFile { get; init; }
    public int GitHubRefreshSeconds { get; init; } = 300;

    public void Validate()
    {
        if (RefreshSeconds is < 30 or > 3600 || StaleAfterSeconds < RefreshSeconds * 2)
            throw new InvalidOperationException("Invalid publication refresh/freshness settings.");
        ValidateIdentity();
        if (GitHubPrivateKeyFile is not null && (GitHubAppId is not > 0 || GitHubInstallationId is not > 0))
            throw new InvalidOperationException("GitHub App credentials require an application and installation ID.");
        if (Publisher && DatabaseConnectionFile is null && DatabaseHost is null)
            throw new InvalidOperationException("A publisher requires a database connection.");
        if (DatabasePort is < 1 or > 65535 || WriterPort is < 1 or > 65535)
            throw new InvalidOperationException("Invalid database port.");
        if (!Regex.IsMatch(GitHubRepository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)) || GitHubRefreshSeconds is < 60 or > 3600)
            throw new InvalidOperationException("GitHub integration requires an owner/repository and a 60-3600 second interval.");
    }

    private void ValidateIdentity()
    {
        if ((TenantId is null) != (ClientId is null)
            || (TenantId is not null && (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _))))
            throw new InvalidOperationException("Entra tenant and client IDs must be configured together as GUIDs.");
        if (WebhookApplicationIds.Any(id => !Guid.TryParse(id, out _)))
            throw new InvalidOperationException("Webhook application IDs must be GUIDs.");
        if (AzureCertificatePath is not null && (TenantId is null || !Guid.TryParse(AzureCollectorClientId, out _)))
            throw new InvalidOperationException("Azure collection requires a tenant and certificate-authenticated collector application.");
        if (AzureMonitoringResources.Count > 0 && (AzureCertificatePath is null || AzureMonitoringResources.Any(resource =>
            string.IsNullOrWhiteSpace(resource.Key) || !Regex.IsMatch(resource.Value,
                @"^/subscriptions/[0-9a-f-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Insights/components/[^/]+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))))
            throw new InvalidOperationException("Monitoring inputs require a dedicated certificate reader and exact Application Insights resource scopes, never workload URLs or workspace-wide access.");
    }
}

public sealed record SiteDefinition
{
    public Site Site { get; init; } = new();
    public ComponentsDocument Components { get; init; } = new();
    public Dictionary<string, string> Probes { get; init; } = [];
    public List<CheckDefinition> Checks { get; init; } = [];
    public Dictionary<string, string[]> Workloads { get; init; } = [];
    public Dictionary<string, string> SharedComponents { get; init; } = [];

    public SiteConfigurationSnapshot Snapshot => new(Site, Components, "", "", DateTimeOffset.UtcNow);
    public IReadOnlyList<CheckDefinition> EffectiveChecks => Checks;
}

public sealed class StatusCatalog
{
    public IReadOnlyList<SiteDefinition> Sites { get; }

    public StatusCatalog(string path)
    {
        Sites = JsonSerializer.Deserialize<List<SiteDefinition>>(File.ReadAllText(path), StatusJson.Options)
            ?? throw new InvalidOperationException("Status catalogue is empty.");
        if (Sites.Count == 0 || Sites.Select(s => s.Site.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Sites.Count)
            throw new InvalidOperationException("Status site IDs must be unique.");
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Sites) ValidateSite(definition, domains);
        ValidateSharedChecks();
        ValidateMonitoringReferences();
    }

    private static void ValidateSite(SiteDefinition definition, HashSet<string> domains)
    {
        if (!Regex.IsMatch(definition.Site.Id, "^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidOperationException("Invalid status site ID.");
        if (definition.Site.Domains.Count == 0 || definition.Site.Domains.Any(domain =>
            Uri.CheckHostName(domain) != UriHostNameType.Dns || !domains.Add(domain)))
            throw new InvalidOperationException("Status domains must be valid and unique.");
        var components = Flatten(definition.Components.Components).ToArray();
        if (components.Length == 0 || components.Select(c => c.Id).Distinct().Count() != components.Length)
            throw new InvalidOperationException("Status component IDs must be unique within a site.");
        if (components.Any(component => component.Kind is not ("group" or "leaf")
            || component.Kind == "leaf" && component.Children.Count > 0))
            throw new InvalidOperationException("Services contain components; leaf components cannot contain children.");
        ValidateProbes(definition, components);
        ValidateSharedComponents(definition);
        if (definition.Workloads.Any(workload => string.IsNullOrWhiteSpace(workload.Key)
            || workload.Value.Length == 0 || workload.Value.Distinct().Count() != workload.Value.Length
            || workload.Value.Any(id => !PublicComponents(definition.Components.Components).Any(c => c.Id == id))))
            throw new InvalidOperationException("Workload mappings must name distinct public components from their own site.");
    }

    private void ValidateSharedChecks()
    {
        var groups = Sites.SelectMany(site => site.EffectiveChecks.Where(check => check.SharedId is not null)
            .Select(check => (Identity: check.SharedId, Component: site.SharedComponents[check.Component], Check: check)))
            .GroupBy(entry => entry.Identity);
        if (groups.Any(shared => shared.Select(entry => (entry.Component, entry.Check.Kind, entry.Check.Target, entry.Check.Method, entry.Check.ExpectedIntervalSeconds)).Distinct().Count() != 1))
            throw new InvalidOperationException("Shared check definitions must identify the same component, kind, target, method and interval.");
    }

    private static void ValidateProbes(SiteDefinition definition, Component[] components)
    {
        if (definition.Probes.Count != 0)
            throw new InvalidOperationException("Status cannot configure workload probes. Define collection and targets in Sitewatch/platform-monitoring.");
        if (definition.EffectiveChecks.Select(check => check.Id).Distinct().Count() != definition.EffectiveChecks.Count
            || definition.EffectiveChecks.Select(check => check.PublicId(definition)).Distinct().Count() != definition.EffectiveChecks.Count)
            throw new InvalidOperationException("Check IDs must be unique within a site.");
        foreach (var check in definition.EffectiveChecks)
            ValidateCheck(definition, components, check);
    }

    private static void ValidateCheck(SiteDefinition definition, Component[] components, CheckDefinition check)
    {
        if (check.SharedId is not null && (string.IsNullOrWhiteSpace(check.SharedId)
            || !definition.SharedComponents.ContainsKey(check.Component)))
            throw new InvalidOperationException("A shared check identity requires an explicitly shared component.");
        if (!components.Any(c => c.Id == check.Component && c.Kind == "leaf") || string.IsNullOrWhiteSpace(check.Id)
            || string.IsNullOrWhiteSpace(check.Name) || check.ExpectedIntervalSeconds is < 30 or > 86400
            || check.Kind != "telemetry" || check.Method != "GET")
            throw new InvalidOperationException("Checks require a unique identity, component, name, supported kind and observation interval.");
        if (check.Target is not null)
            throw new InvalidOperationException("Status checks accept monitoring observations, never workload HTTP targets.");
        if (check.Monitoring is { } source && (string.IsNullOrWhiteSpace(source.SiteId)
            || string.IsNullOrWhiteSpace(source.ComponentId) || string.IsNullOrWhiteSpace(source.CheckId)
            || source.Resources is not { Length: > 0 } || source.Regions is not { Length: > 0 }
            || source.Resources.Any(string.IsNullOrWhiteSpace) || source.Regions.Any(string.IsNullOrWhiteSpace)
            || source.Resources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Resources.Length
            || source.Regions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Regions.Length
            || source.IntervalSeconds is < 30 or > 3600 || source.IntervalSeconds % 30 != 0))
            throw new InvalidOperationException("Monitoring references require explicit source identities, resources, expected regions and a valid observation interval.");
    }

    private void ValidateMonitoringReferences()
    {
        foreach (var site in Sites)
            foreach (var check in site.Checks.Where(check => check.Monitoring is not null))
            {
                var reference = check.Monitoring!;
                var source = Sites.SingleOrDefault(candidate => candidate.Site.Id == reference.SiteId)
                    ?? throw new InvalidOperationException("Monitoring references must name a configured owning site.");
                if (source.Site.Id == site.Site.Id)
                {
                    if (reference.ComponentId != check.Component)
                        throw new InvalidOperationException("Monitoring references must name their own component.");
                }
                else if (check.SharedId is null || !site.SharedComponents.TryGetValue(check.Component, out var identity)
                    || source.SharedComponents.GetValueOrDefault(reference.ComponentId) != identity)
                    throw new InvalidOperationException("Cross-site monitoring inputs require an explicitly shared component.");
            }
        foreach (var shared in Sites.SelectMany(site => site.Checks.Where(check => check.SharedId is not null))
            .GroupBy(check => check.SharedId))
            if (shared.Select(check => check.Monitoring is { } source
                ? $"{source.Identity}|{source.IntervalSeconds}|{string.Join(',', source.Resources.Order())}|{string.Join(',', source.Regions.Order())}"
                : null).Distinct().Count() != 1)
                throw new InvalidOperationException("Shared checks must consume the same monitoring identity, resources, cadence and regions.");
    }

    private static void ValidateSharedComponents(SiteDefinition definition)
    {
        var publicIds = PublicComponents(definition.Components.Components).Select(component => component.Id).ToHashSet();
        if (definition.SharedComponents.Any(pair => !publicIds.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            || definition.SharedComponents.Values.Distinct().Count() != definition.SharedComponents.Count)
            throw new InvalidOperationException("Shared components require distinct identities mapped to public components.");
    }

    public SiteDefinition? FindHost(string host) => Sites.SingleOrDefault(s =>
        s.Site.Domains.Contains(host.TrimEnd('.'), StringComparer.OrdinalIgnoreCase));

    public SiteDefinition Get(string id) => Sites.SingleOrDefault(s => s.Site.Id == id)
        ?? throw new ArgumentException("Unknown status site.", nameof(id));

    public IReadOnlyDictionary<string, ComponentLiveTelemetry> ProjectTelemetry(SiteDefinition target,
        IReadOnlyDictionary<string, Dictionary<string, ComponentLiveTelemetry>> telemetry)
    {
        var result = new Dictionary<string, ComponentLiveTelemetry>(telemetry[target.Site.Id]);
        foreach (var (componentId, identity) in target.SharedComponents)
        {
            var checks = Sites.SelectMany(source => source.SharedComponents.Where(pair => pair.Value == identity)
                    .SelectMany(pair => telemetry[source.Site.Id].GetValueOrDefault(pair.Key)?.Checks ?? []))
                .GroupBy(check => check.Id).Select(group => group.MaxBy(check => check.ObservedAt)!).ToArray();
            if (checks.Length > 0) result[componentId] = ComponentChecks.FromChecks(checks);
        }
        return result;
    }

    public IReadOnlyList<IncidentRecord> ProjectIncidents(SiteDefinition target, IEnumerable<IncidentRecord> records) =>
        records.Select(record => ProjectIncident(target, record)).Where(record => record is not null)
            .Select(record => record!)
            .OrderByDescending(record => record.Published).ThenBy(record => record.State == IncidentState.Resolved)
            .ThenByDescending(record => record.SiteId == target.Site.Id)
            .GroupBy(record => record.SourceUrl ?? $"incident:{record.Id}")
            .Select(group =>
            {
                var incident = group.First();
                return incident with
                {
                    Components = group.Where(record => record.Published == incident.Published
                        && (incident.State == IncidentState.Resolved || record.State != IncidentState.Resolved))
                        .SelectMany(record => record.Components).Distinct().ToArray(),
                    ImpactHistory = group.Where(record => record.Published == incident.Published)
                        .SelectMany(record => record.ImpactHistory.Select(observation => observation with { SourceSite = record.SiteId })).ToArray(),
                    PublicAliases = group.SelectMany(record => record.PublicAliases.Append(record.Id))
                        .Where(id => id > 0 && id != incident.Id).Distinct().ToArray()
                };
            }).ToArray();

    private IncidentRecord? ProjectIncident(SiteDefinition target, IncidentRecord record)
    {
        if (record.SiteId == target.Site.Id) return record;
        var source = Get(record.SiteId);
        var publicComponents = PublicComponents(source.Components.Components).ToArray();
        if (!record.Published || record.Components.Any(id => !publicComponents.Any(component => component.Id == id)))
            return null;
        var components = ProjectSharedScope(source, target, record.Components);
        var history = record.ImpactHistory.Select(observation => observation with
        {
            Components = ProjectSharedScope(source, target, observation.Components)
        }).ToArray();
        DateTimeOffset? scopeEndedAt = null;
        if (components.Length == 0)
        {
            var last = Array.FindLastIndex(history, observation => observation.Components.Count > 0);
            if (last < 0 || last + 1 >= history.Length) return null;
            scopeEndedAt = history[last + 1].At;
            components = history.SelectMany(observation => observation.Components).Distinct().ToArray();
        }
        return record with
        {
            Components = components,
            State = scopeEndedAt.HasValue ? IncidentState.Resolved : record.State,
            ResolvedAt = scopeEndedAt ?? record.ResolvedAt,
            Evidence = null,
            Updates = record.PublicUpdates,
            SourceStateUpdates = [],
            SourceUpdates = [],
            PublicSourceUpdates = [],
            PublicSourceStateUpdates = [],
            PublicSourceSeverity = null,
            PublicSourceObservedAt = null,
            ImpactHistory = history
        };
    }

    private static string[] ProjectSharedScope(SiteDefinition source, SiteDefinition target, IEnumerable<string> components)
    {
        var known = PublicComponents(source.Components.Components).ToDictionary(component => component.Id);
        var affected = components.Where(known.ContainsKey).SelectMany(id => PublicComponents([known[id]]))
            .Select(component => component.Id).ToHashSet();
        var shared = source.SharedComponents.Where(pair => affected.Contains(pair.Key)).Select(pair => pair.Value).ToHashSet();
        return target.SharedComponents.Where(pair => shared.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
    }

    public static IEnumerable<Component> Flatten(IEnumerable<Component> components) =>
        components.SelectMany(component => new[] { component }.Concat(Flatten(component.Children)));

    public static IEnumerable<Component> PublicComponents(IEnumerable<Component> components) =>
        components.Where(component => !component.Hidden)
            .SelectMany(component => new[] { component }.Concat(PublicComponents(component.Children)));
}
