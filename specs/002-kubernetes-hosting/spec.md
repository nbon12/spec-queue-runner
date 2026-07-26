# Feature Specification: Kubernetes Hosting

**Feature Branch**: `002-kubernetes-hosting` *(not yet created)*

**Created**: 2026-07-26

**Status**: Draft — ready for `/speckit.plan`

**Input**: User description: "I'm wondering if we can maybe host this tick service instead of running it locally on my laptop. Basically have it run in Kubernetes where it does the git checkouts in pods."

**Prior art**: [`docs/kubernetes-hosting.md`](../../docs/kubernetes-hosting.md) — the design assessment
this spec formalizes. Read it first; it carries the reasoning, the rejected alternatives, and the
architecture diagram that are deliberately not repeated here.

---

## ⚠️ Read before starting

Two facts about the **running system** that are easy to miss and expensive to discover late.

**1. This spec's own directory will break stage derivation for other items.**
`Tick.FindSpecDir` selects a spec directory with
`Directory.GetDirectories(specs).OrderByDescending(d => d).FirstOrDefault()` — the
highest-sorted one, **not** the one belonging to the item being worked. The moment
`specs/002-kubernetes-hosting/` lands on the base branch, every `feature`/`amendment` item reads
*this* spec's artifacts instead of its own: it will see `spec.md` present and `plan.md` absent, and
derive stage `Plan` regardless of its actual state. Chores are unaffected (`isSpecKind` is false, so
the shaping predicates are vacuously satisfied).

This is a latent defect in feature 001's implementation that this spec merely *exposes*. It MUST be
fixed — see **FR-021** — before or alongside merging this spec. Do not merge this directory to the
base branch of a live instance and walk away.

**2. Do not "just use a fresh git clone per pod."** It is the obvious reading of the original
request and it silently breaks the pipeline. See **FR-004** for why.

---

## Clarifications

### Session 2026-07-26

- Q: Live sessions cannot live in a pod that exits — how should the live channel work on Kubernetes? → A: A long-lived session Deployment (realized as a `StatefulSet`, replicas 1) that shares the instance's volume with the tick and hosts tmux + Remote Control.
- Q: Should implementation begin, or a design document first? → A: Design document first; this spec is the follow-on formalization of it.

### Deferred to `/speckit.plan` (do not guess — these are planning decisions, not requirements)

- Which cluster (managed EKS/GKE/AKS vs. homelab) — determines architecture, storage classes, cost.
- Whether the cluster offers `ReadWriteMany` storage (see FR-011).
- Per-repo namespaces vs. one namespace with per-repo resources.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Unattended execution that does not depend on a laptop (Priority: P1)

The operator files a ready issue and closes their laptop. Ticks continue on a schedule in the
cluster; by morning the item has advanced, or has a pull request open, exactly as it would have on
the laptop.

**Why this priority**: This is the entire reason to move. Today the queue only drains while one
specific machine is awake and unslept — which excludes most of the window the system exists to use.

**Independent test**: Provision one instance in the cluster, file a `chore`, power off the laptop
entirely, and confirm the item reaches a merged pull request unattended.

**Acceptance scenarios**:

1. **Given** a ready operator-authored issue and no laptop running, **When** the scheduled tick
   fires, **Then** the item advances one stage and the tick exits.
2. **Given** a tick already running, **When** the next schedule fires, **Then** the second is
   suppressed rather than run concurrently, and this is not treated as an error.
3. **Given** a tick that crashes mid-stage, **When** the next tick fires, **Then** it converges on
   the correct state without duplicated comments, labels, or commits — the crash-convergence
   property holds unchanged in-cluster.

---

### User Story 2 - A multi-stage feature survives across ticks (Priority: P1)

A `feature` item is filed. Across successive ticks it moves through specify → clarify → plan →
tasks → analyze → implement, and the artifacts each stage writes are still present when the next
tick runs.

**Why this priority**: Equal-highest with US1 because it is the acceptance test that distinguishes a
correct port from one that *appears* to work. A chore exercises a single stage and would pass even
with a fatally wrong storage design.

**Independent test**: File a feature item, let several ticks run, and confirm `spec.md`, `plan.md`,
and `tasks.md` accumulate rather than being rewritten from scratch each tick.

**Acceptance scenarios**:

