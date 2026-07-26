# CLAUDE.md

Project instructions for Claude Code — and for the spec-queue-runner itself, which drives Claude
Code through this repository. Everything here is binding on both.

## Branch management

Two rules. They are the same rule at two moments: **the base is always the latest base**.

### 1. Every branch is cut from the latest base

- New work branches from `origin/<base_branch>` (`master` in this repo; `base_branch` in the
  instance config), **fetched immediately before the branch is created**.
- Never branch from a local ref, from another item's branch, or from a clone that has not just
  been fetched. A long-lived clone's local `master` is the wrong base by default, not by accident.
- **If the fetch fails, no branch is created.** A base whose age nobody can name is not a base.
  The item is left untouched and the next tick starts it once the remote is reachable. Waiting a
  tick is cheap; a branch built on an unknown base is not.

Enforced in `WorktreeLifecycle.EnsureAsync` — it fetches, branches from `origin/<base>`, and
returns null rather than falling back to a stale local ref.

### 2. A stale branch is never merged

Before a change is judged, and again before it lands:

- **Fetch the base and measure.** `git rev-list --count HEAD..origin/<base>` is the distance. A
  count that cannot be read is *unknown*, never zero — zero is the answer that licenses a merge.
- **If the base has moved, rebase onto it** (`git rebase origin/<base>`), then push with
  `--force-with-lease`. Rebase, not merge-in: the branch stays a clean replay of the base and the
  PR shows only the item's own commits. Never `--force` without the lease.
- **If the rebase conflicts, abort it and stop.** Nothing merges, nothing is recorded complete,
  and the issue says why. A conflict is a human's to resolve.
- **Review and verification must run on the rebased branch.** A review of a branch three commits
  behind reviewed a diff that will not land, and a build that passed against the old base proves
  nothing about the new one.
- **The last check is immediately before the merge.** If the base moved *during* review or
  verification, do not merge on the strength of checks that predate the move: rebase, leave the
  item ready, and let the next tick review and verify against the base as it now stands.

Enforced in `Tick.RefreshAgainstBaseAsync` (the mechanics) and `Domain/BranchFreshness.cs` (the
decision: `MayContinue` allows a rebase, `MayMerge` does not).

### The pre-merge checklist

Any task list that ends in a merge — a generated `tasks.md`, a hand-written plan, a PR you are
about to land — carries this as an explicit task, ahead of the merge:

> **Ensure the branch is up to date with the base.** Fetch `origin/<base>`; if the branch is
> behind, rebase onto it and force-push with lease; re-run review and verification on the result.
> Do not merge a branch that is behind the base.

### GitHub-side enforcement

The rules above are also enforced by the platform, so a hand-merge cannot bypass them. The base
branch carries a ruleset requiring a pull request, requiring the `verify` check, and requiring
**branches to be up to date before merging** (`strict_required_status_checks_policy`). Apply or
re-apply it with:

```bash
deploy/branch-protection.sh nbon12/spec-queue-runner master
```

The runner has no bypass. If GitHub refuses a merge, that refusal is a routine outcome: the item
returns to `status/ready` and a later tick retries.

## Working in this repository

- **Ports and adapters.** Octokit, git, tmux, and the `claude` CLI live behind interfaces in
  `Ports/`; nothing else imports them. Decisions live in `Domain/` as pure functions so they are
  testable without a process, a network, or a clock.
- **Do → commit → label.** Work is committed before an issue is labelled complete, never the
  reverse: a crash may cause a re-run, but must never leave a false claim of completion. Stage
  completion labels are written by the caller (`Tick.RunAsync`) only when a stage returns true —
  a handler that writes its own completion label marks itself done before its own gates have run.
- **Never assert an outcome that was not determined.** A stage that did not run has no findings;
  a review that exited non-zero verified nothing; a base that could not be fetched is not
  unchanged. Say what actually happened, on the issue, and stop.
- **The constitution (`.specify/memory/constitution.md`) governs.** Where this file and the
  constitution overlap, the constitution wins; where it is silent, this file is the convention.

## Tests

`dotnet test SpecRunner.slnx` — unit tests are pure and fast, integration tests use the in-memory
GitHub client and a scripted process runner, so the whole suite runs offline and credit-free.
Model process behaviour in the fakes (a rebase that moves HEAD, a fetch that fails) rather than
stubbing the verdict a decision should reach.
