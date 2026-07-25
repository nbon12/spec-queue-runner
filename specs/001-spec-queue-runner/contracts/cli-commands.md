# Contract: CLI Command Surface

**Feature**: 001-spec-queue-runner | **Consumers**: the operator, launchd

The binary is installed once and invoked per-instance with that instance's config path.

## Commands

### `spec-runner tick <config-path>`

Runs exactly one tick: acquire lock → resolve operator identity → collect replies → reap →
perform at most one unit of work → exit. This is what launchd invokes.

- Acquires the instance lock **before anything else**; exits 0 immediately if held (FR-002).
- Exits within a few seconds when there is no work (FR-003).
- Performs work on at most one item (FR-009).

### `spec-runner doctor <config-path>`

Verifies prerequisites without touching the queue. Prints one pass/fail line per check:

These run **inside the container** (where the tick lives), so they check the container's own
toolchain, not the host's.

| Check | Failure means |
|---|---|
| Config parses and validates | fix the TOML (exit 1) |
| .NET runtime present (image layer) | rebuild the image |
| `git` present, version ≥ 2.5 | worktree support missing from image |
| `tmux` present | missing from image |
| `claude` present and on PATH | missing from image |
| GitHub PAT secret file readable at configured path | mount the secret (see quickstart) |
| PAT scope covers issues/contents/PRs | re-issue the fine-grained token |
| **claude.ai OAuth present and not near expiry** | run in-container `/login` — **loud**, expiry silently stalls live sessions (FR-052b) |
| Operator login resolves to a numeric ID | allowlist cannot be enforced — **fail closed** |
| Clone path exists and is a git repo | fix `path` |
| Main branch is protected | review gate is not structural (§6, FR-056) |

### `spec-runner doctor --probe <config-path>`

Runs the Tier 4 live probes that cannot be CI'd. **The only command permitted to spend Claude
credits or require the operator's phone.** Each prints pass/fail:

1. Workspace trust carries into a freshly created worktree directory (the potential blocker).
2. A scripted kickoff message is delivered reliably into a newly spawned interactive session.
3. Session resumption by recorded ID interacts cleanly with Remote Control.
4. Two instances can hold Remote Control sessions simultaneously.

Results resolve the four assumptions the spec parks for empirical validation.

### `spec-runner version`

Prints version and build identifier.

## Exit codes

| Code | Meaning | launchd consequence |
|---|---|---|
| 0 | Work done, nothing to do, or lock held | normal |
| 1 | Configuration invalid or unreadable | operator must fix; logged loudly |
| 2 | Environment/prerequisite failure (missing image tooling, unreadable PAT secret, expired claude.ai login, operator login unresolvable) | operator must fix |
| 3 | Unexpected internal error | investigate log |

Exit 0 covers "lock held" deliberately: overlapping ticks are normal operation, not an error
condition worth alerting on.

## Invocation rules

- All arguments to child processes are passed via `ArgumentList`, never concatenated into a
  shell string (constitution §4).
- Child processes receive an explicitly constructed environment. The runner MUST NOT inherit
  the operator's `claude` shell alias (which carries `--dangerously-skip-permissions`); the
  permission mode comes from config (research R6).
- stdout and stderr of every headless run are drained concurrently and appended in full to the
  instance log (FR-045).
