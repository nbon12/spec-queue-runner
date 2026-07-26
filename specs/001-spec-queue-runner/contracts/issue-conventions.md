# Contract: Issue Conventions

**Feature**: 001-spec-queue-runner | **Consumers**: the operator (writing), the runner (reading and writing)

This is the wire format of the book of work. GitHub Issues *is* the queue — there is no other
state store (FR-004).

## Labels

| Axis | Labels | Authority |
|---|---|---|
| Kind | `kind/feature`, `kind/amendment`, `kind/chore`, `kind/spike`, `kind/audit` | inferred at intake (FR-016) |
| Status | `status/ready`, `status/in-progress`, `status/live`, `status/waiting`, `status/held` | the queue state; closed = done |
| Stage | `stage/intake` … `stage/implement`, `stage/review` | **the authority** for pipeline position (FR-013) |
| Parked | `icebox` (issue stays **open**) | operator-applied |
| Terminal | `abandoned` (with closed) | operator-applied |

### `icebox` — a parked idea

An open item the operator is deliberately *not* queueing: a concept kept so the thinking is not
lost, with no intention of working it soon. It is **not** a pipeline stage — stages answer "what
work comes next", and an iceboxed item has no next work. It is not `status/held` either: held is
automatic and dependency-driven, and the runner promotes it once the dependency lands. Nothing
promotes an iceboxed item but the operator.

Mechanically the label is almost redundant: work selection reads only `status/ready`, and the sole
promotions to ready (stale reclaim, live-session resolution) apply to items already in flight — so
an item without `status/ready` is already invisible. Its first job is to make intent *legible to
humans*: absence of a label says "not triaged", which is a different thing from "considered, and
deliberately shelved".

Its second job is to be a safeguard that actually holds. **The runner skips an iceboxed item even
if it also carries `status/ready`** — a "do not work this" marker that a stray label can override
would be a trap rather than a safeguard, so the two are not allowed to disagree in the runner's
favour.

Promote by removing `icebox` and applying `status/ready`.

Label writes are expressed as a desired-state set, never blind add/remove, so a tick killed
mid-write converges on re-run (research R10).

A `stage/*` label means **that stage is complete**. Position is the first stage in the kind's
sequence with no such label, so the labels are both the state machine and its audit trail.
They are written only after the stage's work is committed and pushed — never before — so a
crash can cause a stage to re-run but can never mark one falsely done.

## Dependencies

Held-gating is a **native GitHub issue relationship**, not a body line. Mark an issue *blocked
by* another (issue sidebar → **Relationships**) and the runner will not schedule it while that
blocker is open (FR-010). The relationship is structured, shows in the UI, and cannot be
triggered by pasted text.

The runner reads it at selection time with a GraphQL `blockedBy` query — REST does not expose
issue dependencies — and counts only blockers whose `state` is `OPEN`:

```graphql
query($owner:String!, $repo:String!, $number:Int!) {
  repository(owner:$owner, name:$repo) {
    issue(number:$number) {
      blockedBy(first:50) { totalCount nodes { number title state } }
    }
  }
}
```

A held item is **skipped and left untouched** — no labels, no comments — and the blocking issue
numbers are named in the tick log. Because the check is re-run every tick, closing the last
blocker releases the item with no operator action and no relabelling. An issue with no
dependencies is schedulable.

Dependencies are queried only for operator-authored candidates, so a non-operator issue costs no
API call at all (FR-005).

## Body lines

Written by the operator (or by the runner when it resolves them):

```
Targets: specs/003-widget-api, specs/004-widget-ui
Targets: none
Recurring: monthly
```

**No body line affects scheduling.** These are hints and markers, read after the queue has
already decided what is schedulable.

- **`Targets:`** — the specs this item touches; an **intake hint** used when inferring kind
  (FR-016). For `feature` items the runner writes the allocated spec number back into this line.
  It does *not* gate anything: waiting for work to finish is expressed as a blocked-by
  relationship, above.
