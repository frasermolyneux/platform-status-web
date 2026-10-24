namespace MX.Platform.Status.Service;

public sealed class GitHubOutboxWorker(RuntimeOptions options, PostgresStore database,
    GitHubIncidentClient github, ILogger<GitHubOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Publisher || !options.GitHubEnabled || options.DatabaseConnectionFile is null && options.DatabaseHost is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await database.InitializeAsync(stoppingToken);
                for (var count = 0; count < 20 && await RunOnceAsync(stoppingToken); count++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "GitHub delivery worker failed; durable local updates and queued changes are retained."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var lease = await database.TryGitHubLockAsync(ct);
        if (lease is null) return false;
        var job = await database.ReadGitHubJobAsync(lease, ct);
        if (job is null) return false;
        var attempted = job.Attempted;
        try
        {
            if (!options.GitHubWriteConfigured) throw new InvalidOperationException("GitHub writer credentials are not configured. The local incident is published; GitHub delivery remains queued.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var token = timeout.Token;
            var number = GitHubIncidentClient.IssueNumber(job.Incident.SourceUrl, options.GitHubRepository);
            if (job.Incident.SourceUrl is not null && number is null)
                throw new GitHubIncidentConflictException("This incident belongs to a different GitHub repository. Restore the configured source before retrying.");
            var snapshot = number.HasValue ? await github.ReadAsync(number.Value, token)
                : job.Attempted ? await github.FindCreatedAsync(job.OperationId, token) : null;
            if (snapshot is not null && github.Matches(job, snapshot) && (!job.Command.Published || github.ContainsUpdate(snapshot, job)))
            {
                await database.CompleteGitHubJobAsync(lease, job, options.GitHubRepository, snapshot.Number, snapshot.UpdatedAt, snapshot.Version, token);
                return true;
            }
            if (job.Attempted)
                throw new GitHubOutcomeUnknownException("A previous GitHub write has an unconfirmed outcome. Review GitHub before explicitly retrying; no duplicate issue or update was sent.");
            if (snapshot is not null && (snapshot.UpdatedAt != job.ExpectedAt || snapshot.Version != job.ExpectedVersion))
                throw new GitHubIncidentConflictException("GitHub changed since this update was prepared. The local status update remains published; review GitHub before applying the queued change.");
            var labels = github.DesiredLabels(job, snapshot);
            if (job.Command.Published) await github.EnsureLabelsAsync(labels, token);
            await database.MarkGitHubAttemptAsync(lease, job.OperationId, token);
            attempted = true;
            snapshot = await WriteAsync(job, snapshot, labels, token);
            if (!github.Matches(job, snapshot) || job.Command.Published && !github.ContainsUpdate(snapshot, job))
                throw new GitHubIncidentConflictException("GitHub changed during delivery. The local update is retained; review the remote issue before continuing.");
            await database.CompleteGitHubJobAsync(lease, job, options.GitHubRepository, snapshot.Number, snapshot.UpdatedAt, snapshot.Version, token);
            return true;
        }
        catch (GitHubIncidentConflictException exception)
        {
            await ProblemAsync(lease, job, "conflict", exception, ct);
        }
        catch (GitHubOutcomeUnknownException exception)
        {
            await ProblemAsync(lease, job, "review", exception, ct);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            await ProblemAsync(lease, job, "review",
                new GitHubIncidentConflictException("GitHub rejected this change or the source issue is missing. Review the issue and App access before retrying."), ct);
        }
        catch (InvalidDataException exception)
        {
            await ProblemAsync(lease, job, "review", exception, ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested
            && exception is HttpRequestException or OperationCanceledException or InvalidOperationException
                or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            var error = attempted
                ? "GitHub delivery was interrupted after a write began. The local update is safe; the remote outcome is being reconciled."
                : "GitHub delivery is unavailable. The local update is safe and delivery will retry automatically.";
            logger.LogWarning(exception, "GitHub delivery failed for operation {Operation}; local publication is retained.", job.OperationId);
            await database.SetGitHubProblemAsync(lease, job, attempted ? "uncertain" : "pending", error, ct);
        }
        return false;
    }

    private async Task ProblemAsync(PublisherLease lease, GitHubJob job, string state, Exception exception, CancellationToken ct)
    {
        logger.LogWarning(exception, "GitHub operation {Operation} requires operator review.", job.OperationId);
        await database.SetGitHubProblemAsync(lease, job, state, exception.Message, ct);
    }

    private async Task<GitHubIssueSnapshot> WriteAsync(GitHubJob job, GitHubIssueSnapshot? snapshot, string[] labels, CancellationToken ct)
    {
        if (snapshot is null)
        {
            if (!job.Command.Published) throw new InvalidOperationException("A private draft cannot create a public GitHub issue.");
            var created = await github.SendAsync(HttpMethod.Post, "issues",
                new { title = job.Command.Title.Trim(), body = GitHubIncidentDocument.Body(job), labels }, ct);
            if (job.Command.State == App.Models.IncidentState.Resolved)
                await github.SendAsync(HttpMethod.Patch, $"issues/{created.GetProperty("number").GetInt32()}", new { state = "closed" }, ct);
            return await github.ReadAsync(created.GetProperty("number").GetInt32(), ct);
        }
        if (!job.Command.Published)
        {
            if (snapshot.Labels.Contains("status:published"))
            {
                await github.SendAsync(HttpMethod.Delete, $"issues/{snapshot.Number}/labels/status%3Apublished", null, ct);
            }
            return await github.ReadAsync(snapshot.Number, ct);
        }
        await github.SendAsync(HttpMethod.Patch, $"issues/{snapshot.Number}",
            new { title = job.Command.Title.Trim(), labels, state = job.Command.State == App.Models.IncidentState.Resolved ? "closed" : "open" }, ct);
        if (!github.ContainsUpdate(snapshot, job))
            await github.SendAsync(HttpMethod.Post, $"issues/{snapshot.Number}/comments", new { body = GitHubIncidentDocument.Body(job) }, ct);
        return await github.ReadAsync(snapshot.Number, ct);
    }
}
