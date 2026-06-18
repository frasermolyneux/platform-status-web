using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Sites;
using MX.Platform.Status.App.Yaml;

namespace MX.Platform.Status.App.Rollup;

public sealed class OverrideApplier
{
    private readonly ContentRepoClient _contentRepoClient;
    private readonly YamlParser _yamlParser;
    private readonly string _repo;
    private readonly string _branch;

    public OverrideApplier(ContentRepoClient contentRepoClient, YamlParser yamlParser)
    {
        _contentRepoClient = contentRepoClient;
        _yamlParser = yamlParser;
        _repo = Environment.GetEnvironmentVariable("STATUS_CONTENT_REPO") ?? "frasermolyneux/status-pages";
        _branch = Environment.GetEnvironmentVariable("STATUS_CONTENT_BRANCH") ?? "main";
    }

    public async Task ApplyAsync(string siteId, HistoryDocument history, CancellationToken cancellationToken = default)
    {
        OverridesDocument overrides;
        try
        {
            var yaml = await _contentRepoClient.GetTextFileAsync(_repo, _branch, $"sites/{siteId}/overrides.yaml", cancellationToken).ConfigureAwait(false);
            overrides = _yamlParser.ParseOverrides(yaml);
        }
        catch
        {
            return;
        }

        foreach (var item in overrides.Overrides)
        {
            if (!history.ComponentsById.TryGetValue(item.Component, out var record))
            {
                continue;
            }

            var index = record.Days.FindIndex(day => day.Date == item.Date);
            if (index < 0)
            {
                continue;
            }

            record.Days[index] = record.Days[index] with { Status = item.Status, Overridden = true };
        }
    }
}
