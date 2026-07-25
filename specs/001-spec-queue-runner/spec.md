# Feature Specification: Spec Queue Runner

**Feature Branch**: `001-spec-queue-runner` *(not yet created — no `before_specify` git hook is configured; this bootstrap spec was authored directly on `master` per the design's own instruction that "bootstrap is manual")*

**Created**: 2026-07-25

**Status**: Draft

**Input**: User description: "Design and build an unattended worker (\"Spec Queue Runner\") that pulls work items from a GitHub Issues book of work and drives each one through the SpecKit pipeline autonomously overnight, resuming across Claude Code usage-limit resets, escalating to a live Remote Control conversation on the operator's phone whenever a decision needs a human, and integrating finished work through pull requests. One runner instance serves exactly one repository; every Claude Code invocation for a work item — live or headless — runs in that item's own git worktree."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Unattended overnight execution (Priority: P1)

An operator labels a fully-clarified issue `status/ready` before going to sleep. Overnight, the runner ticks on its schedule, drives the item through plan → tasks → analyze → implement with nobody watching, survives at least one Claude usage-limit reset by retrying automatically on a later tick, and by morning has pushed a branch, opened a pull request, and closed the issue with a linked changelog.

**Why this priority**: This is the reason the system exists — reclaiming overnight and weekend capacity that today goes unused because a session that hits a usage limit dies and needs a human to restart it.

**Independent Test**: Label one ready item at night with nothing else running. By morning, confirm a pull request exists, the issue is closed, and the log shows at least one rate-limit-triggered retry that required no manual action.

**Acceptance Scenarios**:

1. **Given** an issue labelled `status/ready` past clarify, **When** the schedule ticks overnight and Claude Code fails with a usage-limit error mid-run, **Then** the item reverts to `status/ready` and a later tick resumes and completes it with no manual intervention.
2. **Given** an item completes implement, **When** the tick finishes, **Then** a pull request is open with a description generated from the issue and its decision comments, the issue is closed, and the item's worktree is removed.

---

### User Story 2 - Staged intake and clarification (Priority: P2)

An operator captures a rough feature idea from a phone as a bare GitHub issue. The runner infers its kind and target specs unattended and reports that classification as a decision comment, then arrives at a clarification stage where it asks a small number of concrete, numbered questions, each with a recommended default. The operator's first real interaction with the item is answering those questions — never sorting, labeling, or classifying it.

**Why this priority**: Requests written quickly are ambiguous, and discovering that ambiguity mid-implementation wastes a whole run; the operator's point of engagement should be substantive questions, not taxonomy busywork.

**Independent Test**: File a deliberately terse, unlabeled issue. Confirm the runner assigns kind and target labels and reports the classification as a decision comment without asking, then confirm it stops at clarify with a numbered question list and recommended defaults.

**Acceptance Scenarios**:

1. **Given** a newly filed issue with no labels, **When** intake runs, **Then** the runner infers kind and targets, posts the classification as a decision comment, and asks the operator nothing unless intent is entirely unrecoverable from the text.
2. **Given** a spec draft containing unresolved clarification markers, **When** clarify runs, **Then** the runner presents each marker as a numbered question with a recommended default and rationale, and waits for a response.

---

### User Story 3 - Live conversational unblocking (Priority: P3)

When an item hits a question it cannot resolve on its own, or an execution decision that cannot be undone by later work, the runner opens an interactive Claude Code session inside that item's own worktree, exposes it through Remote Control, and pushes a notification to the operator's phone. The operator answers conversationally from the Claude app; the session itself writes the resolution into the spec or plan. The conversation can sit unanswered for days, across machine sleep, and still resolve correctly the moment the operator responds.

**Why this priority**: The operator demonstrably prefers answering at chat speed from a phone over editing files or composing GitHub comments, and blocking indefinitely — rather than timing out — matches how irregularly the operator is actually available.

**Independent Test**: Force an item into a blocking state during configured waking hours. Confirm a phone push and an open conversation. Put the machine to sleep for 24 hours, wake it, respond from the app, and confirm the resolution lands in the item's spec or plan file, the item proceeds, and there was no timeout, no duplicate push, and no re-asked question.

**Acceptance Scenarios**:

1. **Given** an item blocks during configured waking hours, **When** the runner ticks, **Then** it spawns a named, scope-limited interactive session and the operator receives a push notification.
2. **Given** a live session's process dies (crash, reboot, a brief offline window), **When** a later tick runs, **Then** the runner resumes the same conversation by session ID rather than starting a new one or notifying again.
3. **Given** the operator replies via an issue comment while a live session is still open for the same item, **When** the tick collects replies, **Then** the item resolves from the comment and the now-redundant session is closed.

---

### User Story 4 - Dependency-ordered integration through pull requests (Priority: P4)

A completed item never lands on the main branch directly. It pushes its branch and opens a pull request that serves as the human review gate; the operator reads the decision log alongside the diff and merges or requests changes. An item that targets a spec whose own pull request has not yet merged simply waits, with a stated reason, and becomes eligible to run automatically the moment that dependency lands on main.

**Why this priority**: Unattended, multi-item work needs a human checkpoint before it touches the shared codebase, and dependent items need a correctness-preserving way to wait their turn without a separate scheduler component.

**Independent Test**: File an amendment targeting a spec whose pull request has not merged. Confirm it holds with a stated reason and no work is attempted. Merge the dependency's pull request. Confirm the amendment becomes ready on the very next tick with no manual action.

**Acceptance Scenarios**:

1. **Given** an item's declared target spec is not yet on the main branch, **When** the tick evaluates readiness, **Then** the item is labelled held with a comment naming what it is waiting for, and no work is attempted on it.
2. **Given** the awaited spec merges to main, **When** the next tick runs, **Then** the held item is promoted to ready automatically.
3. **Given** a pull request receives requested changes from the operator, **When** the operator responds, **Then** the correction is filed as a new issue rather than reopening or silently amending the original.

---

### User Story 5 - Graceful degradation when the live channel is unavailable (Priority: P5)

When the interactive session cannot be established — an outage, an expired login, or the one live slot already in use — the runner instead posts the same questions as a single, clearly-formatted issue comment, states plainly why the live path failed, and waits for a reply. The very next item that blocks after the channel recovers goes live again automatically, with no configuration change required.

**Why this priority**: The live channel depends on an external service that can fail in ways outside the runner's control; the system must keep making progress through the one channel that always works — GitHub — rather than stalling silently.

**Independent Test**: Disable the live channel. Force an item to block. Confirm the questions arrive as one well-formatted comment naming the failure reason. Reply to it and confirm the item resolves. Re-enable the channel and confirm the next blocked item goes live without any manual reset.

**Acceptance Scenarios**:

1. **Given** session establishment fails for any reason, **When** the runner falls back, **Then** it posts one comment containing every open question, a recommended default and one-line rationale for each, and an explicit statement of why the live path was unavailable.
2. **Given** an authentication failure specifically caused the fallback, **When** the comment is posted, **Then** the authentication problem is called out distinctly, since it is operator-fixable and a silent fallback would hide it indefinitely.

---

### User Story 6 - Spec drift detection through audits (Priority: P6)

On its own recurring schedule, the runner selects the spec that has gone longest without review, compares it against the code it claims to cover as that code exists on the main branch, and reports any mismatch as a comment naming which side looks wrong — without ever editing the spec or the code itself. A human decides, from the report, whether to file a fix or an amendment.

**Why this priority**: A spec nobody re-checks against reality becomes actively misleading, and having the system auto-correct a mismatch would risk quietly promoting a bug into specified behavior — that judgment call belongs to a person either way.

**Independent Test**: Deliberately let a spec fall out of sync with the code it covers. Run an audit. Confirm it reports the discrepancy, names a side, and leaves both the spec and the code byte-for-byte unchanged.

**Acceptance Scenarios**:

1. **Given** multiple specs exist on main, **When** an audit runs, **Then** it selects the one least recently audited and evaluates it only against code on main.
2. **Given** a mismatch between a spec and the code under its coverage entry, **When** the audit reports it, **Then** it states which side appears wrong and why, and modifies neither.

---

### Edge Cases

- What happens when two work items would otherwise need the same working directory at the same time — e.g. a live session parked for days on one item while a headless run executes a different item tonight? The design must make this collision structurally impossible (separate worktrees) rather than something a lock merely prevents.
- What happens when the GitHub API is unreachable partway through a run? Work already committed in the item's worktree must survive; only the push, comment, and label updates are retried on a later tick.
- What happens if the process is killed at an arbitrary point mid-tick (after a label change but before a comment post, after a commit but before a push, etc.)? Repeated ticks must converge to the same end state as an uninterrupted run, with no duplicated comments or labels.
- What happens when an execution stage racks up an unusually long streak of small autonomous decisions? A run must stop and drop back to clarify past a configured cap, rather than compounding judgment calls unsupervised.
- What happens when two runner instances (serving different repositories) draw on the same underlying Claude usage budget at the same time? Contention must resolve through the existing usage-limit retry path, with no cross-instance coordination required.
- What happens when two runner instances each try to hold a live, phone-notified session at the same moment, if the live channel only supports one such session at a time? The instance that cannot establish one must fall back to the comment channel (User Story 5), not queue or fail silently.
- What happens when a decision an item would otherwise make autonomously touches something that cannot be undone later — a destructive data migration, a call to a third party, a secret, a force-push, or another explicitly protected action? That class of decision must always block for a human, regardless of the run's general "decide and report" posture.
- What happens to an item whose run neither completes nor errors, but simply stops responding? It must be reclaimed as stale after a configured threshold and returned to ready, except while it is legitimately waiting on a human or on a dependency.

## Requirements *(mandatory)*

### Functional Requirements

**Scheduling**

- **FR-001**: The system MUST invoke its tick on a fixed schedule via a mechanism that still runs a missed invocation after the host machine wakes from sleep, rather than silently dropping it.
- **FR-002**: A tick MUST acquire an exclusive lock scoped to its own runner instance before doing anything else, and MUST exit immediately without side effects if that lock is already held.
- **FR-003**: A tick that finds no work to do MUST exit within a few seconds.

**Book of work**

- **FR-004**: The system MUST read and write all queue state exclusively through the issue tracker of its one configured repository — no separate database or state store.
- **FR-005**: The system MUST act only on issue and comment content authored by an explicitly allowlisted operator account; content from any other author MUST be ignored entirely — never read into a prompt, never replied to. The system's own posted comments MUST be recognized as its own output, never mistaken for operator input.
- **FR-006**: Issue and comment content MUST be treated as untrusted input that supplies requests and answers, and MUST NOT be able to redirect a run's behavior away from the subject matter of the item it was posted to.
- **FR-007**: Every tick MUST collect and process comment replies on all currently-waiting and currently-live items before selecting new work.
- **FR-008**: Each collected reply MUST be judged: a resolving reply unblocks the item (returns it to ready); a merely conversational reply receives a response comment and leaves the item waiting.
- **FR-009**: Work selection MUST pick the lowest-numbered open issue labelled ready, and a tick MUST perform work on at most one item.
- **FR-010**: An item MUST NOT be schedulable while any spec it targets is absent from the main branch; such an item MUST be labelled held with a comment stating what it is waiting for, and MUST be promoted back to ready automatically once that target lands on main.
- **FR-011**: The system MUST label an item in-progress before invoking Claude Code on it.

**Worktrees**

- **FR-012**: The system MUST create an item's dedicated git worktree, branching from main, the first time any invocation for that item needs one; every subsequent invocation for that item — any stage, any channel, any retry — MUST run in that same worktree, and nothing MUST ever run directly in the shared repository clone.
- **FR-013**: Stage predicates MUST be evaluated against the item's own worktree, never the shared clone, and the pipeline-position label MUST be updated to match whichever predicate is first unsatisfied.
- **FR-014**: An item's worktree MUST persist for that item's entire life — across ticks, crashes, usage-limit resets, and open live sessions — and MUST be removed only after a pull request is opened at close; a worktree belonging to a closed issue MUST eventually be pruned.

**Stages**

- **FR-015**: Each kind of work item MUST traverse its declared sequence of pipeline stages (feature and amendment: intake → specify → clarify → plan → tasks → analyze → implement; chore: intake → plan → implement; spike and audit: intake → implement).
- **FR-016**: Intake MUST infer an item's kind and targets from the issue text and record that classification as a decision comment; intake MUST block only when intent is genuinely unrecoverable from the text, never merely because classification is uncertain.
- **FR-017**: A manually applied pipeline-stage label MUST take precedence over the computed stage, so an item can be deliberately held at a given stage.
- **FR-018**: The shaping stages (intake, specify, clarify) MUST never write code, under any circumstance; specify MUST run unattended and express any ambiguity as clarification markers rather than asking a question directly.
- **FR-019**: When resolving clarification through the comment fallback channel, the system MUST emulate the effect of the interactive clarification step without a live session: read the outstanding markers, post the questions, apply the accepted answers to the spec, and re-check the exit predicate. The live channel MUST instead run the actual interactive step inside the session.
- **FR-020**: A single run MUST advance through as many consecutive execution stages as it can complete, with each stage's exit predicate serving as the checkpoint a later, resumed run picks up from after a usage-limit failure or crash.

**Live channel**

- **FR-021**: Within configured waking hours, a blocked item MUST receive an interactive session running inside that item's own worktree, exposed through Remote Control, named for the issue, and scoped by its kickoff instructions to resolving only that item's open questions; a session opened for an execution-stage block MUST additionally be instructed not to continue implementation.
- **FR-022**: The session's identifier MUST be recorded on the issue and the item labelled live; this MUST be the only dialogue-related content the issue receives while the live path is in use.
- **FR-023**: A live session MUST have no timeout, and MUST remain reachable across the host machine sleeping and waking.
- **FR-024**: Every tick MUST reconcile live items: a dead session MUST be respawned resuming the recorded session identifier; an item whose blocking predicate is now satisfied MUST have its session ended and be advanced; any other live item MUST be left untouched.
- **FR-025**: At most one live session MUST exist per runner instance at a time; additional blocked items within the same repository MUST queue in issue-number order. Contention with another runner instance's session MUST be treated as a session-establishment failure.
- **FR-026**: Outside configured waking hours, a blocked item MUST hold without a session and without a notification until the window next opens.

**Comment fallback**

- **FR-027**: If session establishment fails for any reason, the system MUST post every open question as a single comment — each numbered, with a recommended default and a one-line rationale — MUST state the reason live could not be established, and MUST label the item waiting.
- **FR-028**: An authentication failure that caused the fallback MUST be called out explicitly and distinctly in that comment, since it is operator-fixable and a silent fallback would mask it indefinitely.
- **FR-029**: A later item establishing a live session successfully MUST be treated as confirmation that a prior fallback was transient; no additional state needs to be cleared for that to happen.

**Execution stages**

- **FR-030**: Execution stages (plan, tasks, analyze, implement) MUST always run unattended, proceeding through all of them without pausing for input, with analyze's recommendations folded directly into the work rather than surfaced separately.
- **FR-031**: On an ambiguity during an execution stage, the system MUST assess whether the choice is reversible: if reversible, it MUST decide, post the decision comment immediately — before any further action, so a crash never loses the reasoning — and continue; if irreversible, it MUST block, using the live channel (FR-021) or its fallback (FR-027).
- **FR-032**: Work MUST be committed to the item's own branch, and decision comments MUST reference the commits they correspond to.
- **FR-033**: On completing implement, the system MUST push the item's branch; open a pull request whose description is generated from the issue, the changelog, and the decision comments; post a closing comment linking that pull request; record the paths it authored in the coverage manifest on that same branch; close the issue; and remove the item's worktree. Merging the pull request MUST remain a manual, human act.
- **FR-034**: Requested changes on a pull request MUST be expressed as a new issue rather than by reopening or altering the original.

**Spec authorship**

- **FR-035**: A spec MUST contain no runner-specific metadata of any kind — no status, no transcript, no decision log.
- **FR-036**: A completed amendment MUST leave its target spec describing only current behavior; superseded behavior MUST be removed rather than annotated.
- **FR-037**: A spec's claim of accuracy MUST extend only to the paths recorded under its entry in the coverage manifest; code outside that coverage is described by no spec.

**Audit**

- **FR-038**: An audit MUST select the spec least recently audited, determined from prior audit history, and MUST evaluate both the spec and the code exactly as they exist on the main branch.
- **FR-039**: An audit MUST report findings as comments describing discrepancies in either direction, each stating which side appears wrong and why, and MUST modify neither the spec nor the code, unconditionally.
- **FR-040**: An audit MAY file follow-up issues for findings, left unlabelled as to kind, since choosing between a fix and an amendment presumes a direction of correction that the audit itself must not decide.
- **FR-041**: Each audit run MUST evaluate exactly one spec; broader coverage MUST come from running audits on a cadence, not from widening a single run's scope.

**Recurrence**

- **FR-042**: A work item marked recurring that reaches a terminal state MUST cause the system to file a successor issue inheriting its kind and configuration; closed issues MUST remain closed, and the book of work MUST be append-only.

**Failure handling**

- **FR-043**: On a usage-limit failure from Claude Code, the system MUST revert the item to ready and exit cleanly, relying on a later tick to retry; detection MUST work against a case-insensitive match on rate-limit and usage-limit phrasing, with the full failure output logged. This same mechanism MUST be sufficient to resolve usage-budget contention between separate runner instances, with no additional coordination.
- **FR-044**: An item left in-progress longer than a configured staleness threshold MUST be reset to ready at the start of the next tick; items in the live or held states MUST be exempt from this reclaiming, since both are expected to wait indefinitely by design.
- **FR-045**: The complete standard output and standard error of every unattended run MUST be appended to the runner instance's own rolling log.
- **FR-046**: If the issue tracker's API is unreachable during a run, any work already committed in the item's worktree MUST be preserved; the push, comment, and label updates MUST simply be retried on a later tick, and API unavailability MUST never cause committed work to be lost.
- **FR-047**: The loss of a live session, for any reason, MUST cost nothing beyond a pause: all state needed to resume MUST live in the worktree, the issue, and the resumable session store — never solely in the running process.

**Reporting**

- **FR-048**: The system MUST NOT ask for approval of a decision it has already made and reported; the only avenues for disagreement are filing a new issue or leaving a pull request unmerged.
- **FR-049**: The system MUST NOT send notifications through any channel beyond the issue tracker's own notifications for queue events and the live channel's own push for live sessions.

**Security**

- **FR-050**: The repository the system operates against MUST be private, since the blast radius of the queue being compromised is already the blast radius of the code being compromised.
- **FR-051**: The author allowlist (FR-005) MUST be the primary safeguard against injected instructions: only operator-authored content is ever read into a prompt.
- **FR-052**: Each runner instance's credential MUST be scoped to exactly the one repository it serves and to the minimum permissions needed (issues, contents, pull requests), and MUST be stored in a secure credential store rather than in configuration or in the repository.
- **FR-053**: Unattended invocations MUST run with a permission mode appropriate to unattended use, confined to the item's own worktree, with no access to secrets beyond what that repository legitimately needs.
- **FR-054**: Prompts MUST be structured so that issue content only ever answers questions the system itself posed; the definition of the pipeline MUST always come from the system's own configuration and spec directory, never from issue text.
- **FR-055**: The live channel MUST rely on its own underlying security properties — outbound-only connections, short-lived scoped credentials, and session access limited to the operator's own signed-in account.
- **FR-056**: The main branch MUST be protected such that no push reaches it directly and every change requires a pull request, making the human review gate structural rather than merely procedural.

### Key Entities

- **Instance**: One runner deployment serving exactly one repository — its own configuration, schedule, lock, and log. Instances never coordinate with one another.
- **Issue**: A work item. Carries its kind, queue status, and pipeline stage as labels; its decision history and any fallback question/answer exchange as comments; a live-session identifier as a comment; and, at close, a link to its integrating pull request.
- **Worktree**: An item's private checkout of its own branch, created the first time it's needed and removed at close. The only place that item's work ever happens.
- **Stage**: A position in the pipeline, defined by a predicate over the item's worktree and/or issue state. Its label is a cache; the predicate is authoritative and doubles as the crash-recovery checkpoint.
- **Spec**: A checked-in, present-tense description of pipeline-generated behavior. Contains no runner metadata.
- **Coverage manifest**: A generated, checked-in record of which paths each spec claims to describe, authoritative as it exists on the main branch.
- **Pull request**: The integration and human-review gate. Opened by the system; merged only by the operator.
- **Tick**: One stateless execution of the runner: collect replies, reconcile long-lived state, then perform at most one unit of new work.
- **Live session**: An interactive Claude Code session scoped to one blocked item, exposed through Remote Control, with no timeout, resumable by its identifier, and disposable.
- **Decision comment**: A record of one execution-stage judgment call — the ambiguity, the choice made, the alternatives considered, the rationale, and the commit it corresponds to.
- **Finding**: An audit's report of a discrepancy between a spec and its covered code, naming which side appears wrong without prescribing the correction.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001 (Unattended)**: An issue labelled ready before the operator sleeps is, by morning, implemented, pushed, opened as a pull request, and closed with a linked changelog — surviving at least one usage-limit reset without any manual intervention.
- **SC-002 (Staged)**: A rough issue filed from a phone reaches the clarification stage with its kind already inferred and reported (never asked about), presenting numbered questions with recommended defaults as the operator's very first point of contact with the item.
- **SC-003 (Isolated)**: While one item's live session sits open with unsaved, in-progress edits, a different item can run its entire execution pipeline to completion, and the open session's files remain byte-for-byte unchanged throughout.
- **SC-004 (Sequenced)**: An item filed against a not-yet-merged dependency holds automatically with a stated reason and becomes ready, with no operator action, within one tick of that dependency merging.
- **SC-005 (Live)**: An item that blocks during waking hours produces exactly one phone push and one conversation; the operator's response resolves the item entirely through that conversation, with no additional typing required anywhere else.
- **SC-006 (Patient)**: A live session left unanswered for 24 hours across two separate periods of machine sleep is still answerable and still resolves the item on the first reply — with no timeout, no duplicate push, and no question asked twice.
- **SC-007 (Degradable)**: With the live channel unavailable, a blocking item's questions reach the operator as one issue comment stating the reason, are answered by reply, and the item completes; the very next blocked item after the channel recovers goes live again with no manual reset.
- **SC-008 (Clean)**: No spec on the main branch ever contains a status field, a transcript, or a decision log; the only dialogue-related content a live-path issue ever receives is its session-identifier line; and an audit run against a deliberately stale spec reports the discrepancy, names the wrong side, and leaves both spec and code unchanged.

## Assumptions

- Every open question in the source design already carries an explicit, stated default ("defaults are fine" is treated as a sufficient answer), so none is marked as needing clarification here; four of those defaults, however, depend on empirical behavior that cannot be verified from this document alone and MUST be validated with live probes before the corresponding functionality is built out:
  - Whether workspace trust must be re-accepted for every newly created worktree directory, or carries over automatically.
  - Whether a scripted kickoff message can be delivered reliably into a freshly spawned interactive session.
  - Whether resuming a session by its recorded identifier interacts cleanly with the live channel, or produces a fresh identifier that must be re-recorded.
  - Whether the live channel can hold two independent sessions open at once across two different runner instances, or is limited to one at a time process-wide.
- An explicit, named list of always-block actions (destructive schema migrations, outbound calls to third parties, secrets, force-push, and any other explicitly configured protected path) is treated as more trustworthy than a runtime judgment call about reversibility, and is assumed to exist and be checked before FR-031's "decide and report" path is taken.
- A run that has made more than a small, fixed number of autonomous decisions in a row (assumed default: five) is treated as suspect and is stopped and returned to clarify rather than allowed to keep compounding judgment calls unattended.
- Repositories configured for a runner instance are assumed to be exclusively operator-owned; pointing an unattended agent at an employer's or client's repository is explicitly out of scope as a contractual and intellectual-property concern, not merely a technical one.
- The runner's own repository is assumed to be just another configured instance, pointed at itself; its very first version is assumed to be built by hand from this spec, after which further changes to it flow through its own queue the same as any other repository.
- The following are assumed as given technical constraints supplied by the requester, rather than choices left open for the planning phase: the tick is a single-file console application; issue-tracker access goes through its REST API using a fine-grained, narrowly-scoped access token; process locking uses exclusive file locks released deterministically even on an unhandled failure; unattended Claude Code invocations pass arguments programmatically rather than through concatenated shell strings; worktrees are managed with standard git worktree operations, never the shared clone; live sessions are managed through a terminal multiplexer, with all of its state treated as disposable; and the unattended execution path is assumed to run unchanged across the operator's various machines without platform-specific branches, aside from the multiplexer calls themselves.
- One-time manual setup of the live channel (account login, workspace trust, notification settings, and matching sign-in on the mobile app) is assumed to precede any automated use, verified by a self-check command rather than performed by the runner itself.
- Recovering access after an expired login is assumed to be a manual, out-of-band administrative act (e.g., a secure remote shell session), not a capability the runner itself provides.
- The following are explicitly assumed out of scope for this feature: coordination of any kind between separate runner instances; automatically merging a finished pull request; treating any spec as a universally true description of the whole codebase; automatically reconciling a detected spec/code mismatch; any form of rollback (correction is assumed to always be forward-only, via new issues); approval prompts for a decision already reported as made; mirroring live-session conversations into issue comments (only the outcome is assumed to need to be durable, not the dialogue that produced it); any chat-platform integration beyond the issue tracker and the live channel; inbound network endpoints for instant wake-up; and running more than one unit of work at a time within a single instance.
- Given the small request volume implied by a schedule on the order of once every few minutes per instance, the issue tracker's API rate limit is assumed not to be a binding constraint, and no pre-flight quota check is assumed necessary before attempting a call.
- Given that the filesystem and the issue tracker are assumed to hold all state that matters, a live session that runs for an unusually long time is assumed not to need special handling; if degraded output is ever observed in practice, starting a fresh session seeded from the current worktree state is assumed to lose nothing that matters.