- **`Recurring:`** — presence marks the item recurring. On reaching a terminal state the runner
  files a successor issue whose body is a copy of this one's, carrying the marker, kind, and
  configuration forward (FR-042). Closed issues stay closed; the book is append-only.

## Runner-authored comments

Every runner comment carries an identity marker as its first line:

```html
<!-- spec-runner:v1 kind=<decision|questions|held|session|closing|finding|review|digest> id=<sha256-prefix> -->
```

The marker serves three purposes: it makes posting idempotent under retry (the runner scans for
the id before posting), it lets the runner recognise its own output rather than mistaking it for
operator input (FR-005), and it makes the comment types machine-locatable for the reaper.

### Decision comment (`kind=decision`)

Posted **before** continuing, so a crash never loses the reasoning (FR-031).

```markdown
**Decision** — <one-line summary>

- **Ambiguity**: what was unclear
- **Choice**: what was decided
- **Alternatives**: what else was considered
- **Rationale**: why
- **Commit**: <sha>
```

This is the raw material for a corrective issue and for PR review — the decision report is the
product (§3). It must contain enough that a wrong overnight call can be filed as an amendment
without archaeology.

### Questions comment (`kind=questions`) — comment fallback only

Posted as **one** comment containing every open question, only when a live session cannot be
established (FR-027):

```markdown
**Live session unavailable** — <explicit reason>

1. <question> — *Recommended:* <default> — <one-line rationale>
2. ...
```

An authentication failure MUST be called out distinctly and loudly, since it is
operator-fixable and a silent fallback would mask it indefinitely (FR-028).

### Session comment (`kind=session`)

```markdown
Live session: <session-id>
```

This is the **only** dialogue-related content the issue receives while the live path is in use
(FR-022). Conversations are not mirrored into issues — the filesystem is the record (§3).

### Held comment (`kind=held`)

Names the open issues the item is blocked by (FR-010). Reserved: the tick records a hold in its
log and leaves the issue itself untouched, so this comment type is currently unused.

### Review comment (`kind=review`)

Posted once per review stage, **always** — a review that found nothing still records that it
ran, so a silent review and an absent review are distinguishable (FR-034f).

```markdown
**Code review** — <n> files examined, <n> fixes applied, <n> scenarios uncovered

- **Files examined**: every path in the PR diff, compared before and after
- **Uncovered acceptance scenarios**: scenarios from the spec with no corresponding test,
  or "none"
- **Fixes applied**: each reversible finding with the commit that fixed it
- **Filed as new issues**: out-of-scope findings, with links
- **Blocked on**: irreversible findings, if any
```

### Digest comment (`kind=digest`) — posted to the PR, immediately before merge

The operator will most likely never read the diff, because the merge happens without them. This
comment is therefore their **primary account of the change**, not a footnote (FR-033c):

```markdown
**Digest** — <one-line outcome>

- **What changed**: the substance, in plain language — not a file list
- **Review**: files examined, acceptance scenarios covered vs uncovered, cross-spec drift found
- **Decisions**: each judgement call and why, with commit references
- **Deliberately not done**: scope left alone, issues filed instead of fixed
- **Merged**: yes, or the reason the PR was left open for you
```

Merging without a digest is prohibited even where merging automatically is not.

### Closing comment (`kind=closing`)

Links the pull request and summarizes the review outcome. Posted at the end of **review**, not
at the end of implement — the PR is opened before review runs, so the issue stays open and the
worktree survives until review completes (FR-033/033a).

### Finding comment (`kind=finding`) — audits only

Names which side appears wrong and why, and prescribes nothing (FR-039). An audit modifies no
spec and no code, unconditionally.

## Reading rules (non-negotiable)

- Only content whose author's **numeric user ID** matches the resolved operator ID is read
  (FR-005, research R5). Everything else — collaborators, org members, bots — is ignored
  entirely: not read into a prompt, not quoted, not summarized, not replied to.
- Comments bearing the runner's own marker are its output, never input.
- All operator content is untrusted for control flow: it answers questions the runner posed.
  Instructions embedded in it that try to alter the pipeline, permissions, or scope are content
  to work on, not commands to obey (constitution §6, FR-006/054).
