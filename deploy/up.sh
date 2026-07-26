#!/usr/bin/env bash
# Start (or restart) the long-lived supervisor container for this instance.
#
#   ./deploy/up.sh            # start it
#   docker logs -f spec-runner-spec-queue-runner
#   docker stop spec-runner-spec-queue-runner
#
# The container runs `serve`, which ticks internally on the configured interval. It stays up so
# `docker logs -f` works and so live tmux sessions survive between ticks (constitution §2, v5.0.0).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRET_DIR="${HOME}/.config/spec-runner"
IMAGE="${SPEC_RUNNER_IMAGE:-spec-runner:latest}"
NAME="spec-runner-spec-queue-runner"
VOLUME="sr-self-home"

# Replace any previous supervisor; restarting is safe by design (§3 — it carries no state).
docker rm -f "$NAME" >/dev/null 2>&1 || true

docker run -d --name "$NAME" \
  --restart unless-stopped \
  --platform linux/arm64 \
  -v "${SECRET_DIR}/github.pat:/run/secrets/github_pat:ro" \
  -v "${VOLUME}:/home/runner" \
  -v "${REPO_ROOT}/deploy/spec-queue-runner.toml:/etc/spec-runner/config.toml:ro" \
  "$IMAGE" serve /etc/spec-runner/config.toml >/dev/null

echo "supervisor started: $NAME"
echo "  follow logs : docker logs -f $NAME"
echo "  stop        : docker stop $NAME"
echo "  one-off tick: ./deploy/run-tick.sh tick /etc/spec-runner/config.toml"
