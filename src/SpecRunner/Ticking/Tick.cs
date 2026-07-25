using SpecRunner.Adapters.Claude;
using SpecRunner.Adapters.Git;
using SpecRunner.Configuration;
using SpecRunner.Domain;
using SpecRunner.Ports;

namespace SpecRunner.Ticking;

/// <summary>
/// One tick: lock → resolve operator → select one ready item → ensure its worktree → run the
/// item's current stage → report, then exit (constitution §3, FR-002/003/009). Stateless: all
/// authority is GitHub + the worktree; nothing is remembered between ticks.
///
/// This is the MVP spine. Intake runs in-process; execution stages run headless Claude; on
/// implement completion the branch is committed, pushed, and a PR opened. The remaining stages
/// (specify/clarify/plan/tasks/analyze as full SpecKit runs, review, merge) are dispatched
/// through the same StageCommand/ClaudeInvoker machinery this loop already uses.
/// </summary>
public sealed class Tick(
    InstanceConfig config,
    IGitHubClient github,
    IProcessRunner processes,
    TextWriter log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var lockPath = string.IsNullOrEmpty(config.Lock)
            ? System.IO.Path.Combine(config.WorktreesRoot, ".tick.lock")
            : config.Lock;

        using var instanceLock = InstanceLock.TryAcquire(lockPath);
        if (instanceLock is null)
        {
            log.WriteLine("lock held by another tick — exiting 0.");
            return (int)Cli.ExitCode.Ok;
        }

        // Allowlist: resolve the operator's numeric id once; fail closed (FR-005, R5).
        var op = await OperatorIdentity.ResolveAsync(github, config.OperatorLogin, ct).ConfigureAwait(false);
        if (!op.Resolved)
        {
            log.WriteLine($"could not resolve operator '{config.OperatorLogin}' — failing closed.");
            return (int)Cli.ExitCode.EnvironmentFailure;
        }

        // Work selection: lowest-numbered open status/ready issue (FR-009).
        var ready = await github.ListOpenIssuesWithLabelAsync("status/ready", ct).ConfigureAwait(false);
        WorkItem? item = null;
        foreach (var candidate in ready)
        {
            if (op.IsOperator(candidate))
            {
                item = candidate;
                break;
            }

            log.WriteLine($"#{candidate.Number} not operator-authored — ignoring (FR-005).");
        }

        if (item is null)
        {
            log.WriteLine("nothing ready — exiting.");
            return (int)Cli.ExitCode.Ok;
        }

        log.WriteLine($"selected #{item.Number}: {item.Title}");

        // Intake is its own unit of work (one unit per tick, FR-009): a fresh, unclassified item
        // is classified and reported, and the next tick picks it up past intake.
        var kindFromLabels = KindFromLabels(item);
        if (kindFromLabels is null)
        {
            await RunIntakeAsync(item, ct).ConfigureAwait(false);
            return (int)Cli.ExitCode.Ok;
        }

        var kind = kindFromLabels.Value;
        var worktrees = new WorktreeLifecycle(
            processes, config.Path, config.WorktreesRoot, ResolveClaudeConfig());
        var worktreePath = await worktrees.EnsureAsync(item.Number, ct).ConfigureAwait(false);

        var snapshot = SnapshotFrom(worktreePath, kind, item);
        var stage = StageDerivation.Derive(kind, snapshot);
        if (stage is null)
        {
            log.WriteLine($"#{item.Number} has no unsatisfied stage — nothing to do.");
            return (int)Cli.ExitCode.Ok;
        }

        log.WriteLine($"#{item.Number} kind={kind} stage={stage}");

        if (stage == Stage.Implement)
        {
            await RunImplementAsync(item, kind, worktreePath, ct).ConfigureAwait(false);
        }
        else
        {
            await RunSpecKitStageAsync(item, stage.Value, worktreePath, ct).ConfigureAwait(false);
        }

        return (int)Cli.ExitCode.Ok;
    }

    private string ResolveClaudeConfig() =>
        string.IsNullOrEmpty(config.ClaudeConfigPath)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : config.ClaudeConfigPath;

    private static Kind? KindFromLabels(WorkItem item)
    {
        foreach (var label in item.Labels)
        {
            if (label.StartsWith("kind/", StringComparison.Ordinal) &&
                Enum.TryParse<Kind>(label["kind/".Length..], ignoreCase: true, out var kind))
            {
                return kind;
            }
        }

        return null;
    }

    private async Task<Kind> RunIntakeAsync(WorkItem item, CancellationToken ct)
    {
        var classification = Intake.Classify(item);
        log.WriteLine($"intake: {classification.Kind} — {classification.Reasoning}");

        var kindLabel = Intake.LabelFor(classification.Kind);
        if (!item.HasLabel(kindLabel))
        {
            var body = $"""
                <!-- spec-runner:v1 kind=decision id=intake-{item.Number}-{classification.Kind} -->
                **Decision** — classified as `{kindLabel}`

                - **Choice**: `{classification.Kind}`
                - **Rationale**: {classification.Reasoning}
                - **Reversible**: yes — relabel to re-derive the stage.
                """;
            await github.AddCommentAsync(item.Number, body, ct).ConfigureAwait(false);
            await github.AddLabelsAsync(item.Number, [kindLabel, "stage/intake"], ct).ConfigureAwait(false);
            log.WriteLine("intake decision recorded.");
        }

        return classification.Kind;
    }

    private async Task RunSpecKitStageAsync(WorkItem item, Stage stage, string worktreePath, CancellationToken ct)
    {
        var prompt = StageCommand.PromptFor(stage, item.Title);
        if (prompt is null)
        {
            log.WriteLine($"stage {stage} is runner-driven; no SpecKit command.");
            return;
        }

        var claude = new ClaudeInvoker(processes, config.PermissionMode);
        log.WriteLine($"running {prompt} headlessly …");
        var result = await claude.RunAsync(prompt, worktreePath, ct).ConfigureAwait(false);
        log.WriteLine($"{stage} exit={result.ExitCode}");
    }

    private async Task RunImplementAsync(WorkItem item, Kind kind, string worktreePath, CancellationToken ct)
    {
        var claude = new ClaudeInvoker(processes, config.PermissionMode);
        var prompt =
            $"Implement this work item in the current repository, making only the changes it asks for:\n\n" +
            $"# {item.Title}\n\n{item.Body}\n\nMake the change, then stop.";
        log.WriteLine("implement: running headless claude …");
        var result = await claude.RunAsync(prompt, worktreePath, ct).ConfigureAwait(false);
        log.WriteLine($"implement exit={result.ExitCode}");

        var git = new Git(processes);
        if (!await git.HasChangesAsync(worktreePath, ct).ConfigureAwait(false))
        {
            log.WriteLine("implement produced no changes — leaving item open.");
            return;
        }

        var branch = WorktreeLifecycle.BranchFor(item.Number);
        await git.CommitAllAsync(worktreePath, $"implement #{item.Number}: {item.Title}", ct).ConfigureAwait(false);
        var sha = await git.HeadShaAsync(worktreePath, ct).ConfigureAwait(false);
        log.WriteLine($"committed {sha[..Math.Min(7, sha.Length)]} on {branch}");

        await git.PushAsync(worktreePath, branch, ct).ConfigureAwait(false);
        log.WriteLine($"pushed {branch}");

        var prBody = $"""
            Closes #{item.Number}.

            Generated by the spec-queue-runner from issue #{item.Number} ({kind}).
            Review and merge is the operator's gate.
            """;
        var url = await github.CreatePullRequestAsync(
            item.Title, prBody, branch, BaseBranch, ct).ConfigureAwait(false);
        log.WriteLine($"opened PR: {url}");

        await github.AddCommentAsync(item.Number,
            $"<!-- spec-runner:v1 kind=closing id=close-{item.Number} -->\nImplemented and opened {url}.",
            ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/implement"], ct).ConfigureAwait(false);
        await github.CloseIssueAsync(item.Number, ct).ConfigureAwait(false);
        log.WriteLine($"#{item.Number} closed; PR open for review.");
    }

    // The repo's integration branch. Configurable in a fuller build; the demo repo uses this.
    private const string BaseBranch = "master";

    // The demo drives the implement path directly; a full build reads spec/plan/tasks presence
    // from the worktree. For a chore, intake -> (plan) -> implement; here we treat a classified
    // item with no open PR as ready to implement.
    private static WorktreeSnapshot SnapshotFrom(string worktreePath, Kind kind, WorkItem item)
    {
        _ = kind;
        _ = worktreePath;
        // MVP: the shaping/planning stages are treated as satisfied so a classified item derives
        // to Implement. A full build reads spec/plan/tasks presence from the worktree and runs
        // those SpecKit stages first (the machinery — StageCommand/ClaudeInvoker — is identical).
        return new WorktreeSnapshot(
            KindResolved: true,
            SpecExists: true,
            UnresolvedMarkerCount: 0,
            PlanExists: true,
            TasksExists: true,
            AnalysisRecorded: true,
            PullRequestOpen: item.HasLabel("stage/implement"),
            ReviewRecorded: false);
    }
}
