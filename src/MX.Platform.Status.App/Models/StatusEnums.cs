using System.Text.Json;
using System.Text.Json.Serialization;

namespace MX.Platform.Status.App.Models;

public enum ComponentStatus { Operational, Degraded, Outage, Maintenance, Unknown }
public enum IncidentState { Investigating, Identified, Monitoring, Resolved }
public enum Severity { Maintenance, Degraded, Outage }

public static class StatusEnumExtensions
{
    public static string ToApiString(this ComponentStatus status) => status switch
    {
        ComponentStatus.Operational => "operational",
        ComponentStatus.Degraded => "degraded",
        ComponentStatus.Outage => "outage",
        ComponentStatus.Maintenance => "maintenance",
        _ => "unknown"
    };

    public static string ToApiString(this IncidentState state) => state switch
    {
        IncidentState.Investigating => "investigating",
        IncidentState.Identified => "identified",
        IncidentState.Monitoring => "monitoring",
        _ => "resolved"
    };

    public static string ToApiString(this Severity severity) => severity switch
    {
        Severity.Maintenance => "maintenance",
        Severity.Degraded => "degraded",
        _ => "outage"
    };

    public static ComponentStatus ToComponentStatus(this Severity severity) => severity switch
    {
        Severity.Maintenance => ComponentStatus.Maintenance,
        Severity.Degraded => ComponentStatus.Degraded,
        _ => ComponentStatus.Outage
    };
}

public static class StatusJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DateOnlyJsonConverter());
        return options;
    }

    private sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private const string Format = "yyyy-MM-dd";

        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateOnly.ParseExact(reader.GetString() ?? throw new JsonException("Date value missing."), Format);

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(Format));
    }
}
