using System.Text;
using System.Text.RegularExpressions;

namespace MX.Platform.Status.App.Incidents;

public static partial class IncidentRenderer
{
    public static string Render(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        var depth = 0;

        foreach (var rawLine in lines)
        {
            var line = RedactLine(rawLine, ref depth);
            if (line is null) continue;
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString().Trim();
    }

    private static string? RedactLine(string line, ref int depth)
    {
        var redacted = depth > 0;
        var visible = new StringBuilder();
        var cursor = 0;
        foreach (Match marker in InternalMarkers().Matches(line))
        {
            if (depth == 0) visible.Append(line, cursor, marker.Index - cursor);
            if (marker.Groups["start"].Success)
            {
                depth++;
                redacted = true;
            }
            else if (depth > 0)
            {
                depth--;
                redacted = true;
            }
            else visible.Append(marker.Value);
            cursor = marker.Index + marker.Length;
        }
        if (depth == 0) visible.Append(line, cursor, line.Length - cursor);
        var text = visible.ToString();
        return redacted && string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [GeneratedRegex(@"(?<start><!--\s*internal\s*-->)|<!--\s*/internal\s*-->|-->", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalMarkers();
}
