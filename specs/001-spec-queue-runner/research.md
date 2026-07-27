# Phase 0 Research: Spec Queue Runner

**Feature**: 001-spec-queue-runner | **Date**: 2026-07-25

Each decision below resolves an unknown in the plan's Technical Context. The constitution
(`.specify/memory/constitution.md`) fixes most of the stack; this document resolves what it
deliberately left open, plus the environment facts discovered on the target machine.

> **SUPERSEDED IN PART by R15 (2026-07-25 architecture probe).** The runner is now
> **containerized**, so R1 and R12's "install on the host" framing no longer applies — .NET 10
> and tmux are **image layers**, not host installs. R4/R6's macOS-Keychain adapter is replaced
> by a mounted-secret adapter. R14's live-channel unknowns (OQ-3/4/5) are **resolved favorably**.
> See R15 below and `probe/probe-results.md`.

## Environment baseline (measured 2026-07-25)

| Tool | Found | Consequence |
|---|---|---|
| `dotnet` | SDKs 8.0.404, 9.0.303 | **No .NET 10 SDK** — see R1 |
| `git` | 2.39.5 (Apple Git-154) | Worktree support present (needs ≥2.5); fine |
| `tmux` | **not installed** | **Blocking prerequisite** — see R12 |
| `claude` | 2.1.220 | Present; user's shell alias adds `--dangerously-skip-permissions`, which the runner MUST NOT inherit (R6) |
| `gh` | 2.76.2 | Present but deliberately unused — see R3 |
| `security` | /usr/bin/security | Keychain access path available (R4) |

---

## R1 — Target framework

**Decision**: Target `net10.0`. Add "install .NET 10 SDK" as an explicit setup task and a
`doctor` check.

**Rationale**: .NET 10 is the current LTS (released Nov 2025, supported through Nov 2028).
Neither installed SDK is a good long-term target: .NET 9 is STS and its support window closed
in May 2026, and .NET 8's LTS window closes Nov 2026 — inside this project's expected life.
Committing to an out-of-support runtime for an unattended agent with repo write access is the
wrong trade.

**Alternatives considered**: `net9.0` — works on the box today with zero setup, rejected as
already out of support. `net8.0` — installed and LTS, rejected because it expires within ~16
months and would force a migration mid-life. Multi-targeting — rejected as pure overhead for a
single-deployment binary.

**Consequence**: The build does not work on this machine until the SDK is installed. This is a
Phase 1 setup task, not a surprise at implement time.

## R2 — TOML configuration parsing

**Decision**: `Tomlyn` for parsing and model binding.

**Rationale**: Actively maintained, pure managed code (no native dependency to complicate
single-file publish), spec-compliant TOML v1.0.0, and — most relevant here — it reports
syntax errors with line/column, which the fail-fast startup validator (constitution §7) needs
to produce an actionable message rather than "config invalid".

**Alternatives considered**: `Tommy` (single-file, minimal) — rejected: weaker diagnostics.
Hand-rolled parser — rejected: the config surface is small but the error-reporting surface is
not, and §8 requires justifying dependencies, which this clears easily.

## R3 — GitHub access

