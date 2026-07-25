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
    private readonly Dictionary<(int, string), DateTimeOffset> _labeledAt = new();

    /// <summary>Clock for label-application timestamps; tests override for determinism.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Seed exactly when a label was applied (for stale-reclaim timing tests).</summary>
    public void SetLabeledAt(int number, string label, DateTimeOffset when) => _labeledAt[(number, label)] = when;

    public sealed record OpenedPr(int Number, string Title, string Head, string BaseBranch);

    public List<OpenedPr> OpenedPrs { get; } = [];
    public List<int> MergedPrs { get; } = [];

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
        _issues[number].Authored.Add(new IssueComment(0, body)); // 0 = the runner itself
        return Task.CompletedTask;
    }

    /// <summary>Seed a comment from a specific author (e.g. the operator's live-session reply).</summary>
    public void AddComment(int number, long authorId, string body)
    {
        _issues[number].Comments.Add(body);
        _issues[number].Authored.Add(new IssueComment(authorId, body));
    }

    public Task<IReadOnlyList<IssueComment>> GetCommentsAsync(int number, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IssueComment>>(_issues[number].Authored);

    public Task AddLabelsAsync(int number, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        foreach (var l in labels)
        {
            if (!_issues[number].Labels.Contains(l))
            {
                _issues[number].Labels.Add(l);
            }

            _labeledAt[(number, l)] = Now();
        }

        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLabelAppliedAtAsync(int number, string label, CancellationToken ct = default) =>
        Task.FromResult(_labeledAt.TryGetValue((number, label), out var when) ? when : (DateTimeOffset?)null);

    public Task RemoveLabelAsync(int number, string label, CancellationToken ct = default)
    {
        _issues[number].Labels.Remove(label);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetCommentBodiesAsync(int number, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_issues[number].Comments);

    public Task<PullRequestRef> CreatePullRequestAsync(
        string title, string body, string head, string baseBranch, CancellationToken ct = default)
    {
        var number = OpenedPrs.Count + 1000;
        OpenedPrs.Add(new OpenedPr(number, title, head, baseBranch));
        return Task.FromResult(new PullRequestRef(number, $"https://example/pr/{number}"));
    }

    public Task MergePullRequestAsync(int number, CancellationToken ct = default)
    {
        MergedPrs.Add(number);
        return Task.CompletedTask;
    }

    public Task CloseIssueAsync(int number, CancellationToken ct = default)
    {
        _issues[number].Open = false;
        return Task.CompletedTask;
    }

    public Task<int> CreateIssueAsync(
        string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var number = _issues.Keys.DefaultIfEmpty(0).Max() + 1;
        AddIssue(number, title, body, authorLogin: "spec-runner", authorId: 0, [.. labels]);
        return Task.FromResult(number);
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
        public List<IssueComment> Authored { get; } = [];
        public bool Open { get; set; }

        public WorkItem ToWorkItem() => new(Number, Title, Body, AuthorLogin, AuthorId, [.. Labels]);
    }
}
