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

    private static WorkItem ToWorkItem(Issue issue) => new(
        Number: issue.Number,
        Title: issue.Title ?? string.Empty,
        Body: issue.Body ?? string.Empty,
        AuthorLogin: issue.User?.Login ?? string.Empty,
        AuthorId: issue.User?.Id ?? 0,
        Labels: issue.Labels.Select(l => l.Name).ToList());
}
