<!--
Sync Impact Report
==================
Version change: 1.2.0 -> 2.0.0
Modified principles:
  Security Model (section 6) — REMOVED the auto-merge prohibition. The runner MAY merge its own
    pull requests after the review stage passes, when the instance is configured for it. The
    consequence is stated rather than hidden: with auto-merge on, the always-block list becomes
    the ONLY human checkpoint on unattended work, so section 3's block list is correspondingly
    hardened and given a cost dimension.
  Architectural Invariants (section 3) — the always-block list gains an estimated-spend
    threshold (default $100, configurable), and the list is now named as load-bearing rather
    than advisory. Added a reporting duty: an auto-merged item MUST deliver a digest of what
    happened, since the operator no longer sees the work by merging it.
  Executable & Living Specifications (section 9) — review now also enforces the existing
    cross-spec consistency and corpus-invariant rules, checking a change against every OTHER
    spec whose coverage entry includes a touched path.
Added sections: None.
Removed sections: None.
Version bump rationale (MAJOR): removal of a non-negotiable rule (the auto-merge prohibition)
  that section 6 previously described as the structural human checkpoint.
CONDITION ON THIS AMENDMENT — the rationale offered was that the project is pre-customer, with
  no production data and therefore no data-loss exposure. That premise is time-bound. This
  amendment MUST be revisited, and auto-merge disabled or narrowed, at the first of: the repo
  serving real users, holding real data, or acquiring a deploy path that reaches either.
Templates requiring updates:
  .specify/templates/plan-template.md ✅ compatible — Constitution Check populated at plan time.
  .specify/templates/spec-template.md ✅ compatible — no change needed.
  .specify/templates/tasks-template.md ✅ compatible — test-first mandate already stated.
Follow-up TODOs: None.

----- prior amendment -----
Version change: 1.1.0 -> 1.2.0
Modified principles:
  Architectural Invariants (section 3) — the execution-stage enumeration now includes a
    **review** stage: plan, tasks, analyze, implement, review. Review is an execution stage and
    follows execution ambiguity policy (decide and report; block only on irreversibility).
  Executable & Living Specifications (section 9) — named the review stage as the enforcement
    mechanism for the existing "no unverified claims" rule: review verifies that the tests a run
    actually wrote cover the acceptance scenarios the spec states in natural language, rather
    than leaving that traceability to assertion.
Added sections: None.
Removed sections: None.
Templates requiring updates:
  .specify/templates/plan-template.md ✅ compatible — Constitution Check populated at plan time.
  .specify/templates/spec-template.md ✅ compatible — no change needed.
  .specify/templates/tasks-template.md ✅ compatible — test-first mandate already stated.
Version bump rationale (MINOR): a stage was added to a governed enumeration and an existing
  principle gained a named enforcement mechanism; no existing rule was removed or redefined.
Follow-up TODOs: None.

----- prior amendment -----
Version change: 1.0.0 -> 1.1.0
Modified principles:
  Security Model (section 6) — materially expanded the injection defenses: prompt injection
    is now named as the primary threat every GitHub-sourced string must be treated against;
    the author allowlist is bound to exactly ONE operator identity — Nicholas Bonilla's
    GitHub account — verified via the GitHub API's authenticated author field (never display
    names, body signatures, or email claims, all of which are spoofable). Content from any
    other identity, including collaborators, org members, and bots, MUST NOT be processed,
    quoted, summarized, or otherwise reach any prompt.
Added sections: None.
Removed sections: None.
Templates requiring updates:
  .specify/templates/plan-template.md ✅ compatible — Constitution Check populated at plan time.
  .specify/templates/spec-template.md ✅ compatible — no change needed.
  .specify/templates/tasks-template.md ✅ compatible — Tier 3 injection-canary mandate already
    referenced; no wording change required.
Version bump rationale (MINOR): materially expanded guidance within section 6 (single named
  operator identity + authenticated-author verification rule); no existing rule removed.
Follow-up TODOs: None.

