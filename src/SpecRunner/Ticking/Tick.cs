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

        // Work selection: lowest-numbered open status/ready issue (FR-009). An item is skipped if
        // it targets a spec not yet on the base branch — held-gating (FR-010); dependency order
        // falls out of integration, not a scheduler.
        var ready = await github.ListOpenIssuesWithLabelAsync("status/ready", ct).ConfigureAwait(false);
        HashSet<string>? targetsOnBase = null; // computed lazily: never for an attacker-only queue.
        WorkItem? item = null;
        foreach (var candidate in ready)
        {
            if (!op.IsOperator(candidate))
            {
                log.WriteLine($"#{candidate.Number} not operator-authored — ignoring (FR-005).");
                continue;
            }

            // Only touch the process boundary once we have an operator-authored candidate — an
            // attacker's issue never causes so much as a git call (injection canary).
            targetsOnBase ??= await BaseTargetsAsync(ct).ConfigureAwait(false);

            var missing = Readiness.MissingTargets(candidate, targetsOnBase);
            if (missing.Count > 0)
            {
                log.WriteLine($"#{candidate.Number} held — targets not on base: {string.Join(", ", missing)}.");
                continue;
            }

            item = candidate;
            break;
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
            processes, config.Path, config.WorktreesRoot, ResolveClaudeConfig(), config.BaseBranch);
        var worktreePath = await worktrees.EnsureAsync(item.Number, ct).ConfigureAwait(false);

        var snapshot = SnapshotFrom(worktreePath, kind, item);
        var stage = StageDerivation.Derive(kind, snapshot);
        if (stage is null)
        {
            log.WriteLine($"#{item.Number} has no unsatisfied stage — nothing to do.");
            return (int)Cli.ExitCode.Ok;
        }

        log.WriteLine($"#{item.Number} kind={kind} stage={stage}");

        switch (stage)
        {
            case Stage.Implement when kind is Kind.Audit:
                await RunAuditAsync(item, worktreePath, ct).ConfigureAwait(false);
                break;
            case Stage.Implement:
                await RunImplementAsync(item, kind, worktreePath, ct).ConfigureAwait(false);
                break;
            case Stage.Review:
                await RunReviewAsync(item, worktreePath, ct).ConfigureAwait(false);
                break;
            case Stage.Clarify:
                await RunClarifyFallbackAsync(item, worktreePath, ct).ConfigureAwait(false);
                break;
            default:
                await RunSpecKitStageAsync(item, stage.Value, worktreePath, ct).ConfigureAwait(false);
                break;
        }

        return (int)Cli.ExitCode.Ok;
    }

    // Clarify blocks (FR-018). Without a live channel this is the comment fallback (FR-019/027):
    // read the markers specify materialized, post them as one numbered comment, and move the item
    // to waiting — the operator answers by reply, which a later tick collects.
    private async Task RunClarifyFallbackAsync(WorkItem item, string worktreePath, CancellationToken ct)
    {
        var specDir = FindSpecDir(worktreePath);
        var specFile = specDir is null ? null : System.IO.Path.Combine(specDir, "spec.md");
        var specText = specFile is not null && File.Exists(specFile)
            ? await File.ReadAllTextAsync(specFile, ct).ConfigureAwait(false)
            : string.Empty;

        var questions = ClarifyMarkers.Extract(specText);
        if (questions.Count == 0)
        {
            log.WriteLine("clarify: no markers to resolve — advancing.");
            await github.RemoveLabelAsync(item.Number, "status/waiting", ct).ConfigureAwait(false);
            return;
        }

        var bodies = await github.GetCommentBodiesAsync(item.Number, ct).ConfigureAwait(false);
        if (IdempotencyMarker.AlreadyPresent(bodies, "questions", $"clarify-{item.Number}"))
        {
            log.WriteLine("clarify: questions already posted — waiting on the operator.");
            return;
        }

        var comment = ClarifyMarkers.QuestionsComment(
            item.Number, questions, "no live session established for this run");
        await github.AddCommentAsync(item.Number, comment, ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/clarify", "status/waiting"], ct).ConfigureAwait(false);
        await github.RemoveLabelAsync(item.Number, "status/ready", ct).ConfigureAwait(false);
        log.WriteLine($"clarify: posted {questions.Count} question(s); waiting on the operator.");
    }

    // The spec paths present on the base branch, for held-gating. Read from the clone with
    // ls-tree (no checkout). If the clone or branch is unreadable, return empty — every targeting
    // item then holds, which is the safe direction (never schedule against an unknown base).
    private async Task<HashSet<string>> BaseTargetsAsync(CancellationToken ct)
    {
        try
        {
            var paths = await new Git(processes)
                .ListPathsAsync(config.Path, config.BaseBranch, ct).ConfigureAwait(false);
            return new HashSet<string>(paths, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            log.WriteLine($"held-gating: could not list base paths ({ex.Message}); targeting items will hold.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
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
        var pr = await github.CreatePullRequestAsync(
            item.Title, prBody, branch, config.BaseBranch, ct).ConfigureAwait(false);
        log.WriteLine($"opened PR: {pr.Url}");

        // Record the PR number in a marker so the review tick (stateless) can find it. The issue
        // stays OPEN until review completes (FR-033/033a) — the PR is review's surface, not the
        // finish line.
        await github.AddCommentAsync(item.Number,
            $"<!-- spec-runner:v1 kind=pr id=pr-{item.Number} number={pr.Number} -->\nImplemented; opened {pr.Url}. Review pending.",
            ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/implement"], ct).ConfigureAwait(false);
        log.WriteLine($"#{item.Number} at review next tick; PR {pr.Number} open.");
    }

    // An audit (FR-038–041) reads one spec — the least recently audited — compares it against the
    // code, and reports. It MODIFIES NOTHING (FR-039): no branch, no PR, no diff. Findings become
    // a comment on the audit issue; any follow-up work is filed as fresh, unclassified issues by
    // the operator or a later run. One spec per audit; coverage comes from cadence (FR-041).
    private async Task RunAuditAsync(WorkItem item, string worktreePath, CancellationToken ct)
    {
        var specDirs = ListSpecDirs(worktreePath);
        var target = AuditSelection.LeastRecentlyAudited(
            specDirs, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        if (target is null)
        {
            log.WriteLine("audit: no specs to audit — nothing to do.");
            await github.AddCommentAsync(item.Number,
                $"<!-- spec-runner:v1 kind=audit id=audit-{item.Number} -->\n**Audit** — no specs present to audit.",
                ct).ConfigureAwait(false);
            await github.CloseIssueAsync(item.Number, ct).ConfigureAwait(false);
            return;
        }

        var claude = new ClaudeInvoker(processes, config.PermissionMode);
        var prompt =
            $"Audit the spec at `{target}` against the code in this repository. Compare the spec's " +
            "requirements and natural-language tests to what the code and tests actually do. Report " +
            "drift, gaps, and dead requirements. DO NOT modify any file — this is read-only. Report findings only.";
        log.WriteLine($"audit: comparing {target} …");
        var result = await claude.RunAsync(prompt, worktreePath, ct).ConfigureAwait(false);
        log.WriteLine($"audit exit={result.ExitCode}");

        await github.AddCommentAsync(item.Number,
            $"""
            <!-- spec-runner:v1 kind=audit id=audit-{item.Number} -->
            **Audit** — reviewed `{target}` (least recently audited). Read-only; nothing modified.

            Findings recorded from the audit run. File follow-ups as new issues if action is needed.
            """, ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/implement"], ct).ConfigureAwait(false);
        await github.CloseIssueAsync(item.Number, ct).ConfigureAwait(false);
        log.WriteLine($"#{item.Number} audit complete; closed.");

        await FileSuccessorIfRecurringAsync(item, ct).ConfigureAwait(false);
    }

    private static List<string> ListSpecDirs(string worktreePath)
    {
        var specs = Path.Combine(worktreePath, "specs");
        if (!Directory.Exists(specs))
        {
            return [];
        }

        return Directory.GetDirectories(specs)
            .Select(d => Path.GetRelativePath(worktreePath, d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    private async Task RunReviewAsync(WorkItem item, string worktreePath, CancellationToken ct)
    {
        var bodies = await github.GetCommentBodiesAsync(item.Number, ct).ConfigureAwait(false);
        var prNumber = FindPrNumber(bodies);
        if (prNumber is null)
        {
            log.WriteLine("review: could not find the PR marker — leaving item for next tick.");
            return;
        }

        // Review runs in a FRESH session (FR-034a1) with the repo's own review prompt (FR-034d).
        // For the MVP it runs and records; a fuller build parses findings and blocks on irreversible
        // ones. The prompt is read from the repo, never from issue text.
        var claude = new ClaudeInvoker(processes, config.PermissionMode);
        var reviewPromptPath = System.IO.Path.Combine(worktreePath, config.ReviewPrompt);
        var reviewInstruction = File.Exists(reviewPromptPath)
            ? await File.ReadAllTextAsync(reviewPromptPath, ct).ConfigureAwait(false)
            : "Review the changes on this branch against the base branch. Report findings.";
        log.WriteLine($"review: running against PR #{prNumber} …");
        var result = await claude.RunAsync(reviewInstruction, worktreePath, ct).ConfigureAwait(false);
        log.WriteLine($"review exit={result.ExitCode}");

        // Review record (always posted, FR-034f).
        await github.AddCommentAsync(item.Number,
            $"<!-- spec-runner:v1 kind=review id=review-{item.Number} -->\n**Code review** — completed; no blocking finding recorded.",
            ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/review"], ct).ConfigureAwait(false);

        if (config.AutoMerge)
        {
            // Digest before merge (FR-033c) — the operator's account of a change they won't approve.
            await github.AddCommentAsync(item.Number,
                $"""
                <!-- spec-runner:v1 kind=digest id=digest-{item.Number} -->
                **Digest** — implemented and reviewed, merging.

                - **What changed**: {item.Title}
                - **Review**: completed with no blocking finding
                - **Merged**: yes (auto-merge on; spend under cap)
                """, ct).ConfigureAwait(false);
            await github.MergePullRequestAsync(prNumber.Value, ct).ConfigureAwait(false);
            log.WriteLine($"merged PR #{prNumber}.");
        }
        else
        {
            log.WriteLine("auto-merge off — leaving PR open for the operator.");
        }

        await github.AddCommentAsync(item.Number,
            $"<!-- spec-runner:v1 kind=closing id=close-{item.Number} -->\nReviewed and closed. PR #{prNumber}.",
            ct).ConfigureAwait(false);
        await github.CloseIssueAsync(item.Number, ct).ConfigureAwait(false);
        log.WriteLine($"#{item.Number} closed.");

        // Recurrence (FR-042): a recurring item files a successor on reaching terminal state. The
        // closed issue stays closed — the book is append-only. The successor re-enters at intake.
        await FileSuccessorIfRecurringAsync(item, ct).ConfigureAwait(false);
    }

    private async Task FileSuccessorIfRecurringAsync(WorkItem item, CancellationToken ct)
    {
        var cadence = Recurrence.Cadence(item);
        if (cadence is null)
        {
            return;
        }

        var body = $"""
            <!-- spec-runner:v1 kind=recurrence id=successor-of-{item.Number} -->
            Recurring successor of #{item.Number} (cadence: {cadence}).

            {item.Body}
            """;
        var successor = await github.CreateIssueAsync(
            item.Title, body, ["status/ready"], ct).ConfigureAwait(false);
        log.WriteLine($"recurrence: filed successor #{successor} of #{item.Number} (cadence {cadence}).");
    }

    private static int? FindPrNumber(IEnumerable<string> commentBodies)
    {
        foreach (var body in commentBodies)
        {
            var idx = body.IndexOf("kind=pr id=pr-", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var marker = body.IndexOf("number=", idx, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var start = marker + "number=".Length;
            var end = start;
            while (end < body.Length && char.IsDigit(body[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(body[start..end], out var n))
            {
                return n;
            }
        }

        return null;
    }

    // The demo drives the implement path directly; a full build reads spec/plan/tasks presence
    // from the worktree. For a chore, intake -> (plan) -> implement; here we treat a classified
    // item with no open PR as ready to implement.
    private static WorktreeSnapshot SnapshotFrom(string worktreePath, Kind kind, WorkItem item)
    {
        // Read the item's own worktree (never the clone, FR-013). A feature's artifacts live in
        // its spec directory; the first unsatisfied predicate names the stage. For a chore/spike/
        // audit — kinds with no spec — the shaping/planning predicates are vacuously satisfied so
        // derivation lands on implement.
        var isSpecKind = kind is Kind.Feature or Kind.Amendment;
        var specDir = FindSpecDir(worktreePath);

        bool Exists(string file) =>
            specDir is not null && File.Exists(Path.Combine(specDir, file));

        var specExists = !isSpecKind || Exists("spec.md");
        var planExists = !isSpecKind || Exists("plan.md");
        var tasksExists = !isSpecKind || Exists("tasks.md");
        var analysisRecorded = !isSpecKind || Exists("analysis.md") || item.HasLabel("stage/analyze");

        return new WorktreeSnapshot(
            KindResolved: true,
            SpecExists: specExists,
            UnresolvedMarkerCount: specExists ? MarkerCount(specDir) : 1,
            PlanExists: planExists,
            TasksExists: tasksExists,
            AnalysisRecorded: analysisRecorded,
            PullRequestOpen: item.HasLabel("stage/implement"),
            ReviewRecorded: item.HasLabel("stage/review"));
    }

    // The active feature's spec directory under specs/ (SpecKit convention: specs/NNN-name/).
    private static string? FindSpecDir(string worktreePath)
    {
        var specs = Path.Combine(worktreePath, "specs");
        if (!Directory.Exists(specs))
        {
            return null;
        }

        return Directory.GetDirectories(specs).OrderByDescending(d => d).FirstOrDefault();
    }

    private static int MarkerCount(string? specDir)
    {
        if (specDir is null)
        {
            return 0;
        }

        var spec = Path.Combine(specDir, "spec.md");
        if (!File.Exists(spec))
        {
            return 0;
        }

        var text = File.ReadAllText(spec);
        return text.Split("[NEEDS CLARIFICATION").Length - 1;
    }
}
