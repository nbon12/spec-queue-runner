# Phase 1 Data Model: Spec Queue Runner

**Feature**: 001-spec-queue-runner | **Date**: 2026-07-25

There is no database (constitution §2). "Data model" here means the in-memory value types the
tick computes over, and the three authoritative stores they are projected from:

| Store | Holds | Authority for |
|---|---|---|
| GitHub Issues | labels, comments, open/closed | queue status, decision log, session ID |
| Item worktree (git) | spec/plan/tasks files, commits | pipeline stage, all work product |
| Claude Code session store | conversation state | live-session continuity (by ID only) |

Nothing else persists. The tick itself holds no state between runs (§3).

---

## Value types (pure, immutable)

### Kind

Enum: `Feature`, `Amendment`, `Chore`, `Spike`, `Audit`.

Determines the stage sequence (FR-015):

| Kind | Stage sequence |
|---|---|
| Feature | intake → specify → clarify → plan → tasks → analyze → implement → review |
| Amendment | intake → specify → clarify → plan → tasks → analyze → implement → review |
| Chore | intake → plan → implement → review |
| Spike | intake → implement |
| Audit | intake → implement |

Spike and audit skip review because neither produces a diff: a spike investigates and reports,
and an audit is constitutionally forbidden from modifying anything.

### Stage

Enum: `Intake`, `Specify`, `Clarify`, `Plan`, `Tasks`, `Analyze`, `Implement`, `Review`.

Grouped as **shaping** (Intake, Specify, Clarify — never write code, FR-018) and **execution**
(Plan, Tasks, Analyze, Implement, Review — decide-and-report, FR-030/031).

### QueueStatus

Enum: `Ready`, `InProgress`, `Live`, `Waiting`, `Held`, `Closed`.

### WorktreeSnapshot

Immutable input to stage derivation (R9). Filesystem reading happens in an adapter; the
derivation function is pure.

| Field | Type | Meaning |
|---|---|---|
| `SpecExists` | bool | target `spec.md` present on the item's branch |
| `UnresolvedMarkerCount` | int | outstanding clarification markers in the target spec |
| `PlanExists` | bool | `plan.md` present |
| `TasksExists` | bool | `tasks.md` present |
| `AnalysisRecorded` | bool | analyze output recorded |
| `PullRequestOpen` | bool | PR opened for this item — the *entry* condition for review, not the finish line |
| `ReviewRecorded` | bool | review ran and recorded its outcome (FR-034f) — the exit predicate for review |

**Derivation rule**: the first unsatisfied exit predicate in the kind's sequence names the
current stage (FR-013). Predicates are evaluated against the *item's own worktree*, never the
shared clone — passing the wrong snapshot is the bug this type exists to make visible.

### WorkItem

Projection of one issue. Built fresh each tick; never cached across ticks.

| Field | Type | Source | Notes |
|---|---|---|---|
| `Number` | int | issue number | priority ordering, lowest first (FR-009) |
| `Kind` | Kind | `kind/*` label | inferred at intake (FR-016) |
| `Status` | QueueStatus | `status/*` label + open/closed | |
| `StageLabel` | Stage? | `stage/*` label | **a cache**; predicate is authoritative (§3) |
| `PinnedStage` | Stage? | manually applied label | overrides computed stage (FR-017) |
| `Targets` | string[] | `Targets:` body line | intake hint only (FR-016); no scheduling effect |
| `BlockedBy` | BlockingIssue[] | GitHub issue relationship | readiness gate (FR-010); open blockers only |
| `Recurring` | string? | `Recurring:` body line | successor filing (FR-042) |
| `LiveSessionId` | string? | `Live session:` comment | resume key (FR-022/024) |
| `AuthorId` | long | issue author's numeric ID | allowlist check (R5, FR-005) |
| `InProgressSince` | DateTimeOffset? | label-change timestamp | staleness reclaim (FR-044) |
| `DecisionCount` | int | count of decision comments | cap enforcement (FR-031) |

**Validation rules**:

- An item whose `AuthorId` ≠ the resolved operator ID is **excluded entirely** — not loaded,
  not prompted with, not replied to (FR-005). Same rule applies per-comment.
- `Targets` entries must resolve to spec directories; unresolvable targets block intake only
  when intent is unrecoverable (FR-016). They never hold an item — no body text does.
- `BlockedBy` is fetched per candidate, and only after the operator check, so a non-operator
  issue provokes no API call (FR-005).
- `Recurring` is carried forward verbatim into the successor's body (FR-042).

### DecisionRecord

One execution-stage judgement call. Posted **before** continuing so a crash never loses the
reasoning (FR-031, §7).

| Field | Meaning |
|---|---|
| `Ambiguity` | what was unclear |
| `Choice` | what was decided |
| `Alternatives` | what else was considered |
| `Rationale` | why |
| `CommitSha` | the commit it corresponds to (FR-032) |
| `IdempotencyId` | sha256 prefix embedded in the comment marker (R10) |

### ReviewRecord

The outcome of one review stage. Recorded even when nothing was found, so a silent review and
an absent review stay distinguishable (FR-034f).

| Field | Meaning |
|---|---|
| `FilesExamined` | every path in the PR diff, each compared before-and-after (FR-034b) |
| `UncoveredScenarios` | acceptance scenarios from the spec with no corresponding test (FR-034c) |
| `FixesApplied` | reversible findings fixed on the branch, each with its commit sha |
| `BlockedOn` | irreversible findings that forced a block, if any |
| `FiledIssues` | out-of-scope findings filed as new issues rather than fixed (FR-034g) |

