# Contract: Instance Configuration Schema

**Feature**: 001-spec-queue-runner | **Format**: TOML v1.0.0 | **One file per instance**

Every field is validated at startup. The tick **refuses to run** on invalid or missing
configuration rather than deferring the failure to mid-tick (constitution §7). New
configuration ships with its validation.

## Schema

```toml
# ── Identity: the repo pair. Neither half alone identifies a book of work. ──
slug            = "nicholas/homelab"      # owner/repo where the issues live
path            = "~/code/homelab"        # the clone the worktrees hang off
worktrees_root  = "~/code/homelab-work"   # one folder per in-flight item

# ── The allowlist. Exactly one operator; widening requires a constitution amendment. ──
operator_login  = "nicholasbonilla"       # resolved to a numeric user ID at startup

# ── Scheduling and patience ──
tick_interval   = 300                     # seconds; mirrors the launchd StartInterval
waking_hours    = "08:00-23:00"           # local time; live sessions only inside this window
stale_hours     = 2                       # in-progress older than this is reclaimed

# ── Judgement limits ──
decision_cap    = 5                       # autonomous decisions before a run blocks

# ── Claude Code ──
permission_mode = "acceptEdits"           # unattended permission mode; never inherited from shell

# ── Code review ──
review_prompt = ".specify/prompts/code-review.md"   # repo-relative; NEVER sourced from issue text

# ── Merge gate ──
auto_merge   = true    # runner merges its own PR after review passes with no blocking finding
spend_cap    = 100     # USD; estimated one-off or recurring spend above this always blocks

# ── Secrets: referenced, never stored ──
[keychain]
service         = "spec-runner"
account         = "nicholas/homelab"      # the PAT lives here, not in this file

# ── Paths ──
log             = "~/.local/state/spec-runner/homelab.log"
lock            = "~/.local/state/spec-runner/homelab.lock"

# ── Always-block list: actions that never fall under "decide and report" ──
[irreversible]
protected_paths = ["infra/**", "**/migrations/**"]
```

## Validation rules

| Field | Rule | Failure |
|---|---|---|
| `slug` | matches `owner/repo` | exit 1 |
| `path` | exists, is a git repository | exit 1 |
| `worktrees_root` | exists or is creatable | exit 1 |
| `operator_login` | non-empty; **resolves to a numeric GitHub user ID** at startup | exit 2, fail closed |
| `tick_interval` | ≥ 60 seconds | exit 1 |
| `waking_hours` | `HH:MM-HH:MM`, parses as local times | exit 1 |
| `stale_hours` | > 0 | exit 1 |
| `decision_cap` | ≥ 1 | exit 1 |
| `permission_mode` | one of the modes Claude Code accepts | exit 1 |
| `review_prompt` | repo-relative path; file exists and is non-empty on the item's branch | exit 1 |
| `auto_merge` | boolean | exit 1 |
| `spend_cap` | > 0 when `auto_merge` is true | exit 1 |
| `[keychain]` | entry resolvable via `security find-generic-password` | exit 2 |
| `log`, `lock` | parent directory exists or is creatable | exit 1 |

**Fail-closed rule**: if `operator_login` cannot be resolved to a numeric ID, the tick performs
no work at all. It does **not** fall back to comparing login strings — that would reintroduce
the rename/re-registration impersonation path the numeric ID exists to close (research R5).

## Prohibited content

The config file MUST NOT contain a token, password, or any other secret. Secrets are referenced
from the macOS keychain by service/account only (constitution §2, §6, FR-052). A config
containing something that looks like a credential is a validation failure, not a warning.

## Cross-instance rule

Two instances MUST NOT share a `lock`, `log`, `path`, or `worktrees_root`. Instances never
coordinate (§3); sharing any of these would create exactly the coupling the design dissolves.
Validation cannot see other instances' configs, so this is enforced by convention and checked
by `doctor` where detectable (e.g. a lock path already held by a different slug).