1. **Given** a tick that ran `specify` and wrote `spec.md`, **When** that pod has exited and a later
   tick runs, **Then** `spec.md` is still present and stage derivation advances past `specify`.
2. **Given** an item with uncommitted worktree changes, **When** an unrelated item's tick runs,
   **Then** the first item's files are unchanged byte-for-byte.

---

### User Story 3 - Live escalation still reaches the phone (Priority: P2)

A run blocks on a decision during waking hours. The operator receives a push, opens the session on
their phone, resolves the block conversationally, and the pipeline resumes — with the runner in a
cluster rather than on a laptop.

**Why this priority**: Below execution because the comment fallback (FR-019 of feature 001) already
degrades gracefully. Losing the live channel slows the system; losing execution stops it.

**Independent test**: Force a block, confirm exactly one push arrives, resolve it from the phone, and
confirm the item advances on the next tick.

**Acceptance scenarios**:

1. **Given** a block inside waking hours, **When** the tick escalates, **Then** an interactive
   session is running in the item's own worktree and is reachable from the phone.
2. **Given** an open live session, **When** the tick pod that created it has long since exited,
   **Then** the session is still alive and still reachable.
3. **Given** a live session whose host process died, **When** a later tick runs, **Then** the
   conversation is resumed by its recorded identifier on the same URL, with no duplicate push and
   no question re-asked.
4. **Given** an open live session for one item, **When** another item blocks, **Then** the second
   queues rather than opening a second concurrent session.

---

### User Story 4 - The operator can tell whether it is healthy (Priority: P3)

Before relying on an instance — and when something looks wrong — the operator runs one command and
gets a truthful account of config, credentials, storage, and the session host.

**Independent test**: Break one prerequisite at a time (unwritable volume, missing credential,
unreachable session pod) and confirm each is reported specifically rather than as a generic failure.

**Acceptance scenarios**:

1. **Given** a correctly provisioned instance, **When** the health check runs, **Then** every check
   passes and nothing in the queue is touched.
2. **Given** an unreachable session host, **When** the health check runs, **Then** that specific
   condition is named — not discovered later by a block that fails to escalate.

---

### Edge Cases

- What happens when the persistent volume fills? Worktrees accumulate per item; something must
  reclaim closed items' worktrees, or the instance wedges in a way no tick can fix.
- What happens when the tick pod and the session pod cannot be co-scheduled (volume access-mode
  constraint)? This must fail loudly at provisioning, not as a pod stuck `Pending`.
- What happens when the cluster restarts the session pod mid-conversation? Covered by resume-by-id,
  but the worktree must survive the restart.
- What happens when a tick is still running as the next schedule fires repeatedly — does suppression
  starve the queue, or is a persistently-overrunning tick surfaced?
- What happens to an in-flight item when the operator migrates an instance from laptop to cluster?
- What happens when the image is pulled for the wrong CPU architecture?

---

## Requirements *(mandatory)*

Requirement numbering is local to this feature. Where a requirement of feature 001 must continue to
hold, it is cited as `001/FR-nnn`.

### Functional Requirements — scheduling

- **FR-001**: A tick MUST be fired on a configured interval by the cluster's own scheduling
  mechanism, without any host-specific trigger. The interval MUST remain per-instance configuration,
  not a hard-coded value.
- **FR-002**: Overlapping ticks MUST be prevented at the scheduler level, and suppression of an
  overlapping run MUST NOT be reported as a failure (it is normal, per `001/FR-002`).
- **FR-003**: A failed tick MUST NOT be retried by the scheduler. The next scheduled tick converges;
  retry machinery would duplicate side effects the design already makes convergent.

### Functional Requirements — persistence

- **FR-004**: An item's worktree MUST persist across ticks for that item's entire life
  (`001/FR-014`). Ephemeral per-pod checkouts are explicitly forbidden: the shaping and planning
  stages write `spec.md`, `plan.md`, and `tasks.md` into the worktree **without committing them**,
  and stage derivation reads those files back from disk. With an ephemeral checkout a feature item
  re-runs `specify` forever, consuming credits and never advancing, while chores appear to work —
  a partial failure that passes a smoke test.
- **FR-005**: The stored Claude Code credential MUST persist across pod restarts. Claude Code
  refreshes its access token and writes the result to disk; a credential store that discards those
  writes risks stalling every instance silently (see FR-020).