----- prior amendment -----
Version change: (none) -> 1.0.0 (initial ratification)
Modified principles: n/a — first version.
Added sections:
  1. Project Purpose
  2. Technology Stack
  3. Architectural Invariants
  4. CLI & Process Principles
  5. External Boundaries
  6. Security Model
  7. Operations, Configuration & Failure Handling
  8. Quality & Code Standards
  9. Executable & Living Specifications
  10. Governance & Amendments
  Spec Kit Testing Constitution (CLI edition: fake claude, fixture repos, four tiers)
Removed sections: n/a.
Templates requiring updates:
  .specify/templates/plan-template.md ✅ compatible — Constitution Check gate is populated
    at plan time from this file; no template edit required.
  .specify/templates/spec-template.md ✅ compatible — no database/web assumptions present.
  .specify/templates/tasks-template.md ✅ updated — "tests optional" note replaced with the
    constitution's test-first mandate and tier mapping; sample tasks remain illustrative.
Follow-up TODOs: None.
-->

# Spec Queue Runner Constitution

**Version**: 2.0.0 | **Ratified**: 2026-07-25 | **Last Amended**: 2026-07-25
**Authors**: Project maintainers

## 1. Project Purpose

This project provides **Spec Queue Runner**, an unattended worker that pulls work items from
a GitHub Issues book of work and drives them through the SpecKit pipeline while the operator
is away, resuming automatically when Claude Code credits reset. All features MUST align with:

- **Unattended correctness**: the system runs overnight with no human present; every design
  decision MUST assume nobody is awake to intervene.
- **One instance per repository**: the same binary runs as fully independent instances —
  separate config, launchd job, lock, and log. Instances MUST NOT coordinate, share state,
  or know of each other's existence.
- **One worktree per work item**: every Claude Code invocation for an item executes in that
  item's own git worktree. Items MUST NOT share a checkout; the clone is never a working
  directory for item work.
- **The book of work is GitHub Issues**: an issue is a work item; labels carry kind, status,
  and stage; open/closed is the lifecycle; decision comments carry the judgement log.
- **Specs describe the present tense**: SpecKit artifacts under `specs/` state what the
  system *is*, never how it got there, and contain no process metadata.
- **Work integrates through pull requests**: the operator's merge is the human review gate
  for unattended work. Nothing the runner writes reaches `main` without an operator merge.
- **Forward-only correction**: closed issues are never reopened; wrong decisions are remedied
  by new issues, never by rollback.

## 2. Technology Stack

All implementations MUST conform to the following:

- **Application**: a **.NET console application** (the "tick"), published as a
  **single-file executable**, installed once and invoked per-instance with its config path
  as the sole positional argument.
- **Language/conventions**: C# with standard .NET naming conventions and nullable reference
  types enabled.
- **Configuration**: **TOML** config files, one per instance, holding exactly one repo pair
  (issues slug + local clone path) plus instance settings. Secrets MUST NOT appear in config.
- **Secrets**: the GitHub token is a fine-grained PAT referenced from the **macOS keychain**;
  it is never stored in config, the repo, or environment files committed to the repo.
- **Scheduling**: **launchd** with `StartInterval` (not cron) — launchd runs a missed job on
  wake, which is essential for a system premised on overnight work. The launchd plist is
  per-instance.
- **GitHub access**: the **REST API** via a fine-grained PAT; Octokit or raw `HttpClient`,
  whichever has less friction, behind an interface the tests can fake.
- **Process orchestration**: `git` (worktrees), `tmux` (live sessions), and `claude`
  (headless and interactive) are invoked as **external processes**; the tick contains no
  embedded git library, terminal emulation, or agent runtime.
- **Locking**: per-instance exclusive file lock via `FileStream` with `FileShare.None`,
  released by `using` scope even on unhandled exception.
- **Persistence**: **no database**. Queue state lives on GitHub; work product lives in git;
  conversation state lives in Claude Code's session store, referenced by ID from the issue.
  The tick itself is stateless. A database MUST NOT be introduced without a constitution
  amendment.
