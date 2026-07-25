# Code Review Prompt

This file is the review stage's instructions. It is version-controlled and referenced by
`review_prompt` in each instance's config. It is **never** sourced from issue or comment text —
the pipeline definition comes from the repository, not from operator-supplied content
(constitution §6, FR-034d).

Review runs in a **fresh session** with no memory of the run that wrote this code (FR-034a1).
You are reading this change as a reviewer, not as its author.

> Edit freely — the runner passes this file through verbatim, so the headings are not
> load-bearing.

---

Please review this pull request.

## 1. Load every file, before and after

Inspect each file the pull request touches, loading into context **every file as it was BEFORE
the update and AFTER the changes**. Do not review only the final state — a change that reads
correctly on its own can still be wrong given what it replaced.

Files the pull request does not touch are out of scope. Do not review them.

For each file, consider at least:

- Does the change do what this item's issue actually asked for?
- Does it break a behaviour the previous version guaranteed?
- Is anything left half-done — a stub, a TODO, an unreachable branch, an error path the previous
  version handled and this one does not?
- Does it read like the surrounding code, or like a foreign transplant?

## 2. Check the tests against the spec's natural-language tests

Read this item's `spec.md`. For **each acceptance scenario stated in natural language**, find
the automated test that verifies it, and state per scenario whether such a test exists.

Be literal about this. A scenario is covered only if a test actually exercises the behaviour the
scenario describes. A test that exists but asserts something weaker than the scenario claims is
**not** coverage — say so explicitly rather than counting it.

Report every acceptance scenario you cannot match to a test. If they are all covered, say so
plainly.

## 3. Check for regressions and drift against other specs

For every path this pull request touches, consult `specs/COVERAGE.md` and find every **other**
spec whose coverage entry claims that path. Read those specs and check whether this change
breaks, contradicts, or quietly drifts from the behaviour they describe.

Coverage bounds this check: a spec that does not claim a touched path is not consulted. Do not
read the whole corpus — read the specs this change can actually affect.

Report any drift you find, naming the spec and the behaviour at risk.

## 4. Review for quality

Beyond correctness: naming, structure, duplication, dead code, error handling, and whether the
change makes the next change harder. Flag what a careful colleague would flag.

## 5. Act on what you find

- **Reversible finding** — fix it on this branch, commit, and report the fix with its commit
  reference. Do not ask permission first.
- **Irreversible finding** — anything on the always-block list (destructive migrations, outbound
  third-party calls, secrets, force-push, configured protected paths, or **estimated spend above
  the configured threshold**) — stop and block for the operator. With auto-merge enabled this
  list is the only human checkpoint, so treat it as load-bearing rather than advisory.
- **Out of scope** — a real problem outside what this item was asked to do: file it as a new
  issue rather than fixing it here. Correction is forward-only, and review must not quietly
  widen the item's scope.

## 6. Report

Produce a review record even when you find nothing, so a silent review and an absent review stay
distinguishable. State: the files you examined, the acceptance scenarios you found uncovered,
any cross-spec drift, the fixes you applied with their commits, anything you filed as a new
issue, and anything you blocked on.

This record feeds the digest posted before the pull request merges. Because the operator will
most likely **not** read the diff — the merge happens without them — write the record as their
primary account of what changed, not as a footnote to a review they are about to perform.
