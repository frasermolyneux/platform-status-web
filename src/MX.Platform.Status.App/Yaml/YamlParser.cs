using MX.Platform.Status.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MX.Platform.Status.App.Yaml;

public sealed class YamlParser
{
    private readonly IDeserializer _deserializer;

    public YamlParser()
    {
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    public Site ParseSite(string yaml) => _deserializer.Deserialize<Site>(yaml) ?? new Site();

    public ComponentsDocument ParseComponents(string yaml) => _deserializer.Deserialize<ComponentsDocument>(yaml) ?? new ComponentsDocument();

    public OverridesDocument ParseOverrides(string yaml) => _deserializer.Deserialize<OverridesDocument>(yaml) ?? new OverridesDocument();
}
