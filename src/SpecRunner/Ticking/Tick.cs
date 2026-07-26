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
    TextWriter log,
    IClaudeSessionStore? sessions = null,
    Func<TimeOnly>? clock = null,
    Func<DateTimeOffset>? utcClock = null)
{
    // Real collaborators by default; tests inject a fake store and fixed clocks. The store reads
    // Claude Code's on-disk transcripts; the time-of-day clock drives the waking-hours gate
    // (FR-021/026); the wall clock drives stale-reclaim age arithmetic (FR-044).
    private readonly IClaudeSessionStore _sessions =
        sessions ?? new ClaudeSessionStore(DefaultProjectsRoot(config));
    private readonly Func<TimeOnly> _now =
        clock ?? (() => TimeOnly.FromDateTime(DateTime.Now));
    private readonly Func<DateTimeOffset> _utcNow =
        utcClock ?? (() => DateTimeOffset.UtcNow);

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

        // Stale reclaim (FR-044): an item stuck in-progress past the threshold — a run that died
        // mid-stage without reverting — is reset to ready so it isn't wedged. Housekeeping, not the
        // tick's unit of work; live and held items are exempt (they carry different labels).
        await ReclaimStaleAsync(op, ct).ConfigureAwait(false);

        // Live sweep (FR-057): before taking new work, tend any open live session — resume it if
        // its process died, or reap it and advance the item if the operator resolved the block.
        // A healthy, still-blocked session is left alone; other ready work may still proceed
        // alongside it (SC-003), so the sweep only consumes the tick when it acts.
        if (await SweepLiveSessionsAsync(op, ct).ConfigureAwait(false))
        {
            return (int)Cli.ExitCode.Ok;
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

            // `icebox` is the operator deliberately parking an item (issue-conventions.md). It
            // normally works by absence of status/ready, but enforce it here too: a "do not work
            // this" marker that a stray label can override is a trap, not a safeguard.
            if (candidate.HasLabel("icebox"))
            {
                log.WriteLine($"#{candidate.Number} is iceboxed — parked by the operator; skipping.");
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

        // Mark in-progress before invoking Claude (FR-011). If the tick dies mid-stage the label
        // persists and a later tick's stale sweep reclaims it (FR-044); a clean run clears it below,
        // and paths that move the item elsewhere (clarify → waiting/live) simply leave it cleared.
        await github.AddLabelsAsync(item.Number, ["status/in-progress"], ct).ConfigureAwait(false);

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
                await RunClarifyBlockAsync(item, worktreePath, isExecutionBlock: false, ct).ConfigureAwait(false);
                break;
            default:
                await RunSpecKitStageAsync(item, stage.Value, worktreePath, ct).ConfigureAwait(false);
                break;
        }

        // Stage completed without a crash — clear the in-progress marker so the stale sweep never
        // reclaims a healthy item.
        await github.RemoveLabelAsync(item.Number, "status/in-progress", ct).ConfigureAwait(false);
        return (int)Cli.ExitCode.Ok;
    }

    // Reset any operator item stuck in-progress past the staleness threshold (FR-044). The label's
    // own applied-at timestamp is the age; live/held items never carry it, so they're exempt by
    // construction. Idempotent — an item already reclaimed simply isn't in-progress anymore.
    private async Task ReclaimStaleAsync(OperatorIdentity op, CancellationToken ct)
    {
        var inProgress = await github.ListOpenIssuesWithLabelAsync("status/in-progress", ct).ConfigureAwait(false);
        foreach (var item in inProgress)
        {
            if (!op.IsOperator(item))
            {
                continue;
            }

            var since = await github.GetLabelAppliedAtAsync(item.Number, "status/in-progress", ct)
                .ConfigureAwait(false);
            if (since is null ||
                !StaleReclaim.ShouldReclaim(QueueStatus.InProgress, since.Value, _utcNow(), config.StaleHours))
            {
                continue;
            }

            await github.AddCommentAsync(item.Number,
                $"<!-- spec-runner:v1 kind=reclaim id=reclaim-{item.Number} -->\nReclaimed as stale (in-progress > {config.StaleHours}h); returning to ready.",
                ct).ConfigureAwait(false);
            await github.RemoveLabelAsync(item.Number, "status/in-progress", ct).ConfigureAwait(false);
            await github.AddLabelsAsync(item.Number, ["status/ready"], ct).ConfigureAwait(false);
            log.WriteLine($"reclaimed #{item.Number} — stale in-progress reset to ready.");
        }
    }

    private static string DefaultProjectsRoot(InstanceConfig config)
    {
        // Claude Code keeps transcripts under <home>/.claude/projects. The config points at the
        // <home>/.claude.json file, so its directory is the home the projects folder sits beside.
        var configPath = string.IsNullOrEmpty(config.ClaudeConfigPath)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : config.ClaudeConfigPath;
        var home = System.IO.Path.GetDirectoryName(configPath) ?? ".";
        return System.IO.Path.Combine(home, ".claude", "projects");
    }

    // A block (FR-031/018): inside waking hours the operator gets a live session on their phone
    // (FR-021); outside them, the comment fallback holds until morning (FR-026/019). The choice is
    // the only difference — both channels resolve the same open questions.
    private async Task RunClarifyBlockAsync(
        WorkItem item, string worktreePath, bool isExecutionBlock, CancellationToken ct)
    {
        var waking = WakingHours.Parse(config.WakingHours);
        if (waking.Contains(_now()))
        {
            await RunLiveSessionAsync(item, worktreePath, isExecutionBlock, ct).ConfigureAwait(false);
        }
        else
        {
            log.WriteLine("block outside waking hours — using the comment fallback (FR-026).");
            await RunClarifyFallbackAsync(item, worktreePath, ct).ConfigureAwait(false);
        }
    }

    // Spawn the live session (FR-021/022): a detached tmux session in the item's own worktree,
    // Claude launched inside it (registering Remote Control off the full-scope OAuth), the kickoff
    // delivered once the session is ready (FR-021a — never blind), then the resumable conversation
    // id recorded on the issue and the item labelled live. At most one live session per instance
    // (FR-025): if one already exists, this item waits its turn via the fallback.
    private async Task RunLiveSessionAsync(
        WorkItem item, string worktreePath, bool isExecutionBlock, CancellationToken ct)
    {
        var tmux = new Adapters.Tmux.TmuxSessions(processes);

        // FR-025: only one live session at a time. Another instance's/item's session in flight ⇒
        // treat as an establishment failure and fall back (FR-028), so we never contend.
        var mine = LiveSession.TmuxName(item.Number);
        var already = await github.ListOpenIssuesWithLabelAsync("status/live", ct).ConfigureAwait(false);
        if (already.Any(i => i.Number != item.Number))
        {
            log.WriteLine("another live session is open — falling back for this item (FR-025).");
            await RunClarifyFallbackAsync(item, worktreePath, ct).ConfigureAwait(false);
            return;
        }

        await tmux.NewDetachedAsync(mine, worktreePath, ct).ConfigureAwait(false);
        await tmux.SendKeysAsync(mine, "claude", ct).ConfigureAwait(false);

        // Readiness = Claude has written its transcript (the conversation exists). Poll for it
        // rather than sending the kickoff blind (FR-021a). Bounded; if it never appears we reap and
        // fall back rather than hang.
        var sessionId = await PollForConversationIdAsync(worktreePath, ct).ConfigureAwait(false);
        if (sessionId is null)
        {
            log.WriteLine("live session did not initialize — reaping and falling back.");
            await tmux.KillSessionAsync(mine, ct).ConfigureAwait(false);
            await RunClarifyFallbackAsync(item, worktreePath, ct).ConfigureAwait(false);
            return;
        }

        await tmux.SendKeysAsync(mine, LiveSession.Kickoff(item.Number, isExecutionBlock), ct)
            .ConfigureAwait(false);

        // Record the resumable id and flip to live — the only dialogue artifact the issue gets
        // while the live path is in use (FR-022).
        var bodies = await github.GetCommentBodiesAsync(item.Number, ct).ConfigureAwait(false);
        if (LiveSession.RecordedId(bodies) is null)
        {
            await github.AddCommentAsync(item.Number, LiveSession.SessionComment(sessionId), ct)
                .ConfigureAwait(false);
        }

        await github.AddLabelsAsync(item.Number, ["stage/clarify", "status/live"], ct).ConfigureAwait(false);
        await github.RemoveLabelAsync(item.Number, "status/ready", ct).ConfigureAwait(false);
        log.WriteLine($"live session {sessionId} open for #{item.Number}; awaiting the operator.");
    }

    // An operator-authored comment that isn't one of the runner's own marker comments — i.e. a
    // human reply resolving the block by comment while the live session is open (FR-024).
    private async Task<bool> OperatorRepliedAsync(OperatorIdentity op, int number, CancellationToken ct)
    {
        var comments = await github.GetCommentsAsync(number, ct).ConfigureAwait(false);
        return comments.Any(c =>
            op.IsOperatorId(c.AuthorId) &&
            !c.Body.Contains("<!-- spec-runner:", StringComparison.Ordinal));
    }

    private async Task<string?> PollForConversationIdAsync(string worktreePath, CancellationToken ct)
    {
        const int attempts = 30;
        for (var i = 0; i < attempts; i++)
        {
            var id = _sessions.NewestConversationId(worktreePath);
            if (id is not null)
            {
                return id;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return null;
    }

    // Tend open live sessions (FR-047/024). Returns true when the sweep did the tick's one unit of
    // work. Resolution is read from the worktree, not a chat scrape: once the operator has driven
    // the session to write its answers (markers gone), the block is resolved — reap and re-ready.
    // A session whose process died is resumed by conversation id on the same URL; a healthy,
    // still-blocked session is left running so other work can proceed alongside it (SC-003).
    private async Task<bool> SweepLiveSessionsAsync(OperatorIdentity op, CancellationToken ct)
    {
        var live = await github.ListOpenIssuesWithLabelAsync("status/live", ct).ConfigureAwait(false);
        var item = live.FirstOrDefault(op.IsOperator);
        if (item is null)
        {
            return false;
        }

        var worktrees = new WorktreeLifecycle(
            processes, config.Path, config.WorktreesRoot, ResolveClaudeConfig(), config.BaseBranch);
        var worktreePath = await worktrees.EnsureAsync(item.Number, ct).ConfigureAwait(false);
        var tmux = new Adapters.Tmux.TmuxSessions(processes);
        var session = LiveSession.TmuxName(item.Number);

        // Resolved by either path: the session wrote answers into the spec (markers cleared), OR
        // the operator answered by comment while the session was open (FR-024) — a plain reply, not
        // one of the runner's own marker comments. Either closes the now-redundant session.
        var specDir = FindSpecDir(worktreePath);
        var resolvedByWorktree = specDir is null || MarkerCount(specDir) == 0;
        var resolvedByComment = await OperatorRepliedAsync(op, item.Number, ct).ConfigureAwait(false);
        if (resolvedByWorktree || resolvedByComment)
        {
            await tmux.KillSessionAsync(session, ct).ConfigureAwait(false);
            await github.AddCommentAsync(item.Number,
                $"<!-- spec-runner:v1 kind=live-resolved id=live-done-{item.Number} -->\nLive session resolved; resuming the pipeline.",
                ct).ConfigureAwait(false);
            await github.RemoveLabelAsync(item.Number, "status/live", ct).ConfigureAwait(false);
            await github.AddLabelsAsync(item.Number, ["status/ready"], ct).ConfigureAwait(false);
            log.WriteLine($"live session for #{item.Number} resolved — reaped and re-readied.");
            return true;
        }

        // Still blocked: resume the conversation if its tmux session is gone (FR-047). Same id ⇒
        // same Remote Control URL, no re-ask, no duplicate push.
        if (!await tmux.HasSessionAsync(session, ct).ConfigureAwait(false))
        {
            var bodies = await github.GetCommentBodiesAsync(item.Number, ct).ConfigureAwait(false);
            var id = LiveSession.RecordedId(bodies);
            if (id is not null)
            {
                await tmux.NewDetachedAsync(session, worktreePath, ct).ConfigureAwait(false);
                await tmux.SendKeysAsync(session, $"claude --resume {id}", ct).ConfigureAwait(false);
                log.WriteLine($"resumed live session {id} for #{item.Number} on the same URL.");
                return true;
            }
        }

        log.WriteLine($"live session for #{item.Number} healthy and unresolved — leaving it.");
        return false;
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
        var outcome = StageOutcome.From(result);
        log.WriteLine($"{stage}: {outcome} (exit {result.ExitCode})");

        if (outcome is StageResult.Succeeded)
        {
            return;
        }

        // The stage's artifacts are its exit predicate, so a failed run simply does not advance
        // derivation — but say so on the issue, or the item looks stalled for no visible reason.
        await github.AddCommentAsync(item.Number,
            $"""
            <!-- spec-runner:v1 kind=stage-failed id=stage-failed-{item.Number}-{stage} -->
            {(outcome is StageResult.RateLimited
                ? $"**{stage} deferred** — usage limit reached; the runner will retry."
                : $"**{stage} did not complete** — the run failed. The item stays at this stage.")}

            {StageOutcome.Excerpt(result.Combined, "Run output")}
            """, ct).ConfigureAwait(false);
    }

    private async Task RunImplementAsync(WorkItem item, Kind kind, string worktreePath, CancellationToken ct)
    {
        var claude = new ClaudeInvoker(processes, config.PermissionMode);
        var prompt =
            $"Implement this work item in the current repository, making only the changes it asks for:\n\n" +
            $"# {item.Title}\n\n{item.Body}\n\nMake the change, then stop.";
        log.WriteLine("implement: running headless claude …");
        var result = await claude.RunAsync(prompt, worktreePath, ct).ConfigureAwait(false);
        var outcome = StageOutcome.From(result);
        log.WriteLine($"implement: {outcome} (exit {result.ExitCode})");

        // A failed implement may still have written half a change. Committing and opening a PR on
        // that would present partial work as finished, so stop here and let the next tick resume;
        // the worktree persists, so nothing already written is lost (FR-014).
        if (outcome is not StageResult.Succeeded)
        {
            await github.AddCommentAsync(item.Number,
                $"""
                <!-- spec-runner:v1 kind=implement-failed id=implement-failed-{item.Number} -->
                {(outcome is StageResult.RateLimited
                    ? "**Implementation deferred** — usage limit reached; the runner will retry."
                    : "**Implementation did not complete** — the run failed. No pull request was opened.")}

                Any partial work stays on the item's worktree; the next tick resumes from there.

                {StageOutcome.Excerpt(result.Combined, "Run output")}
                """, ct).ConfigureAwait(false);
            log.WriteLine("implement: did not complete; no PR opened.");
            return;
        }

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
        var outcome = StageOutcome.From(result);
        log.WriteLine($"audit: {outcome} (exit {result.ExitCode})");

        // An audit that did not run has no findings. Closing on it would retire the item having
        // reported nothing — the failure mode that makes an audit theatre.
        if (outcome is not StageResult.Succeeded)
        {
            var rateLimited = outcome is StageResult.RateLimited;
            await github.AddCommentAsync(item.Number,
                $"""
                <!-- spec-runner:v1 kind=audit-failed id=audit-failed-{item.Number} -->
                {(rateLimited
                    ? "**Audit deferred** — usage limit reached; the runner will retry."
                    : "**Audit did not complete** — the run failed, so nothing was checked.")}

                This item stays open.

                {StageOutcome.Excerpt(result.Combined, "Run output")}
                """, ct).ConfigureAwait(false);
            await github.AddLabelsAsync(item.Number, ["status/ready"], ct).ConfigureAwait(false);
            log.WriteLine($"#{item.Number} audit did not complete; left open for retry.");
            return;
        }

        // The findings ARE the product of an audit (FR-039), so they go in the comment. Previously
        // this said "findings recorded" while recording none.
        await github.AddCommentAsync(item.Number,
            $"""
            <!-- spec-runner:v1 kind=audit id=audit-{item.Number} -->
            **Audit** — reviewed `{target}` (least recently audited). Read-only; nothing modified.

            {StageOutcome.Excerpt(result.Stdout, "Findings")}

            File follow-ups as new issues if action is needed.
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
        var outcome = StageOutcome.From(result);
        log.WriteLine($"review: {outcome} (exit {result.ExitCode})");

        // A review that did not run tells us NOTHING about the diff. Merging on it would assert a
        // verification that never happened, so the item stays open with its PR intact and the next
        // tick retries. Previously this path posted "no blocking finding" and merged regardless.
        if (outcome is not StageResult.Succeeded)
        {
            var rateLimited = outcome is StageResult.RateLimited;
            var headline = rateLimited
                ? "**Code review deferred** — usage limit reached; the runner will retry."
                : "**Code review did not complete** — the run failed, so the change is NOT verified.";

            await github.AddCommentAsync(item.Number,
                $"""
                <!-- spec-runner:v1 kind=review-failed id=review-failed-{item.Number} -->
                {headline}

                PR #{prNumber} was **not merged** and this item stays open.

                {StageOutcome.Excerpt(result.Combined, "Run output")}
                """, ct).ConfigureAwait(false);

            // Back to ready so a later tick picks it up again (FR-043 for the rate-limit case).
            await github.AddLabelsAsync(item.Number, ["status/ready"], ct).ConfigureAwait(false);
            log.WriteLine($"review: not merging #{prNumber}; item left open for retry.");
            return;
        }

        // Review record (always posted, FR-034f). It reports what the review SAID; it does not
        // assert an absence of findings, because nothing here parses findings yet.
        await github.AddCommentAsync(item.Number,
            $"""
            <!-- spec-runner:v1 kind=review id=review-{item.Number} -->
            **Code review** — ran against PR #{prNumber}.

            {StageOutcome.Excerpt(result.Stdout, "Review output")}
            """, ct).ConfigureAwait(false);
        await github.AddLabelsAsync(item.Number, ["stage/review"], ct).ConfigureAwait(false);

        // Verification gate. A review is a Claude prompt: "review succeeded" means the review
        // PROCESS exited 0, not that the code builds. Without this, a clean-exiting review of
        // non-compiling code merges it. Reviewing and verifying are different claims.
        if (config.AutoMerge && !string.IsNullOrWhiteSpace(config.Verify))
        {
            log.WriteLine($"verify: running `{config.Verify}` …");
            var verification = await processes.RunAsync(
                "bash", ["-lc", config.Verify], worktreePath, ct: ct).ConfigureAwait(false);

            if (verification.ExitCode != 0)
            {
                log.WriteLine($"verify: FAILED (exit {verification.ExitCode}) — not merging PR #{prNumber}.");
                await github.AddCommentAsync(item.Number,
                    $"""
                    <!-- spec-runner:v1 kind=verify-failed id=verify-failed-{item.Number} -->
                    **Verification failed** — the change does not build or its tests do not pass, so
                    PR #{prNumber} was **not merged** and this item stays open.

                    Command: `{config.Verify}`

                    {StageOutcome.Excerpt(verification.Combined, "Verification output")}
                    """, ct).ConfigureAwait(false);
                await github.AddLabelsAsync(item.Number, ["status/ready"], ct).ConfigureAwait(false);
                return;
            }

            log.WriteLine("verify: passed.");
        }
        else if (config.AutoMerge)
        {
            // Say it out loud: auto-merge with no verification is a materially weaker gate.
            log.WriteLine("verify: NO verify command configured — merging without building or testing.");
        }

        if (config.AutoMerge)
        {
            // Digest before merge (FR-033c) — the operator's account of a change they won't approve.
            await github.AddCommentAsync(item.Number,
                $"""
                <!-- spec-runner:v1 kind=digest id=digest-{item.Number} -->
                **Digest** — implemented and reviewed, merging.

                - **What changed**: {item.Title}
                - **Review**: completed; output recorded above (findings are not yet parsed
                  automatically, so read it if the change matters)
                - **Verified**: {(string.IsNullOrWhiteSpace(config.Verify)
                    ? "**no** — no verify command is configured, so this merged without building or testing"
                    : $"yes — `{config.Verify}` passed")}
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
