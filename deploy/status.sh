#!/usr/bin/env bash
# One pane of glass for an instance: is it alive, what has it been doing, what is queued,
# is a live session open. Assembles from the container, the log, and GitHub — it stores nothing.
#
#   ./deploy/status.sh [log-lines]
set -uo pipefail

NAME="${SPEC_RUNNER_CONTAINER:-spec-runner-spec-queue-runner}"
SLUG="${SPEC_RUNNER_SLUG:-nbon12/spec-queue-runner}"
LINES="${1:-12}"

bold() { printf "\033[1m%s\033[0m\n" "$1"; }
dim()  { printf "\033[2m%s\033[0m\n" "$1"; }

bold "── supervisor ───────────────────────────────────────────────"
if status=$(docker inspect -f '{{.State.Status}} (up since {{.State.StartedAt}}) restarts={{.RestartCount}}' "$NAME" 2>/dev/null); then
  echo "  $NAME: $status"
else
  echo "  NOT RUNNING — start it with ./deploy/up.sh"
fi

echo
bold "── last $LINES log lines ─────────────────────────────────────"
docker logs --tail "$LINES" "$NAME" 2>&1 | sed 's/^/  /' || dim "  (no logs)"

echo
bold "── queue: open items ────────────────────────────────────────"
if command -v gh >/dev/null; then
  gh issue list --repo "$SLUG" --state open --limit 30 \
      --json number,title,labels \
      --jq '.[] | "  #\(.number)  \(.title[0:52])\n         [\([.labels[].name] | join(", "))]"' \
    2>/dev/null || echo "  (could not reach GitHub)"
  echo
  # An item the runner will act on next: ready, operator-authored, not iceboxed.
  actionable=$(gh issue list --repo "$SLUG" --state open --label status/ready --limit 30 \
      --json number,labels \
      --jq '[.[] | select([.labels[].name] | index("icebox") | not)] | length' 2>/dev/null || echo "?")
  echo "  actionable now: $actionable"
else
  echo "  (gh not installed)"
fi

echo
bold "── live sessions ────────────────────────────────────────────"
sessions=$(docker exec "$NAME" tmux ls 2>/dev/null) && echo "$sessions" | sed 's/^/  /' \
  || echo "  none open"

echo
bold "── recurrence (GitHub Actions) ──────────────────────────────"
if command -v gh >/dev/null; then
  gh run list --repo "$SLUG" --limit 5 \
      --json workflowName,status,conclusion,createdAt \
      --jq '.[] | "  \(.createdAt[0:16])  \(.workflowName)  \(.conclusion // .status)"' \
    2>/dev/null || echo "  (no workflow runs yet)"
else
  echo "  (gh not installed)"
fi

echo
dim "follow live:  docker logs -f $NAME"
