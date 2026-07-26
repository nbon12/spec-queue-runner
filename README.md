# Spec Queue Runner

An unattended worker that turns GitHub Issues into merged pull requests.

You file an issue. Every few minutes a container wakes up, picks the oldest ready item you
authored, and advances it exactly one stage through the [SpecKit](https://github.com/github/spec-kit)
pipeline — intake → specify → clarify → plan → tasks → analyze → implement → review — then exits.
Work reaches you as a pull request with a review and a digest. When it hits a decision it
shouldn't make alone, it escalates to a live Claude Code session you can drive from your phone.

It is designed to reclaim the hours you're asleep: a queue drained overnight, with a readable
account of every judgment call waiting in the morning.

- **Stateless.** Nothing is remembered between ticks. The issue tracker and the git worktree
  hold all state; a tick killed at any point converges on re-run.
- **Isolated.** Everything runs in a Docker container — the blast radius of a confused agent is
  one container and one worktree, not your machine.
- **Single-operator.** Only issues authored by the configured operator are ever acted on, matched
  by immutable numeric user ID. Everyone else's text is data, never instruction.

## Requirements

- **Docker** (Desktop on macOS). Nothing else is installed on the host — the image carries .NET,
  git, tmux, and Claude Code.
- **A Claude subscription** for the in-container login. Setup tokens and API keys will not work
  for live sessions; Remote Control needs a full-scope claude.ai OAuth.
- **macOS** if you want scheduling via launchd. Any Docker host works for running ticks manually.
- **A GitHub token** — one fine-grained PAT can serve every instance; permissions limited to
  Issues, Contents, and Pull requests.

## Quick start (a new repository)

```bash
# 1. Build the image once — it serves every instance.
docker build --platform linux/arm64 -t spec-runner:latest .

# 2. Provision an instance: volume, Claude toolchain, clone, config.
./deploy/new-instance.sh <owner/repo> [base-branch]
```

The script prints the remaining three steps: review the config, run `doctor`, and schedule it.
Each is a single command, reproduced under [Setting up another project](#setting-up-another-project).

Then file an issue in that repo, authored by the operator, labelled `status/ready` — and watch:

```bash
tail -f ~/.config/spec-runner/<owner>-<repo>.scheduler.log
```

## How you use it

Issues are the wire format. The body carries two optional lines:

```
Targets: specs/003-widget-api      # specs this item touches; `none` if it's self-contained
Recurring: monthly                 # presence files a successor issue when this one closes
```

Labels you apply:

| Label | Meaning |
|---|---|
| `status/ready` | the runner may pick this up |
| `abandoned` (+ closed) | stop working this item |

Labels the runner applies: `kind/*` (inferred at intake — you never have to say), `status/*`
(`in-progress`, `live`, `waiting`, `held`), and `stage/*`.

**Kinds** determine the path: `chore` goes intake → implement → review; `feature` and `amendment`
add the full shaping and planning stages; `spike` investigates; `audit` compares one spec against
the code and reports, modifying nothing.

**When it blocks**, inside your waking hours it opens a live Claude Code session on the item's
worktree and pushes it to your phone via Remote Control; outside them it posts the questions as
one issue comment and waits. Either way, answering resolves the item and the pipeline resumes.

## Commands

All run in the container. `deploy/run-tick.sh` is a thin wrapper that mounts the right things:

```bash
./deploy/run-tick.sh doctor  /etc/spec-runner/config.toml   # preflight; touches no work
./deploy/run-tick.sh tick    /etc/spec-runner/config.toml   # force one tick now
./deploy/run-tick.sh version
```

`doctor` is the honest health check — config, toolchain, secret readability, clone validity,
operator-ID resolution, and whether the Claude credential is still *refreshable* (see
[Credentials](#credentials-and-what-actually-expires)). Run it first whenever something is off.

## Setting up another project

Each repository gets its own **instance**: its own config, volume, and launchd job (the GitHub
credential is shared by default). Instances never coordinate and must never share a lock, log,
clone, or worktrees root.

`./deploy/new-instance.sh <owner/repo> [base-branch]` does the provisioning:

1. Reuses the shared credential at `~/.config/spec-runner/github.pat` (mode 600, outside any
   repo), so a new instance usually needs no credential work at all. If none exists it falls
   back to the `gh` CLI token — which works, but carries broader *permissions* than
   [§6 allows](#credentials-and-what-actually-expires); replace it with one fine-grained PAT.
2. Creates the volume `sr-<owner>-<repo>-home`, mounted at `/home/runner`. **One** volume holds
   `.claude/` (the OAuth), `clone/`, `work/` (worktrees), and `state/` (lock + log) — deliberately
   single, so the lock is shared and overlapping ticks mutually-exclude.
3. Copies the image's Claude toolchain into the volume. *(Necessary: mounting the volume at
   `/home/runner` shadows the image's own `~/.local/bin`, hiding the `claude` binary.)*
4. Carries an existing claude.ai OAuth over from another instance's volume when one exists.
5. Clones the repo into the volume with a push remote and a committer identity.
6. Writes a starter config to `~/.config/spec-runner/<owner>-<repo>.toml`.

Then, with `KEY=<owner>-<repo>`:

```bash
# Review the config — operator_login is the ONLY account whose issues are acted on.
$EDITOR ~/.config/spec-runner/$KEY.toml

# Health-check.
docker run --rm \
  -v "$HOME/.config/spec-runner/github.pat:/run/secrets/github_pat:ro" \
  -v "sr-$KEY-home:/home/runner" \
  -v "$HOME/.config/spec-runner/$KEY.toml:/etc/spec-runner/config.toml:ro" \
  spec-runner:latest doctor /etc/spec-runner/config.toml

# One-time Claude login, if doctor reports the credential missing.
# Interactive: it prints a URL you can open on your phone.
docker run --rm -it -v "sr-$KEY-home:/home/runner" \
  --entrypoint /home/runner/.local/bin/claude spec-runner:latest /login

# Schedule it. Host paths are passed explicitly: the plist is generated inside the
# container, but launchd runs on the host, so container paths would mount the wrong files.
docker run --rm -v "$HOME/.config/spec-runner/$KEY.toml:/etc/spec-runner/config.toml:ro" \
  -e SPEC_RUNNER_HOST_CONFIG="$HOME/.config/spec-runner/$KEY.toml" \
  -e SPEC_RUNNER_HOST_PAT="$HOME/.config/spec-runner/github.pat" \
  -e SPEC_RUNNER_HOME_VOLUME="sr-$KEY-home" \
  -e SPEC_RUNNER_HOST_HOME="$HOME" \
  spec-runner:latest install /etc/spec-runner/config.toml spec-runner:latest \
  > ~/Library/LaunchAgents/com.spec-runner.<owner>.<repo>.plist

launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.spec-runner.<owner>.<repo>.plist
```

To pause an instance: `launchctl bootout gui/$(id -u)/com.spec-runner.<owner>.<repo>`.

### Credentials, and what actually expires

Two credentials per instance, with very different maintenance stories.

**The GitHub token** is a file on the host, mounted read-only at `/run/secrets/github_pat`.
It never appears in config, image, or repo. Expiry is whatever you set when you issue it.

By default every instance shares **one** PAT at `~/.config/spec-runner/github.pat`, so adding a
repository needs no credential work. What the constitution constrains is *permissions*, not
repository breadth: issues, contents, and pull requests only — **never** administration,
workflow, or deletion. That ceiling is what bounds the damage a confused run can do, which is
why it is not negotiable even though repo-scoping is.

To give one instance its own narrower token, drop it at
`~/.config/spec-runner/<owner>-<repo>.pat`; a per-instance file always wins over the shared one.

**The claude.ai OAuth** is established once by an in-container `/login` and persisted in the
instance's volume. It is *two* tokens, and conflating them causes needless worry:

| | Lifetime | Who maintains it |
|---|---|---|
| Access token | ~12 hours | **nobody** — Claude Code refreshes it and writes the result back to the volume, so the next tick inherits it |
| Refresh token | long-lived, no fixed expiry | you, but only when it actually dies |

So a routinely "expired" access token is a non-event — the runner heals itself roughly twice a
day, indefinitely. What genuinely stops the runner is the **refresh token** dying: an explicit
logout, a revoked session, or a lapsed subscription. That's why `doctor` checks
`claude.ai credential refreshable` rather than freshness — warning on access-token expiry would
cry wolf every twelve hours while missing the failure that matters.

When that check fails, re-login (interactive; prints a URL you can open on your phone):

```bash
docker run --rm -it -v "sr-$KEY-home:/home/runner" \
  --entrypoint /home/runner/.local/bin/claude spec-runner:latest /login
```

Because the credential lives in the volume, it survives image rebuilds — you do not re-login
after changing runner code.

### Configuration

Full schema and validation rules: [`contracts/config-schema.md`](specs/001-spec-queue-runner/contracts/config-schema.md).
The fields worth deciding per project:

| Field | Default | Why you'd change it |
|---|---|---|
| `operator_login` | — | **the allowlist**; only this account's issues are acted on |
| `base_branch` | `main` | repos on `master` |
| `tick_interval` | `300` | how often it wakes (mirrors the launchd `StartInterval`) |
| `waking_hours` | `08:00-23:00` | when a block may open a live session vs. hold for morning |
| `auto_merge` | `true` | set `false` to gate merges yourself while building trust |
| `spend_cap` | `100` | USD; estimated spend above this always blocks |
| `decision_cap` | `5` | autonomous judgment calls before a run stops and asks |
| `review_prompt` | `.specify/prompts/code-review.md` | repo-relative; **never** sourced from issue text |

Each served repo needs its own `review_prompt` file committed to it.

## Development

Everything builds and tests in a container — no host .NET required:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c 'dotnet build SpecRunner.slnx -c Debug && dotnet test SpecRunner.slnx -c Debug'
```

**131 tests**, all offline and credit-free: unit (pure domain logic), integration (the real tick
against an in-memory GitHub and a scripted process runner), and property families —
crash-convergence (kill after each side effect; re-run to quiescence; assert the end state matches
the never-crashed one) and an injection canary (a non-operator's text must never reach the process
boundary).

After changing runner code, rebuild the image; the next tick uses it automatically:

```bash
docker build --platform linux/arm64 -t spec-runner:latest .
```

Architecture is ports-and-adapters: `Domain/` is pure and heavily tested, `Ports/` are
first-party interfaces, `Adapters/` wrap GitHub (Octokit), git, tmux, and Claude Code, and
`Ticking/` orchestrates. Octokit never escapes its adapter.

## Documentation

| Document | What it covers |
|---|---|
| [`.specify/memory/constitution.md`](.specify/memory/constitution.md) | the non-negotiable rules this project is governed by |
| [`specs/001-spec-queue-runner/spec.md`](specs/001-spec-queue-runner/spec.md) | the full requirements, plus a living implementation-status ledger |
| [`deploy/README.md`](deploy/README.md) | the self-hosting instance: what's running now, and its trade-offs |
| [`contracts/`](specs/001-spec-queue-runner/contracts/) | config schema, CLI surface, issue conventions, Claude invocation |

## Security posture

- **One operator.** Issues from anyone else are ignored entirely — not sanitized, ignored. The
  match is on numeric GitHub user ID, because logins can be renamed and re-registered.
- **Prompt injection.** Issue text is data. The review prompt comes from the repository, never
  from an issue; the runner recognizes its own comments by marker so it can't be fed its own output.
- **Secrets.** The GitHub token is a mounted file, never in config or the image. The Claude OAuth
  is a one-time in-container login persisted in a volume, never baked into the image.
- **Blast radius.** The container is the boundary. It has the clone, the worktrees, and one token
  scoped to one repository.
- **Always-block.** Irreversible actions — protected paths, spend above the cap — stop and ask,
  regardless of how confident the run is.

Point instances only at repositories you own. Aiming an unattended agent at an employer's or
client's repository is out of scope here as a contractual matter, not merely a technical one.
