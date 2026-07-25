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