**Decision**: `Octokit.net`, hidden behind a first-party `IGitHubClient` port defined by this
project (not Octokit's own interface).

**Rationale**: Pagination, conditional requests, rate-limit headers, and retry semantics are
tedious and easy to get subtly wrong by hand. The port keeps Octokit out of the domain layer
so the in-memory fake used by Tiers 2–3 substitutes cleanly, per constitution §5.

**Alternatives considered**: Raw `HttpClient` — viable (the constitution permits either) but
costs pagination and error-shape handling for no gain. **The `gh` CLI — explicitly rejected**,
despite being installed: it authenticates as whatever identity the operator's `gh` login holds,
which defeats both the fine-grained per-repo PAT scoping (§6) and the authenticated-author
verification the allowlist depends on (R5). The runner's credential must be its own.

## R4 — Keychain secret retrieval

**Decision**: Shell out to `/usr/bin/security find-generic-password -w -s <service> -a <account>`
through the same `IProcessRunner` port used for git/tmux/claude.

**Rationale**: No native interop, no P/Invoke marshalling, and it rides the process boundary
that is already faked in tests. It also keeps the macOS-specific surface to exactly one adapter,
which is what the constitution's portability rule (§2) asks for — a Linux port replaces this
adapter with a `libsecret` or file-mode-600 equivalent and touches nothing else.

**Alternatives considered**: P/Invoke to `Security.framework` — rejected: native interop,
awkward to fake, and it hard-codes macOS into the binary rather than into an adapter.
Environment variable or config field — rejected outright by §2 and §6.

## R5 — Operator identity verification

**Decision**: Resolve the configured operator login to its **numeric GitHub user ID** once per
tick (`GET /users/{login}`), then compare every issue's and comment's `author.id` against that
number. Never compare login strings, display names, or body content.

**Rationale**: This is the mechanism behind constitution §6's non-negotiable single-operator
allowlist and FR-005. Numeric IDs are immutable and never reissued; **logins can be renamed and
the freed name re-registered by someone else** — a login-string comparison is therefore a real
impersonation path, not a theoretical one. Resolving once per tick keeps it to one extra API
call against a 5,000/hour budget.

**Alternatives considered**: Login-string comparison — rejected for the rename/re-registration
hole. Deriving the identity from the PAT owner (`GET /user`) — rejected during clarification;
the operator chose an explicit config field, which additionally allows the token owner and the
operator to diverge without silently widening the allowlist.

**Failure mode**: If the login cannot be resolved, the tick MUST fail closed — treat *all*
content as un-allowlisted and do no work — rather than fall back to string matching.

## R6 — External process invocation

**Decision**: `System.Diagnostics.Process` with `ProcessStartInfo.ArgumentList`, stdout and
stderr drained concurrently on separate tasks, `UseShellExecute = false`, and an explicitly
constructed environment.

**Rationale**: `ArgumentList` is mandated by constitution §4 because prompts contain quotes,
newlines, and backticks. Concurrent draining is not optional: reading one stream to completion
before the other deadlocks as soon as a headless run's output exceeds the pipe buffer, which it
will. The explicit environment matters here specifically — the operator's shell aliases `claude`
to `--dangerously-skip-permissions`, and the runner must invoke the real binary with its own
configured permission mode rather than inherit that.

**Alternatives considered**: Shell string invocation — rejected by the constitution.
`CliWrap` — rejected: a dependency for something the BCL does adequately once draining is
handled correctly.

## R7 — Instance locking

**Decision**: `FileStream` on `<state_dir>/<instance>.lock` with `FileShare.None`, held in a
`using` scope for the tick's lifetime.

**Rationale**: Constitution §2 mandates it. The OS releases the handle on process death of any
kind, so a killed tick cannot wedge the instance — which matters because the Tier 3
crash-convergence tests kill ticks at arbitrary points by design.

**Caveat recorded**: This is advisory within a single filesystem namespace, which is exactly
the scope required — the lock lives **inside the instance's own container** (R15), guarding only
that instance's overlapping ticks, and instances never coordinate (§3). It is not meant to
synchronize across containers or hosts, and does not need to: one instance = one container =
one lock. (An NFS-backed lock path would be unreliable and is not a supported layout.)

## R8 — Scheduling

**Decision**: launchd agent per instance, label `com.spec-runner.<slug>`, `StartInterval` 300
(5 minutes, per the clarification), `RunAtLoad` true.

**Rationale**: FR-001 requires that a missed invocation still runs after wake. launchd runs a
missed `StartInterval` job on wake; cron silently skips it. For a system whose entire premise is
overnight work on a laptop that sleeps, that difference is the requirement, not a preference.

**Alternatives considered**: `cron` — rejected, drops missed runs. A resident daemon with an
internal timer — rejected: contradicts the stateless-tick invariant (§3) and creates exactly the
long-lived process the design avoids.

## R9 — Stage derivation as a pure function

**Decision**: Stage derivation takes an immutable snapshot value (which spec/plan/tasks files
exist, whether unresolved clarification markers remain, whether analysis was recorded) and
returns a stage. Filesystem reading happens in an adapter that produces the snapshot.

**Rationale**: Constitution §4 requires the decision logic be testable without processes,
network, or filesystem. This shape lets Tier 1 drive every stage-derivation case from
`InlineData` fixtures, and keeps the "predicates are evaluated in the item's worktree" rule
(§3, FR-013) a property of *which snapshot is passed in* rather than something re-implemented at
each call site.

## R10 — Crash convergence via idempotency markers

**Decision**: Every runner-authored comment embeds an HTML marker carrying a stable identity:
`<!-- spec-runner:v1 kind=decision id=<sha256-prefix> -->`. Before posting, the runner scans the
issue's existing comments for that identity and skips if present. Label writes are expressed as
"set the label set to this desired state", never as blind add/remove.

**Rationale**: This is the mechanism that makes the Tier 3 crash-convergence property hold —
constitution §3 requires that re-running ticks after a kill at *any* point converges with no
duplicated comments or labels, and FR-046 requires retry after API failure. Retry without
identity produces duplicates; identity without retry loses work. Both are needed, and the marker
is also what lets FR-005 recognize the runner's own comments as its output rather than operator
input.

**Alternatives considered**: A local journal file of completed side effects — rejected: it is
state outside the three permitted stores (worktree, issue, session store, §3) and would itself
need crash-safe writes. Timestamp/content heuristics — rejected as unreliable.

## R11 — The fake `claude` binary

**Decision**: A shell script installed first on `PATH` for tests. It reads a scenario file named
by `SPEC_RUNNER_FAKE_SCENARIO`, and **appends every invocation — full argv and stdin — as one
JSON line** to a recording file named by `SPEC_RUNNER_FAKE_RECORD`.

**Rationale**: The recording file is what makes the Tier 3 injection-canary property assertable
rather than aspirational: the test seeds a non-operator comment containing a canary string and
then asserts the canary appears in no recorded invocation's argv or stdin, across every scenario
in the suite (constitution testing §5). Scenario-driven behaviour covers the rest: emit a spec
with N markers, exit with usage-limit text, emit decision output, hang.

## R12 — tmux is a hard prerequisite and is currently missing

**Decision**: Treat tmux as a first-class prerequisite: `doctor` fails loudly with the exact
install command, and Tier 2 integration tests are skipped-with-reason (never silently passed)
when tmux is absent.

**Rationale**: The live channel is unimplementable without it, and a missing tmux would
otherwise surface as a confusing session-establishment failure that the comment fallback
(FR-027) would quietly paper over — precisely the "silent fallback masks an operator-fixable
problem" failure FR-028 exists to prevent.

**Action**: `brew install tmux` before Tier 2 work begins.

## R13 — Logging

**Decision**: `Serilog` with the rolling-file sink, wrapped behind `Microsoft.Extensions.Logging`
abstractions.

**Rationale**: Constitution §7 requires full stdout/stderr of every headless run appended to a
*rolling* instance log, with issue/stage/timestamp context on each line. Size-based rolling plus
retention is fiddly to hand-roll correctly under concurrent ticks, which is the justification
§8 demands for taking a dependency. Serilog's enrichment also gives the per-item context
properties directly.

**Alternatives considered**: Hand-rolled append-only writer — rejected on rolling/retention
correctness. `Microsoft.Extensions.Logging` console-only — rejected: does not satisfy the
durable rolling-log requirement.

## R14 — Claude Code invocation surface

**Decision**: Headless runs use `claude -p <prompt> --output-format json` with the configured
`--permission-mode`, executed with the item's worktree as the working directory; the returned
JSON supplies the session identifier. Live sessions are spawned detached under tmux and resumed
with `claude --resume <session-id>`.

**Rationale**: Print mode with JSON output is the only invocation that returns a session ID the
runner can record on the issue (FR-022) and resume against (FR-024). Working directory — rather
than a path argument — is what binds every invocation to the item's worktree (FR-012).

**Open to probe**: Exact flag behaviour under resumption, and whether resuming re-registers
Remote Control with a fresh ID, are Tier 4 questions (OQ-3 through OQ-6 in the spec's
Assumptions). The plan does not assume an answer; `doctor --probe` establishes it before the
live-channel work is built.

## R15 — Architecture probe (2026-07-25): containerize; live-channel unknowns resolved

**Decision**: Run the entire tick **inside a Docker container** (one per instance). This was
not assumed — it was verified end-to-end with a real probe (`probe/`), phone in hand. Full
record in `probe/probe-results.md`.

**Rationale**: The operator's driving motivation is blast-radius isolation — a prompt-injected
agent running with an unattended permission mode and repo write access should not be able to
reach the host filesystem, other instances' clones, or host credentials. Docker's isolation
model is the operator's preferred boundary (comfortable with it, portable, hard to escape).
Every objection that made containerization look risky was tested and fell:

| Probe | Result |
|---|---|
| In-container claude.ai `/login` | Works (paste-back code flow; no macOS-Keychain dependency — credential lands in `~/.claude` in the container). |
| Headless `claude -p` | Works; not blocked by workspace trust. |
| **Workspace trust across worktrees (OQ-3)** | **Not a blocker.** Trust is a per-path boolean `projects["<abs>"].hasTrustDialogAccepted` in Claude Code's config. Pre-seeded worktree launched with no dialog; unseeded control prompted. The runner writes this at `git worktree add` time (FR-012a). |
| tmux kickoff (OQ-4) | Works, but needs the readiness-probe + retry already specified (one of two blind sends failed). |
| **Remote Control from container (the headline)** | **Works.** Registered, issued a `claude.ai/code` URL, pushed to the operator's phone, and the operator drove it bidirectionally from the mobile app. Undocumented question answered yes. |
| **Session resumption (OQ-5)** | **Best case.** Killed the session, `--resume <uuid> --remote-control` restored the full transcript and re-registered Remote Control on the **same** URL; the phone reconnected and got a context-aware answer with no re-run. Validates FR-024/FR-047. |
| Server mode `--spawn worktree` (OQ-6-adjacent) | **Does not fit** — CC-owned per-connection worktrees can't be shared with the headless path. No FRs deleted; per-item `claude --remote-control` in the item's own worktree is correct. |

**Consequences** (folded into constitution v3.0.0 and the spec):
- .NET 10 and tmux are **image layers**, not host installs (T001/T002 rewritten).
- GitHub PAT → **mounted secret file / Docker secret**, not the macOS Keychain (FR-052; the
  `SecretFileStore` adapter replaces the Keychain adapter).
- Claude Code auth → one-time in-container `/login` in a **named volume**; `doctor` checks its
  **expiry** proactively because an expired login silently stalls live sessions (FR-052b).
- Record the **conversation UUID** (not the Remote Control session id) as the resume id (FR-022).
- launchd stays on the host but fires the tick via **Docker**, and is the only host touchpoint.

**Alternatives considered**: host-native run (rejected — no isolation, the whole point);
`--sandbox`/sandboxing flag instead of Docker (rejected by the operator — would re-create what
Docker already does, and Docker's boundary is the one they trust and get portability from).

## R16 — Headless SpecKit slash-command dispatch (T039a): WORKS, no workaround

**Decision**: The runner invokes stage commands as `claude -p "/speckit.<stage> <args>"`.
Verified in the authed container (2026-07-25):
- A custom project slash command (`.claude/commands/probe-hello.md`) dispatched under `-p` and
  returned its exact token — so custom slash commands DO fire in print mode.
- `$ARGUMENTS` substitution works under `-p`: `claude -p "/probe-args hello-from-runner-42"`
  returned `ARGS=hello-from-runner-42`.

**Consequence**: `StageCommand` builds the slash-command string; the inline-markdown fallback
(read `.claude/commands/*.md`, substitute placeholders, pass as prompt) is NOT needed. The seam
still exists so `WorkRunner` doesn't care, but its live implementation is the slash path.

**Caveat**: the `{SCRIPT}` frontmatter placeholder substitution (distinct from `$ARGUMENTS`) was
not separately exercised here; the SpecKit commands that use it also run their own
`check-prerequisites.sh`, which the runner already invokes. Confirm during the first real
`/speckit.specify` headless run.

## R17 — The review stage's context: what it is told, and how (issue #15)

**Problem observed**: `Tick.RunReviewAsync` reads `.specify/prompts/code-review.md` and passes it
to `claude -p` **verbatim, alone**. That prompt opens with "Please review this pull request" and
goes on to say "each file the pull request touches", "this item's `spec.md`", and "every path this
pull request touches". None of those referents are ever supplied. The reviewer is started in a
worktree with a checked-out branch and left to infer which pull request, which two refs to compare,
which issue it is serving, and which spec directory is the item's own.

That inference is the same failure the constitution already banned once. v6.0.0 removed
filesystem-guessed stage position because "which spec directory belongs to this item" cannot be
answered by looking at `specs/` — `OrderByDescending` silently picked another item's spec. The
review prompt asks the *model* to make exactly that guess, unassisted, on every review. A chore has
no spec directory at all, so §2 of the prompt ("read this item's spec.md") is unanswerable for the
kind that reaches review most often.

**Decision**: compose the review prompt as **instructions + a delimited context block**, and
supply four facts the runner already knows and the reviewer cannot reliably derive:

| Fact | Source in the runner | Why the reviewer can't derive it |
|---|---|---|
| Pull request number + URL | the `kind=pr` marker comment (R10) | not present anywhere in the worktree |
| Base ref and head branch | `config.BaseBranch`, `WorktreeLifecycle.BranchFor(n)` | `HEAD` is detached-ish context; nothing names the diff's other side |
| Issue number, title, body | the selected `WorkItem` (operator-verified) | the worktree has no link back to the book of work |
| The item's spec directory | `Git.SpecDirOnBranchAsync` (§3, v6.0.0) | requires branch-vs-base diffing, and guessing is the banned pattern |

**Ordering is load-bearing (FR-034d, FR-054, §6)**: the version-controlled instructions come
**first and verbatim**, and the context follows in a region explicitly framed as *data under
review, not orders*. The pipeline definition must come from the repository; issue text may only
answer questions the runner posed. Putting the issue body first — or interleaving it — would let an
issue body's imperative sentences read as the reviewer's standing instructions.

**Alternatives considered**:

- *Leave it to the model.* Rejected: it is the guess v6.0.0 removed, and it silently produces a
  confident review of the wrong spec rather than an error.
- *Fetch the PR diff and paste it into the prompt.* Rejected: the reviewer has git and the whole
  worktree; naming the two refs is strictly more useful than a snapshot, costs no tokens, and keeps
  FR-034b's "before and after" a live operation rather than a pre-flattened blob.
- *Rewrite `code-review.md` to hardcode the paths.* Rejected: the prompt is repo-level and
  item-agnostic by design (FR-034d), and the file's own note promises the runner passes it through
  verbatim.
- *Add the context via `$ARGUMENTS`/a slash command.* Rejected: review is deliberately not a
  SpecKit command (`StageCommand.SlashCommandFor(Review) == null`); it reads a config-named file so
  a project can own its own review policy.

**Two honesty items the context block also carries**, because a review that silently skips a
section is indistinguishable from one that performed it:

- **No spec directory** (chore, or specify has not run): the block says so explicitly and tells the
  reviewer that the spec-coverage section does not apply — rather than leaving it to hunt.
- **No coverage manifest**: `specs/COVERAGE.md` is referenced by §3 of the review prompt and does
  **not exist in this repository today**. The block states its presence or absence from the item's
  branch, so cross-spec drift checking is either performed or reported as impossible, never quietly
  dropped. (Creating the manifest is separate work and out of this item's scope — FR-034g.)

**PR URL sourcing**: the `kind=pr` marker records only `number=`. It gains a `url=` field at PR-open
time; items whose marker predates the change fall back to `https://github.com/{slug}/pull/{n}`,
derived from the instance's configured slug. Deriving alone would be enough for github.com, but the
marker is the authoritative record and reading it keeps the runner from asserting a URL it never saw.

**Injection posture is unchanged and must be re-asserted by test**: every field above comes from the
operator-verified issue (FR-005 filters the candidate before selection) or from the runner's own
config and git. **No comment body ever enters the review prompt** — which is what keeps the Tier 3
canary green, and is worth an explicit assertion rather than an inherited one.

---

## R18 — A build-and-test check on pull requests, and what it takes to make it gate (issue #16)

**Problem observed**: constitution §8 states, as a standing rule, that "CI runs the full Tier 1–3
suite on every PR with no Claude credits spent". There is **no `.github/` directory in this
repository** — the rule has never been implemented. Every merge to `master` so far has been gated by
exactly one thing: the runner's own in-container `verify` command
(`deploy/spec-queue-runner.toml:23` — `dotnet build SpecRunner.slnx -c Debug && dotnet test
SpecRunner.slnx -c Debug`), run by the same process that decides to merge. A self-check by the
merging party is not a check on a pull request.

### Decision 1 — the workflow shape

**Decision**: one workflow, `.github/workflows/build-and-test.yml`, one job named
`build-and-test`, triggered on `pull_request` (opened / synchronize / reopened) and on `push` to the
base branch. It restores, builds, and tests **`SpecRunner.slnx` in `Debug`** — the same solution,
configuration, and order the instance's `verify` command uses.

**Rationale**: the value of CI here is a *second, independent* run of the same gate, on
infrastructure the runner does not control. Two gates running *different* commands would produce
disagreements nobody can adjudicate — a green `verify` against a red CI would leave the operator
unable to say which one is lying. Matching the command exactly means a disagreement can only mean
"environment", which is diagnosable. Tier 1–3 is the whole suite, and it is already CI-shaped: an
in-memory GitHub client, a `RecordingProcessRunner` (no real `git`, `tmux`, or `claude` is ever
spawned), and per-test temp directories — so it is offline, credit-free, and needs nothing on the
runner beyond the SDK.

**Runner image**: `ubuntu-latest` (x64), not arm64, even though the product ships `linux-arm64`. The
suite is RID-agnostic — it never publishes, never P/Invokes, never shells out — so architecture buys
nothing here, while arm64 hosted runners add availability and (on private repos) billing-multiplier
risk. The arm64 build is already proven where it matters: `docker build --platform linux/arm64` in
the deploy path.

**SDK**: `actions/setup-dotnet` pinned to `10.0.x`, matching `Directory.Build.props` (`net10.0`) and
the `mcr.microsoft.com/dotnet/sdk:10.0` image the Dockerfile builds from. No `global.json` is added:
the Dockerfile already floats on the 10.0 SDK band, and pinning CI harder than the image would let
CI stay green while the shipped artifact breaks — the wrong direction for a check whose purpose is
predicting the image's behaviour.

**Alternatives considered**:

- *Reuse the Docker image (`docker build .` in CI).* Rejected: the slowest possible way to run a
  suite that needs only the SDK, it requires QEMU for arm64, and it drags Claude Code's
  `curl | bash` installer into the critical path of every pull request.
- *A matrix over Debug and Release.* Rejected for now: `verify` runs Debug only, so a Release-only
  failure would fail CI on a configuration nothing ships. Worth revisiting if `TreatWarningsAsErrors`
  ever diverges by configuration.
- *Add `dotnet format --verify-no-changes` to the same job.* Rejected as separate scope.
  `TreatWarningsAsErrors=true` with `AnalysisLevel=latest-recommended` already makes the build step
  **the** analyzer gate for `src/` (§8's "analyzers MUST be enforced in CI"); a style checker is a
  different rule with a different failure mode, and there is no `.editorconfig` to check against yet.
- *Cache NuGet via `setup-dotnet`'s `cache: true`.* Rejected: it requires a `packages.lock.json`,
  which this repo does not have and which is a dependency-management policy of its own. A plain
  `actions/cache` over `~/.nuget/packages`, keyed on the hash of every `*.csproj` plus
  `Directory.Build.props`, gets the same win and introduces no new policy.

### Decision 2 — the check cannot gate anything without a runner-side change

This finding shapes the increment, and it is not speculative — it is readable in the code as it
stands.

`Tick.RunReviewAsync` runs review, posts the review comment, applies `stage/review`
(`src/SpecRunner/Ticking/Tick.cs:864`), runs `verify`, posts the digest, then calls
`MergePullRequestAsync` (`src/SpecRunner/Ticking/Tick.cs:916`) — which is `PullRequest.Merge(...)`
with no mergeability check at all. The pull request is opened and merged **within a single tick**,
seconds apart. Three consequences follow:

1. **Unrequired, the check is decorative.** A workflow that starts when the PR opens is still
   queueing when the runner merges. It would report on already-merged commits for every PR the
   runner files — which is nearly all of them.
2. **Required, the merge throws.** GitHub answers `PullRequest.Merge` with 405 while a required
   check is pending or failing. Octokit raises — and the digest claiming
   `**Merged**: yes (auto-merge on; spend under cap)` has *already been posted*, so the issue would
   carry a written assertion of a merge that never happened.
3. **The retry is a silent, permanent stall.** `stage/review` is applied **before** the merge gate,
   so on the next tick `StageProgress.Derive` returns `null`, and the tick logs
   `"has every stage recorded — nothing to do"` and exits 0 (`src/SpecRunner/Ticking/Tick.cs:137`).
   The item sits open with an unmerged PR forever and nothing ever reports it. **This defect exists
   today**: the identical path is taken whenever `config.Verify` fails, which sets `status/ready`
   and returns — into a tick that will find nothing to do.

**Decision**: split merging out of review into its own stage, **`Stage.Merge`**, appended to the
sequences of the three kinds that write code (feature, amendment, chore). Review keeps what review
is — run the reviewer, post the record, apply `stage/review`. The new stage takes what review should
never have been doing: evaluate mergeability, run `verify`, post the digest, merge, close, prune.

**Rationale**: this is not a new mechanism; it is the mechanism v6.0.0 already established. "Do →
commit → label" holds per stage, so a pending check simply means the merge stage has not completed,
its label is absent, and the next tick re-enters at exactly that point — with **no Claude invocation
and no credit spend**, because merging is pure API work. All three consequences dissolve: the digest
moves behind the merge, the retry becomes cheap and correct, and the existing `verify`-failure stall
is fixed as a consequence rather than as a separate patch. Every alternative required inventing a
waiting state that the label model already expresses.

**Alternatives considered**:

- *A `status/awaiting-checks` label plus a tick-start reconciler*, shaped like the existing reapers.
  Rejected: it creates a second, parallel notion of "where the item is" alongside the stage labels —
  precisely what v6.0.0 collapsed into one.
- *Delay `stage/review` until after the merge.* Rejected: a blocked merge would then re-run the
  **reviewer** every tick, spending credits on a condition no review can fix, and the review label
  would lie about whether review happened.
- *Enable GitHub's native auto-merge (`enablePullRequestAutoMerge`) and close the item immediately.*
  Rejected: the item would close before the merge, so a red check leaves the PR unmerged, the issue
  closed, and nobody told — and §3's "auto-merged work MUST be reported" would be satisfied by a
  digest that predates the outcome it reports. Forward-only correction makes a wrongly-closed issue
  expensive to undo.
- *Leave the check unrequired and accept (1).* Rejected as the primary answer, but retained as the
  **fallback if Part B slips**: a decorative check still beats no check for operator-authored PRs and
  for `push` on `master`. Part A is therefore specified to stand alone.

### Decision 3 — how mergeability is read, without widening the token

**Decision**: read the pull request's `mergeable` / `mergeable_state` fields, which the **Pull
requests: read** permission the instance already holds returns. Map them to a three-way pure
decision: `Ready` (`clean`); `Wait` (`null`/`unknown` — GitHub computes mergeability
asynchronously, so this is the *normal* state for a seconds-old PR — or `blocked`, which is what a
pending or failing required check produces); `Blocked` (`dirty` — a real conflict, which no amount
of waiting fixes).

**Rationale**: the obvious API is Check Runs (`GET /repos/{o}/{r}/commits/{sha}/check-runs`), which
would name the failing check and link its log. It needs the fine-grained **Checks** permission —
read-only, but still an addition to the ceiling §6 calls non-negotiable and enumerates as "issues,
contents, and pull requests". Widening a constitutional ceiling to obtain nicer error text is a poor
trade, and `mergeable_state` answers the only question the merge stage actually asks: *may I merge
right now?* The report links the PR's own checks tab, where the operator sees strictly more than the
runner could relay.

**The cost, stated**: `blocked` is ambiguous — it means "some branch-protection requirement is
unmet", covering a pending check, a failed check, and a missing human approval alike. An instance
whose protection requires approval would wait indefinitely, one API call per tick. That is visible
in the log rather than silent, and it is the correct behaviour regardless: the runner must not merge
past a human gate. If per-check reporting is later judged worth it, the follow-up is a §6 amendment
adding **Checks: read** — filed as the security change it is.

**Belt and braces**: `MergePullRequestAsync` is additionally wrapped so a 405 is caught and reported
rather than thrown. A read-then-write is inherently racy; the catch is what keeps the race from
becoming an unhandled fault.

### Decision 4 — the runner cannot land Part A itself

**The token ceiling forbids it.** §6 limits the PAT to issues, contents, and pull requests, and
names **workflow** among the permissions it must never hold. GitHub refuses any push that creates or
modifies a file under `.github/workflows/` unless the credential carries the Workflows permission —
the same rule applies to a GitHub App installation token. The runner can *author*
`build-and-test.yml` in its worktree; its push will be rejected.

**Decision**: **Part A lands by hand.** The operator commits the workflow file (or pushes the branch
the runner prepared). Part B — the merge stage — touches no workflow file and flows through the
queue normally.

**Rationale**: the alternative is amending §6 to grant Workflows write, which would let an
unattended, prompt-injectable agent rewrite its own CI. The check exists to constrain that agent;
letting the agent edit the constraint inverts the entire point. Here the ceiling is doing exactly
the job it was written to do, and the manual step is the feature rather than the friction.

**Making the check *required* is manual for the same reason**: branch protection is an
administration setting, and administration is outside the ceiling by the same rule. Recorded as an
operator step in quickstart, never as runner behaviour.

### Open empirical item (the first CI run answers it)

`src/SpecRunner/SpecRunner.csproj` sets `SelfContained=true` and `PublishSingleFile=true` with no
`RuntimeIdentifier` (the root props deliberately leave it empty for non-test projects). The
instance's `verify` command runs `dotnet build SpecRunner.slnx -c Debug` in the SDK image and is
reported to pass, so this is expected to be fine — `PublishSingleFile` is a publish-time property,
and RID-less `SelfContained` resolves to the host RID at build. If the first CI run contradicts
that, the fix is `-p:SelfContained=false` on the CI **build** step only, leaving the Dockerfile's
explicit `-r linux-arm64` publish untouched. Recorded here so a first-run failure is a known branch
rather than a surprise.