- **FR-006**: Each instance MUST have its own storage. Instances never coordinate (`001/§3`), and
  sharing storage would create exactly the coupling the design dissolves.
- **FR-007**: Worktrees belonging to closed items MUST be reclaimable, so that storage does not grow
  without bound.

### Functional Requirements — configuration and secrets

- **FR-008**: Instance configuration MUST be delivered to the tick as a mounted file, and MUST NOT
  require rebuilding the image to change.
- **FR-009**: The GitHub credential MUST be delivered as a mounted secret, readable only by the
  workload, and MUST NOT appear in the image, in configuration, or in the repository
  (`001/FR-052`). Its permission ceiling — issues, contents, pull requests only — is unchanged and
  non-negotiable (constitution §6 v4.0.0).
- **FR-010**: Establishing the Claude credential MUST remain possible without a persistent
  interactive host: a one-time interactive bootstrap attached to the instance's storage, after which
  unattended runs use it.

### Functional Requirements — the live channel

- **FR-011**: A live session MUST be hosted by a long-lived workload that outlives any individual
  tick, sharing the item's worktree with the tick. Where the storage layer cannot serve both
  workloads simultaneously, provisioning MUST fail loudly with the reason, rather than leaving a
  workload unschedulable.
- **FR-012**: At most one live session MUST exist per instance (`001/FR-025`); the hosting workload's
  replica count MUST encode that rather than rely on convention.
- **FR-013**: The tick MUST remain the sole decision-maker about when a session is spawned, resumed,
  or reaped. The session host MUST NOT independently select work, so the queue rules and the operator
  allowlist have exactly one implementation.
- **FR-014**: The privilege the tick uses to drive the session host MUST be scoped to that single
  workload, never to a class of workloads.
- **FR-015**: The live channel MUST NOT require an inbound network path. Remote Control is
  outbound-initiated (verified: 2026-07-25 probe, a NAT'd container with no inbound ports was
  reachable from the phone); egress to the service MUST be permitted where egress is restricted.
- **FR-016**: A session's kickoff MUST NOT be delivered blind (`001/FR-021a`). If the added latency
  of driving the session across a workload boundary changes readiness behaviour, the readiness probe
  MUST be adapted rather than the requirement relaxed.

### Functional Requirements — image and platform

- **FR-017**: The runner image MUST be buildable for the CPU architecture of the target cluster as
  well as the operator's workstation; architecture MUST NOT be hard-coded in the build.
- **FR-018**: The container MUST remain the isolation boundary (constitution §2). Nothing may require
  privileged execution, host mounts, or host networking.

### Functional Requirements — operability

- **FR-019**: The health check MUST verify, without touching the queue: configuration validity, the
  toolchain, credential readability, credential refreshability, storage writability, and reachability
  of the session host. Each failure MUST name its specific cause.
- **FR-020**: Before ephemeral credential storage is chosen anywhere, it MUST be established whether
  the claude.ai refresh token is reusable or single-use/rotating. If rotating, a store that discards
  the refreshed token invalidates the credential on first use and every later tick fails to
  authenticate — silently. Persisting the credential alongside the worktree avoids the question.
- **FR-021**: Stage derivation MUST read the spec directory belonging to the item being worked, not
  the highest-sorted directory under `specs/`. The current implementation
  (`Tick.FindSpecDir`) returns `OrderByDescending(...).FirstOrDefault()`, so a second spec directory
  silently captures every `feature`/`amendment` item's derivation. This MUST be fixed before this
  feature's own spec directory reaches the base branch of a live instance.
- **FR-022**: An existing laptop instance and a cluster instance MUST be able to serve *different*
  repositories concurrently without interfering. Serving the *same* repository from both
  simultaneously MUST be prevented or clearly documented as unsupported — two schedulers against one
  book of work is the coupling `001/FR-002`'s lock exists to prevent, and a file lock does not span
  hosts.

### Key Entities

- **Instance**: one repository's runner — its configuration, credential, storage, schedule, and
  session host. Instances never coordinate.
- **Tick workload**: the scheduled, short-lived unit that performs at most one unit of work.
- **Session host**: the long-lived workload that owns interactive sessions and their terminal
  multiplexer state. Its state is disposable; losing it costs a pause, not work.
- **Instance storage**: the durable filesystem holding the clone, worktrees, the Claude credential,
  and the lock — the same layout the laptop deployment uses today.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001 (Independent)**: With the operator's laptop powered off for a full night, a queue of
  ready items is drained to merged pull requests by morning, with no manual intervention.