- **Portability**: no platform-specific tooling beyond the tmux and launchd touchpoints; the
  headless path MUST run unchanged on Linux. launchd- and keychain-specific code MUST be
  isolated behind small seams so a Linux port replaces integration points, not logic.
- **Testing**: **xUnit**, per the Spec Kit Testing Constitution below. No Testcontainers,
  no database fixtures — the test doubles are a fake `claude` binary, disposable git fixture
  repos, and a faked GitHub client behind its interface.

## 3. Architectural Invariants

These are the load-bearing rules of the design. Violating any of them is an architectural
regression, not a style issue.

- **The tick is stateless reconciliation.** A tick reads authoritative state (GitHub, the
  worktree filesystem, tmux), computes what to do, does at most one unit of work, and exits.
  It MUST be killable at any point: re-running ticks to quiescence MUST converge to the same
  end state as a never-crashed run, with no duplicated comments or labels.
- **Predicates are the authority; labels are a cache.** Every pipeline stage has an exit
  condition that is a filesystem or issue predicate, observable by any tick with no memory
  of previous ticks. Stage predicates MUST be evaluated in the item's worktree, never the
  clone. The `stage/*` label records what was last observed and MUST NOT be treated as a
  source of truth.
- **The filesystem is the record; conversations are scaffolding.** Resolutions from live
  sessions land in the spec or plan on disk. No correctness-relevant state may live only in
  a running process, a tmux pane, or a chat transcript.
- **Worktree isolation is structural.** The first invocation an item needs creates its
  worktree (`git worktree add <worktrees_root>/<nr> work/<nr>`); every invocation for that
  item runs there; the worktree persists across ticks, crashes, rate limits, and open live
  sessions, and is removed only after the PR is opened at close. Item collisions MUST be
  inexpressible, not merely locked against.
- **Instances never coordinate.** No shared locks, no shared scheduler, no cross-repo
  priority. Credit contention and Remote Control slot contention resolve through the
  existing failure paths (usage-limit revert; comment fallback).
- **Ambiguity policy is a property of stage.** Shaping stages (intake, specify, clarify)
  block and ask, and MUST NOT write code under any circumstance. Execution stages (plan,
  tasks, analyze, implement, review) decide and report — except where a wrong choice is
  irreversible, which blocks. Reversibility judgement MUST prefer an explicit always-block
  list over in-the-moment judgement. The list is: destructive migrations, outbound third-party
  calls, secrets, force-push, configured protected paths, and any change whose **estimated
  recurring or one-off spend exceeds the configured threshold** (default $100). Where
  auto-merge is enabled this list is the sole human checkpoint (§6) — a named, auditable list
  is the only thing standing between an unattended run and an unreviewed merge.
- **Auto-merged work MUST be reported.** When the runner merges its own pull request, the
  operator no longer sees the change by approving it. The runner MUST therefore deliver a
  digest of what happened — what changed, what the review found, what it decided and why —
  through the channels §5 already permits. Merging silently is prohibited even where merging
  automatically is not.
- **Generated code is reviewed before the item closes.** A run that writes code MUST pass
  through a review stage that examines the item's changes as a diff — the before and after of
  every file the run touched — and verifies that the tests it wrote cover the acceptance
  scenarios its spec states. Review is an execution stage: it fixes what it can and reports
  the fix, and blocks only on irreversibility. An item is not done until it has been reviewed.
- **Correction is forward-only.** Closed issues stay closed; the book is append-only;
  requested PR changes become new issues. No feature may introduce a rollback or reopen path.
- **Audits observe; they never reconcile.** An audit MUST NOT modify a spec or code,
  unconditionally — not subject to the reversibility judgement.

## 4. CLI & Process Principles

- **Exit discipline**: a tick that finds the lock held, or has nothing to do, MUST exit 0
  within a few seconds. Exit codes MUST distinguish "nothing to do" (0) from configuration
  and environment errors (non-zero) so launchd logs are diagnosable.
- **One unit of work per tick.** Work selection is the lowest-numbered open issue labelled
  `status/ready`; exactly one item per tick. Parallel work within an instance is out of
  scope without amendment.
