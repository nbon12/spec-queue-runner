#!/usr/bin/env bash
# One tick of the spec-queue-runner, in the container, against its own repo.
# launchd invokes exactly this (see the plist). Safe to run by hand to test.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRET_DIR="${HOME}/.config/spec-runner"
IMAGE="spec-runner:latest"

# No --init: the image's ENTRYPOINT is already tini as PID 1 (it reaps the claude/git/tmux
# children each tick spawns). Adding --init would nest a second tini and warn.
exec docker run --rm --platform linux/arm64 \
  -v "${SECRET_DIR}/github.pat:/run/secrets/github_pat:ro" \
  -v "sr-self-home:/home/runner" \
  -v "${REPO_ROOT}/deploy/spec-queue-runner.toml:/etc/spec-runner/config.toml:ro" \
  "${IMAGE}" "$@"
