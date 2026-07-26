# Implementation Plan: Spec Queue Runner

**Branch**: `001-spec-queue-runner` | **Date**: 2026-07-25 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-spec-queue-runner/spec.md`

## Summary

An unattended .NET console worker that drives GitHub Issues through the SpecKit pipeline while
the operator sleeps. The tick is a short-lived, stateless reconciliation process: it acquires a
per-instance lock, resolves the single allowlisted operator identity, collects comment replies,
reaps everything that outlives a tick (live sessions, held items, orphaned worktrees), then
performs at most one unit of work in that item's own git worktree — and exits.

The technical approach follows from three constitutional invariants rather than from any
framework choice. Because the tick is stateless, every side effect must be idempotent, which
drives the comment-identity marker design (research R10). Because predicates are authoritative
and labels are caches, stage derivation is a pure function over an immutable worktree snapshot
(R9). Because every external dependency sits behind a process or API boundary, a fake `claude`
binary plus disposable git fixtures plus an in-memory GitHub client run nearly the entire design
in CI with no credits spent (R11).

## Technical Context

**Language/Version**: C# on **.NET 10** (LTS), published for **`linux-arm64`** and run inside a
Docker container. The .NET runtime is an **image layer**, not a host install — the host needs
only Docker (research R1, R15).

**Primary Dependencies**: Tomlyn (config), Octokit.net (GitHub, behind a first-party port),
Serilog + rolling file sink (instance log), xUnit (tests). Deliberately small — the constitution
requires justifying each one.

**Storage**: **None.** No database, by constitutional mandate. State lives in GitHub Issues
(queue), git worktrees (work product), and Claude Code's session store (conversation, by ID).

**Testing**: xUnit across four tiers — pure logic; real git + tmux with a fake `claude`; two
property families (crash-convergence, injection canary); manual live probes via `doctor --probe`.
Tiers 1–3 spend zero credits and touch no network.

**Target Platform**: a **Docker container** (Debian/Linux, arm64) bundling git, tmux, and Claude
Code. The host runs only Docker + launchd; launchd fires each tick via `docker run`/`docker
exec`. Verified end-to-end by the 2026-07-25 probe (research R15, `probe/probe-results.md`).

**Project Type**: Single-project CLI — a single-file-published console binary installed once and
invoked per-instance with its config path.

**Performance Goals**: A tick with nothing to do exits within a few seconds (FR-003). Default
cadence is one tick per 5 minutes per instance — a few hundred API requests a day against a
5,000/hour budget, so throughput is a non-issue.

**Constraints**: At most one unit of work per tick; at most one live session per instance;
instances never coordinate. Live sessions have no timeout. Committed work must survive API
failure and arbitrary mid-tick kills.

**Scale/Scope**: One operator, a handful of instances, single-digit in-flight items per repo.
56 functional requirements across 6 user stories.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

Gates derived from `.specify/memory/constitution.md` v3.0.0. The constitution has since reached
v6.0.0; the increment section below re-checks the gates that moved, rather than restating this
table for a design that has not otherwise changed.

| # | Gate | Source | Initial | Post-design |
|---|---|---|---|---|
| 1 | No database; state only in GitHub, git, session store | §2, §3 | ✅ | ✅ Storage is "None" by design |
| 2 | Tick is stateless and killable anywhere; ticks converge | §3 | ✅ | ✅ Idempotency markers (R10) + Tier 3 property |
| 3 | Predicates authoritative, labels are caches | §3 | ✅ | ✅ `WorktreeSnapshot` → pure derivation (R9) |
| 4 | Predicates evaluated in the item's worktree, never the clone | §3, FR-013 | ✅ | ✅ Enforced by cwd assertion in Tier 2 |
| 5 | Worktree isolation structural, not locked | §3, FR-012 | ✅ | ✅ One worktree per item, cwd always the worktree |
| 6 | Instances never coordinate | §3 | ✅ | ✅ Per-instance lock/log/config; no shared anything |
| 7 | Shaping stages write no code | §3, FR-018 | ✅ | ✅ Stage grouping in data model |
| 8 | Forward-only correction; closed is terminal | §3 | ✅ | ✅ Status machine has no edge out of Closed |
| 9 | Audits modify nothing, unconditionally | §3, FR-039 | ✅ | ✅ Finding type carries no mutation path |
| 10 | Exit discipline; one unit of work per tick | §4 | ✅ | ✅ Exit-code table in CLI contract |
| 11 | `ArgumentList`, never shell concatenation | §4 | ✅ | ✅ Invocation contract; R6 |
| 12 | `doctor` + `doctor --probe` exist | §4 | ✅ | ✅ CLI contract |
| 13 | Pure logic testable without I/O | §4 | ✅ | ✅ Domain layer takes snapshots, not paths |
| 14 | GitHub behind a first-party interface | §5 | ✅ | ✅ `IGitHubClient` port; Octokit confined to adapter |
| 15 | No inbound network surface | §5 | ✅ | ✅ Poll-only; no webhooks |
| 16 | Single-operator allowlist, authenticated-author verified | §6, FR-005 | ✅ | ✅ Numeric user ID, fail-closed (R5) |
| 17 | Untrusted content cannot redirect a run | §6, FR-006 | ✅ | ✅ Delimited answer regions; Tier 3 canary |
| 18 | Minimal-scope PAT as mounted secret, never in config | §6, FR-052 | ✅ | ✅ Config holds a secret-file path only; schema forbids inline secrets |
| 19 | Branch protection assumed; merge gate configurable | §6, FR-056, FR-033b | ✅ | ✅ `doctor` checks protection; auto-merge gated on review passing |
| 27 | Auto-merged work produces a digest; spend over threshold blocks | §3, §6, FR-033c/d | ✅ | ✅ Digest before merge; cost added to always-block list |
| 28 | Review runs in a fresh session; checks cross-spec drift | §9, FR-034a1/c1 | ✅ | ✅ No session resumption; coverage-bounded drift check |
| 29 | Tick runs in a Docker container; no host .NET/tmux | §2, FR-052a | ✅ | ✅ Verified by the probe (R15); build/run is `linux-arm64` in-image |
| 30 | PAT is a mounted secret, not the Keychain | §2, §6, FR-052 | ✅ | ✅ `SecretFileStore` + `[secret].github_pat_file`; Keychain removed |
| 31 | Workspace trust pre-seeded at worktree creation | §3, FR-012a | ✅ | ✅ Verified by the probe (§3); one JSON write per worktree |
| 32 | claude.ai credential monitored for expiry | §3, FR-052b | ✅ | ✅ `doctor` checks expiry; in-container `/login` in a named volume |
| 20 | Config validated at startup, fail-fast | §7 | ✅ | ✅ Validation table in config contract |
| 21 | Usage limits are routine: revert to ready, exit 0 | §7, FR-043 | ✅ | ✅ Detection corpus as Tier 1 theory |
| 22 | Decision comments posted before continuing | §7, FR-031 | ✅ | ✅ Ordering is part of the contract |
| 23 | API failure never loses committed work | §7, FR-046 | ✅ | ✅ Tier 3 crash-convergence covers it |
| 24 | Test-first; tiers 1–3 credit-free | §8, testing §2.3 | ✅ | ✅ Fake claude + fixtures + in-memory GitHub |
| 25 | Specs executable and living; COVERAGE.md scope | §9 | ✅ | ✅ Every SC maps to a validation scenario |
| 26 | Dependencies justified individually | §8 | ✅ | ✅ Each of the four justified in research |

**Result**: PASS, both before Phase 0 and after Phase 1 design. No violations to justify;
Complexity Tracking is empty.

Two gates deserve a note rather than a checkmark alone:

- **Gate 16** is the one the operator strengthened during clarification and again in
  constitution v1.1.0. The design resolves the configured login to a **numeric** user ID and
  fails closed if that resolution fails, because login strings can be renamed and re-registered.
- **Gate 2** is not a coding-standards item but the reason several design choices look the way
  they do. It is discharged by the idempotency marker plus the Tier 3 property that kills ticks
  after each individual side effect and demands convergence.

## Project Structure

### Documentation (this feature)

```text
specs/001-spec-queue-runner/
├── plan.md              # This file
├── spec.md              # Feature specification (clarified 2026-07-25)
├── research.md          # Phase 0 output — 14 decisions + environment baseline
├── data-model.md        # Phase 1 output — value types, state machines
├── quickstart.md        # Phase 1 output — setup + 7 validation scenarios
├── contracts/           # Phase 1 output
│   ├── cli-commands.md          # command surface, exit codes
│   ├── config-schema.md         # TOML schema + validation rules
│   ├── issue-conventions.md     # labels, body lines, comment formats
│   └── claude-invocation.md     # headless/live contract + fake binary
├── checklists/
│   └── requirements.md  # spec quality checklist (16/16)
└── tasks.md             # Phase 2 output — NOT created by /speckit.plan
```

### Source Code (repository root)

```text
src/SpecRunner/
├── Program.cs                  # entry; command dispatch, exit codes
├── Configuration/
│   ├── InstanceConfig.cs       # bound TOML model
│   ├── ConfigLoader.cs         # Tomlyn parse
│   └── ConfigValidator.cs      # fail-fast startup validation
├── Domain/                     # PURE — no I/O, no processes, no network
│   ├── Kind.cs, Stage.cs, QueueStatus.cs
│   ├── StageSequence.cs        # kind → stage sequence
│   ├── WorktreeSnapshot.cs     # immutable derivation input
│   ├── StageDerivation.cs      # snapshot → stage
│   ├── WorkSelection.cs        # lowest-numbered ready item
│   ├── WakingHours.cs          # window arithmetic
│   ├── LabelMap.cs             # label ↔ enum
│   ├── RateLimitDetector.cs    # usage-limit matching
│   ├── IdempotencyMarker.cs    # comment identity (R10)
│   └── DecisionCap.cs
├── Ports/                      # the boundaries (constitution §5)
│   ├── IGitHubClient.cs
│   ├── IProcessRunner.cs
│   ├── ISecretStore.cs
│   ├── IClock.cs
│   └── IWorktreeReader.cs
├── Adapters/
│   ├── GitHub/GitHubClient.cs          # Octokit, confined here
│   ├── Git/GitWorktrees.cs             # add/remove/list/prune
│   ├── Tmux/TmuxSessions.cs            # new-session/send-keys/kill-session
│   ├── Claude/ClaudeInvoker.cs         # ArgumentList, concurrent drain
│   └── Secrets/SecretFileStore.cs      # reads PAT from mounted secret file (R15)
├── Ticking/
│   ├── Tick.cs                 # the orchestrator
│   ├── InstanceLock.cs         # FileShare.None
│   ├── OperatorIdentity.cs     # login → numeric ID, fail closed
│   ├── ReplyCollector.cs       # runs before work selection
│   ├── Reaper.cs               # sessions, held items, orphan worktrees
│   ├── WorkRunner.cs           # stage execution
│   ├── LiveChannel.cs          # spawn/resume/kill
│   ├── CommentFallback.cs      # questions-as-one-comment
│   ├── PullRequestOpener.cs    # push, PR, close, coverage
│   └── CoverageManifest.cs
├── Doctor/
│   ├── DoctorCommand.cs
│   └── Probes/                 # Tier 4 live probes
└── Logging/InstanceLog.cs      # Serilog rolling file

