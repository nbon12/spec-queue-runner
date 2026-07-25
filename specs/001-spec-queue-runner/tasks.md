---

description: "Task list for Spec Queue Runner implementation"
---

# Tasks: Spec Queue Runner

**Input**: Design documents from `/specs/001-spec-queue-runner/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: REQUIRED. The constitution mandates test-first (red → green) for pure decision logic
(Tier 1) and process/git-touching behaviour (Tier 2), plus two constitutional property families
(Tier 3). Tests precede implementation in every phase below.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US6)
- Include exact file paths in descriptions

## Path Conventions

Single project, ports-and-adapters (see plan.md "Structure Decision"):
`src/SpecRunner/`, `tests/SpecRunner.{TestKit,UnitTests,IntegrationTests,PropertyTests}/`

---

## Phase 1: Setup (Toolchain, Dependencies, Environment)

**Purpose**: Install everything the build and the runtime depend on, then create the skeleton.
Two toolchain prerequisites are **missing on this machine** (verified 2026-07-25).

### Container image (the runtime — replaces host installs; see probe/probe-results.md)

- [ ] T001 Author the runner `Dockerfile` (base the probe image at `probe/Dockerfile`): Debian, non-root user, bundling git, tmux, ripgrep, and Claude Code. The tick runs here, not on the host — there is NO host .NET or tmux install (constitution §2, FR-052a)
- [ ] T002 [P] Add the .NET 10 runtime to the image and confirm a `linux-arm64` single-file publish runs inside the container (`dotnet --info` in-container shows 10.x); the host needs only Docker
- [ ] T003 [P] Verify `claude --version` in the container (2.1.220 confirmed by probe) and that invocations use the real binary with an explicit environment — never a shell alias carrying `--dangerously-skip-permissions` (research R6)
- [ ] T003a [P] Wire credential delivery: GitHub PAT as a mounted secret file / Docker secret (`[secret].github_pat_file`), and Claude Code's `~/.claude` as a named volume seeded by a one-time in-container `/login` (FR-052/052b; probe §1 confirmed in-container login works)

### Project skeleton

- [ ] T004 Create `SpecRunner.sln` plus `src/SpecRunner/SpecRunner.csproj` and the four test projects `tests/SpecRunner.{TestKit,UnitTests,IntegrationTests,PropertyTests}`, all targeting `net10.0`

### Runtime dependencies (each justified in research.md)

- [ ] T005 Add the TOML parser: `dotnet add src/SpecRunner package Tomlyn` — config parsing with line/column diagnostics the fail-fast validator needs (research R2)
- [ ] T006 [P] Add the GitHub client: `dotnet add src/SpecRunner package Octokit` — confined to the adapter behind a first-party port (research R3)
- [ ] T007 [P] Add logging: `dotnet add src/SpecRunner package Serilog` and `dotnet add src/SpecRunner package Serilog.Sinks.File` — rolling instance log with retention (research R13)
- [ ] T008 [P] Add logging abstractions: `dotnet add src/SpecRunner package Microsoft.Extensions.Logging.Abstractions` so call sites depend on `ILogger`, not Serilog directly

### Test dependencies

- [ ] T009 Add the test stack to all four test projects: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `coverlet.collector`
- [ ] T010 [P] Add `FsCheck.Xunit` to `tests/SpecRunner.PropertyTests` for the two constitutional property families (crash-convergence, injection canary)
- [ ] T011 [P] Add project references: all four test projects reference `src/SpecRunner`, and the three test-suite projects reference `tests/SpecRunner.TestKit`

### Build and environment configuration

- [ ] T012 [P] Create `Directory.Build.props` at repo root enabling nullable reference types, warnings-as-errors, and analyzer settings
- [ ] T013 [P] Verify `dotnet test` runs the full (empty) suite green across all four projects
- [ ] T014 [P] Store the fine-grained PAT in the keychain via `security add-generic-password -s spec-runner -a "<owner/repo>" -w`, scoped to one repo with Issues, Contents, and Pull requests permissions only (constitution §6, FR-052)
- [ ] T015 [P] Enable branch protection on `main` for the target repo (no direct pushes, PR required) so the review gate is structural rather than procedural (FR-056)
- [ ] T015a [P] Author the code-review prompt at `.specify/prompts/code-review.md` in each served repo and point `review_prompt` at it in that instance's config — a scaffold carrying the three stated requirements is already committed in this repo (FR-034d)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The test kit, the pure domain, the boundaries, and the security gate. Nothing in any
user story can be built or trusted until this phase is complete.

**⚠️ CRITICAL**: No user story work begins until this phase completes.

### Test kit (built first — every later test depends on it)

- [ ] T016 [P] Create the fake `claude` binary in `tests/SpecRunner.TestKit/fake-claude.sh` honouring `SPEC_RUNNER_FAKE_SCENARIO` and appending one JSON line per invocation (argv, stdin, cwd) to `SPEC_RUNNER_FAKE_RECORD`, per `contracts/claude-invocation.md`
- [ ] T017 [P] Implement scenario behaviours (`emit-spec markers=n`, `emit-plan`, `emit-tasks`, `emit-analysis`, `fail-usage-limit`, `emit-decision count=n`, `hang`, `emit-session id=x`) in `tests/SpecRunner.TestKit/fake-claude.sh`
- [ ] T018 [P] Implement the disposable git fixture repo builder in `tests/SpecRunner.TestKit/FixtureRepo.cs` (per-test temp dir, branches, worktrees, spec files, unique naming for parallel safety)
- [ ] T019 [P] Implement the in-memory GitHub client in `tests/SpecRunner.TestKit/InMemoryGitHubClient.cs` holding issues, labels, and comments behind the production port
- [ ] T020 [P] Implement invocation-recording assertions in `tests/SpecRunner.TestKit/InvocationRecord.cs` (parse JSONL; assert on argv, stdin, cwd)

### Pure domain — Tier 1 tests first, then implementation

- [ ] T021 [P] Write failing stage-derivation theories in `tests/SpecRunner.UnitTests/StageDerivationTests.cs` (no spec.md → specify; markers present → clarify; plan.md without tasks.md → tasks)
- [ ] T022 [P] Write failing kind→stage-sequence theories in `tests/SpecRunner.UnitTests/StageSequenceTests.cs` covering all five kinds
- [ ] T023 [P] Write failing rate-limit detection theories in `tests/SpecRunner.UnitTests/RateLimitDetectorTests.cs` against a captured-output corpus
- [ ] T024 [P] Write failing theories for waking-hours arithmetic, label mapping, and idempotency markers in `tests/SpecRunner.UnitTests/PureHelpersTests.cs`
- [ ] T025 [P] Write failing theories for work selection and decision cap in `tests/SpecRunner.UnitTests/WorkSelectionTests.cs`
- [ ] T026 [P] Implement `Kind`, `Stage`, `QueueStatus` enums and `StageSequence` in `src/SpecRunner/Domain/` per data-model.md
- [ ] T027 Implement `WorktreeSnapshot` and `StageDerivation` in `src/SpecRunner/Domain/` as a pure snapshot→stage function (research R9; depends on T026)
- [ ] T028 [P] Implement `RateLimitDetector` in `src/SpecRunner/Domain/RateLimitDetector.cs` (case-insensitive `rate limit|usage limit`)
- [ ] T029 [P] Implement `WakingHours`, `LabelMap`, and `IdempotencyMarker` in `src/SpecRunner/Domain/`
- [ ] T030 [P] Implement `WorkSelection` and `DecisionCap` in `src/SpecRunner/Domain/`

### Configuration — fail-fast at startup

- [ ] T031 [P] Write failing config parse/validation theories in `tests/SpecRunner.UnitTests/ConfigValidatorTests.cs` covering every rule in `contracts/config-schema.md`
- [ ] T032 Implement `InstanceConfig` and `ConfigLoader` (Tomlyn) in `src/SpecRunner/Configuration/`
- [ ] T033 Implement `ConfigValidator` in `src/SpecRunner/Configuration/ConfigValidator.cs` — refuses to start on invalid config, maps failures to exit codes 1/2, and rejects anything resembling a secret in the file

### Ports and adapters — the boundaries

- [ ] T034 Define ports `IGitHubClient`, `IProcessRunner`, `ISecretStore`, `IClock`, `IWorktreeReader` in `src/SpecRunner/Ports/`
- [ ] T035 Implement `ProcessRunner` in `src/SpecRunner/Adapters/ProcessRunner.cs` using `ArgumentList`, concurrent stdout/stderr draining, and an explicitly constructed environment (research R6)
- [ ] T036 [P] Implement `SecretFileStore` in `src/SpecRunner/Adapters/Secrets/SecretFileStore.cs` reading the GitHub PAT from the mounted secret file at `[secret].github_pat_file` (replaces the macOS-Keychain adapter — the container is Linux; §2, FR-052)
- [ ] T037 [P] Implement `GitWorktrees` in `src/SpecRunner/Adapters/Git/GitWorktrees.cs` wrapping `git worktree add/remove/list/prune`
- [ ] T038 [P] Implement `TmuxSessions` in `src/SpecRunner/Adapters/Tmux/TmuxSessions.cs` wrapping `new-session -d`, `send-keys`, `kill-session`, and pane-content readiness probing
- [ ] T039 Implement `ClaudeInvoker` in `src/SpecRunner/Adapters/Claude/ClaudeInvoker.cs` for headless `-p --output-format json --permission-mode <configured>` with the item's worktree as cwd
- [x] T039a **[SPIKE — RESOLVED, see research R16]** Determine how headless `claude -p` invokes the SpecKit stage commands, which are **project slash commands** (`.claude/commands/speckit.*.md`), not skills. Test whether `claude -p "/speckit.specify …"` actually dispatches a custom project slash command in print mode. **If it does:** `StageCommand` builds the slash-command string. **If it does not:** fall back to the known workaround — read the command's markdown template from `.claude/commands/`, substitute the script path and args the way the slash-command layer would, and pass the resulting instructions as the `-p` prompt. Either way `StageCommand` abstracts it so `WorkRunner` (T059) and the stage implementations don't care which mechanism won. Record the outcome in `research.md`. This is the headless analogue of the Remote Control probe: a load-bearing assumption (FR-015, the whole stage table) that must be verified before execution-stage logic is trusted.
- [ ] T040 Implement `GitHubClient` in `src/SpecRunner/Adapters/GitHub/GitHubClient.cs` (Octokit confined to this file, behind the port)

### Security gate — before any content reaches any prompt

- [ ] T041 [P] Write failing tests in `tests/SpecRunner.UnitTests/OperatorIdentityTests.cs` for numeric-ID matching, login-rename impersonation rejection, and fail-closed behaviour when resolution fails
- [ ] T042 Implement `OperatorIdentity` in `src/SpecRunner/Ticking/OperatorIdentity.cs` resolving the configured login to a numeric GitHub user ID once per tick, failing closed and doing no work if unresolvable (research R5, FR-005)
- [ ] T043 Implement the author allowlist filter in `src/SpecRunner/Ticking/AuthorFilter.cs` applied to every issue and comment before any prompt construction, recognising the runner's own marker-bearing comments as output rather than input

### Tick infrastructure

- [ ] T044 Implement `InstanceLock` in `src/SpecRunner/Ticking/InstanceLock.cs` using `FileStream` with `FileShare.None`, released by `using` scope
- [ ] T045 [P] Write the Tier 2 lock mutual-exclusion test in `tests/SpecRunner.IntegrationTests/InstanceLockTests.cs` — two simultaneous ticks, one config, exactly one works
- [ ] T046 [P] Implement `InstanceLog` in `src/SpecRunner/Logging/InstanceLog.cs` (Serilog rolling file, issue/stage/timestamp context on every line)
- [ ] T047 Implement `Program.cs` command dispatch (`tick`, `doctor`, `doctor --probe`, `version`) and the exit-code table in `contracts/cli-commands.md`
- [ ] T048 Implement `DoctorCommand` in `src/SpecRunner/Doctor/DoctorCommand.cs` with every prerequisite check from `contracts/cli-commands.md`, including tmux presence and main-branch protection
- [ ] T049 Implement the Tier 3 injection-canary harness in `tests/SpecRunner.PropertyTests/InjectionCanaryTests.cs` asserting a seeded non-operator canary string appears in no recorded invocation's argv or stdin across every scenario in the suite

### Tier 4 live probes — ALREADY RESOLVED manually (see probe/probe-results.md, 2026-07-25)

- [x] T050 **Resolved by the manual probe.** All four questions answered favorably in a container: workspace trust is a per-directory boolean the runner pre-seeds (not a carry-over problem); tmux kickoff works with retry; session resumption restores transcript + re-registers Remote Control on the same URL; the two-concurrent-sessions question is moot for v1 (one live session per instance, FR-025). Fold the `doctor --probe` command in as a re-runnable regression of these, but the gating answers exist.
- [ ] T051 Implement `doctor --probe` in `src/SpecRunner/Doctor/Probes/` as a re-runnable version of the manual checklist (so the answers can be re-verified on a new host/image), citing `probe/probe-results.md` as the baseline

**Checkpoint**: Foundation ready. Tier 1 green, lock and canary tests green, probe answers recorded.

---

## Phase 3: User Story 1 - Unattended overnight execution (Priority: P1) 🎯 MVP

**Goal**: A ready item is implemented, pushed, opened as a PR, and closed by morning — surviving
at least one usage-limit reset with no manual intervention.

**Independent Test**: Label one ready item at night with nothing else running. By morning a PR
exists, the issue is closed, and the log shows a rate-limit-triggered retry that needed no action.

### Tests for User Story 1 (write first, must fail)

- [ ] T052 [P] [US1] Tier 2 test in `tests/SpecRunner.IntegrationTests/RateLimitRevertTests.cs`: fake claude fails mid-implement → status returns to ready, commits preserved, next tick resumes at the correct stage
- [ ] T053 [P] [US1] Tier 2 test in `tests/SpecRunner.IntegrationTests/ImplementCompletionTests.cs`: implement completes → branch pushed, PR opened with description from issue + decisions, closing comment links PR, issue closed, worktree removed
- [ ] T054 [P] [US1] Tier 3 crash-convergence family in `tests/SpecRunner.PropertyTests/CrashConvergenceTests.cs`: inject a kill after each individual side effect (label set, comment posted, worktree created, commit made), re-run ticks to quiescence, assert the end state equals the never-crashed state with no duplicated comments or labels
- [ ] T055 [P] [US1] Tier 2 test in `tests/SpecRunner.IntegrationTests/StaleReclaimTests.cs`: an in-progress item older than the threshold resets to ready; live and held items are exempt

### Implementation for User Story 1

- [ ] T056 [US1] Implement worktree lifecycle in `src/SpecRunner/Ticking/WorktreeLifecycle.cs`: lazy creation via `git worktree add <root>/<nr> work/<nr>`, persistence across ticks, removal after the PR opens (FR-012/014)
- [ ] T056a [US1] On worktree creation, pre-seed workspace trust in `src/SpecRunner/Ticking/WorktreeLifecycle.cs`: write `projects["<worktree-path>"].hasTrustDialogAccepted = true` into Claude Code's config before any interactive session — otherwise the live session stalls on a trust dialog (FR-012a; validated by probe §3)
- [ ] T057 [US1] Implement the `Tick` orchestrator in `src/SpecRunner/Ticking/Tick.cs`: lock → operator identity → collect replies → reap → at most one unit of work → exit (FR-002/003/009)
- [ ] T058 [US1] Implement work selection and `status/in-progress` labelling before any Claude invocation in `src/SpecRunner/Ticking/Tick.cs` (FR-009/011)
- [ ] T059 [US1] Implement `WorkRunner` execution-stage advance (plan → tasks → analyze → implement) in `src/SpecRunner/Ticking/WorkRunner.cs`, folding analyze recommendations in directly and checkpointing at each predicate (FR-020/030)
- [ ] T060 [US1] Implement decision-comment posting *before* continuing, with commit references, in `src/SpecRunner/Ticking/DecisionReporter.cs` (FR-031/032)
- [ ] T061 [US1] Implement reversibility assessment and the always-block list (destructive migrations, third-party calls, secrets, force-push, configured protected paths) in `src/SpecRunner/Domain/Reversibility.cs` (FR-031)
- [ ] T062 [US1] Implement decision-cap breach handling in `src/SpecRunner/Ticking/WorkRunner.cs`: kinds with a clarify stage return to clarify; chore/spike/audit block via the live-channel-or-fallback path (FR-031, per clarification)
- [ ] T063 [US1] Implement the usage-limit revert path in `src/SpecRunner/Ticking/Tick.cs`: revert to ready, log full output, exit 0 (FR-043)
- [ ] T064 [US1] Implement stale reclaim at tick start in `src/SpecRunner/Ticking/Reaper.cs`, exempting live and held (FR-044)
- [ ] T065 [US1] Implement idempotent side effects in `src/SpecRunner/Ticking/IdempotentWrites.cs`: labels written as a desired-state set, comments guarded by marker identity before posting (research R10)
- [ ] T066 [US1] Implement `PullRequestOpener` in `src/SpecRunner/Ticking/PullRequestOpener.cs`: push branch and open PR from issue + changelog + decision comments — **without** closing the issue or removing the worktree, both of which move to review completion (FR-033)
- [ ] T067 [US1] Implement `CoverageManifest` append in `src/SpecRunner/Ticking/CoverageManifest.cs` recording authored paths on the work branch (FR-033/037)
- [ ] T068 [US1] Implement API-unreachable resilience in `src/SpecRunner/Adapters/GitHub/GitHubClient.cs`: committed work preserved, pushes/comments/labels retried next tick (FR-046)

### Code review stage (US1 — an item is not done until it is reviewed)

- [ ] T068a [P] [US1] Tier 2 test in `tests/SpecRunner.IntegrationTests/CodeReviewTests.cs`: after the PR opens, review runs in the item's worktree, examines every touched file before-and-after, names an acceptance scenario left uncovered, fixes a reversible defect on the branch, and only then does the issue close and the worktree get pruned
- [ ] T068b [P] [US1] Tier 3 canary extension in `tests/SpecRunner.PropertyTests/InjectionCanaryTests.cs`: assert the review prompt is read from the repo file and that no issue or comment text reaches the review invocation's argv or stdin (FR-034d)
- [ ] T068c [US1] Implement the review stage in `src/SpecRunner/Ticking/Stages/ReviewStage.cs`: load the prompt from the configured repo path, invoke headless against `git diff main...work/NN` in the item's worktree (FR-034a/b)
- [ ] T068d [US1] Implement spec-to-test traceability reporting in `src/SpecRunner/Ticking/Stages/ReviewStage.cs`: each acceptance scenario checked against the tests the run wrote, uncovered scenarios named explicitly (FR-034c)
- [ ] T068e [US1] Implement review finding disposition in `src/SpecRunner/Ticking/Stages/ReviewStage.cs`: reversible → fix, commit, decision comment; irreversible → block; out-of-scope → new issue, never fixed in place (FR-034e/g)
- [ ] T068f [US1] Implement the always-posted review record comment in `src/SpecRunner/Ticking/Stages/ReviewStage.cs` so a silent review and an absent review are distinguishable (FR-034f)
- [ ] T068g [US1] Implement review-completion close-out in `src/SpecRunner/Ticking/PullRequestOpener.cs`: push review fixes, post the digest, append coverage, merge if permitted, post the closing comment, close the issue, remove the worktree (FR-033a)
- [ ] T068i [US1] Implement fresh-session enforcement for review in `src/SpecRunner/Adapters/Claude/ClaudeInvoker.cs`: review invocations MUST NOT pass `--resume` or inherit the implementing run's session (FR-034a1)
- [ ] T068j [US1] Implement coverage-bounded cross-spec drift checking in `src/SpecRunner/Ticking/Stages/ReviewStage.cs`: resolve touched paths to other specs via `specs/COVERAGE.md` and consult only those (FR-034c1)
- [ ] T068k [US1] Implement the merge gate in `src/SpecRunner/Ticking/MergeGate.cs`: merge only when auto-merge is enabled, review recorded no blocking finding, and no operator block is unresolved; otherwise leave the PR open with a stated reason (FR-033b)
- [ ] T068l [US1] Implement the digest in `src/SpecRunner/Ticking/Digest.cs`, posted to the PR immediately before merge and never skipped (FR-033c)
- [ ] T068m [US1] Add the spend threshold to the always-block list in `src/SpecRunner/Domain/Reversibility.cs`: estimated one-off or recurring spend above `spend_cap` blocks regardless of code-level reversibility (FR-033d)
- [ ] T068n [P] [US1] Tier 2 test in `tests/SpecRunner.IntegrationTests/MergeGateTests.cs`: a blocking review finding leaves the PR open with a stated reason; a clean review merges and posts a digest first; a decision over `spend_cap` blocks even though the change is trivially revertible
- [ ] T068h [US1] Add `ReviewRecorded` to `WorktreeSnapshot` and the review predicate to `src/SpecRunner/Domain/StageDerivation.cs`; extend the kind→sequence table so feature, amendment, and chore end in review while spike and audit do not (FR-015)

**Checkpoint**: User Story 1 fully functional — the MVP. Overnight execution works end to end,
and nothing closes unreviewed.

---

## Phase 4: User Story 2 - Staged intake and clarification (Priority: P2)

**Goal**: A rough phone-filed issue reaches clarify as a structured draft with numbered
questions and defaults, its kind inferred and reported rather than asked.

**Independent Test**: File a terse, unlabeled issue. Confirm kind and target labels are assigned
and reported as a decision comment without asking, then that it stops at clarify with numbered
questions and recommended defaults.

### Tests for User Story 2 (write first, must fail)

- [ ] T069 [P] [US2] Tier 2 test in `tests/SpecRunner.IntegrationTests/IntakeInferenceTests.cs`: terse unlabeled issue → kind and targets inferred, classification posted as a decision comment, nothing asked
- [ ] T070 [P] [US2] Tier 2 test in `tests/SpecRunner.IntegrationTests/SpecifyStageTests.cs`: specify runs headless, materializes markers, and writes no code under any circumstance

### Implementation for User Story 2

- [ ] T071 [US2] Implement the intake stage in `src/SpecRunner/Ticking/Stages/IntakeStage.cs`: infer kind and targets from issue text, post classification as a decision comment, block only when intent is unrecoverable (FR-016)
- [ ] T072 [US2] Implement the specify stage in `src/SpecRunner/Ticking/Stages/SpecifyStage.cs` running headless and materializing ambiguity as clarification markers (FR-018)
- [ ] T073 [US2] Implement the shaping-stage no-code guard in `src/SpecRunner/Ticking/Stages/ShapingGuard.cs`, asserting no file outside the spec directory is written during intake/specify/clarify (FR-018)
- [ ] T074 [US2] Implement the clarify predicate (zero unresolved markers in the target spec) in `src/SpecRunner/Domain/StageDerivation.cs`
- [ ] T075 [US2] Implement pinned-stage-label precedence over the computed stage in `src/SpecRunner/Domain/StageDerivation.cs` (FR-017)
- [ ] T076 [US2] Implement `Targets:` write-back for feature items whose spec number is allocated during specify in `src/SpecRunner/Ticking/Stages/SpecifyStage.cs`

**Checkpoint**: User Stories 1 and 2 both work independently.

---

## Phase 5: User Story 3 - Live conversational unblocking (Priority: P3)

**Goal**: A blocked item opens an interactive session in its own worktree, pushes the operator's
phone, waits indefinitely, and resolves through the conversation writing to the filesystem.

**⚠️ Depends on T051** (Tier 4 probe results) per constitution §10.

**Independent Test**: Force a block during waking hours. Confirm a phone push and open
conversation. Sleep the machine 24 hours, wake, answer from the app, confirm the resolution lands
in the spec or plan, with no timeout, duplicate push, or re-asked question.

### Tests for User Story 3 (write first, must fail)

- [ ] T077 [P] [US3] Tier 2 reaper matrix test in `tests/SpecRunner.IntegrationTests/ReaperTests.cs`: dead session → respawn carries the recorded resume ID; predicate satisfied → session killed and item advanced; closed issue with orphan worktree → pruned
- [ ] T078 [P] [US3] Tier 2 isolation test in `tests/SpecRunner.IntegrationTests/WorktreeIsolationTests.cs`: a session open and mid-edit in item A's worktree while item B runs implement end to end; A's files byte-identical after, and every recorded invocation's cwd was an item worktree, never the clone
- [ ] T079 [P] [US3] Tier 2 test in `tests/SpecRunner.IntegrationTests/WakingHoursTests.cs`: an item blocking outside the window holds with no session and no notification until the window opens

### Implementation for User Story 3

- [ ] T080 [US3] Implement `LiveChannel` spawn in `src/SpecRunner/Ticking/LiveChannel.cs`: tmux session in the item's worktree, Remote Control enabled, named for the issue, kickoff scoped to this item's open questions and barring continued implementation for execution blocks (FR-021)
- [ ] T081 [US3] Implement kickoff delivery with a readiness probe and retry in `src/SpecRunner/Adapters/Tmux/TmuxSessions.cs`
- [ ] T082 [US3] Implement session-ID recording and `status/live` labelling in `src/SpecRunner/Ticking/LiveChannel.cs`, keeping it the only dialogue artifact on the issue (FR-022)
- [ ] T083 [US3] Implement reaper live-session reconciliation in `src/SpecRunner/Ticking/Reaper.cs`: dead → respawn with `--resume <id>`; resolved → kill and advance; otherwise leave untouched (FR-024)
- [ ] T084 [US3] Implement the waking-hours gate for session spawning in `src/SpecRunner/Ticking/LiveChannel.cs` (FR-026)
- [ ] T085 [US3] Implement the one-live-session-per-instance constraint with issue-number queueing in `src/SpecRunner/Ticking/LiveChannel.cs` (FR-025)
- [ ] T086 [US3] Implement `ReplyCollector` in `src/SpecRunner/Ticking/ReplyCollector.cs` running before work selection across all waiting and live items, judging resolving vs conversational replies (FR-007/008)
- [ ] T087 [US3] Implement answered-elsewhere handling in `src/SpecRunner/Ticking/Reaper.cs`: a reply resolving a live item kills the now-pointless session (FR-047)

**Checkpoint**: All three highest-priority stories work independently.

---

## Phase 6: User Story 4 - Dependency-ordered integration (Priority: P4)

**Goal**: An item targeting an unmerged spec holds with a stated reason and becomes ready
automatically when that dependency lands on main.

**Independent Test**: File an amendment targeting a spec whose PR has not merged. Confirm it
holds with a stated reason and no work is attempted. Merge the dependency. Confirm it becomes
ready on the very next tick with no manual action.

### Tests for User Story 4 (write first, must fail)

- [ ] T088 [P] [US4] Tier 2 test in `tests/SpecRunner.IntegrationTests/HeldGatingTests.cs`: an amendment targeting an unmerged spec holds with a stated reason; merging the fixture PR promotes it on the next tick

### Implementation for User Story 4

- [ ] T089 [US4] Implement the targets-on-main readiness predicate in `src/SpecRunner/Domain/Readiness.cs` (FR-010)
- [ ] T090 [US4] Implement held labelling with a comment naming the awaited target in `src/SpecRunner/Ticking/Tick.cs` (FR-010)
- [ ] T091 [US4] Implement reaper promotion of held → ready when targets land on main in `src/SpecRunner/Ticking/Reaper.cs` (FR-010)
- [ ] T092 [US4] Implement forward-only correction guards in `src/SpecRunner/Ticking/PullRequestOpener.cs`: closed issues are never reopened; requested changes are expressed as a new issue (FR-034)

**Checkpoint**: Dependency ordering falls out of integration state, with no scheduler.

---

## Phase 7: User Story 5 - Graceful degradation (Priority: P5)

**Goal**: When the live channel cannot be established, questions land as one issue comment with
the reason stated, and the next blocked item after recovery goes live again automatically.

**Independent Test**: Disable the live channel. Force a block. Confirm one well-formatted comment
naming the failure reason. Reply and confirm resolution. Re-enable and confirm the next blocked
item goes live with no manual reset.

### Tests for User Story 5 (write first, must fail)

- [ ] T093 [P] [US5] Tier 2 test in `tests/SpecRunner.IntegrationTests/CommentFallbackTests.cs`: forced establishment failure → one comment with all questions, defaults, rationales and the stated reason; a reply resolves the item; the next blocked item attempts live again
- [ ] T094 [P] [US5] Tier 2 test in `tests/SpecRunner.IntegrationTests/AuthFailureFallbackTests.cs`: an auth-caused fallback calls the auth failure out distinctly

### Implementation for User Story 5

- [ ] T095 [US5] Implement `CommentFallback` in `src/SpecRunner/Ticking/CommentFallback.cs`: one comment, numbered questions, each with a recommended default and one-line rationale, plus an explicit statement of why live was unavailable, then label waiting (FR-027)
- [ ] T096 [US5] Implement distinct, loud auth-failure reporting in `src/SpecRunner/Ticking/CommentFallback.cs` (FR-028)
- [ ] T097 [US5] Implement headless clarify emulation in `src/SpecRunner/Ticking/Stages/ClarifyStage.cs`: read markers, post questions, apply accepted answers to the spec, re-check the predicate (FR-019)
- [ ] T098 [US5] Implement transient-fallback semantics (no state to clear when a later item goes live) in `src/SpecRunner/Ticking/LiveChannel.cs` (FR-029)

**Checkpoint**: Progress continues through the one channel that always works.

---

## Phase 8: User Story 6 - Spec drift detection through audits (Priority: P6)

**Goal**: The least-recently-audited spec is compared against its covered code on main, and
discrepancies are reported without modifying anything.

**Independent Test**: Let a spec fall out of sync with its code. Run an audit. Confirm it reports
the discrepancy, names a side, and leaves both spec and code byte-for-byte unchanged.

### Tests for User Story 6 (write first, must fail)

- [ ] T099 [P] [US6] Tier 2 test in `tests/SpecRunner.IntegrationTests/AuditTests.cs`: an audit against a deliberately stale spec reports the discrepancy, names the wrong side, and leaves spec and code byte-identical

### Implementation for User Story 6

- [ ] T100 [US6] Implement audit spec selection (least recently audited, from prior audit issues) in `src/SpecRunner/Ticking/Stages/AuditStage.cs` (FR-038/041)
- [ ] T101 [US6] Implement finding comments naming which side appears wrong and why in `src/SpecRunner/Ticking/Stages/AuditStage.cs` (FR-039)
- [ ] T102 [US6] Implement the unconditional no-modification guard in `src/SpecRunner/Ticking/Stages/AuditStage.cs` — not subject to reversibility judgement (FR-039, constitution §3)
- [ ] T103 [US6] Implement follow-up issue filing left unlabelled as to kind in `src/SpecRunner/Ticking/Stages/AuditStage.cs` (FR-040)

**Checkpoint**: All six user stories independently functional.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [ ] T104 [P] Implement recurrence successor filing from the `Recurring:` body line in `src/SpecRunner/Ticking/Recurrence.cs` (FR-042)
- [ ] T105 [P] Implement one-way reporting guards in `src/SpecRunner/Ticking/DecisionReporter.cs`: never ask approval of a completed decision, never notify outside GitHub and Remote Control (FR-048/049)
- [ ] T106 [P] Implement `install`/`uninstall` launchd commands in `src/SpecRunner/Doctor/LaunchdInstaller.cs` writing a per-instance plist (label `com.spec-runner.<slug>`, `StartInterval` 300, `RunAtLoad` true). The plist's program invokes **Docker** (`docker run`/`docker exec` against the instance's container) — not a host binary — passing the config path, mounting the PAT secret and the `~/.claude` named volume (§2, FR-052a)
- [ ] T106a [P] Provide the container run recipe (compose or a documented `docker run`) in `deploy/`: per-instance container, secret mount, named `~/.claude` volume, worktrees volume; referenced by the launchd plist
- [ ] T107 [P] Configure single-file publish in `src/SpecRunner/SpecRunner.csproj` and document the install step in `specs/001-spec-queue-runner/quickstart.md`
- [ ] T108 Run the full Tier 1–3 suite and confirm all green with zero credits spent and no network access
- [ ] T109 Walk the seven validation scenarios in `specs/001-spec-queue-runner/quickstart.md`, including the two manual ones (Live, Patient)
- [ ] T110 Append this feature's authored paths to `specs/COVERAGE.md` and confirm `spec.md` reflects what was actually built (constitution §9 pre-PR freshness)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies. T001 and T002 are environment installs that block everything downstream; T004 blocks all package adds.
- **Foundational (Phase 2)**: Depends on Setup. **BLOCKS all user stories.**
- **User Stories (Phases 3–8)**: All depend on Foundational. US3 additionally depends on **T051** (probe results), per constitution §10.
- **Polish (Phase 9)**: Depends on all desired stories.

### User Story Dependencies

- **US1 (P1)**: Foundational only. The MVP; delivers the reason the system exists.
- **US2 (P2)**: Foundational only. Independently testable against a terse fixture issue.
- **US3 (P3)**: Foundational + T051. The only story gated on empirical probe results.
- **US4 (P4)**: Foundational; shares `PullRequestOpener` with US1 but its held-gating path is independently testable.
- **US5 (P5)**: Foundational; exercises the fallback path US3 falls back *from*, but does not require US3 to be complete — establishment failure can be forced directly.
- **US6 (P6)**: Foundational only. Fully independent of every other story.

### Within Each Story

- Tests are written first and must fail before implementation (constitution testing §2.3).
- Pure domain before adapters; adapters before orchestration.
- Idempotency (T065) before any story that posts comments at scale.

### Parallel Opportunities

- **Setup**: T002 and T003 in parallel; after T004, the package adds T006–T008 and T010–T011 run in parallel; T012–T015 in parallel.
- **Foundational test kit**: T016–T020 fully parallel (different files).
- **Foundational Tier 1**: T021–T025 (tests) parallel, then T026 and T028–T030 parallel, with T027 following T026.
- **Foundational adapters**: T036, T037, T038 parallel after T035.
- **Every story's test tasks** are parallel with each other within that story.
- **Across stories**: once Foundational completes, US1, US2, US4, US5, and US6 can proceed in parallel; US3 waits on T051.
- **Polish**: T104–T107 parallel.

---

## Parallel Example: Setup Dependencies

```bash
# After T004 creates the projects, add packages concurrently:
Task: "dotnet add src/SpecRunner package Octokit"
Task: "dotnet add src/SpecRunner package Serilog && ... package Serilog.Sinks.File"
Task: "dotnet add src/SpecRunner package Microsoft.Extensions.Logging.Abstractions"
Task: "dotnet add tests/SpecRunner.PropertyTests package FsCheck.Xunit"
```

## Parallel Example: Foundational Test Kit

```bash
# Four test-kit assets, different files, no shared state:
Task: "Create fake claude binary in tests/SpecRunner.TestKit/fake-claude.sh"
Task: "Implement fixture repo builder in tests/SpecRunner.TestKit/FixtureRepo.cs"
Task: "Implement in-memory GitHub client in tests/SpecRunner.TestKit/InMemoryGitHubClient.cs"
Task: "Implement invocation-recording assertions in tests/SpecRunner.TestKit/InvocationRecord.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Phase 1: Setup — install .NET 10 and tmux first; nothing builds or runs without them.
2. Phase 2: Foundational — the largest phase, and unavoidably so: the test kit, pure domain,
   boundaries, and the security gate are what make every later story trustworthy.
3. Phase 3: User Story 1.
4. **STOP and VALIDATE**: run quickstart scenario 1 (Unattended) end to end.
5. At this point the system already delivers its core value — overnight capacity reclaimed.

### Incremental Delivery

1. Setup + Foundational → foundation ready, Tier 1–3 harness green.
2. Add US1 → validate → **MVP**: unattended overnight execution across a usage-limit reset.
3. Add US2 → validate → rough phone-filed issues become structured drafts.
4. Add US3 (after probes) → validate → conversational unblocking from the phone.
5. Add US4, US5, US6 in any order → each independently testable.
6. Polish.

### Notes

- [P] tasks touch different files and have no incomplete dependencies.
- The two Tier 3 property families (T054 crash-convergence, T049 injection canary) are
  constitutional: they must exist, run in CI, and pass before any merge touching side-effect
  ordering, prompt construction, or reply collection.
- Tier 2 tests skip **with a stated reason** when tmux is absent — never silently pass.
- No test in Tiers 1–3 spends a credit or touches the network.
- Commit after each task or logical group.
