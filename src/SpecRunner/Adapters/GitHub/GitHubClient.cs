using Octokit;
using SpecRunner.Domain;
using SpecRunner.Ports;
using Port = SpecRunner.Ports.IGitHubClient;

namespace SpecRunner.Adapters.GitHub;

/// <summary>Octokit implementation of the GitHub port. Octokit stays confined here (§5).</summary>
public sealed class GitHubClient : Port
{
    private readonly Octokit.GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubClient(string slug, string token)
    {
        var parts = slug.Split('/', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"slug must be owner/repo, got '{slug}'", nameof(slug));
        }

        (_owner, _repo) = (parts[0], parts[1]);
        _client = new Octokit.GitHubClient(new ProductHeaderValue("spec-runner"))
        {
            Credentials = new Credentials(token),
        };
    }

    public async Task<long?> ResolveUserIdAsync(string login, CancellationToken ct = default)
    {
        try
        {
            var user = await _client.User.Get(login).ConfigureAwait(false);
            return user.Id;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WorkItem>> ListOpenIssuesWithLabelAsync(
        string label, CancellationToken ct = default)
    {
        var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };
        request.Labels.Add(label);
        var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request).ConfigureAwait(false);
        return issues
            .Where(i => i.PullRequest is null) // issues only, never PRs
            .OrderBy(i => i.Number)
            .Select(ToWorkItem)
            .ToList();
    }

    public async Task<WorkItem> GetIssueAsync(int number, CancellationToken ct = default)
    {
        var issue = await _client.Issue.Get(_owner, _repo, number).ConfigureAwait(false);
        return ToWorkItem(issue);
    }

    public async Task AddCommentAsync(int number, string body, CancellationToken ct = default) =>
        await _client.Issue.Comment.Create(_owner, _repo, number, body).ConfigureAwait(false);

    public async Task AddLabelsAsync(int number, IReadOnlyList<string> labels, CancellationToken ct = default) =>
        await _client.Issue.Labels.AddToIssue(_owner, _repo, number, labels.ToArray()).ConfigureAwait(false);

    public async Task RemoveLabelAsync(int number, string label, CancellationToken ct = default)
    {
        try
        {
            await _client.Issue.Labels.RemoveFromIssue(_owner, _repo, number, label).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // label wasn't on the issue — the desired end state already holds
        }
    }

    public async Task<IReadOnlyList<string>> GetCommentBodiesAsync(int number, CancellationToken ct = default)
    {
        var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, number).ConfigureAwait(false);
        return comments.Select(c => c.Body ?? string.Empty).ToList();
    }

    public async Task<IReadOnlyList<SpecRunner.Ports.IssueComment>> GetCommentsAsync(
        int number, CancellationToken ct = default)
    {
        var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, number).ConfigureAwait(false);
        return comments
            .Select(c => new SpecRunner.Ports.IssueComment(c.User?.Id ?? 0, c.Body ?? string.Empty))
            .ToList();
    }

    public async Task<DateTimeOffset?> GetLabelAppliedAtAsync(
        int number, string label, CancellationToken ct = default)
    {
        var events = await _client.Issue.Events.GetAllForIssue(_owner, _repo, number).ConfigureAwait(false);
        DateTimeOffset? latest = null;
        foreach (var e in events)
        {
            if (string.Equals(e.Event.StringValue, "labeled", StringComparison.OrdinalIgnoreCase) &&
                e.Label?.Name == label &&
                (latest is null || e.CreatedAt > latest))
            {
                latest = e.CreatedAt;
            }
        }

        return latest;
    }

    public async Task<PullRequestRef> CreatePullRequestAsync(
        string title, string body, string head, string baseBranch, CancellationToken ct = default)
    {
        var pr = await _client.PullRequest
            .Create(_owner, _repo, new NewPullRequest(title, head, baseBranch) { Body = body })
            .ConfigureAwait(false);
        return new PullRequestRef(pr.Number, pr.HtmlUrl);
    }

    public async Task MergePullRequestAsync(int number, CancellationToken ct = default) =>
        await _client.PullRequest.Merge(_owner, _repo, number, new MergePullRequest()).ConfigureAwait(false);

    public async Task CloseIssueAsync(int number, CancellationToken ct = default) =>
        await _client.Issue.Update(_owner, _repo, number, new IssueUpdate { State = ItemState.Closed })
            .ConfigureAwait(false);

    public async Task<int> CreateIssueAsync(
        string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var create = new NewIssue(title) { Body = body };
        foreach (var l in labels)
        {
            create.Labels.Add(l);
        }

        var issue = await _client.Issue.Create(_owner, _repo, create).ConfigureAwait(false);
        return issue.Number;
    }

    private static WorkItem ToWorkItem(Issue issue) => new(
        Number: issue.Number,
        Title: issue.Title ?? string.Empty,
        Body: issue.Body ?? string.Empty,
        AuthorLogin: issue.User?.Login ?? string.Empty,
        AuthorId: issue.User?.Id ?? 0,
        Labels: issue.Labels.Select(l => l.Name).ToList());
}
