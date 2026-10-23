using System.Text.Json;
using System.Text.RegularExpressions;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record TrafficHost(string Hostname, string Site, string ZoneId, bool Canary = false);

public sealed record TrafficConfiguration
{
    public int Version { get; init; }
    public string AccountId { get; init; } = "";
    public Dictionary<string, string> Scripts { get; init; } = [];
    public TrafficHost[] Hosts { get; init; } = [];

    public static TrafficConfiguration? Read(RuntimeOptions options, StatusCatalog catalog)
    {
        if (options.TrafficConfigurationFile is null && options.TrafficTokenFile is null) return null;
        if (string.IsNullOrWhiteSpace(options.TrafficConfigurationFile) || string.IsNullOrWhiteSpace(options.TrafficTokenFile))
            throw new InvalidDataException("Traffic configuration and credential file paths must be configured together.");
        return Read(options.TrafficConfigurationFile, catalog);
    }

    public static TrafficConfiguration Read(string path, StatusCatalog catalog)
    {
        var configuration = JsonSerializer.Deserialize<TrafficConfiguration>(File.ReadAllText(path), StatusJson.Options)
            ?? throw new InvalidDataException("The traffic configuration is empty.");
        configuration.Validate(catalog);
        return configuration;
    }

    internal void Validate(StatusCatalog catalog)
    {
        var timeout = TimeSpan.FromMilliseconds(100);
        var valid = Version == 1 && AccountId is not null && Regex.IsMatch(AccountId, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant, timeout)
            && Scripts is not null && Scripts.Count == 2 && Scripts.GetValueOrDefault("page") == "platform-maintenance-page"
            && Scripts.GetValueOrDefault("redirect") == "platform-maintenance-redirect"
            && Hosts is { Length: > 0 and <= 250 } && Hosts.All(host => host is not null && host.Hostname is not null && host.ZoneId is not null)
            && Hosts.Count(host => host.Canary) <= 1
            && Hosts.All(host => !host.Canary || host.Hostname == "maintenance-check.bishopsbees.co.uk")
            && Hosts.Select(host => host.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Hosts.Length
            && Hosts.All(host => host.Hostname == host.Hostname.ToLowerInvariant()
                && Regex.IsMatch(host.Hostname, @"^[a-z0-9][a-z0-9.-]{0,251}[a-z0-9]$", RegexOptions.CultureInvariant, timeout)
                && Uri.CheckHostName(host.Hostname) == UriHostNameType.Dns
                && catalog.FindHost(host.Hostname) is null
                && catalog.Sites.Any(site => site.Site.Id == host.Site)
                && Regex.IsMatch(host.ZoneId, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant, timeout));
        if (!valid) throw new InvalidDataException("Traffic configuration must contain approved exact hosts, known sites and maintenance Workers; status hosts are forbidden.");
    }
}

public sealed record TrafficCommand(string Mode, string Reason, string Confirmation, int Revision = 0, string? RouteId = null,
    string? RouteFingerprint = null)
{
    public string? Validate(string hostname)
    {
        if (Mode is not ("normal" or "page" or "redirect")) return "Choose normal traffic, a maintenance page, or a temporary browser redirect.";
        if (Confirmation != hostname) return "Type the exact hostname to confirm the traffic change.";
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 500) return "Enter a private audit reason of up to 500 characters.";
        if (Revision < 0) return "Invalid traffic revision.";
        return null;
    }
}

public sealed record TrafficRecord(string Hostname, string Site, int Revision, string Mode, string Phase,
    string Actor, string Reason, string? RouteId, DateTimeOffset UpdatedAt, string? Error = null);

public sealed record TrafficRow(string Hostname, string Mode, string State, bool Verified, string? RouteId,
    int Revision, string StatusUrl, int? HttpStatus, string? Error, TrafficRecord? LastOperation, string? RouteFingerprint = null);

public sealed record TrafficQuota(long? Requests, DateTimeOffset ObservedAt, string? Error)
{
    public int FreeDailyAllowance { get; } = 100_000;
    public bool Warning => Requests >= 80_000;
}

public sealed record TrafficWorkspace(bool Enabled, TrafficRow[] Hosts, TrafficQuota? Quota);

public sealed class TrafficConflictException(string message) : Exception(message);
