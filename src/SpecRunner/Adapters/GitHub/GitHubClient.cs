using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Octokit;
using SpecRunner.Domain;
using SpecRunner.Ports;
using Port = SpecRunner.Ports.IGitHubClient;

namespace SpecRunner.Adapters.GitHub;

/// <summary>
/// Octokit implementation of the GitHub port. Octokit stays confined here (§5). One operation —
/// issue dependencies — has no REST surface at all, so it goes over GraphQL with a raw
/// <see cref="HttpClient"/> alongside the REST client, which the constitution permits (§2:
/// "Octokit or raw HttpClient, whichever has less friction, behind an interface the tests can
/// fake"). Both authenticate with the same token.
/// </summary>
public sealed class GitHubClient : Port
{
    private static readonly Uri GraphQlEndpoint = new("https://api.github.com/graphql");

    // Static so the adapter stays non-disposable: the port has no lifetime and its callers do not
    // own one. One shared handler is also the documented way to use HttpClient.
    private static readonly HttpClient Http = new();

    private readonly Octokit.GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _token;

    public GitHubClient(string slug, string token)
    {
        var parts = slug.Split('/', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"slug must be owner/repo, got '{slug}'", nameof(slug));
        }

        (_owner, _repo) = (parts[0], parts[1]);
        _token = token;
        _client = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("spec-runner"))
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

    /// <summary>
    /// Issue dependencies over GraphQL — Octokit is REST-only and REST does not expose them.
    /// A node's <c>state</c> is <c>OPEN</c> or <c>CLOSED</c>; only open blockers hold an item.
    /// Throws on transport or GraphQL failure rather than reporting "unblocked": a silently
    /// swallowed error would schedule work whose dependencies are unknown.
    /// </summary>
    public async Task<IReadOnlyList<BlockingIssue>> GetOpenBlockersAsync(
        int number, CancellationToken ct = default)
    {
        const string Query = """
            query($owner:String!, $repo:String!, $number:Int!) {
              repository(owner:$owner, name:$repo) {
                issue(number:$number) {
                  blockedBy(first:50) { totalCount nodes { number title state } }
                }
              }
            }
            """;

        var payload = JsonSerializer.Serialize(new
        {
            query = Query,
            variables = new { owner = _owner, repo = _repo, number },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("spec-runner", "1.0"));

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"blockedBy query failed for #{number}: HTTP {(int)response.StatusCode} — {json}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Child(root, "errors") is { ValueKind: JsonValueKind.Array } errors && errors.GetArrayLength() > 0)
        {
            var message = Child(errors[0], "message")?.GetString() ?? "unspecified error";
            throw new InvalidOperationException($"blockedBy query failed for #{number}: {message}");
        }

        // Any level can come back null (unknown repo, unknown issue); a null anywhere means no
        // dependency information to act on, which reads the same as "not blocked".
        var blockers = new List<BlockingIssue>();
        if (Child(root, "data") is { } data &&
            Child(data, "repository") is { } repository &&
            Child(repository, "issue") is { } issue &&
            Child(issue, "blockedBy") is { } blockedBy &&
            Child(blockedBy, "nodes") is { ValueKind: JsonValueKind.Array } nodes)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var state = Child(node, "state")?.GetString();
                if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                blockers.Add(new BlockingIssue(
                    Child(node, "number")?.GetInt32() ?? 0,
                    Child(node, "title")?.GetString() ?? string.Empty));
            }
        }

        return blockers;
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

    /// <summary>A present, non-null child of a JSON object, or null — GraphQL nulls any level.</summary>
    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var child) &&
        child.ValueKind != JsonValueKind.Null
            ? child
            : null;

    private static WorkItem ToWorkItem(Issue issue) => new(
        Number: issue.Number,
        Title: issue.Title ?? string.Empty,
        Body: issue.Body ?? string.Empty,
        AuthorLogin: issue.User?.Login ?? string.Empty,
        AuthorId: issue.User?.Id ?? 0,
        Labels: issue.Labels.Select(l => l.Name).ToList());
}
