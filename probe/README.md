# Architecture probe

One container, one evening, four answers. This decides whether the Spec Queue
Runner is containerised — and possibly deletes a chunk of its design.

It contains **no .NET**, so it runs today, ahead of every toolchain blocker.

## What it answers

| # | Question | Why it decides something |
|---|---|---|
| 1 | Does Remote Control work from inside a container? | Undocumented in both directions. If yes, full containerisation with the phone channel intact. If no, choose comment-fallback or a host/container split. |
| 2 | Does workspace trust survive a fresh worktree? (**OQ-3**) | Trust is per-directory with no documented pre-accept, and Remote Control fails on untrusted workspaces. Every work item makes a new directory. |
| 3 | Does `--spawn worktree` server mode replace hand-rolled worktrees? | Server mode does worktree-per-session natively at 32 concurrent sessions, and lifts the one-session-per-process limit FR-025 encodes as permanent. |
| 4 | Does session resumption re-register Remote Control? (**OQ-5**) | The reaper respawns by recorded ID. If resumption mints a new one, that's cosmetic; if it fails, FR-024's no-re-ask guarantee weakens. |

tmux kickoff delivery (**OQ-4**) rides along for free.

## Before you start

- **Your phone**, with the Claude app signed in to the same account you'll use in the container.
- **A browser on your Mac** for the one-time OAuth flow.
- **Push enabled**: inside Claude Code, `/config` → *Push when actions required*.
- ~20 minutes. Most of it is you answering prompts, not waiting.

The container needs an eligible subscription (Pro, Max, Team, or Enterprise).
API keys and `setup-token` credentials **cannot** establish Remote Control — this
is documented, so don't try to shortcut the login.

## Run it

```bash
cd probe
docker build -t specrunner-probe .

# Persist the whole home directory, not just ~/.claude.
#
# Claude Code splits its state across TWO paths: ~/.claude/ (credentials) and
# ~/.claude.json (project config — which is almost certainly where workspace
# trust lives). A volume on ~/.claude alone would silently lose trust state on
# restart, which is exactly what section 3 is trying to measure.
#
# Docker seeds an empty named volume from the image's contents at that path, so
# the first run carries the Claude Code install in with it.
docker volume create specrunner-probe-home

docker run -it --name specrunner-probe \
  -v specrunner-probe-home:/home/runner \
  specrunner-probe bash
```

Inside the container, log in first:

```bash
claude          # then /login, complete in your Mac's browser, then /exit
```

Then run the probe:

```bash
./run-probe.sh
```

It automates what it can and stops to ask you the things only a human with a
phone can confirm. Several steps ask you to open a **second shell** — that's just:

```bash
docker exec -it specrunner-probe bash
```

## Get the results out

```bash
docker cp specrunner-probe:/home/runner/probe-results.md ./probe-results.md
```

## Reading the outcome

**Question 1 is the fork in the road.**

- **Remote Control works** → containerise everything. The `.NET 10` and `tmux`
  host installs stop being prerequisites and become lines in a Dockerfile; the
  runner targets `linux-arm64` with no cross-publish.
- **Remote Control doesn't work** → two shapes, both viable:
  - *Contained, degraded*: everything in the container, live channel permanently
    falls back to issue comments. The design already supports this end to end —
    "Degradable" is one of the eight acceptance conditions. Cost: you lose the
    phone-chat channel, which is an explicit product goal, not a nicety.
  - *Split by risk*: headless runs contained, live sessions on the host. The
    split lands well — the contained process is the one running unattended at 3am
    writing code, while the host process only talks to you, only edits spec and
    plan files, and is barred from implementing.

**If question 2 fails**, read the `~/.claude.json` diff the probe captures. If
trust is stored as a path list, seed it when creating each worktree and OQ-3 goes
from blocker to config line. If it's opaque or device-bound, the live channel
needs a different directory strategy — for example one long-lived trusted
directory per instance instead of one per item.

**If question 3 passes**, stop before writing worktree code. Server mode may
subsume FR-012, FR-014, and FR-021 through FR-026, and would remove the
one-live-session-per-instance constraint entirely.

## Notes

- `DISABLE_AUTOUPDATER=1` is set in the image. A background update mid-probe
  would make a failure impossible to attribute.
- `ANTHROPIC_API_KEY` and `ANTHROPIC_BASE_URL` are explicitly blanked. Either one
  set wrong disables Remote Control, and inheriting them from a host shell is a
  confusing way to fail.
- Debian, not Alpine: musl needs extra setup (`libgcc`, `libstdc++`, and
  `USE_BUILTIN_RIPGREP=0`) that would add variables to a run whose entire job is
  isolating one unknown.
- This probe is the manual ancestor of `doctor --probe` (tasks T050/T051). What
  it learns should end up there.
