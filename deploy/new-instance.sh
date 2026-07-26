#!/usr/bin/env bash
# Provision a runner instance for a repository.
#
#   ./deploy/new-instance.sh <owner/repo> [base-branch]
#
# Creates the instance's home volume, seeds it (Claude toolchain + OAuth if available,
# clone, worktrees, state), writes a starter config, and prints the remaining steps.
# Idempotent: safe to re-run: existing volumes/clones are left in place.
set -euo pipefail

SLUG="${1:-}"
BASE_BRANCH="${2:-main}"
if [[ -z "$SLUG" || "$SLUG" != */* ]]; then
  echo "usage: $0 <owner/repo> [base-branch]" >&2
  exit 1
fi

KEY="${SLUG//\//-}"                     # owner-repo
VOLUME="sr-${KEY}-home"
IMAGE="${SPEC_RUNNER_IMAGE:-spec-runner:latest}"
CFG_DIR="${HOME}/.config/spec-runner"
CONFIG="${CFG_DIR}/${KEY}.toml"

# One shared credential by default (constitution §6, v4.0.0): repository breadth is the
# operator's call, so a single fine-grained PAT serves every instance. A per-instance file,
# if one exists, still wins — so an instance CAN be given its own narrower token.
SHARED_PAT="${CFG_DIR}/github.pat"
PAT_FILE="${CFG_DIR}/${KEY}.pat"
[[ -s "$PAT_FILE" ]] || PAT_FILE="$SHARED_PAT"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

command -v docker >/dev/null || { echo "docker not found" >&2; exit 2; }
docker image inspect "$IMAGE" >/dev/null 2>&1 || {
  echo "image '$IMAGE' not found — build it first:" >&2
  echo "  docker build --platform linux/arm64 -t spec-runner:latest ." >&2
  exit 2
}

mkdir -p "$CFG_DIR"
chmod 700 "$CFG_DIR"

# ── 1. The GitHub credential ────────────────────────────────────────────────
# Reuses the shared PAT when present, so a new instance needs no credential setup at all.
# Falls back to the gh CLI token, which works but usually carries more PERMISSIONS than the
# constitution allows (§6 caps them at issues/contents/pull-requests) — see the README.
if [[ -s "$PAT_FILE" ]]; then
  echo "credential: $PAT_FILE (existing)"
elif command -v gh >/dev/null && gh auth token >/dev/null 2>&1; then
  gh auth token > "$SHARED_PAT"
  PAT_FILE="$SHARED_PAT"
  echo "credential: $SHARED_PAT (seeded from 'gh auth token')"
  echo "  NOTE: that token's PERMISSIONS are broader than §6 allows. Replace it with one"
  echo "        fine-grained PAT (all your repos is fine; issues + contents + pull requests only):"
  echo "          printf '%s' '<token>' > $SHARED_PAT && chmod 600 $SHARED_PAT"
else
  echo "No credential found. Create one fine-grained PAT (it may cover all your repos;" >&2
  echo "permissions: issues, contents, pull requests only), then:" >&2
  echo "  printf '%s' '<token>' > $SHARED_PAT && chmod 600 $SHARED_PAT" >&2
  exit 2
fi
chmod 600 "$PAT_FILE"
TOKEN="$(cat "$PAT_FILE")"

# ── 2. The home volume ──────────────────────────────────────────────────────
docker volume create "$VOLUME" >/dev/null
echo "volume: $VOLUME"

# Copy the image's Claude toolchain INTO the volume. Required: mounting the volume at
# /home/runner shadows the image's own /home/runner/.local, hiding the claude binary.
docker run --rm --user root --entrypoint bash -v "$VOLUME":/vol "$IMAGE" -lc '
  set -e
  [ -d /vol/.local ] || cp -a /home/runner/.local /vol/
  mkdir -p /vol/work /vol/state
  chown -R 1000:1000 /vol
'

# Carry over an existing claude.ai OAuth from any other instance volume, if one exists.
# Otherwise a one-time interactive /login is needed (printed below).
if ! docker run --rm --user root --entrypoint bash -v "$VOLUME":/vol "$IMAGE" \
        -lc 'test -f /vol/.claude/.credentials.json' 2>/dev/null; then
  DONOR="$(docker volume ls -q | grep -E '^(sr-.*-home|specrunner-probe-home)$' | grep -v "^${VOLUME}$" | head -1 || true)"
  if [[ -n "$DONOR" ]]; then
    docker run --rm --user root --entrypoint bash \
      -v "$DONOR":/from:ro -v "$VOLUME":/to "$IMAGE" -lc '
        set -e
        if [ -f /from/.claude/.credentials.json ]; then
          mkdir -p /to/.claude
          cp -a /from/.claude/.credentials.json /to/.claude/ 2>/dev/null || true
          [ -f /from/.claude.json ] && cp -a /from/.claude.json /to/ || true
          chown -R 1000:1000 /to/.claude /to/.claude.json 2>/dev/null || true
        fi
      '
    echo "carried the claude.ai OAuth over from volume '$DONOR'"
  fi
fi

# ── 3. The clone ────────────────────────────────────────────────────────────
docker run --rm --user root --entrypoint bash -v "$VOLUME":/home/runner "$IMAGE" -lc "
  set -e
  git config --global --add safe.directory /home/runner/clone
  if [ ! -d /home/runner/clone/.git ]; then
    git clone --quiet 'https://x-access-token:${TOKEN}@github.com/${SLUG}.git' /home/runner/clone
  fi
  git -C /home/runner/clone remote set-url origin 'https://x-access-token:${TOKEN}@github.com/${SLUG}.git'
  git -C /home/runner/clone config user.name  'spec-queue-runner'
  git -C /home/runner/clone config user.email 'spec-queue-runner@users.noreply.github.com'
  chown -R 1000:1000 /home/runner/clone
  echo \"clone HEAD: \$(git -C /home/runner/clone rev-parse --short HEAD)\"
"

# ── 4. The config ───────────────────────────────────────────────────────────
if [[ ! -f "$CONFIG" ]]; then
  sed -e "s|^slug .*|slug            = \"${SLUG}\"|" \
      -e "s|^base_branch .*|base_branch     = \"${BASE_BRANCH}\"|" \
      -e "s|^log .*|log             = \"/home/runner/state/${KEY}.log\"|" \
      -e "s|^lock .*|lock            = \"/home/runner/state/${KEY}.lock\"|" \
      "${REPO_ROOT}/deploy/spec-queue-runner.toml" > "$CONFIG"
  echo "wrote starter config: $CONFIG"
else
  echo "config already exists (left alone): $CONFIG"
fi

cat <<EOF

── next steps ────────────────────────────────────────────────────────────────
1. Review the config (operator_login is the ONLY account whose issues are acted on):
     \$EDITOR $CONFIG

2. Health-check the instance:
     docker run --rm -v "$PAT_FILE:/run/secrets/github_pat:ro" \\
       -v "$VOLUME:/home/runner" -v "$CONFIG:/etc/spec-runner/config.toml:ro" \\
       $IMAGE doctor /etc/spec-runner/config.toml

   If it reports the claude.ai credential missing, do the one-time login:
     docker run --rm -it -v "$VOLUME:/home/runner" \\
       --entrypoint /home/runner/.local/bin/claude $IMAGE /login

3. Schedule it. The plist is generated in-container, so the HOST paths are passed in
   explicitly (launchd runs on the host — container paths would mount the wrong files):
     docker run --rm -v "$CONFIG:/etc/spec-runner/config.toml:ro" \\
       -e SPEC_RUNNER_HOST_CONFIG="$CONFIG" \\
       -e SPEC_RUNNER_HOST_PAT="$PAT_FILE" \\
       -e SPEC_RUNNER_HOME_VOLUME="$VOLUME" \\
       -e SPEC_RUNNER_HOST_HOME="$HOME" \\
       $IMAGE install /etc/spec-runner/config.toml $IMAGE \\
       > ~/Library/LaunchAgents/com.spec-runner.${SLUG//\//.}.plist
     launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/com.spec-runner.${SLUG//\//.}.plist
──────────────────────────────────────────────────────────────────────────────
EOF