- **SC-002 (Durable)**: A `feature` item advances through at least four distinct stages across four
  separate ticks, and the artifacts written by each stage are present and unmodified at the start of
  the next.
- **SC-003 (Isolated)**: While one item's live session sits open with unsaved edits, a different
  item runs its entire execution pipeline to completion, and the open session's files remain
  byte-for-byte unchanged (`001/SC-003`, preserved across the workload split).
- **SC-004 (Reachable)**: An item that blocks during waking hours produces exactly one push and one
  conversation, resolvable entirely from the phone, with the originating tick pod long exited.
- **SC-005 (Patient)**: A live session left unanswered across a session-host restart is still
  answerable and still resolves the item on the first reply — no timeout, no duplicate push, no
  question asked twice.
- **SC-006 (Diagnosable)**: For each of at least four distinct broken prerequisites, the health
  check names the specific cause, and a first-time reader can act on it without reading source.
- **SC-007 (Reversible)**: An instance can be migrated back to the laptop deployment, or removed
  entirely, without leaving orphaned work — an in-flight item either completes or returns to ready.

---

## Assumptions

- The tick's existing structure — acquire lock, perform at most one unit of work, exit — is assumed
  to be a correct fit for scheduled short-lived execution, requiring no change to its control flow.
- The domain layer, GitHub adapter, stage machinery, and the existing offline test suite are assumed
  to port unchanged; this feature is assumed to touch scheduling, storage, and session hosting only.
  If that assumption breaks, the ports-and-adapters boundary (constitution §5) is being violated and
  the design should be revisited rather than worked around.
- A cluster is assumed to be available and operated by the operator; provisioning the cluster itself
  is out of scope.
- Cost is assumed acceptable in exchange for continuous availability, including a session host that
  is running even when no session is live. Scaling that host to zero between sessions is assumed to
  be a possible later optimization, deliberately not attempted first because it adds a failure mode
  to the most delicate path.
- The operator's laptop deployment is assumed to remain valid and supported; this feature adds a
  deployment target rather than replacing one.
- The following are assumed OUT of scope: coordination between instances; horizontal scaling of
  ticks (one unit of work per tick is an invariant, not a bottleneck); more than one live session per
  instance; multi-tenant hosting for operators other than the single allowlisted operator; and any
  change to what the runner *does* with a work item.

---

## Handoff notes

For the session picking this up.

**Start here**: read [`docs/kubernetes-hosting.md`](../../docs/kubernetes-hosting.md) in full, then
`.specify/memory/constitution.md` §2 (containerization), §3 (invariants), §5 (ports and adapters),
and §6 (security). This spec states *what*; the design doc carries *why*, including alternatives
already considered and rejected with reasons.

**Verified facts, not assumptions** — each was checked against the running system on 2026-07-26:

| Fact | Evidence |
|---|---|
| Only the implement stage commits; shaping/planning artifacts sit uncommitted in the worktree | `Tick.cs` — `CommitAllAsync` appears once, in `RunImplementAsync` |
| `FindSpecDir` returns the highest-sorted spec dir, not the item's | `Tick.cs` — `OrderByDescending(d => d).FirstOrDefault()` |
| Claude Code refreshes its access token and writes it back to disk | measured: stored expiry advanced 09:03 → 21:40 during one run |
| Remote Control needs no inbound path | 2026-07-25 probe: NAT'd container, no inbound ports, reachable from phone |
| The image hard-codes `linux-arm64` | `Dockerfile` lines 11 and 14 |

**Suggested sequencing** (also in the design doc's migration path): multi-arch image → tick-only in
cluster with the live channel disabled → session host and the exec seam → cut over. Steps 1–2 are
low-risk and independently useful; step 3 is the real work.

**What cannot be tested offline**: the live Remote Control handshake — spawn, phone attach, kickoff
delivery, resume-by-id — is a real conversation with a real service and has no fake. It is verified
by hand, as it is today. Everything around it (scheduling, storage, derivation, selection, fallback
choice) can and should be covered by the existing offline tiers.

**Do not** relax `001/FR-021a` (poll-then-send kickoff) to work around latency introduced by the
workload split; adapt the readiness probe instead. The blind-send failure it guards against was
observed empirically, one attempt in two.
