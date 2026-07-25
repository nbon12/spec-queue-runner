using SpecRunner.Adapters.GitHub;
using SpecRunner.Domain;
using SpecRunner.Ticking;

namespace SpecRunner.Cli;

/// <summary>
/// A thin, live vertical slice of the tick — enough to demonstrate the spine end to end against
/// a real issue: select the lowest-numbered ready item, enforce the operator allowlist, run
/// intake (infer kind, post the decision comment, label), all through the real GitHub port.
/// Not the full pipeline; the reusable pieces (port, identity, intake) are production code.
///
/// Usage: spec-runner demo &lt;owner/repo&gt; &lt;operator-login&gt;   (token via GH_TOKEN env)
/// </summary>
public static class DemoCommand
{
    public static async Task<int> RunAsync(string slug, string operatorLogin)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("GH_TOKEN not set.");
            return (int)ExitCode.EnvironmentFailure;
        }

        var github = new GitHubClient(slug, token);

        // Allowlist: resolve the operator's numeric id, fail closed if we can't (FR-005).
        var operatorId = await OperatorIdentity.ResolveAsync(github, operatorLogin).ConfigureAwait(false);
        if (!operatorId.Resolved)
        {
            Console.Error.WriteLine($"could not resolve operator '{operatorLogin}' — failing closed, no work done.");
            return (int)ExitCode.EnvironmentFailure;
        }

        Console.WriteLine($"operator '{operatorLogin}' resolved; allowlist active.");

        // Work selection: lowest-numbered open status/ready issue (FR-009).
        var ready = await github.ListOpenIssuesWithLabelAsync("status/ready").ConfigureAwait(false);
        var item = ready.Count > 0 ? ready[0] : null;
        if (item is null)
        {
            Console.WriteLine("no status/ready items — nothing to do.");
            return (int)ExitCode.Ok;
        }

        Console.WriteLine($"selected #{item.Number}: {item.Title}");

        // Author allowlist gate: content from anyone but the operator is ignored entirely (FR-005).
        if (!operatorId.IsOperator(item))
        {
            Console.Error.WriteLine(
                $"#{item.Number} authored by '{item.AuthorLogin}' (id {item.AuthorId}), not the operator — ignoring.");
            return (int)ExitCode.Ok;
        }

        // Intake: infer kind, report it as a decision comment, apply labels (FR-016).
        var classification = Intake.Classify(item);
        Console.WriteLine($"intake inferred: {classification.Kind} — {classification.Reasoning}");

        var comment = IdempotentDecisionComment(item, classification);
        if (item.Body.Contains(DecisionMarker, StringComparison.Ordinal) ||
            await AlreadyCommentedAsync(github, item, classification).ConfigureAwait(false))
        {
            Console.WriteLine("intake decision already recorded — not duplicating.");
        }
        else
        {
            await github.AddCommentAsync(item.Number, comment).ConfigureAwait(false);
            Console.WriteLine("posted intake decision comment.");
        }

        var labels = new List<string> { Intake.LabelFor(classification.Kind), "stage/intake" };
        var toAdd = labels.Where(l => !item.HasLabel(l)).ToList();
        if (toAdd.Count > 0)
        {
            await github.AddLabelsAsync(item.Number, toAdd).ConfigureAwait(false);
            Console.WriteLine($"applied labels: {string.Join(", ", toAdd)}");
        }
        else
        {
            Console.WriteLine("labels already applied.");
        }

        Console.WriteLine("intake complete.");
        return (int)ExitCode.Ok;
    }

    private const string DecisionMarker = "<!-- spec-runner:v1 kind=decision";

    private static string IdempotentDecisionComment(WorkItem item, Classification c)
    {
        var id = $"intake-{item.Number}-{c.Kind}".ToLowerInvariant();
        return $"""
            {DecisionMarker} id={id} -->
            **Decision** — classified as `{Intake.LabelFor(c.Kind)}`

            - **Ambiguity**: the issue did not state its kind; intake infers rather than asks.
            - **Choice**: `{c.Kind}`
            - **Rationale**: {c.Reasoning}
            - **Reversible**: yes — relabel to re-derive the stage.
            """;
    }

    // A stateless tick re-checks reality rather than trusting a label: scan existing comments for
    // this intake's marker so a re-run never double-posts (research R10 idempotency).
    private static async Task<bool> AlreadyCommentedAsync(GitHubClient github, WorkItem item, Classification c)
    {
        _ = github;
        _ = c;
        // The demo's GetIssue does not fetch comments; the label check below is the practical guard
        // for this slice. Full comment-marker scanning lands with the idempotent-writes task (T065).
        var refreshed = await Task.FromResult(item).ConfigureAwait(false);
        return refreshed.HasLabel("stage/intake");
    }
}
