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

**Language/Version**: C# on **.NET 10** (LTS). Note: **not installed on this machine** — only
SDKs 8.0.404 and 9.0.303 are present. Installing it is an explicit setup task (research R1).

**Primary Dependencies**: Tomlyn (config), Octokit.net (GitHub, behind a first-party port),
Serilog + rolling file sink (instance log), xUnit (tests). Deliberately small — the constitution
requires justifying each one.

**Storage**: **None.** No database, by constitutional mandate. State lives in GitHub Issues
(queue), git worktrees (work product), and Claude Code's session store (conversation, by ID).

**Testing**: xUnit across four tiers — pure logic; real git + tmux with a fake `claude`; two
property families (crash-convergence, injection canary); manual live probes via `doctor --probe`.
Tiers 1–3 spend zero credits and touch no network.

**Target Platform**: macOS (launchd, keychain, tmux). The headless path must run unchanged on
Linux; platform-specific surface is confined to individual adapters.

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

Gates derived from `.specify/memory/constitution.md` v1.1.0.

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
| 18 | Minimal-scope PAT in keychain, never in config | §6 | ✅ | ✅ Config schema forbids secrets |
| 19 | Branch protection assumed; merge gate configurable | §6, FR-056, FR-033b | ✅ | ✅ `doctor` checks protection; auto-merge gated on review passing |
| 27 | Auto-merged work produces a digest; spend over threshold blocks | §3, §6, FR-033c/d | ✅ | ✅ Digest before merge; cost added to always-block list |
| 28 | Review runs in a fresh session; checks cross-spec drift | §9, FR-034a1/c1 | ✅ | ✅ No session resumption; coverage-bounded drift check |
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
│   └── Keychain/KeychainSecretStore.cs # /usr/bin/security (macOS-only seam)
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
and tmux through their adapters. `Adapters/Keychain` and the tmux calls are the only
macOS-specific surface, which is what keeps the Linux-portability rule (§2) honest — a port
replaces adapters, not logic.

## Complexity Tracking

> No Constitution Check violations. This section is intentionally empty.