- **Argument safety**: external processes MUST be invoked with `ProcessStartInfo.ArgumentList`
  (or equivalent per-argument APIs), never shell string concatenation — prompts contain
  quotes, newlines, and backticks.
- **A `doctor` command** MUST exist to verify one-time manual prerequisites (Claude OAuth
  login, workspace trust, Remote Control push setting, app sign-in) and, via `doctor --probe`,
  to run the manual live-probe checklist. Environmental preconditions are verified by
  tooling, not remembered by the operator.
- **Pure logic stays pure.** Stage derivation, kind → stage-sequence dispatch, waking-hours
  arithmetic, label mapping, config parsing, and rate-limit detection MUST be implemented as
  pure functions testable without processes, network, or filesystem side effects.

## 5. External Boundaries

Every external dependency sits behind a process or API boundary, and the code MUST preserve
that:

- **GitHub** is accessed only through a client interface; production implements it over the
  REST API, tests fake it. The tick reads and writes the book of work exclusively through
  this boundary.
- **`claude`**, **`git`**, and **`tmux`** are shelled out to. Their invocation sites MUST be
  narrow enough that the fake `claude` binary and disposable fixture repos exercise the real
  invocation code paths in CI.
- **No new resident processes.** The only resident process per instance is the (at most one)
  tmux-wrapped live session, which is deliberately disposable: spawned by a tick, resumed by
  recorded session ID after any death, killed by the reaper when its item resolves. Loss of
  a live session MUST cost nothing beyond a pause.
- **No inbound network surface.** No webhooks, no listening sockets, no relays. The runner
  polls; notification is GitHub's own plus Remote Control's push, and the runner sends
  nothing out-of-band beyond these.

## 6. Security Model

The primary threat is **prompt injection**: content that reaches a prompt and redirects an
agent with write access to the repo. Because the runner's entire book of work arrives
through GitHub Issues, every string sourced from GitHub — issue titles, bodies, comments,
labels applied by others, linked content — MUST be treated as a potential injection vector,
at every stage, without exception.

- **Single-operator allowlist is the primary gate (NON-NEGOTIABLE).** The tick processes
  issues and comments authored by exactly **one** allowlisted identity: **Nicholas
  Bonilla's GitHub account**, as configured per instance. Authorship MUST be verified via
  the GitHub API's **authenticated author identity** (the account the API attributes the
  content to) — never via display names, body signatures, or claimed email addresses, all
  of which are spoofable. Content from **any** other identity — collaborators, org members,
  external users, or bots — MUST be ignored entirely: not read into any prompt, not quoted,
  not summarized, not replied to. Bot-authored comments are recognised as the runner's own
  output, never as input. Widening the allowlist beyond the single operator requires a
  constitution amendment.
- **Issue and comment content is untrusted input even when the author check passes.**
  Operator-authored text supplies requests and answers; it MUST NOT be able to redirect a
  run away from its own subject matter. Prompts MUST be structured so issue content answers
  questions the runner asked; the pipeline definition comes from the tick binary and the
  spec directory, never from issue text. Instructions embedded in issue content that
  attempt to alter the pipeline, permissions, or scope are content to be worked on, not
  commands to be obeyed.
- **Token scope is minimal.** Each instance's PAT is fine-grained, scoped to its one repo,
  with issues, contents, and pull-request permissions only, stored in the macOS keychain.
- **Headless invocations are contained**: they run with the permission mode configured for
  unattended use, in the item's worktree, with no access to secrets beyond what the repo
  legitimately needs.
- **Branch protection is structural; the merge gate is configurable.** Branch protection on
  `main` (no direct pushes, PR required) MUST be assumed and never worked around — every change
  reaches main through a pull request. Whether the *runner* may merge that pull request is an
  instance configuration. When auto-merge is enabled, the runner MAY merge only after the review
  stage has completed and recorded no blocking finding.