### ReviewContext

Everything the review stage is *told* about what it is reviewing (R17). Assembled by the tick from
facts it already holds; the reviewer derives none of it. Pure and immutable, so the composed prompt
is a Tier 1 assertion rather than something only a live run reveals.

| Field | Meaning | Source |
|---|---|---|
| `IssueNumber` | the work item under review | selected `WorkItem` |
| `IssueTitle` | its title | selected `WorkItem` |
| `IssueBody` | its body — **operator-authored, untrusted data** (FR-005/006) | selected `WorkItem` |
| `PullRequestNumber` | the PR review works against (FR-034a) | `kind=pr` marker comment |
| `PullRequestUrl` | its URL | marker `url=`, else derived from the slug |
| `BaseRef` | the "before" side of the diff, e.g. `origin/main` | `config.BaseBranch` |
| `HeadBranch` | the "after" side, e.g. `work/15` | `WorktreeLifecycle.BranchFor` |
| `SpecDir` | the item's own spec directory, repo-relative — **`null` when the branch adds none** | `Git.SpecDirOnBranchAsync` (§3, v6.0.0) |
| `CoverageManifest` | `specs/COVERAGE.md` if present on the branch, else `null` | filesystem check in the worktree |

Invariants:

- `SpecDir` is **discovered from the item's own branch, never guessed** by scanning `specs/` — the
  same rule stage derivation obeys (§3). `null` is a legitimate value (a chore has no spec) and the
  composed prompt says so out loud rather than leaving the reviewer to hunt.
- `IssueBody` is the only untrusted field, and it is rendered **inside a delimited data region,
  after** the version-controlled instructions (FR-034d/054). No comment body is ever a field here —
  that is what keeps the Tier 3 injection canary green through the review path.
- Every field is knowable before the invocation, so composition is a pure function:
  `ReviewPrompt.Compose(instructions, context) -> string`.

### Finding

An audit discrepancy. Names which side appears wrong; never prescribes the correction (FR-039/040).

| Field | Meaning |
|---|---|
| `SpecPath` | spec evaluated |
| `CodePath` | path under its coverage entry |
| `SideAppearingWrong` | `Spec` \| `Code` |
| `Evidence` | why |

### InstanceConfig

Parsed from TOML, validated fail-fast at startup (§7). See `contracts/config-schema.md`.

---

## State transitions

### Status axis

```
                  ┌──────────────────────────────────────────┐
                  │                                          │
ready ──▶ in-progress ──┬──▶ live ──(resolved in session)────┘
  ▲                     │      │
  │                     │      └──(cannot establish)──▶ waiting ──(reply)──▶ ready
  │                     ├──▶ waiting ──(reply resolves)──────────────────────┘
  │                     ├──▶ closed  (PR open) ── terminal, never reopened
  │                     └──▶ ready   (usage limit / crash / stale reclaim)
  │
held ─────────────────────────(targets land on main)──────────────────────────┘
```

Invariants:

- `Closed` is **terminal**. No transition leaves it — correction is forward-only via a new
  issue (§3, FR-034).
- `Live` and `Held` are **exempt from staleness reclaim** (FR-044); both wait indefinitely
  by design.
- `Ready → InProgress` is set *before* invoking Claude Code (FR-011).
- Any usage-limit failure returns the item to `Ready` and exits 0 (FR-043).

### Stage axis

Advances only forward through the kind's sequence, one predicate at a time, except:

- A run advances through as many *consecutive execution* stages as it can in one invocation
  (FR-020); each predicate is the resume checkpoint.
- Exceeding the decision cap sends items with a clarify stage **back** to `Clarify`; kinds
  without one block through the live channel instead (FR-031, per clarification).
- A pinned stage label overrides derivation entirely (FR-017).

### Live session lifecycle

```
(blocked, within waking hours) ──▶ spawn tmux + claude ──▶ record ID on issue ──▶ status/live
                                          │
        ┌─────────────────────────────────┼──────────────────────────────┐
        ▼                                 ▼                              ▼
   predicate satisfied            process died                  answered on issue
   → kill session, ready       → respawn --resume <id>       → kill session, ready
                                  (no re-ask, no re-push)
```

Loss of the session costs nothing beyond a pause — all resumable state lives in the worktree,
the issue, and the session store (FR-047, §3).

---

## Relationships

- **Instance** 1—1 **repository**; instances never coordinate (§3).
- **WorkItem** 1—1 **Worktree** (created lazily at first need, removed after the PR opens;
  pruned by the reaper if the issue is closed — FR-012/014).
- **WorkItem** 1—0..1 **LiveSession**; at most one live session exists per *instance* (FR-025).
- **WorkItem** 1—* **DecisionRecord**, 1—0..1 **PullRequest**, 1—0..1 **ReviewRecord**.
- **PullRequest** precedes **ReviewRecord**: the PR is opened at the end of implement and is
  the surface review works against, so an item carries an open PR while still in flight.
- **ReviewContext** 1—1 the review *invocation*: assembled per run from the **WorkItem**, its
  **PullRequest**, the instance config, and the item's branch. It is not persisted — every tick
  rebuilds it from authoritative state (§3, per-iteration statelessness).
- **Spec** 1—1 **coverage entry** in `specs/COVERAGE.md`; a spec's claim of accuracy extends
  only to paths under that entry (FR-037, §9).
