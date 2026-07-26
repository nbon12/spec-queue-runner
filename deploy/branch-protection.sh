#!/usr/bin/env bash
# Apply the branch-protection ruleset that makes "no stale merges" a platform rule rather than a
# convention (CLAUDE.md, "Branch management"). Idempotent: re-running updates the existing ruleset
# in place rather than creating a second one.
#
#   deploy/branch-protection.sh [owner/repo] [base-branch]
#
# Defaults to nbon12/spec-queue-runner and master. Requires the `gh` CLI, authenticated as an
# account with admin on the repository:
#
#   gh auth login && gh auth refresh -s admin:repo_hook,repo
#
# NOTE: this is an ADMIN operation and is deliberately NOT something the runner can do. The
# runner's PAT must never hold administration permissions (constitution §6), so the rules that
# bound it are applied by the operator, out of band, from here.
set -euo pipefail

SLUG="${1:-nbon12/spec-queue-runner}"
BASE="${2:-master}"
RULESET_NAME="base-branch-up-to-date"

# The check the runner's own verify command mirrors: build + test. Required, and required to have
# run against a branch that already contains the latest base — that pairing is what makes a stale
# merge impossible rather than merely discouraged.
CHECK_CONTEXT="verify"

command -v gh >/dev/null || { echo "gh CLI not found — install it or apply the ruleset in the GitHub UI (see below)." >&2; exit 1; }

read -r -d '' RULESET <<JSON || true
{
  "name": "${RULESET_NAME}",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/${BASE}"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "allowed_merge_methods": ["merge", "squash", "rebase"]
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": true,
        "do_not_enforce_on_create": false,
        "required_status_checks": [ { "context": "${CHECK_CONTEXT}" } ]
      }
    }
  ],
  "bypass_actors": []
}
JSON

echo "==> repository setting: offer branch updates on out-of-date PRs"
gh api -X PATCH "repos/${SLUG}" -F allow_update_branch=true --silent

echo "==> ruleset '${RULESET_NAME}' on ${SLUG}:${BASE}"
existing="$(gh api "repos/${SLUG}/rulesets" --jq ".[] | select(.name == \"${RULESET_NAME}\") | .id" || true)"

if [[ -n "${existing}" ]]; then
  printf '%s' "${RULESET}" | gh api -X PUT "repos/${SLUG}/rulesets/${existing}" --input - >/dev/null
  echo "    updated (id ${existing})"
else
  printf '%s' "${RULESET}" | gh api -X POST "repos/${SLUG}/rulesets" --input - >/dev/null
  echo "    created"
fi

cat <<'SUMMARY'

Now in force on the base branch:

  * every change arrives by pull request — no direct pushes
  * the `verify` check (.github/workflows/verify.yml) must pass
  * "require branches to be up to date before merging" — a pull request whose branch is behind
    the base is UNMERGEABLE until it is rebased and re-checked
  * no force-pushes, no deletion
  * bypass_actors is empty: the rules apply to the operator and to the runner alike

Equivalent UI path, if you would rather not run this: Settings → Rules → Rulesets → New branch
ruleset → target the default branch → enable "Require a pull request before merging" and
"Require status checks to pass", tick `verify`, and tick "Require branches to be up to date
before merging".
SUMMARY
