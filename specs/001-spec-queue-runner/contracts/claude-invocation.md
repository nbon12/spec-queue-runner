# Contract: Claude Code Invocation & the Fake Binary

**Feature**: 001-spec-queue-runner | **Consumers**: the runner (production), the test suite (fake)

Both the real and the fake `claude` honour this contract, which is what lets nearly the whole
design run in CI with no credits spent.

## Headless invocation (execution stages, specify, comment-fallback clarify)

| Property | Value |
|---|---|
| Mode | `-p <prompt>` (print mode) |
| Output | `--output-format json` — supplies the session identifier |
| Permissions | `--permission-mode <configured>` — never the shell alias (research R6) |
| Working directory | **the item's worktree**, always; never the shared clone (FR-012) |
| Arguments | passed via `ArgumentList`; prompts contain quotes, newlines, backticks |
| Streams | stdout and stderr drained concurrently, appended in full to the instance log (FR-045) |

**Prompt construction (FR-054, §6)**: the pipeline definition comes from the runner binary and
the spec directory. Operator issue content is inserted only as *answers to questions the runner
posed*, in a clearly delimited region. Content from any non-operator author never reaches the
prompt at all.

**Usage-limit detection (FR-043)**: case-insensitive match on `rate limit|usage limit` against
the combined output. On match: revert the item to `status/ready`, log the full output, exit 0.
The detection corpus grows from real captured failures — it is a Tier 1 theory fixture, not a
guess frozen at implementation time.

## Live session (blocked items, within waking hours)

| Step | Mechanism |
|---|---|
| Spawn | `tmux new-session -d` in the item's worktree, session named for the issue |
| Kickoff | delivered via `send-keys` after a readiness probe (poll pane content), with retry |
| Scope | kickoff instructs resolution of *this item's* open questions only; for execution blocks it additionally bars continuing implementation (FR-021) |
| Record | session ID posted to the issue; item labelled `status/live` (FR-022) |
| Resume | `claude --resume <recorded-id>` — never a fresh conversation (FR-024) |
| Kill | `tmux kill-session` when the reaper confirms resolution |

Live sessions have no timeout and survive machine sleep (FR-023). tmux state is disposable;
losing it costs a pause, nothing more (FR-047).

## The fake `claude` binary (tests)

A shell script placed **first on `PATH`** for Tiers 2 and 3.

### Inputs

| Variable | Meaning |
|---|---|
| `SPEC_RUNNER_FAKE_SCENARIO` | path to the scenario file driving behaviour |
| `SPEC_RUNNER_FAKE_RECORD` | path to the JSONL invocation recording |

### Recording format

Every invocation appends exactly one line:

```json
{"argv": ["-p", "..."], "stdin": "...", "cwd": "/path/to/worktree", "env_permission_mode": "acceptEdits"}
```

This recording is what makes the injection-canary property **assertable rather than
aspirational**: the test seeds a non-operator comment containing a canary string and asserts the
canary appears in no recorded `argv` or `stdin` across every scenario in the suite (constitution
testing §5). It also lets Tier 2 assert that every invocation's `cwd` is the item's worktree and
never the clone.

### Scenario behaviours

| Behaviour | Effect |
|---|---|
| `emit-spec markers=<n>` | write a `spec.md` with n unresolved clarification markers |
| `emit-plan` / `emit-tasks` / `emit-analysis` | create the corresponding artifact |
| `fail-usage-limit` | exit non-zero with realistic usage-limit text |
| `emit-decision count=<n>` | produce n decision records (drives the cap test) |
| `hang` | sleep indefinitely (drives stale reclaim) |
| `emit-session id=<id>` | return JSON carrying a session identifier |

A scenario may sequence behaviours so one fake run advances several stages, exercising FR-020's
consecutive-stage advance and its crash checkpoints.
