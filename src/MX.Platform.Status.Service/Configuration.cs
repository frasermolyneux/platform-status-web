using System.Text.Json;
using System.Text.RegularExpressions;
using MX.Platform.Status.App.Models;

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
    public string[] WebhookApplicationIds { get; init; } = [];

    public void Validate()
    {
        if (RefreshSeconds is < 30 or > 3600 || StaleAfterSeconds < RefreshSeconds * 2)
            throw new InvalidOperationException("Invalid publication refresh/freshness settings.");
        if ((TenantId is null) != (ClientId is null)
            || (TenantId is not null && (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _))))
            throw new InvalidOperationException("Entra tenant and client IDs must be configured together as GUIDs.");
        if (Publisher && DatabaseConnectionFile is null && DatabaseHost is null)
            throw new InvalidOperationException("A publisher requires a database connection.");
        if (DatabasePort is < 1 or > 65535 || WriterPort is < 1 or > 65535)
            throw new InvalidOperationException("Invalid database port.");
        if (WebhookApplicationIds.Any(id => !Guid.TryParse(id, out _)))
            throw new InvalidOperationException("Webhook application IDs must be GUIDs.");
        if (AzureCertificatePath is not null && (TenantId is null || !Guid.TryParse(AzureCollectorClientId, out _)))
            throw new InvalidOperationException("Azure collection requires a tenant and certificate-authenticated collector application.");
    }
}

public sealed record SiteDefinition
{
    public Site Site { get; init; } = new();
    public ComponentsDocument Components { get; init; } = new();
    public Dictionary<string, string> Probes { get; init; } = [];

    public SiteConfigurationSnapshot Snapshot => new(Site, Components, "", "", DateTimeOffset.UtcNow);
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
        foreach (var definition in Sites)
        {
            if (!Regex.IsMatch(definition.Site.Id, "^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Invalid status site ID.");
            if (definition.Site.Domains.Count == 0 || definition.Site.Domains.Any(domain =>
                Uri.CheckHostName(domain) != UriHostNameType.Dns || !domains.Add(domain)))
                throw new InvalidOperationException("Status domains must be valid and unique.");
            var components = Flatten(definition.Components.Components).ToArray();
            if (components.Length == 0 || components.Select(c => c.Id).Distinct().Count() != components.Length)
                throw new InvalidOperationException("Status component IDs must be unique within a site.");
            foreach (var (componentId, target) in definition.Probes)
            {
                if (!components.Any(c => c.Id == componentId && c.Kind == "leaf")
                    || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                    || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
                    throw new InvalidOperationException("Probes require a known leaf component and a public HTTPS URL without credentials or query strings.");
            }
        }
    }

    public SiteDefinition? FindHost(string host) => Sites.SingleOrDefault(s =>
        s.Site.Domains.Contains(host.TrimEnd('.'), StringComparer.OrdinalIgnoreCase));

    public SiteDefinition Get(string id) => Sites.SingleOrDefault(s => s.Site.Id == id)
        ?? throw new ArgumentException("Unknown status site.", nameof(id));

    public static IEnumerable<Component> Flatten(IEnumerable<Component> components) =>
        components.SelectMany(component => new[] { component }.Concat(Flatten(component.Children)));

    public static IEnumerable<Component> PublicComponents(IEnumerable<Component> components) =>
        components.Where(component => !component.Hidden)
            .SelectMany(component => new[] { component }.Concat(PublicComponents(component.Children)));
}