- **With auto-merge enabled, the always-block list is the only human checkpoint.** This is the
  cost of the trade and MUST be treated as such: the list in section 3 is load-bearing, not
  advisory, and additions to it are security changes rather than conveniences. An instance that
  enables auto-merge without a reviewed block list has no human in the loop at all.
- **Operator-owned repos only.** Configured repos must be operator-owned; the runner MUST
  NOT be pointed at employer or client code on a personal subscription.
- The injection guarantee MUST be enforced by tests (canary property tests, below), not by
  convention.

## 7. Operations, Configuration & Failure Handling

- **Configuration is validated at startup, fail-fast.** The tick MUST validate its parsed
  config (paths exist or are creatable, waking-hours window parses, slug well-formed,
  keychain entry resolvable) and refuse to run on invalid or missing configuration rather
  than deferring failures to mid-tick. New configuration of any kind MUST ship with its
  validation.
- **Structured, append-only logging.** Full stdout/stderr of every headless run is appended
  to the instance's rolling log. Log lines MUST carry enough context (issue number, stage,
  timestamp) to reconstruct a night's activity from the log alone.
- **Usage-limit failures are routine, not errors.** On a usage-limit failure from Claude,
  revert the item to `status/ready` and exit 0; the next tick retries. Detection is a
  case-insensitive match on `rate limit|usage limit`, with full output logged and the
  detection corpus grown from captured real output.
- **Stale reclaim**: an issue in `status/in-progress` older than the configured stale
  threshold is reset to `status/ready` at tick start. `status/live` and `status/held` are
  exempt — both wait indefinitely by design.
- **API failure never loses committed work.** If GitHub is unreachable mid-run, work already
  committed in the worktree is preserved; pushes, comments, and label changes are retried by
  a later tick.
- **Decision comments are posted before continuing**, so a crash never loses the reasoning.
  The decision report — ambiguity, choice, alternatives, rationale, commit reference — is a
  product of the run, sufficient to file a corrective issue without archaeology.
- **Reporting is one-way.** No message asks for approval of a completed decision;
  disagreement acts through a new issue or an unmerged PR.

## 8. Quality & Code Standards

- Adhere to .NET naming conventions and consistent project code style; analyzers/formatting
  MUST be enforced in CI where configured.
- Pure decision logic MUST have unit tests; process- and git-touching behaviour MUST have
  integration tests against real git and tmux with the fake `claude`; the two property-test
  families (crash-convergence, injection canary) MUST stay green at all times.
- New functional requirements land with tests in the tier that matches them (see testing
  constitution); a change that alters stage predicates, reaper behaviour, or failure paths
  without touching the corresponding tests is incomplete by definition.
- Dependencies are kept minimal: this is a single small binary; adding a package requires
  the plan to say why shelling out or the BCL is insufficient.
- CI runs the full Tier 1–3 suite on every PR with no Claude credits spent; Tier 4 probes
  are manual by nature and documented as such.

## 9. Executable & Living Specifications

Specifications are the executable contract of the system and MUST stay true at all times.

- **Always executable**: every feature spec (`spec.md`) MUST remain executable. Each
  mandatory acceptance scenario and functional requirement MUST be backed by at least one
  automated test (unit, integration, or property test — or a documented Tier 4 manual probe
  where automation is impossible) that can be run on demand and currently passes against
  merged code.
- **No unverified claims**: a spec MUST NOT describe behaviour that no executable test (or
  named manual probe) verifies. Acceptance scenarios MUST be traceable to tests and tests
  back to acceptance criteria. **The review stage is the enforcement mechanism**: it checks
  the tests a run actually wrote against the acceptance scenarios its spec states in natural
  language, so traceability is verified rather than asserted.
- **Living and truthful (`spec.md` only)**: when implemented behaviour diverges from any
  `spec.md` — including older, already-merged specs — the divergence MUST be resolved before
  merge: update the spec to reflect reality, or fix the code. Spec drift is a defect.
  `tasks.md`, `plan.md`, and `research.md` are point-in-time artifacts and carry no
  freshness obligation once merged.
