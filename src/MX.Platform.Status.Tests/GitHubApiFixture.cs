using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MX.Platform.Status.Tests;

internal sealed class GitHubApiFixture : HttpMessageHandler, IHttpClientFactory
{
    public JsonElement[] Issues { get; set; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int Requests { get; private set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(Issues) });
    }

    public static JsonElement Incident(bool published, int revision, bool closed = false)
    {
        var created = DateTimeOffset.Parse("2026-10-23T08:00:00Z");
        var labels = new List<string> { "status:incident", "site:mx", "workload:molyneux-me", "severity:outage" };
        if (published) labels.Add("status:published");
        return JsonSerializer.SerializeToElement(new
        {
            number = 42,
            title = "GitHub persistence fixture",
            body = "Approved initial update",
            comments = 0,
            user = new { login = "status-fixture" },
            created_at = created,
            updated_at = created.AddMinutes(revision),
            closed_at = closed ? created.AddMinutes(revision) : (DateTimeOffset?)null,
            labels = labels.Select(name => new { name })
        });
    }
}