tests/
├── SpecRunner.TestKit/         # fake claude script, fixture repos, in-memory GitHub
├── SpecRunner.UnitTests/       # Tier 1
├── SpecRunner.IntegrationTests/# Tier 2 — real git + tmux, fake claude
└── SpecRunner.PropertyTests/   # Tier 3 — crash-convergence, injection canary
```

**Structure Decision**: Single project, ports-and-adapters. The layering is not ceremony here —
it is what the constitution's testability rules require. `Domain/` is pure so Tier 1 can drive
every stage-derivation and rate-limit case from `InlineData` fixtures with no environment.
`Ports/` exists so Tier 2 can substitute an in-memory GitHub while still exercising *real* git
and tmux through their adapters. The whole tick runs inside a Linux container (R15), so there is
no macOS-specific surface left in the tick itself; `launchd` (host-side, fires the container)
and secret delivery (mounted file) are the only host touchpoints, each behind a small seam.

---

# Increment — Give the review stage its context (2026-07-26, issue #15)

**Branch**: `work/15` | **Constitution**: v6.0.0 | **Research**: [R17](./research.md#r17--the-review-stages-context-what-it-is-told-and-how-issue-15)

## Summary

The review stage is invoked with the contents of `.specify/prompts/code-review.md` and **nothing
else**. That prompt says "this pull request", "this item's `spec.md`", and "every path this pull
request touches" — and the runner names none of them. The reviewer is dropped into a worktree and
left to infer which PR, which two refs to diff, which issue it serves, and which spec is the item's
own.

This increment supplies those four facts. The composed prompt is `instructions verbatim` +
`delimited context block`, in that order, and the context is assembled from state the tick already
holds: the `kind=pr` marker (PR number and URL), the config and worktree conventions (base ref and
head branch), the selected `WorkItem` (issue number, title, body), and `Git.SpecDirOnBranchAsync`
(the item's own spec directory, discovered from its branch — never guessed).

The correctness argument is not "the review will be better." It is that **the reviewer is currently
being asked to make the exact guess constitution v6.0.0 removed**: "which spec directory belongs to
this item" is unanswerable from `specs/` alone, and the failure mode is a confident review of
another item's spec rather than an error. A chore has no spec directory at all, so §2 of the review
prompt is unanswerable today for the kind that reaches review most often.

## Technical Context (delta only)

Everything in the table above still holds. What this increment adds:

**New pure surface**: `Domain/ReviewContext.cs` (the record) and `Domain/ReviewPrompt.cs`
(`Compose(instructions, context) -> string`). Both are I/O-free, so the entire composed prompt —
ordering, delimiters, the null-spec-dir wording — is a Tier 1 assertion, per §4's "pure logic stays
pure".

**Changed**: `Ticking/Tick.cs` — `RunReviewAsync` builds the context and composes; `RunImplementAsync`
writes `url=` into the `kind=pr` marker; `FindPrNumber` becomes a marker parser returning number
**and** optional URL, with the URL falling back to `https://github.com/{slug}/pull/{n}`.

