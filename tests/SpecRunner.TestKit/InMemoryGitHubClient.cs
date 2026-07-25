using SpecRunner.Domain;
using SpecRunner.Ports;

namespace SpecRunner.TestKit;

/// <summary>
/// In-memory GitHub for Tiers 2–3 — no network, no credits (testing constitution). Holds issues,
/// labels, comments, and opened PRs behind the production port so the Tick runs unchanged.
/// </summary>
public sealed class InMemoryGitHubClient : IGitHubClient
{
    private readonly Dictionary<string, long> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<int, MutableIssue> _issues = new();

    public sealed record OpenedPr(string Title, string Head, string BaseBranch);

    public List<OpenedPr> OpenedPrs { get; } = [];

    public void AddUser(string login, long id) => _users[login] = id;

    public MutableIssue AddIssue(int number, string title, string body, string authorLogin, long authorId,
        params string[] labels)
    {
        var issue = new MutableIssue
        {
            Number = number,
            Title = title,
            Body = body,
            AuthorLogin = authorLogin,
            AuthorId = authorId,
            Labels = [.. labels],
            Open = true,
        };
        _issues[number] = issue;
        return issue;
    }

    public MutableIssue Issue(int number) => _issues[number];

    public Task<long?> ResolveUserIdAsync(string login, CancellationToken ct = default) =>
        Task.FromResult(_users.TryGetValue(login, out var id) ? id : (long?)null);

    public Task<IReadOnlyList<WorkItem>> ListOpenIssuesWithLabelAsync(string label, CancellationToken ct = default)
    {
        IReadOnlyList<WorkItem> result = _issues.Values
            .Where(i => i.Open && i.Labels.Contains(label))
            .OrderBy(i => i.Number)
            .Select(i => i.ToWorkItem())
            .ToList();
        return Task.FromResult(result);
    }

    public Task<WorkItem> GetIssueAsync(int number, CancellationToken ct = default) =>
        Task.FromResult(_issues[number].ToWorkItem());

    public Task AddCommentAsync(int number, string body, CancellationToken ct = default)
    {
        _issues[number].Comments.Add(body);
        return Task.CompletedTask;
    }

    public Task AddLabelsAsync(int number, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        foreach (var l in labels)
        {
            if (!_issues[number].Labels.Contains(l))
            {
                _issues[number].Labels.Add(l);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(int number, string label, CancellationToken ct = default)
    {
        _issues[number].Labels.Remove(label);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetCommentBodiesAsync(int number, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_issues[number].Comments);

    public Task<string> CreatePullRequestAsync(
        string title, string body, string head, string baseBranch, CancellationToken ct = default)
    {
        OpenedPrs.Add(new OpenedPr(title, head, baseBranch));
        return Task.FromResult($"https://example/pr/{OpenedPrs.Count}");
    }

    public Task CloseIssueAsync(int number, CancellationToken ct = default)
    {
        _issues[number].Open = false;
        return Task.CompletedTask;
    }

    public sealed class MutableIssue
    {
        public required int Number { get; init; }
        public required string Title { get; init; }
        public required string Body { get; init; }
        public required string AuthorLogin { get; init; }
        public required long AuthorId { get; init; }
        public required List<string> Labels { get; init; }
        public List<string> Comments { get; } = [];
        public bool Open { get; set; }

        public WorkItem ToWorkItem() => new(Number, Title, Body, AuthorLogin, AuthorId, [.. Labels]);
    }
}