- **Pre-PR freshness**: before a PR is submitted, the active feature's `spec.md` and
  `tasks.md` MUST reflect the work actually performed, and any older `spec.md` that has
  drifted MUST be corrected.
- **Cross-spec consistency**: contradictions between specs MUST be reconciled before merge —
  update the superseded spec, record which prevails and why. The spec corpus MUST be free of
  mutually contradictory executable assertions at all times. **The review stage enforces this
  too**: for every path a change touches, review MUST check that change against every *other*
  spec whose coverage entry claims that path, and report any drift or regression it introduces
  in behaviour those specs describe. Coverage bounds the check — a spec that does not claim a
  touched path is not consulted.
- **Scope of truth is narrow by design**: a spec's claim extends only to paths under its
  `specs/COVERAGE.md` entry. Code outside coverage is described by no spec, and no spec may
  claim it. Specs contain no runner metadata of any kind — no status, no transcripts, no
  decision logs.

## 10. Governance & Amendments

- Changes to this constitution MUST be reviewed and approved like any architectural decision
  record affecting the whole project.
- Pull requests SHOULD be focused vertical slices delivering independently testable
  increments; horizontal PRs are allowed for cross-cutting infrastructure, quality,
  security, or refactoring work. Broad mixed-scope PRs MUST be split or explicitly
  justified.
- The project MUST be built incrementally; implementation plans MUST NOT attempt to deliver
  unrelated surface area at once. In particular, the four live-probe open questions (trust
  across worktrees, tmux kickoff delivery, session resumption under Remote Control,
  concurrent Remote Control sessions) MUST be resolved by `doctor --probe` before dependent
  queue logic is built on their answers.
- Each amendment MUST document rationale, author, and version bump per semantic versioning:
  - **MAJOR**: breaking governance or removal/redefinition of non-negotiable rules.
  - **MINOR**: new principle, section, or materially expanded guidance.
  - **PATCH**: clarifications, wording, typos, non-semantic refinements.
- Prior versions SHOULD be retained in version control history for auditability.

---

# Spec Kit Testing Constitution

(.NET CLI, process-boundary fakes, no database)

## Purpose

This constitution defines rules for automated tests using **xUnit**. Tests are:

- **Deterministic** — repeatable and order-independent
- **Isolated** — test artifacts do not leak across tests
- **Realistic** — real `git` and `tmux`, faithful process invocation paths
- **Credit-free** — no test ever spends Claude credits; the agent is always the fake

It applies to unit, integration, property, and manual-probe tests.

## 1. Core Test Assets

### 1.1 The fake `claude` binary

The central test asset: a script placed first on PATH that reads its arguments and behaves
per a scenario file — emit a spec with N clarification markers, fail with a usage-limit
message, produce decision comments, hang. All integration and property tests that exercise
agent invocation MUST go through the fake so the real invocation code paths (argument
construction, output capture, exit-code handling) are what is tested.

### 1.2 Fixture repos

Disposable git repositories created per test in a temp directory, with whatever branches,
worktrees, and spec files the scenario needs. Tests MUST create their own fixtures and MUST
NOT share mutable repos across tests.

### 1.3 Faked GitHub client

The GitHub boundary is an interface; tests substitute an in-memory implementation holding
issues, labels, and comments. No test calls the real GitHub API.

## 2. Guiding Principles

### 2.1 Universal isolation

Tests MUST assume other tests run in parallel and that previous runs may have left temp
directories, tmux sessions, or lock files behind. Tests MUST use unique scoped resources
(per-test temp dirs, uniquely named tmux sessions, per-test lock paths) and idempotent
setup/cleanup so they never depend on a clean global environment. Any tmux session a test
creates, the test kills — including on failure paths.

### 2.2 Boundary honesty

Unit tests fake nothing they don't own: pure functions take values in and return values out.
Integration tests use real `git` and real `tmux` with the fake `claude`; they MUST NOT mock
process invocation itself, because argument construction and output handling are exactly
what they exist to verify.

### 2.3 Test-first (red-green)

Tests MUST be written **before** implementation where this constitution applies to a task.