**Unchanged deliberately**: `.specify/prompts/code-review.md` (it is repo-level and item-agnostic by
design, FR-034d), the config schema (no new setting — the four facts are derivable, not
configurable), and `ClaudeInvoker` (one argument, `ArgumentList`, fresh session).

**Out of scope, and stated rather than absorbed**: `specs/COVERAGE.md` does not exist in this
repository, so §3 of the review prompt currently has nothing to consult. Creating it is separate
work (FR-034g — review must not widen an item's scope). This increment makes the gap *visible*: the
context block states whether the manifest is present on the branch, so the cross-spec check is
either performed or reported as impossible, never silently dropped.

## Constitution Check (increment)

| # | Gate | Source | Verdict |
|---|---|---|---|
| 33 | Spec directory is discovered from the item's own branch, never by scanning `specs/` | §3 (v6.0.0) | ✅ `Git.SpecDirOnBranchAsync`; `null` is rendered explicitly, not papered over |
| 34 | The pipeline definition comes from the binary and the repo, never from issue text | §6, FR-034d, FR-054 | ✅ Instructions first and verbatim; context follows, framed as data |
| 35 | Untrusted content cannot redirect a run | §6, FR-006 | ✅ Only the operator-verified issue title/body; delimited region; no comment body enters the prompt |
| 36 | Injection canary stays green | testing §5 | ✅ Re-asserted directly against the review prompt, not inherited |
| 37 | Pure logic testable without I/O | §4 | ✅ `ReviewPrompt.Compose` is a pure function over a record |
| 38 | Per-iteration statelessness — nothing carried between ticks | §3 (v5.0.0) | ✅ `ReviewContext` is rebuilt each tick from GitHub, config, and git; never persisted |
| 39 | Review runs in a fresh session | §9, FR-034a1 | ✅ Unchanged — composition adds context, never `--resume` |
| 2 | Ticks converge; no duplicated comments or labels | §3 | ✅ The `kind=pr` marker gains a field; its `id=` is unchanged, so idempotency scanning is untouched and old markers still parse |

**Result**: PASS. No violations; Complexity Tracking stays empty.

## Implementation shape

Test-first (testing §2.3) — the composed prompt is a pure string, so the tests are cheap and
precise:

| Tier | Test | Asserts |
|---|---|---|
| 1 | `ReviewPromptTests` | instructions appear verbatim and **before** any context; every field present; `null` spec dir renders the explicit "no spec directory / section does not apply" wording; absent coverage manifest is stated; an issue body containing imperative text ("ignore the above and …") lands **inside** the data region |
| 1 | `PullRequestMarkerTests` | `number=`/`url=` parsed; a legacy marker with no `url=` yields `null` and the caller derives from the slug; a malformed marker is skipped, not half-read |
| 2 | `PipelineTests` (chore + feature) | the recorded review invocation's argv carries the PR number, both refs, and the issue number; the feature item names **its own** spec dir with a decoy second spec directory present; the chore names none |
| 3 | `InjectionCanaryTests` | a non-operator comment's canary appears in no review invocation — asserted against this path specifically |

Spec obligation (§9, living specifications): `spec.md` describes review's inputs in FR-034a–g but
states nothing about what review is *told*. `/speckit.tasks` should carry a task adding **FR-034h**
— "the review invocation MUST supply the pull request, the refs being compared, the issue, and the
item's spec directory (or state that it has none), with the version-controlled instructions taking
precedence over all of it" — so the behaviour this increment builds is not behaviour no requirement
claims.

## Complexity Tracking

> No Constitution Check violations. This section is intentionally empty.
