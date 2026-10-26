using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Merging;

public sealed class ImpactPropagation
{
    private readonly Dictionary<string, (string? Parent, string Maximum)> components = new(StringComparer.OrdinalIgnoreCase);

    public ImpactPropagation(ComponentsDocument document)
    {
        foreach (var component in document.Components) Add(component, null);
    }

    private void Add(Component component, string? parent)
    {
        components.Add(component.Id, (parent, component.MaximumParentImpact));
        foreach (var child in component.Children) Add(child, component.Id);
    }

    public string Contribute(string target, string source, string status)
    {
        var current = source;
        var capped = status;
        while (!current.Equals(target, StringComparison.OrdinalIgnoreCase)
            && components.TryGetValue(current, out var component))
        {
            if (component.Maximum == "degraded" && capped == "outage") capped = "degraded";
            if (component.Parent is null) return status;
            current = component.Parent;
        }
        return current.Equals(target, StringComparison.OrdinalIgnoreCase) ? capped : status;
    }

    public ComponentStatus Contribute(string target, string source, ComponentStatus status) =>
        Contribute(target, source, status.ToApiString()) == "degraded" && status == ComponentStatus.Outage
            ? ComponentStatus.Degraded : status;
}
