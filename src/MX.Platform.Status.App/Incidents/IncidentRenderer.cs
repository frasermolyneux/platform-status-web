using System.Text;
using System.Text.RegularExpressions;

namespace MX.Platform.Status.App.Incidents;

public sealed class IncidentRenderer
{
    private static readonly Regex SingleLineComment = new(@"<!--\s*internal\s*-->.*?(?:-->)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Render(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        var skipping = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (skipping)
            {
                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    skipping = false;
                }

                continue;
            }

            if (line.TrimStart().StartsWith("<!-- internal -->", StringComparison.OrdinalIgnoreCase))
            {
                if (!line["<!-- internal -->".Length..].Contains("-->", StringComparison.Ordinal))
                {
                    skipping = true;
                }

                continue;
            }

            line = SingleLineComment.Replace(line, string.Empty);
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString().Trim();
    }
}