- Tests MUST be traceable to specification acceptance criteria before implementation begins.
- Non-compiling tests MAY be temporarily commented with a clear path to restore them.
- Implementation MUST proceed in **red → green** cycles until tests pass.

### 2.4 Data-varied tests with Theories

Tests validating the same behaviour across multiple inputs — stage-derivation fixtures,
label mappings, waking-hours boundaries, rate-limit corpus lines, config validation cases —
MUST use xUnit **Theories** with explicit data (`InlineData`, `MemberData`, or `ClassData`).
Copy/paste Facts for data variations are prohibited unless a test documents why cases have
materially different setup or assertions.

## 3. Tier 1 — Pure Logic (unit tests)

- Stage derivation as a pure function: directory fixtures in, stage out (no `spec.md` →
  specify; markers present → clarify; `plan.md` without `tasks.md` → tasks).
- Kind → stage-sequence dispatch, including intake inference recording a decision rather
  than blocking.
- Rate-limit detection against a corpus of captured real error output, grown as production
  logging accumulates reality.
- Config parsing and validation, waking-hours window arithmetic, label mapping.

## 4. Tier 2 — Real git and tmux, fake claude (integration)

Mandatory scenario families:

- **Lock**: two simultaneous ticks, one config; exactly one works.
- **Rate-limit revert**: fake claude fails mid-implement; status returns to `ready`, commits
  preserved, next tick resumes at the correct stage.
- **Reaper matrix**: dead session → respawn carries the recorded resume ID; predicate
  satisfied → session killed, item advanced; closed issue with orphan worktree → pruned;
  held item whose target merged → promoted.
- **Isolation**: a session open and mid-edit in item A's worktree while item B runs
  implement end-to-end; A's files byte-identical after.
- **Held gating**: an amendment targeting an unmerged spec holds with a stated reason;
  merging the fixture PR promotes it next tick.
- **Fallback**: live establishment forced to fail; questions land as one comment with the
  reason; a reply resolves the item; the next blocked item attempts live again.

## 5. Tier 3 — Invariants (property tests)

- **Crash-convergence**: inject a kill after each individual side effect — label set,
  comment posted, worktree created, commit made — then re-run ticks to quiescence. The end
  state MUST equal the never-crashed end state, with no duplicated comments or labels.
- **Injection canary**: seed fixture issues with non-operator comments containing a canary
  string. Assert the canary never appears in any fake-claude invocation's arguments or
  stdin, across every scenario in the suite. The author allowlist as a guarantee, not a
  promise.

These two families are constitutional: they MUST exist, MUST run in CI, and MUST pass before
any merge that touches side-effect ordering, prompt construction, or reply collection.

## 6. Tier 4 — Live probes (`doctor --probe`)

Questions that cannot be CI'd are scripted as a manual checklist, each printing pass/fail:
workspace trust across worktree directories, tmux kickoff delivery, session resumption under
Remote Control, concurrent Remote Control sessions. Probe results are recorded where the
affected spec or plan can cite them. Tier 4 is the only tier permitted to spend credits or
require a phone.

## 7. Cross-cutting rules

- **Order independence**, **repeatability**, **single-reason failures**, **resource
  ownership** by each test, **parallel safety**, **rerun safety**, and **zero credit spend**
  in Tiers 1–3.

## 8. Strategic outcome

- **Tier 1**: decision logic provably correct without any environment
- **Tier 2**: real process orchestration verified without any credits
- **Tier 3**: the two invariants the whole design leans on, held as guarantees
- **Tier 4**: the handful of realities only a phone can verify, checklisted

## 9. Completion gate

Implementation for a feature or task MUST NOT be considered complete until Tier 1–3 tests
run locally (or in CI) and **all pass**, and any Tier 4 probe the feature depends on has
been run and recorded. The acceptance conditions of the feature's spec double as its
end-to-end suite: automatable conditions run in the harness with fake claude; inherently
manual conditions (live sessions, patience across sleeps) are documented as manual
acceptance steps.
