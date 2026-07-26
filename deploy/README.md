# Deploying the spec-queue-runner (self-hosting instance)

This directory holds everything that runs the runner against **its own repo**
(`nbon12/spec-queue-runner`). The tick runs inside the `spec-runner:latest` container;
launchd fires one every 5 minutes. Nothing runs on the host except Docker + launchd.

> Setting up a **different** repository? See [Setting up another project](../README.md#setting-up-another-project)
> in the root README — `./deploy/new-instance.sh <owner/repo>` does the provisioning.
> This file documents the already-deployed self-hosting instance specifically.

## What's live right now

- **Image**: `spec-runner:latest` (linux/arm64), built from the repo `Dockerfile`.
- **Volume**: `sr-self-home` mounted at `/home/runner` — holds the Claude OAuth
  (`.claude/`), the clone (`clone/`), worktrees (`work/`), and the lock/log (`state/`).
  One volume so the lock file is shared and overlapping ticks mutually-exclude.
- **Secret**: `~/.config/spec-runner/spec-queue-runner.pat` (chmod 600, **outside the repo**),
  mounted read-only at `/run/secrets/github_pat`.
- **launchd job**: `com.spec-runner.nbon12.spec-queue-runner`, StartInterval 300s.
  Output goes to `~/.config/spec-runner/scheduler.log`.

## Daily use

File an issue in `nbon12/spec-queue-runner`, authored by **nbon12**, labelled
`status/ready`. Within ~5 minutes the runner classifies it (intake), then across
subsequent ticks drives it through the pipeline: chores go intake → implement → PR →
review → auto-merge → close; features add the specify/clarify/plan/tasks/analyze stages.
One unit of work per tick.

Watch it:

```bash
tail -f ~/.config/spec-runner/scheduler.log          # what each tick does
./deploy/run-tick.sh doctor /etc/spec-runner/config.toml   # preflight health check
./deploy/run-tick.sh tick  /etc/spec-runner/config.toml    # force one tick now (by hand)
```

## Stop / start / remove

```bash
# Pause (survives until you re-load; does NOT survive reboot once booted out):
launchctl bootout gui/$(id -u)/com.spec-runner.nbon12.spec-queue-runner

# Resume:
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.spec-runner.nbon12.spec-queue-runner.plist

# Fully remove:
launchctl bootout gui/$(id -u)/com.spec-runner.nbon12.spec-queue-runner
rm ~/Library/LaunchAgents/com.spec-runner.nbon12.spec-queue-runner.plist
```

## Known trade-offs (worth tightening before you rely on it)

1. **GitHub credential is the broad `gh` OAuth token, not a least-privilege PAT.**
   It works, but the constitution (§6) wants a fine-grained token scoped to this one
   repo (Issues, Contents, Pull requests only). To swap it in:
   ```bash
   printf '%s' '<fine-grained-token>' > ~/.config/spec-runner/spec-queue-runner.pat
   chmod 600 ~/.config/spec-runner/spec-queue-runner.pat
   # also update the clone's push remote inside the volume to use the new token
   ```

2. **`auto_merge = true`** — the runner merges its own PRs to `master` after a clean
   review, with only a digest comment. Flip to `false` in `spec-queue-runner.toml` if you
   want to gate merges yourself while you build trust.

3. **Claude credential** (FR-052b). Reused from the 2026-07-25 in-container login and
   held in `sr-self-home/.claude`. The access token lives ~12 hours, but Claude Code
   refreshes it automatically and writes the result back to the volume, so every tick
   inherits a fresh one — **routine expiry needs nothing from you** (measured: the
   expiry advanced from 09:03 to 21:40 during a single run).

   What does need you is the **refresh token** dying — logout, credential revocation,
   or a lapsed subscription. `doctor` reports `claude.ai credential refreshable`, which
   is the condition that actually matters. When it fails, re-login:
   ```bash
   docker run --rm -it -v sr-self-home:/home/runner --entrypoint /home/runner/.local/bin/claude \
     spec-runner:latest /login
   ```

4. **The clone lives in the volume**, seeded once from `origin/master`. The runner
   branches worktrees off the clone's `master`; it fetches as part of normal git use,
   but if you ever need to hard-reset it, re-seed the volume.

## Rebuilding the image after code changes

```bash
docker build --platform linux/arm64 -t spec-runner:latest .
# then copy the fresh binary's claude install is unaffected; nothing else to do —
# the next tick uses the new image automatically.
```
